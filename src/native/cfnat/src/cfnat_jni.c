/**
 * @file cfnat_jni.c
 * @brief Android JNI 原生适配层，连接 Java / C# (.NET MAUI) Android VpnService
 *
 * 核心安全机制与生命周期保证：
 * - 采用 64 位单调递增 Handle ID 与全局 Registry 机制，彻底杜绝向 Java 层暴露原生堆对象指针；
 * - 所有 JNI 入口统一通过 cfnat_handle_acquire 校验状态并在锁保护下登记 active_calls；
 * - nativeDestroy 严格执行独占销毁所有权取得 -> 注册表脱链阻断新调用 -> cfnat_stop 唤醒阻塞任务 ->
 *   阻塞等待 active_calls 归零 -> cfnat_destroy 回收 C 实例 -> 释放 VpnService GlobalRef 与 protect_ctx ->
 *   释放 Handle 内存，彻底杜绝 Use-After-Free (UAF)；
 * - Android 模式下强制要求提供合法 VpnService 并核验 protect 方法，禁止无保护实例创建。
 */

#if defined(__ANDROID__) || defined(ANDROID) || defined(CFNAT_TEST_JNI)

#include <jni.h>
#include "../include/cfnat_api.h"
#include "cfnat_registry.h"
#include <stdlib.h>
#include <string.h>
#include <pthread.h>

/** JNI 保护上下文结构 */
typedef struct {
    JavaVM *jvm;
    jobject vpn_service_global_ref;
    jmethodID protect_method_id;
} cfnat_jni_protect_ctx_t;

/**
 * @brief Android VpnService.protect(int fd) JNI 穿透回调函数
 *
 * 由底层 cfnat 原生网络工作线程在执行外部出站 connect() 或 sendto() 前触发
 */
static int jni_vpn_protect_callback(CfnatSocketHandle handle, void *user_data) {
    cfnat_jni_protect_ctx_t *ctx = (cfnat_jni_protect_ctx_t *)user_data;
    if (!ctx || !ctx->jvm || !ctx->vpn_service_global_ref || !ctx->protect_method_id) {
        return 0;
    }

    if (handle == CFNAT_INVALID_SOCKET) {
        return 0;
    }

    JNIEnv *env = NULL;
    jint res = (*ctx->jvm)->GetEnv(ctx->jvm, (void **)&env, JNI_VERSION_1_6);
    int need_detach = 0;

    if (res == JNI_EDETACHED) {
        if ((*ctx->jvm)->AttachCurrentThread(ctx->jvm, &env, NULL) != JNI_OK) {
            return 0;
        }
        need_detach = 1;
    } else if (res != JNI_OK || !env) {
        return 0;
    }

    /* 调用当前 VpnService.protect(int socket) 实例方法 */
    jboolean ok = (*env)->CallBooleanMethod(env, ctx->vpn_service_global_ref, ctx->protect_method_id, (jint)handle);

    /* 检查并处理 JNI 异常：发生异常一律视为保护失败并清除异常 */
    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        ok = JNI_FALSE;
    }

    if (need_detach) {
        (*ctx->jvm)->DetachCurrentThread(ctx->jvm);
    }

    /* 1: 保护成功，允许出站；0: 保护失败，底层将强行关闭 Socket */
    return (ok == JNI_TRUE) ? 1 : 0;
}

/* ========================================================================= */
/*                          JNI 导出方法实现                                  */
/* ========================================================================= */

