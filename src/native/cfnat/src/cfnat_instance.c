#include "cfnat_instance.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdarg.h>

#ifdef _WIN32
  #include <windows.h>
  #define cfnat_close_socket(s) closesocket(s)
#else
  #include <unistd.h>
  #include <fcntl.h>
  #include <poll.h>
  #include <errno.h>
  #define cfnat_close_socket(s) close(s)
#endif

#define CFNAT_ENGINE_VERSION "cfnat-core-v2.0.0-multi-inst"

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
    } else {
        const char *lvl_name = "INFO";
        switch (level) {
            case CFNAT_LOG_ERROR: lvl_name = "ERROR"; break;
            case CFNAT_LOG_WARN:  lvl_name = "WARN";  break;
            case CFNAT_LOG_INFO:  lvl_name = "INFO";  break;
            case CFNAT_LOG_DEBUG: lvl_name = "DEBUG"; break;
            default: break;
        }
        fprintf(stdout, "[cfnat-%u][%s] %s\n", inst->config.remote_tls_port, lvl_name, msg);
        fflush(stdout);
    }
}

void cfnat_set_error(cfnat_instance_t *inst, int code, const char *fmt, ...) {
    if (!inst) return;
    inst->last_error_code = code;
    va_list args;
    va_start(args, fmt);
    vsnprintf(inst->last_error_msg, sizeof(inst->last_error_msg), fmt, args);
    va_end(args);
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

    /* 必须在此处对所有外部出站 Socket 执行保护 */
    if (inst && inst->config.protect_socket_cb) {
        int ok = inst->config.protect_socket_cb((CfnatSocketHandle)s, inst->config.protect_user_data);
        if (!ok) {
            cfnat_log_internal(inst, CFNAT_LOG_ERROR, "套接字保护回调失败(fd=%ld)，主动关闭套接字，严防未保护出站连接引发回环！", (long)s);
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
                if (lat <= 0) lat = 100;
                cfnat_pool_add(&inst->pool, ip, lat);
                loaded++;
            }
        }
    }
    fclose(fp);
    return loaded;
}

static void cfnat_save_cache_to_file(cfnat_instance_t *inst) {
    if (!inst || !inst->config.cache_file_path) return;
    pthread_mutex_lock(&inst->pool.mu);
    if (inst->pool.count == 0) {
        pthread_mutex_unlock(&inst->pool.mu);
        return;
    }
    FILE *fp = fopen(inst->config.cache_file_path, "w");
    if (!fp) {
        pthread_mutex_unlock(&inst->pool.mu);
        cfnat_log_internal(inst, CFNAT_LOG_WARN, "无法写入缓存文件: %s", inst->config.cache_file_path);
        return;
    }
    for (size_t i = 0; i < inst->pool.count; i++) {
        fprintf(fp, "%s,%d\n", inst->pool.items[i].ip, inst->pool.items[i].latency_ms);
    }
    fclose(fp);
    pthread_mutex_unlock(&inst->pool.mu);
    cfnat_log_internal(inst, CFNAT_LOG_INFO, "优选缓存已保存到: %s (共 %zu 节点)", inst->config.cache_file_path, inst->pool.count);
}

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

    /* 真实 TLS 握手测试 (支持指定 SNI 并验证 ServerHello) */
    int lat = 0;
    int ok = cfnat_tls_probe_handshake(s, inst->config.probe_sni, inst->config.tls_probe_timeout_ms, &lat);
    cfnat_close_socket(s);
    if (ok) {
        if (out_lat) *out_lat = lat;
        return 1;
    }
    return 0;
}

