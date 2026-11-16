#include "cfnat_instance.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>

#ifdef __ANDROID__
  #include <android/log.h>
#endif

#ifdef _WIN32
  #include <windows.h>
  #include <io.h>
  #define cfnat_close_socket(s) closesocket(s)
  #define cfnat_shutdown_socket(s, how) shutdown(s, how)
  #define CFNAT_SHUT_RDWR SD_BOTH
  #define CFNAT_SHUT_WR SD_SEND
#else
  #include <unistd.h>
  #include <fcntl.h>
  #include <poll.h>
  #include <errno.h>
  #include <sys/socket.h>
  #include <netinet/in.h>
  #include <arpa/inet.h>
  #define cfnat_close_socket(s) close(s)
  #define cfnat_shutdown_socket(s, how) shutdown(s, how)
  #define CFNAT_SHUT_RDWR SHUT_RDWR
  #define CFNAT_SHUT_WR SHUT_WR
#endif

#define CFNAT_ENGINE_VERSION "cfnat-core-v2.0.0-multi-inst"

/* 进程级控制台日志互斥锁 (仅用于控制台原子输出防交错，不保存任何实例运行状态) */
static pthread_mutex_t g_log_mu = PTHREAD_MUTEX_INITIALIZER;

const char *cfnat_version(void) {
    return CFNAT_ENGINE_VERSION;
}

static char *cfnat_strdup(const char *s) {
    if (!s) return NULL;
    size_t len = strlen(s);
    char *copy = (char *)malloc(len + 1);
    if (copy) {
        memcpy(copy, s, len + 1);
    }
    return copy;
}