JNIEXPORT jlong JNICALL
Java_com_tesl_accelerator_CfnatBridge_nativeCreateInstance(
        JNIEnv *env, jclass clazz,
        jstring j_listen_addr, jint j_remote_tls_port, jint j_remote_http_port,
        jstring j_probe_sni, jstring j_cache_path, jstring j_ip_source_path,
        jobject j_vpn_service) {

    if (!env || !j_listen_addr || !j_probe_sni || !j_cache_path) {
        return 0;
    }

    /* 1. Android VPN 模式下强制要求提供非空 VpnService */
    if (!j_vpn_service) {
        return 0;
    }

    /* 2. 检查远端端口合法性 (防止溢出截断) */
    if (j_remote_tls_port <= 0 || j_remote_tls_port > 65535 ||
        j_remote_http_port <= 0 || j_remote_http_port > 65535) {
        return 0;
    }

    cfnat_config_t cfg;
    if (cfnat_config_init_default(&cfg) != 0) {
        return 0;
    }

    const char *listen_addr = (*env)->GetStringUTFChars(env, j_listen_addr, NULL);
    const char *probe_sni = (*env)->GetStringUTFChars(env, j_probe_sni, NULL);
    const char *cache_path = (*env)->GetStringUTFChars(env, j_cache_path, NULL);
    const char *ip_source = j_ip_source_path ? (*env)->GetStringUTFChars(env, j_ip_source_path, NULL) : NULL;

    if (!listen_addr || !probe_sni || !cache_path) {
        if (listen_addr) (*env)->ReleaseStringUTFChars(env, j_listen_addr, listen_addr);
        if (probe_sni) (*env)->ReleaseStringUTFChars(env, j_probe_sni, probe_sni);
        if (cache_path) (*env)->ReleaseStringUTFChars(env, j_cache_path, cache_path);
        if (ip_source) (*env)->ReleaseStringUTFChars(env, j_ip_source_path, ip_source);
        return 0;
    }

    cfg.local_listen_addr = listen_addr;
    cfg.remote_tls_port = (uint16_t)j_remote_tls_port;
    cfg.remote_http_port = (uint16_t)j_remote_http_port;
    cfg.probe_sni = probe_sni;
    cfg.cache_file_path = cache_path;
    cfg.ip_source_path = ip_source;

    /* 3. 初始化 VpnService Socket 保护上下文并进行全链路检查 */
    cfnat_jni_protect_ctx_t *pctx = (cfnat_jni_protect_ctx_t *)calloc(1, sizeof(cfnat_jni_protect_ctx_t));
    if (!pctx) {
        (*env)->ReleaseStringUTFChars(env, j_listen_addr, listen_addr);
        (*env)->ReleaseStringUTFChars(env, j_probe_sni, probe_sni);
        (*env)->ReleaseStringUTFChars(env, j_cache_path, cache_path);
        if (ip_source) (*env)->ReleaseStringUTFChars(env, j_ip_source_path, ip_source);
        return 0;
    }

    if ((*env)->GetJavaVM(env, &pctx->jvm) != JNI_OK || !pctx->jvm) {
        free(pctx);
        (*env)->ReleaseStringUTFChars(env, j_listen_addr, listen_addr);
        (*env)->ReleaseStringUTFChars(env, j_probe_sni, probe_sni);
        (*env)->ReleaseStringUTFChars(env, j_cache_path, cache_path);
        if (ip_source) (*env)->ReleaseStringUTFChars(env, j_ip_source_path, ip_source);
        return 0;
    }

    pctx->vpn_service_global_ref = (*env)->NewGlobalRef(env, j_vpn_service);
    if (!pctx->vpn_service_global_ref) {
        free(pctx);
        (*env)->ReleaseStringUTFChars(env, j_listen_addr, listen_addr);
        (*env)->ReleaseStringUTFChars(env, j_probe_sni, probe_sni);
        (*env)->ReleaseStringUTFChars(env, j_cache_path, cache_path);
        if (ip_source) (*env)->ReleaseStringUTFChars(env, j_ip_source_path, ip_source);
        return 0;
    }

    jclass vpn_class = (*env)->GetObjectClass(env, j_vpn_service);
    if (!vpn_class) {
        if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
        (*env)->DeleteGlobalRef(env, pctx->vpn_service_global_ref);
        free(pctx);
        (*env)->ReleaseStringUTFChars(env, j_listen_addr, listen_addr);
        (*env)->ReleaseStringUTFChars(env, j_probe_sni, probe_sni);
        (*env)->ReleaseStringUTFChars(env, j_cache_path, cache_path);
        if (ip_source) (*env)->ReleaseStringUTFChars(env, j_ip_source_path, ip_source);
        return 0;
    }

    pctx->protect_method_id = (*env)->GetMethodID(env, vpn_class, "protect", "(I)Z");
    (*env)->DeleteLocalRef(env, vpn_class);

    if (!pctx->protect_method_id) {
        if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
        (*env)->DeleteGlobalRef(env, pctx->vpn_service_global_ref);
        free(pctx);
        (*env)->ReleaseStringUTFChars(env, j_listen_addr, listen_addr);
        (*env)->ReleaseStringUTFChars(env, j_probe_sni, probe_sni);
        (*env)->ReleaseStringUTFChars(env, j_cache_path, cache_path);
        if (ip_source) (*env)->ReleaseStringUTFChars(env, j_ip_source_path, ip_source);
        return 0;
    }

    cfg.protect_socket_cb = jni_vpn_protect_callback;
    cfg.protect_user_data = pctx;

    /* 4. 创建纯 C 隔离实例 */
    cfnat_instance_t *inst = NULL;
    int rc = cfnat_create_instance(&cfg, &inst);

    (*env)->ReleaseStringUTFChars(env, j_listen_addr, listen_addr);
    (*env)->ReleaseStringUTFChars(env, j_probe_sni, probe_sni);
    (*env)->ReleaseStringUTFChars(env, j_cache_path, cache_path);
    if (ip_source) (*env)->ReleaseStringUTFChars(env, j_ip_source_path, ip_source);

    if (rc != 0 || !inst) {
        (*env)->DeleteGlobalRef(env, pctx->vpn_service_global_ref);
        free(pctx);
        return 0;
    }

    /* 5. 在全局注册表中登记并获取单调递增 Handle ID */
    uint64_t handle_id = cfnat_handle_register(inst, pctx);
    if (handle_id == 0) {
        cfnat_destroy(inst);
        (*env)->DeleteGlobalRef(env, pctx->vpn_service_global_ref);
        free(pctx);
        return 0;
    }

    return (jlong)handle_id;
}

