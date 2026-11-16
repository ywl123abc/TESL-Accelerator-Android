#include "cfnat_registry.h"
#include <stdlib.h>
#include <string.h>

/* 全局注册表互斥锁与链表头节点 */
static pthread_mutex_t g_registry_mu = PTHREAD_MUTEX_INITIALIZER;
static cfnat_jni_handle_t *g_registry_head = NULL;
static uint64_t g_next_handle_id = 1;

uint64_t cfnat_handle_register(cfnat_instance_t *inst, void *protect_ctx) {
    if (!inst) return 0;

    pthread_mutex_lock(&g_registry_mu);

    /* 64 位单调递增 ID 溢出防御：绝不回绕复用 */
    if (g_next_handle_id == UINT64_MAX) {
        pthread_mutex_unlock(&g_registry_mu);
        return 0;
    }

    cfnat_jni_handle_t *h = (cfnat_jni_handle_t *)calloc(1, sizeof(cfnat_jni_handle_t));
    if (!h) {
        pthread_mutex_unlock(&g_registry_mu);
        return 0;
    }

    if (pthread_mutex_init(&h->mu, NULL) != 0 ||
        pthread_cond_init(&h->cond, NULL) != 0) {
        pthread_mutex_destroy(&h->mu);
        free(h);
        pthread_mutex_unlock(&g_registry_mu);
        return 0;
    }

    h->handle_id = g_next_handle_id++;
    h->inst = inst;
    h->protect_ctx = protect_ctx;
    h->active_calls = 0;
    h->is_destroying = 0;
    h->is_destroyed = 0;

    /* 头插法加入全局注册表 */
    h->next = g_registry_head;
    g_registry_head = h;

    uint64_t assigned_id = h->handle_id;
    pthread_mutex_unlock(&g_registry_mu);

    return assigned_id;
}

cfnat_jni_handle_t *cfnat_handle_acquire(uint64_t handle_id) {
    if (handle_id == 0) return NULL;

    pthread_mutex_lock(&g_registry_mu);
    cfnat_jni_handle_t *cur = g_registry_head;
    while (cur) {
        if (cur->handle_id == handle_id) {
            break;
        }
        cur = cur->next;
    }

    /* ID 不存在或 Handle 正在被销毁，立即安全拒绝 */
    if (!cur || cur->is_destroying) {
        pthread_mutex_unlock(&g_registry_mu);
        return NULL;
    }

    /* 在锁保护下登记 active_calls，确保在销毁线程介入前建立引用凭据 */
    pthread_mutex_lock(&cur->mu);
    cur->active_calls++;
    pthread_mutex_unlock(&cur->mu);

    pthread_mutex_unlock(&g_registry_mu);
    return cur;
}

void cfnat_handle_release(cfnat_jni_handle_t *handle) {
    if (!handle) return;

    pthread_mutex_lock(&handle->mu);
    handle->active_calls--;

    /* 当销毁线程在等待且所有调用已退出时，广播唤醒销毁线程 */
    if (handle->active_calls == 0 && handle->is_destroying) {
        pthread_cond_broadcast(&handle->cond);
    }
    pthread_mutex_unlock(&handle->mu);
}

int cfnat_handle_destroy_begin(uint64_t handle_id, cfnat_jni_handle_t **out_handle) {
    if (handle_id == 0 || !out_handle) return 0;
    *out_handle = NULL;

    pthread_mutex_lock(&g_registry_mu);

    cfnat_jni_handle_t *prev = NULL;
    cfnat_jni_handle_t *cur = g_registry_head;
    while (cur) {
        if (cur->handle_id == handle_id) {
            break;
        }
        prev = cur;
        cur = cur->next;
    }

    /* ID 不存在或已被其他线程标记为销毁，避免二次销毁 */
    if (!cur || cur->is_destroying) {
        pthread_mutex_unlock(&g_registry_mu);
        return 0;
    }

    /* 1. 标记销毁状态 */
    cur->is_destroying = 1;

    /* 2. 立即从全局可检索链表中脱链，彻底切断后续任何新调用进入 */
    if (prev) {
        prev->next = cur->next;
    } else {
        g_registry_head = cur->next;
    }
    cur->next = NULL;

    pthread_mutex_unlock(&g_registry_mu);

    *out_handle = cur;
    return 1; /* 成功取得独占销毁所有权 */
}

void cfnat_handle_destroy_finish(cfnat_jni_handle_t *handle) {
    if (!handle) return;

    /* 阻塞等待所有此前已登记的并发 JNI 调用完全退出 */
    pthread_mutex_lock(&handle->mu);
    while (handle->active_calls > 0) {
        pthread_cond_wait(&handle->cond, &handle->mu);
    }
    handle->is_destroyed = 1;
    pthread_mutex_unlock(&handle->mu);

    pthread_cond_destroy(&handle->cond);
    pthread_mutex_destroy(&handle->mu);
    free(handle);
}

size_t cfnat_registry_count(void) {
    pthread_mutex_lock(&g_registry_mu);
    size_t cnt = 0;
    cfnat_jni_handle_t *cur = g_registry_head;
    while (cur) {
        cnt++;
        cur = cur->next;
    }
    pthread_mutex_unlock(&g_registry_mu);
    return cnt;
}