static void *cfnat_health_thread_fn(void *arg) {
    cfnat_instance_t *inst = (cfnat_instance_t *)arg;
    cfnat_log_internal(inst, CFNAT_LOG_INFO, "健康检查引擎已启动 (目标端口: %u, SNI: %s)", inst->config.remote_tls_port, inst->config.probe_sni);

    while (inst->is_running) {
        char test_ip[64] = {0};
        pthread_mutex_lock(&inst->pool.mu);
        if (inst->pool.count > 0) {
            snprintf(test_ip, sizeof(test_ip), "%s", inst->pool.items[0].ip);
        }
        pthread_mutex_unlock(&inst->pool.mu);

        if (test_ip[0]) {
            int lat = 0;
            int ok = cfnat_probe_ip(inst, test_ip, &lat);
            if (ok) {
                pthread_mutex_lock(&inst->state_mu);
                snprintf(inst->current_selected_ip, sizeof(inst->current_selected_ip), "%s", test_ip);
                inst->current_latency_ms = lat;
                if (!inst->is_ready) {
                    inst->is_ready = 1;
                    pthread_cond_broadcast(&inst->ready_cond);
                }
                pthread_mutex_unlock(&inst->state_mu);
                cfnat_log_internal(inst, CFNAT_LOG_DEBUG, "健康检查通过: IP %s 握手延迟 %d ms", test_ip, lat);
            } else {
                cfnat_log_internal(inst, CFNAT_LOG_WARN, "节点健康检查未通过: IP %s", test_ip);
                /* 切换下一个备选节点 */
                pthread_mutex_lock(&inst->pool.mu);
                if (inst->pool.count > 1) {
                    cfnat_candidate_node_t first = inst->pool.items[0];
                    memmove(&inst->pool.items[0], &inst->pool.items[1], (inst->pool.count - 1) * sizeof(cfnat_candidate_node_t));
                    inst->pool.items[inst->pool.count - 1] = first;
                }
                pthread_mutex_unlock(&inst->pool.mu);
            }
        }

        /* 睡眠等待下一次健康检查 */
        int interval_sec = inst->config.health_check_interval_ms / 1000;
        if (interval_sec <= 0) interval_sec = 5;
        for (int i = 0; i < interval_sec && inst->is_running; i++) {
#ifdef _WIN32
            Sleep(1000);
#else
            sleep(1);
#endif
        }
    }
    return NULL;
}

typedef struct {
    cfnat_instance_t *inst;
    socket_t client_fd;
} cfnat_conn_ctx_t;

static void *cfnat_connection_worker(void *arg) {
    cfnat_conn_ctx_t *ctx = (cfnat_conn_ctx_t *)arg;
    cfnat_instance_t *inst = ctx->inst;
    socket_t client_fd = ctx->client_fd;
    free(ctx);

    inst->active_connections++;
    uint64_t cid = ++inst->conn_seq;

    char target_ip[64] = {0};
    pthread_mutex_lock(&inst->state_mu);
    snprintf(target_ip, sizeof(target_ip), "%s", inst->current_selected_ip);
    pthread_mutex_unlock(&inst->state_mu);

    if (!target_ip[0]) {
        cfnat_close_socket(client_fd);
        inst->active_connections--;
        return NULL;
    }

    /* 嗅探首包 ClientHello */
    unsigned char first_buf[4096];
    int n = 0;
#ifdef _WIN32
    n = recv(client_fd, (char *)first_buf, sizeof(first_buf), 0);
#else
    n = (int)recv(client_fd, first_buf, sizeof(first_buf), 0);
#endif
    if (n <= 0) {
        cfnat_close_socket(client_fd);
        inst->active_connections--;
        return NULL;
    }

    char client_sni[256] = {0};
    int is_tls = (first_buf[0] == 0x16);
    if (is_tls) {
        cfnat_parse_tls_sni(first_buf, (size_t)n, client_sni, sizeof(client_sni));
    }

    cfnat_log_internal(inst, CFNAT_LOG_DEBUG, "#%llu 收到客户端连接 (TLS: %d, SNI: %s)，准备转发至 %s:%u",
                       (unsigned long long)cid, is_tls, client_sni[0] ? client_sni : "-", target_ip, inst->config.remote_tls_port);

    /* 创建受保护的出站 Socket */
    socket_t upstream_fd = cfnat_create_and_protect_socket(inst, AF_INET, SOCK_STREAM, 0);
    if (upstream_fd == (socket_t)(-1)
#ifdef _WIN32
        || upstream_fd == INVALID_SOCKET
#endif
    ) {
        cfnat_close_socket(client_fd);
        inst->active_connections--;
        return NULL;
    }

    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(inst->config.remote_tls_port);
    inet_pton(AF_INET, target_ip, &sin.sin_addr);

    if (cfnat_connect_timeout(upstream_fd, (struct sockaddr *)&sin, sizeof(sin), inst->config.connect_timeout_ms) != 0) {
        cfnat_close_socket(upstream_fd);
        cfnat_close_socket(client_fd);
        inst->active_connections--;
        return NULL;
    }

    /* 将客户端首包透传转发至远端 */
#ifdef _WIN32
    send(upstream_fd, (const char *)first_buf, n, 0);
#else
    send(upstream_fd, first_buf, (size_t)n, MSG_NOSIGNAL);
#endif

    /* 双向数据中继转发 */
    unsigned char relay_buf[8192];
    while (inst->is_running) {
#ifdef _WIN32
        WSAPOLLFD pfds[2];
        pfds[0].fd = client_fd;   pfds[0].events = POLLIN; pfds[0].revents = 0;
        pfds[1].fd = upstream_fd; pfds[1].events = POLLIN; pfds[1].revents = 0;
        int pr = WSAPoll(pfds, 2, 5000);
#else
        struct pollfd pfds[2];
        pfds[0].fd = client_fd;   pfds[0].events = POLLIN; pfds[0].revents = 0;
        pfds[1].fd = upstream_fd; pfds[1].events = POLLIN; pfds[1].revents = 0;
        int pr = poll(pfds, 2, 5000);
#endif
        if (pr <= 0) {
            if (pr == 0) continue;
            break;
        }

        if (pfds[0].revents & POLLIN) {
#ifdef _WIN32
            int r = recv(client_fd, (char *)relay_buf, sizeof(relay_buf), 0);
#else
            int r = (int)recv(client_fd, relay_buf, sizeof(relay_buf), 0);
#endif
            if (r <= 0) break;
#ifdef _WIN32
            send(upstream_fd, (const char *)relay_buf, r, 0);
#else
            send(upstream_fd, relay_buf, (size_t)r, MSG_NOSIGNAL);
#endif
        }

        if (pfds[1].revents & POLLIN) {
#ifdef _WIN32
            int r = recv(upstream_fd, (char *)relay_buf, sizeof(relay_buf), 0);
#else
            int r = (int)recv(upstream_fd, relay_buf, sizeof(relay_buf), 0);
#endif
            if (r <= 0) break;
#ifdef _WIN32
            send(client_fd, (const char *)relay_buf, r, 0);
#else
            send(client_fd, relay_buf, (size_t)r, MSG_NOSIGNAL);
#endif
        }

        if ((pfds[0].revents & (POLLERR | POLLHUP)) || (pfds[1].revents & (POLLERR | POLLHUP))) {
            break;
        }
    }

    cfnat_close_socket(upstream_fd);
    cfnat_close_socket(client_fd);
    inst->active_connections--;
    return NULL;
}