JNIEXPORT jint JNICALL
Java_com_tesl_accelerator_CfnatBridge_nativeStart(JNIEnv *env, jclass clazz, jlong handle_id) {
    cfnat_jni_handle_t *h = cfnat_handle_acquire((uint64_t)handle_id);
    if (!h) return -2;

    int rc = cfnat_start(h->inst);
    cfnat_handle_release(h);
    return (jint)rc;
}

JNIEXPORT jint JNICALL
Java_com_tesl_accelerator_CfnatBridge_nativeWaitReady(JNIEnv *env, jclass clazz, jlong handle_id, jint timeout_ms) {
    cfnat_jni_handle_t *h = cfnat_handle_acquire((uint64_t)handle_id);
    if (!h) return -2;

    /* 在没有持有 Registry 锁的状态下执行等待 */
    int rc = cfnat_wait_ready(h->inst, (int)timeout_ms);
    cfnat_handle_release(h);
    return (jint)rc;
}

JNIEXPORT jint JNICALL
Java_com_tesl_accelerator_CfnatBridge_nativeGetStatus(JNIEnv *env, jclass clazz, jlong handle_id,
                                                     jintArray j_out_ints, jobjectArray j_out_strings) {
    if (!j_out_ints || (*env)->GetArrayLength(env, j_out_ints) < 4) {
        return -1;
    }
    if (j_out_strings && (*env)->GetArrayLength(env, j_out_strings) < 2) {
        return -1;
    }

    cfnat_jni_handle_t *h = cfnat_handle_acquire((uint64_t)handle_id);
    if (!h) return -2;

    cfnat_status_t st;
    int rc = cfnat_get_status(h->inst, &st);
    cfnat_handle_release(h);

    if (rc == 0) {
        jint ints[4] = {
            (jint)st.state,
            (jint)st.active_connections,
            (jint)st.current_latency_ms,
            (jint)st.total_candidates
        };
        (*env)->SetIntArrayRegion(env, j_out_ints, 0, 4, ints);

        if (j_out_strings) {
            jstring j_ip = (*env)->NewStringUTF(env, st.current_selected_ip);
            jstring j_err = (*env)->NewStringUTF(env, st.last_error_msg);
            (*env)->SetObjectArrayElement(env, j_out_strings, 0, j_ip);
            (*env)->SetObjectArrayElement(env, j_out_strings, 1, j_err);
            if (j_ip) (*env)->DeleteLocalRef(env, j_ip);
            if (j_err) (*env)->DeleteLocalRef(env, j_err);
        }
    }
    return (jint)rc;
}