void cfnat_log_internal(cfnat_instance_t *inst, cfnat_log_level_t level, const char *fmt, ...) {
    if (!inst) return;
    if (level > inst->config.log_level) return;

    char msg[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf(msg, sizeof(msg), fmt, args);
    va_end(args);

    if (inst->config.log_cb) {
        inst->config.log_cb(level, msg, inst->config.log_user_data);
    }
#ifdef __ANDROID__
    int prio = ANDROID_LOG_INFO;
    switch (level) {
        case CFNAT_LOG_ERROR: prio = ANDROID_LOG_ERROR; break;
        case CFNAT_LOG_WARN:  prio = ANDROID_LOG_WARN;  break;
        case CFNAT_LOG_INFO:  prio = ANDROID_LOG_INFO;  break;
        case CFNAT_LOG_DEBUG: prio = ANDROID_LOG_DEBUG; break;
        default: break;
    }
    __android_log_print(prio, "cfnat", "[cfnat-%u] %s", (unsigned int)inst->config.remote_tls_port, msg);
#else
    if (!inst->config.log_cb) {
        const char *lvl_name = "INFO";
        switch (level) {
            case CFNAT_LOG_ERROR: lvl_name = "ERROR"; break;
            case CFNAT_LOG_WARN:  lvl_name = "WARN";  break;
            case CFNAT_LOG_INFO:  lvl_name = "INFO";  break;
            case CFNAT_LOG_DEBUG: lvl_name = "DEBUG"; break;
            default: break;
        }
        pthread_mutex_lock(&g_log_mu);
        fprintf(stdout, "[cfnat-%u][%s] %s\n", (unsigned int)inst->config.remote_tls_port, lvl_name, msg);
        fflush(stdout);
        pthread_mutex_unlock(&g_log_mu);
    }
#endif
}

void cfnat_set_error(cfnat_instance_t *inst, int code, const char *fmt, ...) {
    if (!inst) return;
    pthread_mutex_lock(&inst->state_mu);
    inst->last_error_code = code;
    va_list args;
    va_start(args, fmt);
    vsnprintf(inst->last_error_msg, sizeof(inst->last_error_msg), fmt, args);
    va_end(args);
    pthread_mutex_unlock(&inst->state_mu);
    cfnat_log_internal(inst, CFNAT_LOG_ERROR, "错误[%d]: %s", code, inst->last_error_msg);
}

socket_t cfnat_create_and_protect_socket(cfnat_instance_t *inst, int domain, int type, int protocol) {
    socket_t s = socket(domain, type, protocol);
    if (s == (socket_t)(-1)
#ifdef _WIN32
        || s == INVALID_SOCKET
#endif
    ) {
        return (socket_t)(-1);
    }

    /* 必须对所有外部出站套接字执行平台保护 (Android VpnService.protect) */
    if (inst && inst->config.protect_socket_cb) {
        int ok = inst->config.protect_socket_cb((CfnatSocketHandle)s, inst->config.protect_user_data);
        if (!ok) {
            cfnat_log_internal(inst, CFNAT_LOG_ERROR, "套接字保护回调失败(fd=%ld)，已主动关闭底层套接字，拒绝发起未保护出站连接！", (long)s);
            cfnat_close_socket(s);
            return (socket_t)(-1);
        }
    }
    return s;
}

static int parse_endpoint(const char *addr_str, char *out_host, size_t host_sz, int *out_port) {
    if (!addr_str || !out_host || !out_port) return -1;
    const char *colon = strrchr(addr_str, ':');
    if (!colon) {
        snprintf(out_host, host_sz, "127.0.0.1");
        *out_port = atoi(addr_str);
        return (*out_port > 0) ? 0 : -1;
    }
    size_t hlen = (size_t)(colon - addr_str);
    if (hlen >= host_sz) hlen = host_sz - 1;
    memcpy(out_host, addr_str, hlen);
    out_host[hlen] = '\0';
    *out_port = atoi(colon + 1);
    return (*out_port > 0) ? 0 : -1;
}

static int cfnat_set_nonblocking(socket_t s, int nonblocking) {
#ifdef _WIN32
    u_long mode = nonblocking ? 1 : 0;
    return ioctlsocket(s, FIONBIO, &mode);
#else
    int flags = fcntl(s, F_GETFL, 0);
    if (flags < 0) return -1;
    return fcntl(s, F_SETFL, nonblocking ? (flags | O_NONBLOCK) : (flags & ~O_NONBLOCK));
#endif
}

static int cfnat_connect_timeout(socket_t s, const struct sockaddr *addr, int addrlen, int timeout_ms) {
    cfnat_set_nonblocking(s, 1);
    int rc = connect(s, addr, addrlen);
    if (rc == 0) {
        cfnat_set_nonblocking(s, 0);
        return 0;
    }
#ifdef _WIN32
    int err = WSAGetLastError();
    if (err != WSAEWOULDBLOCK) { cfnat_set_nonblocking(s, 0); return -1; }
    WSAPOLLFD pfd = { s, POLLOUT, 0 };
    int pr = WSAPoll(&pfd, 1, timeout_ms);
#else
    if (errno != EINPROGRESS) { cfnat_set_nonblocking(s, 0); return -1; }
    struct pollfd pfd = { s, POLLOUT, 0 };
    int pr = poll(&pfd, 1, timeout_ms);
#endif
    cfnat_set_nonblocking(s, 0);
    if (pr <= 0) return -1;
    int optval = 0;
    socklen_t optlen = sizeof(optval);
    getsockopt(s, SOL_SOCKET, SO_ERROR, (char *)&optval, &optlen);
    return (optval == 0) ? 0 : -1;
}

/** 保证将指定长度缓冲区完整发送 (处理 partial send) */
static int cfnat_send_all(socket_t s, const unsigned char *buf, size_t len, int timeout_ms) {
    if (!buf || len == 0) return 1;
    size_t sent = 0;
    while (sent < len) {
#ifdef _WIN32
        WSAPOLLFD pfd = { s, POLLOUT, 0 };
        int pr = WSAPoll(&pfd, 1, timeout_ms);
        if (pr <= 0) return 0;
        int n = send(s, (const char *)(buf + sent), (int)(len - sent), 0);
        if (n <= 0) return 0;
        sent += (size_t)n;
#else
        struct pollfd pfd = { s, POLLOUT, 0 };
        int pr = poll(&pfd, 1, timeout_ms);
        if (pr <= 0) return 0;
        ssize_t n = send(s, buf + sent, len - sent, MSG_NOSIGNAL);
        if (n <= 0) return 0;
        sent += (size_t)n;
#endif
    }
    return 1;
}

/** 带超时的接收函数 (定期轮询检查 inst->is_running，确保 stop 时迅速退出) */
static int cfnat_recv_timeout(cfnat_instance_t *inst, socket_t s, unsigned char *buf, size_t len, int timeout_ms) {
    long deadline = cfnat_now_ms() + timeout_ms;
    while (inst && inst->is_running && cfnat_now_ms() < deadline) {
#ifdef _WIN32
        WSAPOLLFD pfd = { s, POLLIN, 0 };
        int pr = WSAPoll(&pfd, 1, 100);
        if (pr > 0) return recv(s, (char *)buf, (int)len, 0);
        if (pr < 0) return -1;
#else
        struct pollfd pfd = { s, POLLIN, 0 };
        int pr = poll(&pfd, 1, 100);
        if (pr > 0) return (int)recv(s, buf, len, 0);
        if (pr < 0) return -1;
#endif
    }
    return -1;
}

static void cfnat_pool_add(cfnat_candidate_pool_t *pool, const char *ip, int latency_ms) {
    if (!pool || !ip || !*ip) return;
    pthread_mutex_lock(&pool->mu);
    for (size_t i = 0; i < pool->count; i++) {
        if (strcmp(pool->items[i].ip, ip) == 0) {
            pool->items[i].latency_ms = latency_ms;
            pool->items[i].consecutive_fails = 0;
            pthread_mutex_unlock(&pool->mu);
            return;
        }
    }
    if (pool->count >= pool->capacity) {
        size_t new_cap = pool->capacity ? pool->capacity * 2 : 16;
        cfnat_candidate_node_t *new_items = (cfnat_candidate_node_t *)realloc(pool->items, new_cap * sizeof(cfnat_candidate_node_t));
        if (!new_items) {
            pthread_mutex_unlock(&pool->mu);
            return;
        }
        pool->items = new_items;
        pool->capacity = new_cap;
    }
    cfnat_candidate_node_t *node = &pool->items[pool->count++];
    memset(node, 0, sizeof(*node));
    snprintf(node->ip, sizeof(node->ip), "%s", ip);
    node->latency_ms = latency_ms;
    node->ewma_latency_ms = latency_ms;
    pthread_mutex_unlock(&pool->mu);
}

/** 候选池按延迟升序排序 (调用方需持有 pool->mu) */
static void cfnat_pool_sort_locked(cfnat_candidate_pool_t *pool) {
    if (!pool || pool->count <= 1) return;
    for (size_t i = 0; i < pool->count - 1; i++) {
        for (size_t j = i + 1; j < pool->count; j++) {
            if (pool->items[j].latency_ms < pool->items[i].latency_ms) {
                cfnat_candidate_node_t tmp = pool->items[i];
                pool->items[i] = pool->items[j];
                pool->items[j] = tmp;
            }
        }
    }
}

static int cfnat_load_candidates_from_file(cfnat_instance_t *inst, const char *path) {
    if (!inst || !path) return 0;
    FILE *fp = fopen(path, "r");
    if (!fp) return 0;

    char line[128];
    int loaded = 0;
    while (fgets(line, sizeof(line), fp)) {
        char *p = line;
        while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++;
        if (*p == '#' || *p == '\0') continue;
        char *end = p + strlen(p) - 1;
        while (end > p && (*end == ' ' || *end == '\t' || *end == '\r' || *end == '\n')) {
            *end = '\0';
            end--;
        }
        if (*p) {
            char ip[64] = {0};
            int lat = 0;
            if (sscanf(p, "%63[^, \t#]%*[, \t]%d", ip, &lat) >= 1) {
                if (lat <= 0) lat = 999;
                cfnat_pool_add(&inst->pool, ip, lat);
                loaded++;
            }
        }
    }
    fclose(fp);
    return loaded;
}

/** 原子保存缓存：写临时文件 -> fflush -> 强制刷盘 -> 原子替换 */
static void cfnat_save_cache_to_file(cfnat_instance_t *inst) {
    if (!inst || !inst->config.cache_file_path || !*inst->config.cache_file_path) return;

    pthread_mutex_lock(&inst->pool.mu);
    if (inst->pool.count == 0) {
        pthread_mutex_unlock(&inst->pool.mu);
        return;
    }

    char tmp_path[512];
    snprintf(tmp_path, sizeof(tmp_path), "%s.tmp", inst->config.cache_file_path);

    FILE *fp = fopen(tmp_path, "w");
    if (!fp) {
        pthread_mutex_unlock(&inst->pool.mu);
        cfnat_log_internal(inst, CFNAT_LOG_WARN, "无法创建缓存临时文件: %s", tmp_path);
        return;
    }

    for (size_t i = 0; i < inst->pool.count; i++) {
        fprintf(fp, "%s,%d\n", inst->pool.items[i].ip, inst->pool.items[i].latency_ms);
    }
    fflush(fp);

#ifdef _WIN32
    _commit(_fileno(fp));
#else
    fsync(fileno(fp));
#endif
    fclose(fp);
    pthread_mutex_unlock(&inst->pool.mu);

#ifdef _WIN32
    MoveFileExA(tmp_path, inst->config.cache_file_path, MOVEFILE_REPLACE_EXISTING);
#else
    rename(tmp_path, inst->config.cache_file_path);
#endif

    cfnat_log_internal(inst, CFNAT_LOG_INFO, "优选缓存已原子保存至: %s", inst->config.cache_file_path);
}

/** 探测指定候选 IP 是否可用及往返延迟 (真实 TLS 握手验证 ServerHello) */
static int cfnat_probe_ip(cfnat_instance_t *inst, const char *ip, int *out_lat) {
    if (!inst || !ip || !*ip) return 0;
    socket_t s = cfnat_create_and_protect_socket(inst, AF_INET, SOCK_STREAM, 0);
    if (s == (socket_t)(-1)
#ifdef _WIN32
        || s == INVALID_SOCKET
#endif
    ) {
        return 0;
    }

    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(inst->config.remote_tls_port);
    if (inet_pton(AF_INET, ip, &sin.sin_addr) != 1) {
        cfnat_close_socket(s);
        return 0;
    }

    int rc = cfnat_connect_timeout(s, (struct sockaddr *)&sin, sizeof(sin), inst->config.connect_timeout_ms);
    if (rc != 0) {
        cfnat_close_socket(s);
        return 0;
    }

    /* 真实 TLS 握手测试 (使用探针专属 probe_sni 并严格校验 ServerHello 0x02) */
    int lat = 0;
    int ok = cfnat_tls_probe_handshake(s, inst->config.probe_sni, inst->config.tls_probe_timeout_ms, &lat);
    cfnat_close_socket(s);
    if (ok) {
        if (out_lat) *out_lat = lat;
        return 1;
    }
    return 0;
}

/**
 * 运行候选节点健康扫描
 * @param inst 目标实例指针
 * @param full_scan 1 为全量扫描 (初次启动、rescan 触发、低频全量、主节点连续失效)，0 为常规巡检 (主节点 + Top 3 备选)
 */
static int cfnat_scan_candidates_internal(cfnat_instance_t *inst, int full_scan) {
    if (!inst || !inst->is_running) return 0;

    pthread_mutex_lock(&inst->pool.mu);
    size_t count = inst->pool.count;
    if (count == 0) {
        pthread_mutex_unlock(&inst->pool.mu);
        return 0;
    }

    /* 确定本次测试数量：全量则为 pool.count，常规巡检则最多测试前 4 个已排序节点 (主节点 + Top 3 备选) */
    size_t scan_count = full_scan ? count : (count > 4 ? 4 : count);

    /* 复制一份待测 IP 列表，避免长持 pool.mu 影响并发查询 */
    char **ips = (char **)malloc(scan_count * sizeof(char *));
    if (!ips) {
        pthread_mutex_unlock(&inst->pool.mu);
        return 0;
    }
    for (size_t i = 0; i < scan_count; i++) {
        ips[i] = cfnat_strdup(inst->pool.items[i].ip);
    }
    pthread_mutex_unlock(&inst->pool.mu);

    char best_ip[64] = {0};
    int best_lat = 999999;
    int found_working = 0;
    int main_node_failed = 0;

    for (size_t i = 0; i < scan_count && inst->is_running; i++) {
        if (!ips[i]) continue;
        int lat = 0;
        int ok = cfnat_probe_ip(inst, ips[i], &lat);
        if (ok) {
            found_working = 1;
            /* 严格遵循锁顺序：state_mu 在前，pool.mu 在后 */
            pthread_mutex_lock(&inst->state_mu);
            pthread_mutex_lock(&inst->pool.mu);

            for (size_t k = 0; k < inst->pool.count; k++) {
                if (strcmp(inst->pool.items[k].ip, ips[i]) == 0) {
                    inst->pool.items[k].latency_ms = lat;
                    inst->pool.items[k].consecutive_fails = 0;
                    break;
                }
            }
            cfnat_pool_sort_locked(&inst->pool);

            if (lat < best_lat) {
                best_lat = lat;
                snprintf(best_ip, sizeof(best_ip), "%s", ips[i]);
                snprintf(inst->current_selected_ip, sizeof(inst->current_selected_ip), "%s", best_ip);
                inst->current_latency_ms = best_lat;
            }

            /* 只有真实健康检查成功验证节点后，才允许进入 READY */
            if (inst->state == CFNAT_STATE_LISTENING || inst->state == CFNAT_STATE_STARTING) {
                inst->state = CFNAT_STATE_READY;
                pthread_cond_broadcast(&inst->ready_cond);
            }

            pthread_mutex_unlock(&inst->pool.mu);
            pthread_mutex_unlock(&inst->state_mu);

            cfnat_log_internal(inst, CFNAT_LOG_DEBUG, "候选节点健康检查成功: %s (%d ms)", ips[i], lat);
        } else {
            pthread_mutex_lock(&inst->pool.mu);
            for (size_t k = 0; k < inst->pool.count; k++) {
                if (strcmp(inst->pool.items[k].ip, ips[i]) == 0) {
                    inst->pool.items[k].consecutive_fails++;
                    inst->pool.items[k].latency_ms += 100;
                    if (k == 0 && inst->pool.items[k].consecutive_fails >= 3) {
                        main_node_failed = 1;
                    }
                    break;
                }
            }
            pthread_mutex_unlock(&inst->pool.mu);
            cfnat_log_internal(inst, CFNAT_LOG_WARN, "候选节点健康检查未通过: %s，立即尝试下一个节点", ips[i]);
        }
    }

    for (size_t i = 0; i < scan_count; i++) {
        free(ips[i]);
    }
    free(ips);

    if (found_working) {
        cfnat_save_cache_to_file(inst);
    }

    if (full_scan) {
        cfnat_log_internal(inst, CFNAT_LOG_INFO, "全量候选节点优选扫描完成 (总计 %zu 节点)，已锁定最优: %s (%d ms)",
                           count, inst->current_selected_ip, inst->current_latency_ms);
    } else {
        cfnat_log_internal(inst, CFNAT_LOG_DEBUG, "主节点与备用节点常规巡检完成 (探针 %zu 节点)，当前最优: %s (%d ms)",
                           scan_count, inst->current_selected_ip, inst->current_latency_ms);
    }

    /* 若常规巡检中主节点连续 3 次探测失败，自动升级触发全量深度重扫自愈 */
    if (!full_scan && main_node_failed && inst->is_running) {
        cfnat_log_internal(inst, CFNAT_LOG_WARN, "当前主节点连续 3 次探针未通过，自动升级触发全量深度重扫自愈...");
        return cfnat_scan_candidates_internal(inst, 1);
    }

    return found_working;
}

static int cfnat_scan_candidates(cfnat_instance_t *inst) {
    return cfnat_scan_candidates_internal(inst, 1);
}

static void *cfnat_health_thread_fn(void *arg) {
    cfnat_instance_t *inst = (cfnat_instance_t *)arg;
    cfnat_log_internal(inst, CFNAT_LOG_INFO, "健康检查引擎已启动 (目标端口: %u, 探针SNI: %s)",
                       (unsigned int)inst->config.remote_tls_port, inst->config.probe_sni);

    /* 启动时立即执行初次全量健康扫描 (必须通过真实探测后才进入 READY) */
    cfnat_scan_candidates_internal(inst, 1);

    int routine_round = 0;

    while (inst->is_running) {
        int was_rescan_requested = 0;
        /* 使用条件变量等待下一次检查周期，或被 cfnat_rescan 即刻唤醒 */
        pthread_mutex_lock(&inst->rescan_mu);
        if (!inst->rescan_requested && inst->is_running) {
            int interval_ms = inst->config.health_check_interval_ms;
            if (interval_ms <= 0) interval_ms = 60000;
#ifdef _WIN32
            /* Windows 下轮询唤醒检查，保持响应度 */
            long deadline = (long)GetTickCount() + interval_ms;
            while (!inst->rescan_requested && inst->is_running && (long)GetTickCount() < deadline) {
                pthread_mutex_unlock(&inst->rescan_mu);
                Sleep(200);
                pthread_mutex_lock(&inst->rescan_mu);
            }
#else
            struct timespec ts;
            clock_gettime(CLOCK_REALTIME, &ts);
            ts.tv_sec += interval_ms / 1000;
            ts.tv_nsec += (interval_ms % 1000) * 1000000;
            if (ts.tv_nsec >= 1000000000) {
                ts.tv_sec += 1;
                ts.tv_nsec -= 1000000000;
            }
            pthread_cond_timedwait(&inst->rescan_cond, &inst->rescan_mu, &ts);
#endif
        }
        was_rescan_requested = inst->rescan_requested;
        inst->rescan_requested = 0;
        pthread_mutex_unlock(&inst->rescan_mu);

        if (!inst->is_running) break;

        /* 若由外部 rescan 即刻唤醒：执行全量重扫；否则执行常规主备巡检，每 5 轮巡检穿插一次低频全量扫描 */
        if (was_rescan_requested) {
            cfnat_log_internal(inst, CFNAT_LOG_INFO, "收到重新优选测速指令，即刻执行全量深度重扫...");
            cfnat_scan_candidates_internal(inst, 1);
            routine_round = 0;
        } else {
            routine_round++;
            if (routine_round >= 5) {
                cfnat_log_internal(inst, CFNAT_LOG_INFO, "低频全量优选周期到达 (第 %d 轮)，执行全量深度扫描...", routine_round);
                cfnat_scan_candidates_internal(inst, 1);
                routine_round = 0;
            } else {
                cfnat_scan_candidates_internal(inst, 0);
            }
        }
    }
    return NULL;
}

typedef struct {
    cfnat_instance_t *inst;
    cfnat_conn_node_t *node;
} cfnat_conn_worker_arg_t;

static void *cfnat_connection_worker(void *arg) {
    cfnat_conn_worker_arg_t *warg = (cfnat_conn_worker_arg_t *)arg;
    cfnat_instance_t *inst = warg->inst;
    cfnat_conn_node_t *node = warg->node;
    free(warg);

    socket_t client_fd = node->client_fd;
    uint64_t cid = node->conn_id;

    char target_ip[64] = {0};
    pthread_mutex_lock(&inst->state_mu);
    snprintf(target_ip, sizeof(target_ip), "%s", inst->current_selected_ip);
    pthread_mutex_unlock(&inst->state_mu);

    if (!target_ip[0]) {
        /* 无有效节点时直接断开 */
        goto cleanup_conn;
    }

    /* 嗅探首包报文 (最多等待 60 秒) */
    unsigned char first_buf[4096];
    int n = cfnat_recv_timeout(inst, client_fd, first_buf, sizeof(first_buf), 60000);
    if (n <= 0) {
        goto cleanup_conn;
    }

    /* 识别客户端协议：首字节 0x16 为 TLS ClientHello，走 remote_tls_port；否则走 remote_http_port */
    int is_tls = (first_buf[0] == 0x16);
    uint16_t target_port = is_tls ? inst->config.remote_tls_port : inst->config.remote_http_port;
    if (target_port == 0) target_port = 80;

    char client_sni[256] = {0};
    if (is_tls) {
        cfnat_parse_tls_sni(first_buf, (size_t)n, client_sni, sizeof(client_sni));
    }

    cfnat_log_internal(inst, CFNAT_LOG_DEBUG, "#%llu 客户端连接协议: %s (原生SNI: %s)，转发目标 %s:%u",
                       (unsigned long long)cid, is_tls ? "TLS" : "HTTP", client_sni[0] ? client_sni : "-", target_ip, (unsigned int)target_port);

    /* 创建受保护的出站套接字 (所有出站必须保护，失败立即关闭) */
    socket_t upstream_fd = cfnat_create_and_protect_socket(inst, AF_INET, SOCK_STREAM, 0);
    if (upstream_fd == (socket_t)(-1)
#ifdef _WIN32
        || upstream_fd == INVALID_SOCKET
#endif
    ) {
        goto cleanup_conn;
    }

    pthread_mutex_lock(&inst->conns_mu);
    node->upstream_fd = upstream_fd;
    pthread_mutex_unlock(&inst->conns_mu);

    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(target_port);
    inet_pton(AF_INET, target_ip, &sin.sin_addr);

    if (cfnat_connect_timeout(upstream_fd, (struct sockaddr *)&sin, sizeof(sin), inst->config.connect_timeout_ms) != 0) {
        cfnat_close_socket(upstream_fd);
        pthread_mutex_lock(&inst->conns_mu);
        node->upstream_fd = (socket_t)(-1);
        pthread_mutex_unlock(&inst->conns_mu);
        goto cleanup_conn;
    }

    /* 将客户端首包数据完整转发至上游 (处理 partial send) */
    if (!cfnat_send_all(upstream_fd, first_buf, (size_t)n, 5000)) {
        cfnat_close_socket(upstream_fd);
        pthread_mutex_lock(&inst->conns_mu);
        node->upstream_fd = (socket_t)(-1);
        pthread_mutex_unlock(&inst->conns_mu);
        goto cleanup_conn;
    }

    /* 双向数据中继转发循环 */
    unsigned char relay_buf[8192];
    while (inst->is_running) {
#ifdef _WIN32
        WSAPOLLFD pfds[2];
        pfds[0].fd = client_fd;   pfds[0].events = POLLIN; pfds[0].revents = 0;
        pfds[1].fd = upstream_fd; pfds[1].events = POLLIN; pfds[1].revents = 0;
        int pr = WSAPoll(pfds, 2, 1000);
#else
        struct pollfd pfds[2];
        pfds[0].fd = client_fd;   pfds[0].events = POLLIN; pfds[0].revents = 0;
        pfds[1].fd = upstream_fd; pfds[1].events = POLLIN; pfds[1].revents = 0;
        int pr = poll(pfds, 2, 1000);
#endif
        if (pr <= 0) {
            if (pr == 0) continue; /* 超时继续检查 is_running 标志 */
            break;
        }

        if (pfds[0].revents & POLLIN) {
#ifdef _WIN32
            int r = recv(client_fd, (char *)relay_buf, sizeof(relay_buf), 0);
#else
            int r = (int)recv(client_fd, relay_buf, sizeof(relay_buf), 0);
#endif
            if (r <= 0) {
                /* 客户端关闭连接，半关闭上游写入端 */
                cfnat_shutdown_socket(upstream_fd, CFNAT_SHUT_WR);
                break;
            }
            if (!cfnat_send_all(upstream_fd, relay_buf, (size_t)r, 5000)) {
                break;
            }
        }

        if (pfds[1].revents & POLLIN) {
#ifdef _WIN32
            int r = recv(upstream_fd, (char *)relay_buf, sizeof(relay_buf), 0);
#else
            int r = (int)recv(upstream_fd, relay_buf, sizeof(relay_buf), 0);
#endif
            if (r <= 0) {
                /* 上游关闭连接，半关闭客户端写入端 */
                cfnat_shutdown_socket(client_fd, CFNAT_SHUT_WR);
                break;
            }
            if (!cfnat_send_all(client_fd, relay_buf, (size_t)r, 5000)) {
                break;
            }
        }

        if ((pfds[0].revents & (POLLERR | POLLHUP)) || (pfds[1].revents & (POLLERR | POLLHUP))) {
            break;
        }
    }

    if (upstream_fd != (socket_t)(-1)) {
        cfnat_close_socket(upstream_fd);
        pthread_mutex_lock(&inst->conns_mu);
        node->upstream_fd = (socket_t)(-1);
        pthread_mutex_unlock(&inst->conns_mu);
    }

cleanup_conn:
    /* 从活动连接列表中安全注销并释放未关闭套接字 */
    pthread_mutex_lock(&inst->conns_mu);
    if (node->client_fd != (socket_t)(-1)) {
        cfnat_close_socket(node->client_fd);
        node->client_fd = (socket_t)(-1);
    }
    if (node->upstream_fd != (socket_t)(-1)) {
        cfnat_close_socket(node->upstream_fd);
        node->upstream_fd = (socket_t)(-1);
    }
    if (node->prev) node->prev->next = node->next;
    if (node->next) node->next->prev = node->prev;
    if (inst->conns_head == node) inst->conns_head = node->next;

    inst->active_connections--;
    if (inst->active_connections == 0) {
        pthread_cond_broadcast(&inst->conns_cond);
    }
    pthread_mutex_unlock(&inst->conns_mu);

    free(node);
    return NULL;
}

static void *cfnat_accept_thread_fn(void *arg) {
    cfnat_instance_t *inst = (cfnat_instance_t *)arg;
    cfnat_log_internal(inst, CFNAT_LOG_INFO, "本地转发监听器正在工作: %s", inst->config.local_listen_addr);

    while (inst->is_running) {
#ifdef _WIN32
        WSAPOLLFD pfd = { inst->listen_fd, POLLIN, 0 };
        int pr = WSAPoll(&pfd, 1, 200);
        if (pr <= 0) {
            if (!inst->is_running) break;
            continue;
        }
#else
        struct pollfd pfd = { inst->listen_fd, POLLIN, 0 };
        int pr = poll(&pfd, 1, 200);
        if (pr <= 0) {
            if (!inst->is_running) break;
            continue;
        }
#endif
        struct sockaddr_storage ss;
        socklen_t slen = sizeof(ss);
        socket_t cfd = accept(inst->listen_fd, (struct sockaddr *)&ss, &slen);
        if (cfd == (socket_t)(-1)
#ifdef _WIN32
            || cfd == INVALID_SOCKET
#endif
        ) {
            if (!inst->is_running) break;
#ifdef _WIN32
            Sleep(50);
#else
            usleep(50000);
#endif
            continue;
        }

        /* 注册活动连接节点，严禁游离 detached 线程 */
        cfnat_conn_node_t *node = (cfnat_conn_node_t *)calloc(1, sizeof(cfnat_conn_node_t));
        if (!node) {
            cfnat_close_socket(cfd);
            continue;
        }
        node->client_fd = cfd;
        node->upstream_fd = (socket_t)(-1);
        node->conn_id = ++inst->conn_seq;

        pthread_mutex_lock(&inst->conns_mu);
        node->next = inst->conns_head;
        if (inst->conns_head) inst->conns_head->prev = node;
        inst->conns_head = node;
        inst->active_connections++;
        pthread_mutex_unlock(&inst->conns_mu);

        cfnat_conn_worker_arg_t *warg = (cfnat_conn_worker_arg_t *)malloc(sizeof(cfnat_conn_worker_arg_t));
        if (!warg) {
            cfnat_close_socket(cfd);
            pthread_mutex_lock(&inst->conns_mu);
            if (node->prev) node->prev->next = node->next;
            if (node->next) node->next->prev = node->prev;
            if (inst->conns_head == node) inst->conns_head = node->next;
            inst->active_connections--;
            pthread_mutex_unlock(&inst->conns_mu);
            free(node);
            continue;
        }
        warg->inst = inst;
        warg->node = node;

        if (pthread_create(&node->thread, NULL, cfnat_connection_worker, warg) == 0) {
            pthread_detach(node->thread);
        } else {
            free(warg);
            cfnat_close_socket(cfd);
            pthread_mutex_lock(&inst->conns_mu);
            if (node->prev) node->prev->next = node->next;
            if (node->next) node->next->prev = node->prev;
            if (inst->conns_head == node) inst->conns_head = node->next;
            inst->active_connections--;
            pthread_mutex_unlock(&inst->conns_mu);
            free(node);
        }
    }
    return NULL;
}

/* ========================================================================= */
/*                          核心对外 API 实现                                 */
/* ========================================================================= */

int cfnat_config_init_default(cfnat_config_t *cfg) {
    if (!cfg) return -1;
    memset(cfg, 0, sizeof(*cfg));
    cfg->abi_version = CFNAT_ABI_VERSION;
    cfg->struct_size = (uint32_t)sizeof(cfnat_config_t);
    cfg->local_listen_addr = "127.0.0.1:10443";
    cfg->remote_tls_port = 443;
    cfg->remote_http_port = 80;
    cfg->probe_sni = "tesl-reborn.com";
    cfg->connect_timeout_ms = 1500;
    cfg->tls_probe_timeout_ms = 2500;
    cfg->health_check_interval_ms = 30000;
    cfg->ip_version = 4;
    cfg->random_mode = 0;
    cfg->ipnum = 20;
    cfg->num = 1;
    cfg->task = 4;
    cfg->code = 0;
    cfg->colo = NULL;
    cfg->ip_source_path = "ips-v4.txt";
    cfg->cache_file_path = "direct-cache.txt";
    cfg->locations_path = NULL;
    cfg->log_level = CFNAT_LOG_INFO;
    return 0;
}

int cfnat_create_instance(const cfnat_config_t *cfg, cfnat_instance_t **out_instance) {
    if (!cfg || !out_instance) return -1;
    *out_instance = NULL;

    if (cfg->abi_version != CFNAT_ABI_VERSION) {
        return -2; /* ABI 版本不兼容 */
    }
    if (cfg->struct_size != sizeof(cfnat_config_t)) {
        return -3; /* 结构体大小不匹配 */
    }
    if (!cfg->local_listen_addr || !*cfg->local_listen_addr ||
        !cfg->probe_sni || !*cfg->probe_sni ||
        !cfg->cache_file_path || !*cfg->cache_file_path ||
        cfg->remote_tls_port == 0) {
        return -4; /* 必填参数校验失败 */
    }

#if defined(__ANDROID__) || defined(ANDROID)
    /* Android 构建模式下强制要求平台提供 protect_socket_cb */
    if (!cfg->protect_socket_cb) {
        return -6;
    }
#endif

    cfnat_instance_t *inst = (cfnat_instance_t *)calloc(1, sizeof(cfnat_instance_t));
    if (!inst) return -5;

    /* 拷贝基础标量配置 */
    inst->config = *cfg;

    /* 深拷贝字符串资源并校验分配结果 */
    inst->deep_local_listen_addr = cfnat_strdup(cfg->local_listen_addr);
    inst->deep_probe_sni         = cfnat_strdup(cfg->probe_sni);
    inst->deep_colo              = cfnat_strdup(cfg->colo);
    inst->deep_ip_source_path    = cfnat_strdup(cfg->ip_source_path);
    inst->deep_cache_file_path   = cfnat_strdup(cfg->cache_file_path);
    inst->deep_locations_path    = cfnat_strdup(cfg->locations_path);

    if (!inst->deep_local_listen_addr || !inst->deep_probe_sni || !inst->deep_cache_file_path) {
        free(inst->deep_local_listen_addr);
        free(inst->deep_probe_sni);
        free(inst->deep_colo);
        free(inst->deep_ip_source_path);
        free(inst->deep_cache_file_path);
        free(inst->deep_locations_path);
        free(inst);
        return -5;
    }

    /* 重定向配置指针至内部深拷贝地址 */
    inst->config.local_listen_addr = inst->deep_local_listen_addr;
    inst->config.probe_sni         = inst->deep_probe_sni;
    inst->config.colo              = inst->deep_colo;
    inst->config.ip_source_path    = inst->deep_ip_source_path;
    inst->config.cache_file_path   = inst->deep_cache_file_path;
    inst->config.locations_path    = inst->deep_locations_path;

    /* 初始化同步原语 */
    if (pthread_mutex_init(&inst->state_mu, NULL) != 0 ||
        pthread_mutex_init(&inst->destroy_mu, NULL) != 0 ||
        pthread_cond_init(&inst->ready_cond, NULL) != 0 ||
        pthread_cond_init(&inst->stop_cond, NULL) != 0 ||
        pthread_mutex_init(&inst->rescan_mu, NULL) != 0 ||
        pthread_cond_init(&inst->rescan_cond, NULL) != 0 ||
        pthread_mutex_init(&inst->conns_mu, NULL) != 0 ||
        pthread_cond_init(&inst->conns_cond, NULL) != 0 ||
        pthread_mutex_init(&inst->pool.mu, NULL) != 0) {

        pthread_mutex_destroy(&inst->state_mu);
        pthread_mutex_destroy(&inst->destroy_mu);
        pthread_cond_destroy(&inst->ready_cond);
        pthread_cond_destroy(&inst->stop_cond);
        pthread_mutex_destroy(&inst->rescan_mu);
        pthread_cond_destroy(&inst->rescan_cond);
        pthread_mutex_destroy(&inst->conns_mu);
        pthread_cond_destroy(&inst->conns_cond);
        pthread_mutex_destroy(&inst->pool.mu);

        free(inst->deep_local_listen_addr);
        free(inst->deep_probe_sni);
        free(inst->deep_colo);
        free(inst->deep_ip_source_path);
        free(inst->deep_cache_file_path);
        free(inst->deep_locations_path);
        free(inst);
        return -5;
    }

    inst->listen_fd = (socket_t)(-1);
    inst->state = CFNAT_STATE_STOPPED;
    inst->is_running = 0;
    inst->conns_head = NULL;

    *out_instance = inst;
    return 0;
}

int cfnat_start(cfnat_instance_t *inst) {
    if (!inst) return -1;

    pthread_mutex_lock(&inst->state_mu);
    if (inst->state != CFNAT_STATE_STOPPED) {
        pthread_mutex_unlock(&inst->state_mu);
        return -2; /* 仅在 STOPPED 状态下允许启动 */
    }
    inst->state = CFNAT_STATE_STARTING;
    inst->is_running = 1;
    pthread_mutex_unlock(&inst->state_mu);

#ifdef _WIN32
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
#endif

    /* 1. 尝试从专属缓存读取候选 IP，若无则从源 IP 文件读取 */
    int loaded = cfnat_load_candidates_from_file(inst, inst->config.cache_file_path);
    if (loaded == 0 && inst->config.ip_source_path) {
        loaded = cfnat_load_candidates_from_file(inst, inst->config.ip_source_path);
    }
    cfnat_log_internal(inst, CFNAT_LOG_INFO, "初始化载入候选节点: %d 个", loaded);

    /* 2. 建立本地监听套接字 (本地回环监听，不需要也不允许执行 protect) */
    char host[64] = {0};
    int port = 0;
    if (parse_endpoint(inst->config.local_listen_addr, host, sizeof(host), &port) != 0) {
        cfnat_set_error(inst, 101, "解析本地监听端点失败: %s", inst->config.local_listen_addr);
        pthread_mutex_lock(&inst->state_mu);
        inst->state = CFNAT_STATE_ERROR;
        inst->is_running = 0;
        pthread_mutex_unlock(&inst->state_mu);
        return -3;
    }

    socket_t lfd = socket(AF_INET, SOCK_STREAM, 0);
    if (lfd == (socket_t)(-1)
#ifdef _WIN32
        || lfd == INVALID_SOCKET
#endif
    ) {
        cfnat_set_error(inst, 102, "创建本地监听套接字失败");
        pthread_mutex_lock(&inst->state_mu);
        inst->state = CFNAT_STATE_ERROR;
        inst->is_running = 0;
        pthread_mutex_unlock(&inst->state_mu);
        return -4;
    }

    int opt = 1;
    setsockopt(lfd, SOL_SOCKET, SO_REUSEADDR, (const char *)&opt, sizeof(opt));

    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons((uint16_t)port);
    inet_pton(AF_INET, host, &sin.sin_addr);

    if (bind(lfd, (struct sockaddr *)&sin, sizeof(sin)) != 0) {
        cfnat_set_error(inst, 103, "绑定端口失败: %s:%d", host, port);
        cfnat_close_socket(lfd);
        pthread_mutex_lock(&inst->state_mu);
        inst->state = CFNAT_STATE_ERROR;
        inst->is_running = 0;
        pthread_mutex_unlock(&inst->state_mu);
        return -5;
    }

    if (listen(lfd, 128) != 0) {
        cfnat_set_error(inst, 104, "监听端口失败: %s:%d", host, port);
        cfnat_close_socket(lfd);
        pthread_mutex_lock(&inst->state_mu);
        inst->state = CFNAT_STATE_ERROR;
        inst->is_running = 0;
        pthread_mutex_unlock(&inst->state_mu);
        return -6;
    }

    inst->listen_fd = lfd;

    /* 3. 本地监听建立完成，流转为 LISTENING (严禁在此处误判为 READY！) */
    pthread_mutex_lock(&inst->state_mu);
    inst->state = CFNAT_STATE_LISTENING;
    pthread_mutex_unlock(&inst->state_mu);

    /* 4. 启动健康检查与 Accept 线程 (带严格回滚) */
    if (pthread_create(&inst->health_thread, NULL, cfnat_health_thread_fn, inst) != 0) {
        cfnat_set_error(inst, 105, "创建健康检查线程失败");
        cfnat_close_socket(lfd);
        inst->listen_fd = (socket_t)(-1);
        pthread_mutex_lock(&inst->state_mu);
        inst->state = CFNAT_STATE_ERROR;
        inst->is_running = 0;
        pthread_mutex_unlock(&inst->state_mu);
        return -7;
    }
    inst->health_thread_started = 1;

    if (pthread_create(&inst->accept_thread, NULL, cfnat_accept_thread_fn, inst) != 0) {
        cfnat_set_error(inst, 106, "创建 Accept 线程失败");
        pthread_mutex_lock(&inst->state_mu);
        inst->is_running = 0;
        pthread_mutex_unlock(&inst->state_mu);
        pthread_join(inst->health_thread, NULL);
        inst->health_thread_started = 0;
        cfnat_close_socket(lfd);
        inst->listen_fd = (socket_t)(-1);
        pthread_mutex_lock(&inst->state_mu);
        inst->state = CFNAT_STATE_ERROR;
        pthread_mutex_unlock(&inst->state_mu);
        return -8;
    }
    inst->accept_thread_started = 1;

    cfnat_log_internal(inst, CFNAT_LOG_INFO, "本地监听已建立，正由健康检查线程执行初次候选节点探测...");
    return 0;
}

int cfnat_wait_ready(cfnat_instance_t *inst, int timeout_ms) {
    if (!inst) return -1;
    pthread_mutex_lock(&inst->state_mu);
    if (inst->state == CFNAT_STATE_READY) {
        pthread_mutex_unlock(&inst->state_mu);
        return 1;
    }

    if (timeout_ms <= 0) {
        while (inst->state != CFNAT_STATE_READY && inst->is_running) {
            pthread_cond_wait(&inst->ready_cond, &inst->state_mu);
        }
        int ready = (inst->state == CFNAT_STATE_READY);
        pthread_mutex_unlock(&inst->state_mu);
        return ready ? 1 : -1;
    }

    /* 轮询超时等待达到 READY 状态 */
    long start = 0;
#ifdef _WIN32
    start = (long)GetTickCount();
    while (inst->state != CFNAT_STATE_READY && inst->is_running) {
        pthread_mutex_unlock(&inst->state_mu);
        Sleep(50);
        pthread_mutex_lock(&inst->state_mu);
        if (inst->state == CFNAT_STATE_READY) break;
        if ((long)GetTickCount() - start >= timeout_ms) break;
    }
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    start = ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
    while (inst->state != CFNAT_STATE_READY && inst->is_running) {
        pthread_mutex_unlock(&inst->state_mu);
        usleep(50000);
        pthread_mutex_lock(&inst->state_mu);
        if (inst->state == CFNAT_STATE_READY) break;
        clock_gettime(CLOCK_MONOTONIC, &ts);
        long now = ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
        if (now - start >= timeout_ms) break;
    }
#endif
    int ready = (inst->state == CFNAT_STATE_READY);
    pthread_mutex_unlock(&inst->state_mu);
    return ready ? 1 : 0;
}

int cfnat_get_status(cfnat_instance_t *inst, cfnat_status_t *out_status) {
    if (!inst || !out_status) return -1;
    memset(out_status, 0, sizeof(*out_status));
    out_status->struct_size = (uint32_t)sizeof(cfnat_status_t);

    /* 严格保证锁顺序：先 state_mu，后 pool.mu，杜绝死锁并保证一致性瞬时快照 */
    pthread_mutex_lock(&inst->state_mu);
    pthread_mutex_lock(&inst->pool.mu);

    out_status->state = (cfnat_state_t)inst->state;
    out_status->active_connections = (uint32_t)inst->active_connections;
    snprintf(out_status->current_selected_ip, sizeof(out_status->current_selected_ip), "%s", inst->current_selected_ip);
    out_status->current_latency_ms = inst->current_latency_ms;
    out_status->last_error_code = inst->last_error_code;
    snprintf(out_status->last_error_msg, sizeof(out_status->last_error_msg), "%s", inst->last_error_msg);
    out_status->total_candidates = (uint32_t)inst->pool.count;

    pthread_mutex_unlock(&inst->pool.mu);
    pthread_mutex_unlock(&inst->state_mu);
    return 0;
}

int cfnat_get_last_error(cfnat_instance_t *inst, char *buf, size_t buf_sz) {
    if (!inst || !buf || buf_sz == 0) return -1;
    pthread_mutex_lock(&inst->state_mu);
    snprintf(buf, buf_sz, "%s", inst->last_error_msg);
    pthread_mutex_unlock(&inst->state_mu);
    return 0;
}

int cfnat_rescan(cfnat_instance_t *inst) {
    if (!inst) return -1;
    cfnat_log_internal(inst, CFNAT_LOG_INFO, "收到重新优选测速指令，即刻唤醒后台扫描器...");
    pthread_mutex_lock(&inst->rescan_mu);
    inst->rescan_requested = 1;
    pthread_cond_signal(&inst->rescan_cond);
    pthread_mutex_unlock(&inst->rescan_mu);
    return 0;
}

int cfnat_stop(cfnat_instance_t *inst) {
    if (!inst) return -1;

    pthread_mutex_lock(&inst->state_mu);
    if (inst->state == CFNAT_STATE_STOPPED) {
        pthread_mutex_unlock(&inst->state_mu);
        return 0; /* 幂等性：已停止直接返回 */
    }
    if (inst->state == CFNAT_STATE_STOPPING) {
        /* 并发调用者等待正在执行的停止过程结束 */
        while (inst->state == CFNAT_STATE_STOPPING) {
            pthread_cond_wait(&inst->stop_cond, &inst->state_mu);
        }
        pthread_mutex_unlock(&inst->state_mu);
        return 0;
    }
    inst->state = CFNAT_STATE_STOPPING;
    inst->is_running = 0;
    pthread_cond_broadcast(&inst->ready_cond);
    pthread_mutex_unlock(&inst->state_mu);

    cfnat_log_internal(inst, CFNAT_LOG_INFO, "正在执行实例安全停止流程...");

    /* 1. 唤醒健康检查线程 */
    pthread_mutex_lock(&inst->rescan_mu);
    inst->rescan_requested = 1;
    pthread_cond_signal(&inst->rescan_cond);
    pthread_mutex_unlock(&inst->rescan_mu);

    /* 2. 先等待 accept 监听线程退出 (poll 200ms 探测 is_running=0 后自动退出)，
       避免在线程仍在使用 listen_fd 时提前 close 导致描述符被系统复用竞态 */
    if (inst->accept_thread_started) {
        pthread_join(inst->accept_thread, NULL);
        inst->accept_thread_started = 0;
    }

    /* 3. 在 accept 线程彻底退出后，再安全关闭本地监听套接字 */
    if (inst->listen_fd != (socket_t)(-1)
#ifdef _WIN32
        && inst->listen_fd != INVALID_SOCKET
#endif
    ) {
        cfnat_close_socket(inst->listen_fd);
        inst->listen_fd = (socket_t)(-1);
    }

    /* 4. 唤醒并切断所有活跃连接，防止工作线程死等 */
    pthread_mutex_lock(&inst->conns_mu);
    cfnat_conn_node_t *cur = inst->conns_head;
    while (cur) {
        if (cur->client_fd != (socket_t)(-1)) {
            cfnat_shutdown_socket(cur->client_fd, CFNAT_SHUT_RDWR);
            cfnat_close_socket(cur->client_fd);
            cur->client_fd = (socket_t)(-1);
        }
        if (cur->upstream_fd != (socket_t)(-1)) {
            cfnat_shutdown_socket(cur->upstream_fd, CFNAT_SHUT_RDWR);
            cfnat_close_socket(cur->upstream_fd);
            cur->upstream_fd = (socket_t)(-1);
        }
        cur = cur->next;
    }
    pthread_mutex_unlock(&inst->conns_mu);

    /* 5. 阻塞等待所有连接 Worker 优雅退出，严禁悬挂游离线程 */
    pthread_mutex_lock(&inst->conns_mu);
    while (inst->active_connections > 0) {
        pthread_cond_wait(&inst->conns_cond, &inst->conns_mu);
    }
    pthread_mutex_unlock(&inst->conns_mu);

    /* 6. Join 回收健康巡检后台线程 */
    if (inst->health_thread_started) {
        pthread_join(inst->health_thread, NULL);
        inst->health_thread_started = 0;
    }

    /* 6. 保存最新优选缓存到磁盘 (原子写入) */
    cfnat_save_cache_to_file(inst);

    pthread_mutex_lock(&inst->state_mu);
    inst->state = CFNAT_STATE_STOPPED;
    pthread_cond_broadcast(&inst->stop_cond);
    pthread_mutex_unlock(&inst->state_mu);

    cfnat_log_internal(inst, CFNAT_LOG_INFO, "实例已安全停止 (所有工作线程已回收，连接数归零)。");
    return 0;
}

void cfnat_destroy(cfnat_instance_t *inst) {
    if (!inst) return;

    /* 防并发 destroy 互斥保护 */
    pthread_mutex_lock(&inst->destroy_mu);

    /* 若实例仍在运行，先执行 stop 确保所有工作线程安全退出 */
    if (inst->state != CFNAT_STATE_STOPPED) {
        cfnat_stop(inst);
    }

    /* 释放深拷贝的字符串配置 */
    free(inst->deep_local_listen_addr);
    free(inst->deep_probe_sni);
    free(inst->deep_colo);
    free(inst->deep_ip_source_path);
    free(inst->deep_cache_file_path);
    free(inst->deep_locations_path);

    /* 释放候选节点内存池 */
    pthread_mutex_lock(&inst->pool.mu);
    free(inst->pool.items);
    inst->pool.items = NULL;
    inst->pool.count = 0;
    inst->pool.capacity = 0;
    pthread_mutex_unlock(&inst->pool.mu);
    pthread_mutex_destroy(&inst->pool.mu);

    /* 销毁同步原语 */
    pthread_mutex_destroy(&inst->state_mu);
    pthread_cond_destroy(&inst->ready_cond);
    pthread_cond_destroy(&inst->stop_cond);
    pthread_mutex_destroy(&inst->rescan_mu);
    pthread_cond_destroy(&inst->rescan_cond);
    pthread_mutex_destroy(&inst->conns_mu);
    pthread_cond_destroy(&inst->conns_cond);

    pthread_mutex_unlock(&inst->destroy_mu);
    pthread_mutex_destroy(&inst->destroy_mu);

    free(inst);
}