static void *cfnat_accept_thread_fn(void *arg) {
    cfnat_instance_t *inst = (cfnat_instance_t *)arg;
    cfnat_log_internal(inst, CFNAT_LOG_INFO, "本地转发监听器已启动: %s", inst->config.local_listen_addr);

    while (inst->is_running) {
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
            Sleep(100);
#else
            usleep(100000);
#endif
            continue;
        }

        cfnat_conn_ctx_t *ctx = (cfnat_conn_ctx_t *)malloc(sizeof(cfnat_conn_ctx_t));
        if (!ctx) {
            cfnat_close_socket(cfd);
            continue;
        }
        ctx->inst = inst;
        ctx->client_fd = cfd;

        pthread_t tid;
        if (pthread_create(&tid, NULL, cfnat_connection_worker, ctx) == 0) {
            pthread_detach(tid);
        } else {
            free(ctx);
            cfnat_close_socket(cfd);
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
    cfg->preserve_client_sni = 1;
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
    if (!cfg->local_listen_addr || !cfg->cache_file_path) {
        return -4; /* 缺少必填参数 */
    }

    cfnat_instance_t *inst = (cfnat_instance_t *)calloc(1, sizeof(cfnat_instance_t));
    if (!inst) return -5;

    /* 拷贝基础标量配置 */
    inst->config = *cfg;

    /* 深拷贝字符串资源 */
    inst->deep_local_listen_addr = cfnat_strdup(cfg->local_listen_addr);
    inst->deep_probe_sni         = cfnat_strdup(cfg->probe_sni);
    inst->deep_colo              = cfnat_strdup(cfg->colo);
    inst->deep_ip_source_path    = cfnat_strdup(cfg->ip_source_path);
    inst->deep_cache_file_path   = cfnat_strdup(cfg->cache_file_path);
    inst->deep_locations_path    = cfnat_strdup(cfg->locations_path);

    /* 将指针重定向到实例内部深拷贝地址 */
    inst->config.local_listen_addr = inst->deep_local_listen_addr;
    inst->config.probe_sni         = inst->deep_probe_sni;
    inst->config.colo              = inst->deep_colo;
    inst->config.ip_source_path    = inst->deep_ip_source_path;
    inst->config.cache_file_path   = inst->deep_cache_file_path;
    inst->config.locations_path    = inst->deep_locations_path;

    /* 初始化同步原语 */
    pthread_mutex_init(&inst->state_mu, NULL);
    pthread_cond_init(&inst->ready_cond, NULL);
    pthread_mutex_init(&inst->pool.mu, NULL);

    inst->listen_fd = (socket_t)(-1);
    inst->state = CFNAT_STATE_STOPPED;
    inst->is_running = 0;
    inst->is_ready = 0;

    *out_instance = inst;
    return 0;
}

int cfnat_start(cfnat_instance_t *inst) {
    if (!inst) return -1;

    pthread_mutex_lock(&inst->state_mu);
    if (inst->state != CFNAT_STATE_STOPPED) {
        pthread_mutex_unlock(&inst->state_mu);
        return -2; /* 仅在停止状态下可启动 */
    }
    inst->state = CFNAT_STATE_STARTING;
    inst->is_running = 1;
    inst->is_ready = 0;
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
    cfnat_log_internal(inst, CFNAT_LOG_INFO, "初始化载入候选 IP: %d 个", loaded);

    pthread_mutex_lock(&inst->pool.mu);
    if (inst->pool.count > 0) {
        pthread_mutex_lock(&inst->state_mu);
        snprintf(inst->current_selected_ip, sizeof(inst->current_selected_ip), "%s", inst->pool.items[0].ip);
        inst->current_latency_ms = inst->pool.items[0].latency_ms;
        inst->is_ready = 1;
        pthread_cond_broadcast(&inst->ready_cond);
        pthread_mutex_unlock(&inst->state_mu);
    }
    pthread_mutex_unlock(&inst->pool.mu);

    /* 2. 建立本地监听套接字 (本地回环监听，不需要也不允许打 protect) */
    char host[64] = {0};
    int port = 0;
    if (parse_endpoint(inst->config.local_listen_addr, host, sizeof(host), &port) != 0) {
        cfnat_set_error(inst, 101, "解析本地监听端点失败: %s", inst->config.local_listen_addr);
        inst->state = CFNAT_STATE_ERROR;
        inst->is_running = 0;
        return -3;
    }

    socket_t lfd = socket(AF_INET, SOCK_STREAM, 0);
    if (lfd == (socket_t)(-1)
#ifdef _WIN32
        || lfd == INVALID_SOCKET
#endif
    ) {
        cfnat_set_error(inst, 102, "创建本地监听套接字失败");
        inst->state = CFNAT_STATE_ERROR;
        inst->is_running = 0;
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
        inst->state = CFNAT_STATE_ERROR;
        inst->is_running = 0;
        return -5;
    }

    if (listen(lfd, 128) != 0) {
        cfnat_set_error(inst, 104, "监听失败: %s:%d", host, port);
        cfnat_close_socket(lfd);
        inst->state = CFNAT_STATE_ERROR;
        inst->is_running = 0;
        return -6;
    }

    inst->listen_fd = lfd;

    /* 3. 启动健康检查与 Accept 线程 */
    pthread_create(&inst->health_thread, NULL, cfnat_health_thread_fn, inst);
    pthread_create(&inst->accept_thread, NULL, cfnat_accept_thread_fn, inst);

    pthread_mutex_lock(&inst->state_mu);
    inst->state = CFNAT_STATE_RUNNING;
    pthread_mutex_unlock(&inst->state_mu);

    cfnat_log_internal(inst, CFNAT_LOG_INFO, "实例已成功启动运行。");
    return 0;
}

int cfnat_wait_ready(cfnat_instance_t *inst, int timeout_ms) {
    if (!inst) return -1;
    pthread_mutex_lock(&inst->state_mu);
    if (inst->is_ready) {
        pthread_mutex_unlock(&inst->state_mu);
        return 1;
    }

    if (timeout_ms <= 0) {
        while (!inst->is_ready && inst->is_running) {
            pthread_cond_wait(&inst->ready_cond, &inst->state_mu);
        }
        int ready = inst->is_ready;
        pthread_mutex_unlock(&inst->state_mu);
        return ready ? 1 : 0;
    }

    /* 轮询等待 */
    long start = 0;
#ifdef _WIN32
    start = (long)GetTickCount();
    while (!inst->is_ready && inst->is_running) {
        pthread_mutex_unlock(&inst->state_mu);
        Sleep(50);
        pthread_mutex_lock(&inst->state_mu);
        if (inst->is_ready) break;
        if ((long)GetTickCount() - start >= timeout_ms) break;
    }
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    start = ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
    while (!inst->is_ready && inst->is_running) {
        pthread_mutex_unlock(&inst->state_mu);
        usleep(50000);
        pthread_mutex_lock(&inst->state_mu);
        if (inst->is_ready) break;
        clock_gettime(CLOCK_MONOTONIC, &ts);
        long now = ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
        if (now - start >= timeout_ms) break;
    }
#endif
    int ready = inst->is_ready;
    pthread_mutex_unlock(&inst->state_mu);
    return ready ? 1 : 0;
}

int cfnat_get_status(cfnat_instance_t *inst, cfnat_status_t *out_status) {
    if (!inst || !out_status) return -1;
    memset(out_status, 0, sizeof(*out_status));
    out_status->struct_size = (uint32_t)sizeof(cfnat_status_t);

    pthread_mutex_lock(&inst->state_mu);
    out_status->state = (cfnat_state_t)inst->state;
    out_status->active_connections = (uint32_t)inst->active_connections;
    snprintf(out_status->current_selected_ip, sizeof(out_status->current_selected_ip), "%s", inst->current_selected_ip);
    out_status->current_latency_ms = inst->current_latency_ms;
    out_status->last_error_code = inst->last_error_code;
    snprintf(out_status->last_error_msg, sizeof(out_status->last_error_msg), "%s", inst->last_error_msg);
    pthread_mutex_unlock(&inst->state_mu);

    pthread_mutex_lock(&inst->pool.mu);
    out_status->total_candidates = (uint32_t)inst->pool.count;
    pthread_mutex_unlock(&inst->pool.mu);
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
    cfnat_log_internal(inst, CFNAT_LOG_INFO, "收到重新优选触发指令。");
    /* 立即置就绪状态为 0，由健康检查重新挑选 */
    pthread_mutex_lock(&inst->state_mu);
    inst->is_ready = 0;
    pthread_mutex_unlock(&inst->state_mu);
    return 0;
}

int cfnat_stop(cfnat_instance_t *inst) {
    if (!inst) return -1;

    pthread_mutex_lock(&inst->state_mu);
    if (inst->state != CFNAT_STATE_RUNNING) {
        pthread_mutex_unlock(&inst->state_mu);
        return 0;
    }
    inst->state = CFNAT_STATE_STOPPING;
    inst->is_running = 0;
    inst->is_ready = 0;
    pthread_cond_broadcast(&inst->ready_cond);
    pthread_mutex_unlock(&inst->state_mu);

    cfnat_log_internal(inst, CFNAT_LOG_INFO, "正在停止实例...");

    /* 关闭监听套接字以解除 accept 阻塞 */
    if (inst->listen_fd != (socket_t)(-1)
#ifdef _WIN32
        && inst->listen_fd != INVALID_SOCKET
#endif
    ) {
        cfnat_close_socket(inst->listen_fd);
        inst->listen_fd = (socket_t)(-1);
    }

    /* 等待并回收工作线程 */
    pthread_join(inst->accept_thread, NULL);
    pthread_join(inst->health_thread, NULL);

    /* 保存优选缓存到磁盘 */
    cfnat_save_cache_to_file(inst);

    pthread_mutex_lock(&inst->state_mu);
    inst->state = CFNAT_STATE_STOPPED;
    pthread_mutex_unlock(&inst->state_mu);

    cfnat_log_internal(inst, CFNAT_LOG_INFO, "实例已安全停止。");
    return 0;
}

void cfnat_destroy(cfnat_instance_t *inst) {
    if (!inst) return;

    if (inst->state == CFNAT_STATE_RUNNING) {
        cfnat_stop(inst);
    }

    /* 释放深拷贝的配置字符串 */
    free(inst->deep_local_listen_addr);
    free(inst->deep_probe_sni);
    free(inst->deep_colo);
    free(inst->deep_ip_source_path);
    free(inst->deep_cache_file_path);
    free(inst->deep_locations_path);

    /* 释放候选池 */
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

    free(inst);
}