JNIEXPORT jint JNICALL
Java_com_tesl_accelerator_CfnatBridge_nativeRescan(JNIEnv *env, jclass clazz, jlong handle_id) {
    cfnat_jni_handle_t *h = cfnat_handle_acquire((uint64_t)handle_id);
    if (!h) return -2;

    int rc = cfnat_rescan(h->inst);
    cfnat_handle_release(h);
    return (jint)rc;
}

JNIEXPORT jint JNICALL
Java_com_tesl_accelerator_CfnatBridge_nativeStop(JNIEnv *env, jclass clazz, jlong handle_id) {
    cfnat_jni_handle_t *h = cfnat_handle_acquire((uint64_t)handle_id);
    if (!h) return 0;

    int rc = cfnat_stop(h->inst);
    cfnat_handle_release(h);
    return (jint)rc;
}

/**
 * @brief 安全销毁 JNI 实例 (基于 Handle ID 注册表与独占销毁所有权)
 *
 * 严格执行顺序：
 * 1. 取得全局 Registry 锁，检索 Handle ID；
 * 2. 检查是否正在销毁，若已在销毁立即安全返回 (并发销毁幂等)；
 * 3. 取得独占销毁所有权，将 Handle 标记为 is_destroying 并从检索链表脱链 (阻止后续新调用)；
 * 4. 释放 Registry 全局锁；
 * 5. 立即调用 cfnat_stop 唤醒/中断正在等待的任务 (如 wait_ready) 与工作线程；
 * 6. 阻塞等待此前已登记的 active_calls 严格归零；
 * 7. 调用 cfnat_destroy 释放纯 C 实例内部资源；
 * 8. 在确认不会再发生 Protect 回调后，安全删除 VpnService GlobalRef 并释放 protect_ctx；
 * 9. 销毁 Handle 内部同步原语并释放 Handle 内存。
 */
JNIEXPORT void JNICALL
Java_com_tesl_accelerator_CfnatBridge_nativeDestroy(JNIEnv *env, jclass clazz, jlong handle_id) {
    cfnat_jni_handle_t *h = NULL;

    /* 1~3. 请求取得指定 Handle ID 的独占销毁所有权 */
    if (!cfnat_handle_destroy_begin((uint64_t)handle_id, &h)) {
        return; /* ID 无效或已被其他线程抢先开始销毁 */
    }

    /* 4~5. 唤醒并停止底层工作线程 */
    if (h->inst) {
        cfnat_stop(h->inst);
    }

    /* 6. 等待已登记的并发 JNI 调用完全退出 */
    cfnat_handle_destroy_finish(h);

    /* 7. 销毁纯 C 实例 */
    if (h->inst) {
        cfnat_destroy(h->inst);
        h->inst = NULL;
    }

    /* 8. 安全删除 JNI 全局引用与释放保护上下文 */
    cfnat_jni_protect_ctx_t *pctx = (cfnat_jni_protect_ctx_t *)h->protect_ctx;
    if (pctx) {
        if (env && pctx->vpn_service_global_ref) {
            (*env)->DeleteGlobalRef(env, pctx->vpn_service_global_ref);
            pctx->vpn_service_global_ref = NULL;
        }
        free(pctx);
        h->protect_ctx = NULL;
    }
}

#endif /* __ANDROID__ || ANDROID || CFNAT_TEST_JNI */
