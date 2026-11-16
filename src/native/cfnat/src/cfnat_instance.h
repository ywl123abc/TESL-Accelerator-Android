#ifndef CFNAT_INSTANCE_H
#define CFNAT_INSTANCE_H

#include "../include/cfnat_api.h"
#include "cfnat_tls.h"
#include <pthread.h>
#include <stdbool.h>

#define CFNAT_MAX_IP_LEN 64
#define CFNAT_MAX_ERR_LEN 256

/** 候选节点结构体 */
typedef struct {
    char ip[CFNAT_MAX_IP_LEN];
    int latency_ms;
    int loss_rate;
    int ewma_latency_ms;
    int consecutive_fails;
} cfnat_candidate_node_t;

/** 候选节点内存池 */
typedef struct {
    cfnat_candidate_node_t *items;
    size_t count;
    size_t capacity;
    pthread_mutex_t mu;
} cfnat_candidate_pool_t;

/** 活动连接跟踪节点 */
typedef struct cfnat_conn_node {
    socket_t client_fd;
    socket_t upstream_fd;
    pthread_t thread;
    uint64_t conn_id;
    struct cfnat_conn_node *prev;
    struct cfnat_conn_node *next;
} cfnat_conn_node_t;

/** 实例内部核心数据结构 (纯 C 实例隔离，零全局可变状态) */
struct cfnat_instance_internal {
    cfnat_config_t config;

    /* 深拷贝字符串堆内存指针，destroy 时负责释放 */
    char *deep_local_listen_addr;
    char *deep_probe_sni;
    char *deep_colo;
    char *deep_ip_source_path;
    char *deep_cache_file_path;
    char *deep_locations_path;

    /* 生命周期与同步状态 */
    _Atomic int state;                 /* cfnat_state_t: STOPPED, STARTING, LISTENING, READY, STOPPING, ERROR */
    _Atomic int is_running;            /* 1 运行，0 退出 */
    _Atomic int active_connections;    /* 当前活跃连接数 */
    _Atomic uint64_t conn_seq;         /* 实例独立连接流水号 */

    /* 内部监听套接字与核心线程句柄 */
    socket_t listen_fd;
    pthread_t accept_thread;
    pthread_t health_thread;
    int accept_thread_started;
    int health_thread_started;

    /* 互斥锁与条件变量 (严格遵循 state_mu -> pool.mu 锁顺序，杜绝死锁) */
    pthread_mutex_t state_mu;
    pthread_mutex_t destroy_mu;
    pthread_cond_t ready_cond;
    pthread_cond_t stop_cond;

    /* 重新测速同步信号 */
    pthread_mutex_t rescan_mu;
    pthread_cond_t rescan_cond;
    int rescan_requested;

    /* 活动连接跟踪列表 */
    pthread_mutex_t conns_mu;
    pthread_cond_t conns_cond;
    cfnat_conn_node_t *conns_head;

    /* 候选 IP 池 */
    cfnat_candidate_pool_t pool;
    char current_selected_ip[CFNAT_MAX_IP_LEN];
    int current_latency_ms;

    /* 错误信息记录 */
    int last_error_code;
    char last_error_msg[CFNAT_MAX_ERR_LEN];
};

/** 实例内部辅助函数 */
void cfnat_log_internal(cfnat_instance_t *inst, cfnat_log_level_t level, const char *fmt, ...);
void cfnat_set_error(cfnat_instance_t *inst, int code, const char *fmt, ...);

/** 套接字创建与安全保护包装器 */
socket_t cfnat_create_and_protect_socket(cfnat_instance_t *inst, int domain, int type, int protocol);

#endif /* CFNAT_INSTANCE_H */
