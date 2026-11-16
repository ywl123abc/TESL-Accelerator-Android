#ifndef CFNAT_REGISTRY_H
#define CFNAT_REGISTRY_H

#include "../include/cfnat_api.h"
#include <stdint.h>
#include <pthread.h>

/**
 * @brief JNI Handle 实体结构体
 *
 * 维护实例指针、关联保护上下文、并发调用引用计数与销毁状态同步。
 * 由全局 Registry 分配并使用单调递增的 64 位整数 handle_id 进行索引，
 * 彻底禁止向 Java 层暴露堆对象原生地址。
 */
typedef struct cfnat_jni_handle {
    uint64_t handle_id;                  /**< 全局唯一单调递增 Handle ID */
    cfnat_instance_t *inst;              /**< 底层 C 实例指针 */
    void *protect_ctx;                   /**< 关联的 JNI 保护上下文 */
    int active_calls;                    /**< 当前正在执行操作的并发调用引用计数 */
    int is_destroying;                   /**< 是否已被标记为正在销毁 */
    int is_destroyed;                    /**< 是否已完成销毁 */
    pthread_mutex_t mu;                  /**< Handle 内部互斥锁 (保护 active_calls 与 cond) */
    pthread_cond_t cond;                 /**< 当 active_calls 归零时唤醒销毁线程 */
    struct cfnat_jni_handle *next;       /**< 注册表链表指针 */
} cfnat_jni_handle_t;

/**
 * @brief 在全局注册表中登记一个新的 Handle
 * @param inst 纯 C 实例指针
 * @param protect_ctx JNI 保护上下文
 * @return 成功返回非 0 单调递增 Handle ID；失败或溢出返回 0
 */
uint64_t cfnat_handle_register(cfnat_instance_t *inst, void *protect_ctx);

/**
 * @brief 按照 Handle ID 安全获取 Handle 引用 (JNI 入口)
 *
 * 获取 Registry 全局锁，校验 ID 存在性与销毁状态；
 * 若有效则在锁保护下原子递增 active_calls 计数后返回。
 *
 * @param handle_id 目标 Handle ID
 * @return 成功返回 Handle 指针；若 ID 不存在或正在销毁则返回 NULL
 */
cfnat_jni_handle_t *cfnat_handle_acquire(uint64_t handle_id);

/**
 * @brief 释放 Handle 调用引用 (JNI 出口，所有路径必须配对调用)
 *
 * 原子递减 active_calls；若计数归零且 Handle 正处于销毁等待状态，唤醒等待销毁的线程。
 *
 * @param handle 目标 Handle 指针
 */
void cfnat_handle_release(cfnat_jni_handle_t *handle);

/**
 * @brief 请求取得指定 Handle ID 的独占销毁所有权 (nativeDestroy 阶段 1)
 *
 * 检查 Handle 是否有效且未被标记为销毁。若成功，将 Handle 标记为 is_destroying，
 * 并立即从可检索的注册表链表中移除（阻止任何后续新调用进入）。
 *
 * @param handle_id 目标 Handle ID
 * @param out_handle 输出取得销毁所有权的 Handle 指针
 * @return 1 成功取得销毁所有权；0 表示 ID 不存在或已被其他线程抢先销毁
 */
int cfnat_handle_destroy_begin(uint64_t handle_id, cfnat_jni_handle_t **out_handle);

/**
 * @brief 阻塞等待所有活跃调用退出并释放 Handle 内存资源 (nativeDestroy 阶段 2)
 *
 * 阻塞等待 active_calls 严格归零，销毁同步互斥锁与条件变量，释放 Handle 内存。
 *
 * @param handle 取得销毁所有权的 Handle 指针
 */
void cfnat_handle_destroy_finish(cfnat_jni_handle_t *handle);

/**
 * @brief 查询当前注册表中存活的 Handle 总数 (用于单元测试验证与泄漏排查)
 */
size_t cfnat_registry_count(void);

#endif /* CFNAT_REGISTRY_H */
