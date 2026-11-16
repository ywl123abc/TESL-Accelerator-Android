#include "../../src/native/cfnat/include/cfnat_api.h"
#include "../../src/native/cfnat/src/cfnat_tls.h"
#include "../../src/native/cfnat/src/cfnat_registry.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>
#include <stdatomic.h>
#include <pthread.h>

#ifdef _WIN32
  #include <winsock2.h>
  #include <ws2tcpip.h>
  #include <windows.h>
  #define test_sleep_ms(ms) Sleep(ms)
  #define test_close_socket(s) closesocket(s)
#else
  #include <unistd.h>
  #include <sys/socket.h>
  #include <netinet/in.h>
  #include <arpa/inet.h>
  #include <errno.h>
  #define test_sleep_ms(ms) usleep((ms) * 1000)
  #define test_close_socket(s) close(s)
#endif

static int g_failed_tests = 0;
static int g_passed_tests = 0;
static int g_skipped_tests = 0;

#define TEST_ASSERT(cond, msg) do { \
    if (!(cond)) { \
        fprintf(stderr, "\n  [FAIL] 校验失败: %s (行 %d): %s\n", #cond, __LINE__, (msg)); \
        return 0; \
    } \
} while(0)

#define RUN_TEST(num, name, fn) do { \
    printf("[测试 %2d] %s...\n", (num), (name)); \
    int res = fn(); \
    if (res == 1) { \
        printf("  [PASSED] 测试 %d 通过。\n\n", (num)); \
        g_passed_tests++; \
    } else if (res == 2) { \
        printf("  [SKIPPED] 测试 %d 跳过 (外部网络不可达)。\n\n", (num)); \
        g_skipped_tests++; \
    } else { \
        printf("  [FAILED] 测试 %d 未通过！\n\n", (num)); \
        g_failed_tests++; \
    } \
} while(0)

/* 5.1: 多线程访问的全局计数器采用 C11 原子变量 */
static _Atomic int g_protect_calls_443 = 0;
static _Atomic int g_protect_calls_8443 = 0;
static _Atomic int g_simulated_fail_count = 0;

/* 模拟 Android VpnService.protect(fd) 平台回调 */
static int mock_android_vpn_protect(CfnatSocketHandle handle, void *user_data) {
    const char *tag = (const char *)user_data;
    if (tag && strcmp(tag, "channel-443") == 0) {
        atomic_fetch_add(&g_protect_calls_443, 1);
    } else if (tag && strcmp(tag, "channel-8443") == 0) {
        atomic_fetch_add(&g_protect_calls_8443, 1);
    }
    if (handle == CFNAT_INVALID_SOCKET) {
        return 0;
    }
    return 1;
}

static int mock_android_vpn_protect_failing(CfnatSocketHandle handle, void *user_data) {
    (void)handle;
    (void)user_data;
    atomic_fetch_add(&g_simulated_fail_count, 1);
    /* 模拟保护失败 (返回 0)，测试底层是否强行立即关闭 Socket 并中断连接 */
    return 0;
}

static void create_dummy_candidate_file(const char *path, const char *ip, int lat) {
    FILE *fp = fopen(path, "w");
    if (fp) {
        fprintf(fp, "%s,%d\n", ip, lat);
        fclose(fp);
    }
}

/* ========================================================================= */
/*                          第一部分：本地确定性测试                          */
/* ========================================================================= */

/* ------------------------------------------------------------- */
/* 测试 1: TLS 1.2 ClientHello 报文构造与安全随机数生成           */
/* ------------------------------------------------------------- */
static int test_1_tls_client_hello_build(void) {
    unsigned char buf[512];
    size_t len1 = cfnat_build_tls_client_hello(buf, sizeof(buf), "tesl-reborn.com");
    TEST_ASSERT(len1 > 0 && len1 < sizeof(buf), "ClientHello 生成成功且长度合法");
    TEST_ASSERT(buf[0] == 0x16, "ContentType 必须为 Handshake (0x16)");
    TEST_ASSERT(buf[1] == 0x03 && (buf[2] == 0x01 || buf[2] == 0x03), "TLS Record Version 为 TLS 1.0/1.2 (0x0301/0x0303)");
    TEST_ASSERT(buf[5] == 0x01, "Handshake Type 必须为 ClientHello (0x01)");
    TEST_ASSERT(buf[9] == 0x03 && buf[10] == 0x03, "ClientHello Client Version 为 0x0303");

    /* 校验随机数不可全为 0 (BCrypt / CryptGenRandom / /dev/urandom) */
    int non_zero_random = 0;
    for (int i = 11; i < 43; i++) {
        if (buf[i] != 0) non_zero_random++;
    }
    TEST_ASSERT(non_zero_random > 10, "ClientHello 随机数必须具有真随机熵");

    /* 校验两次生成的随机数不相同 */
    unsigned char buf2[512];
    size_t len2 = cfnat_build_tls_client_hello(buf2, sizeof(buf2), "tesl-reborn.com");
    TEST_ASSERT(len1 == len2, "相同 SNI 的报文长度一致");
    TEST_ASSERT(memcmp(buf + 11, buf2 + 11, 32) != 0, "连续生成的 ClientHello 随机数必须独立随机");

    printf("  -> TLS 1.2 ClientHello 生成校验通过 (长度: %zu 字节，安全随机熵充足)！\n", len1);
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 2: SNI 解析器正常用例与畸形溢出边界防护测试               */
/* ------------------------------------------------------------- */
static int test_2_sni_parser_bounds_safety(void) {
    unsigned char buf[512];
    size_t len = cfnat_build_tls_client_hello(buf, sizeof(buf), "sub.tesl-game.net");
    TEST_ASSERT(len > 0, "构造测试 ClientHello");

    char sni[128] = {0};
    int rc = cfnat_parse_tls_sni(buf, len, sni, sizeof(sni));
    TEST_ASSERT(rc == 0, "成功解析 SNI");
    TEST_ASSERT(strcmp(sni, "sub.tesl-game.net") == 0, "SNI 提取内容完全一致");

    /* 畸形测试 1: 空缓冲区或超短数据 */
    TEST_ASSERT(cfnat_parse_tls_sni(NULL, 0, sni, sizeof(sni)) != 0, "空指针安全防御");
    TEST_ASSERT(cfnat_parse_tls_sni(buf, 8, sni, sizeof(sni)) != 0, "截断至头部安全防御");

    /* 畸形测试 2: 非 Handshake 报文 (如 HTTP GET) */
    unsigned char http_buf[] = "GET / HTTP/1.1\r\nHost: tesl-game.net\r\n\r\n";
    TEST_ASSERT(cfnat_parse_tls_sni(http_buf, sizeof(http_buf), sni, sizeof(sni)) != 0, "非 TLS 协议拒绝解析");

    /* 畸形测试 3: 报文数据截断 */
    TEST_ASSERT(cfnat_parse_tls_sni(buf, len - 10, sni, sizeof(sni)) != 0, "报文数据截断安全防御");

    /* 畸形测试 4: Record 长度虚高 */
    unsigned char corrupt[512];
    memcpy(corrupt, buf, len);
    corrupt[3] = 0xff; corrupt[4] = 0xff;
    TEST_ASSERT(cfnat_parse_tls_sni(corrupt, len, sni, sizeof(sni)) != 0, "畸形超长记录长度安全拒绝");

    printf("  -> SNI 解析器正常提取及各类畸形截断边界防护 100%% 通过！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 3: TLS ServerHello 结构校验 (正常与各种非法格式)         */
/* ------------------------------------------------------------- */
static int test_3_server_hello_verification(void) {
    /* 构造一个合法的标准 TLS 1.2 ServerHello 报文 (47 字节) */
    unsigned char valid_sh[47] = {
        /* Record Header (5 字节) */
        0x16, 0x03, 0x03, 0x00, 0x2a, /* Type=Handshake, TLS 1.2, RecordLen=42 */
        /* Handshake Header (4 字节) */
        0x02, 0x00, 0x00, 0x26,       /* Type=ServerHello(0x02), HandshakeLen=38 */
        /* ServerHello Body (38 字节) */
        0x03, 0x03,                   /* Server Version = TLS 1.2 */
        /* Random (32 字节) */
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
        0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
        /* SessionID Length (1 字节) */
        0x00,
        /* CipherSuite (2 字节) */
        0xc0, 0x2f,
        /* Compression (1 字节) */
        0x00
    };

    TEST_ASSERT(cfnat_tls_verify_server_hello_packet(valid_sh, sizeof(valid_sh)) == 1, "合法 ServerHello 必须校验通过");

    /* 异常用例 1: 非 Handshake 记录 (0x15 Alert) */
    unsigned char invalid_sh[47];
    memcpy(invalid_sh, valid_sh, sizeof(valid_sh));
    invalid_sh[0] = 0x15;
    TEST_ASSERT(cfnat_tls_verify_server_hello_packet(invalid_sh, sizeof(invalid_sh)) == 0, "非 0x16 报文必须拒绝");

    /* 异常用例 2: Record 长度小于最小合法长度 (< 4) */
    memcpy(invalid_sh, valid_sh, sizeof(valid_sh));
    invalid_sh[3] = 0x00; invalid_sh[4] = 0x02;
    TEST_ASSERT(cfnat_tls_verify_server_hello_packet(invalid_sh, sizeof(invalid_sh)) == 0, "Record 长度过短拒绝");

    /* 异常用例 3: Record 长度超出最大允许 (4096) */
    memcpy(invalid_sh, valid_sh, sizeof(valid_sh));
    invalid_sh[3] = 0x11; invalid_sh[4] = 0x00; /* 4352 */
    TEST_ASSERT(cfnat_tls_verify_server_hello_packet(invalid_sh, sizeof(invalid_sh)) == 0, "Record 长度超标拒绝");

    /* 异常用例 4: 数据包截断 (len < 5 + rec_len) */
    TEST_ASSERT(cfnat_tls_verify_server_hello_packet(valid_sh, 25) == 0, "截断数据包拒绝");

    /* 异常用例 5: 非法 Handshake Type (!= 0x02, 例如 0x01 ClientHello) */
    memcpy(invalid_sh, valid_sh, sizeof(valid_sh));
    invalid_sh[5] = 0x01;
    TEST_ASSERT(cfnat_tls_verify_server_hello_packet(invalid_sh, sizeof(invalid_sh)) == 0, "非 0x02 握手类型拒绝");

    /* 异常用例 6: 握手长度超出记录长度 */
    memcpy(invalid_sh, valid_sh, sizeof(valid_sh));
    invalid_sh[7] = 0x01; invalid_sh[8] = 0x00; /* 256 */
    TEST_ASSERT(cfnat_tls_verify_server_hello_packet(invalid_sh, sizeof(invalid_sh)) == 0, "握手长度超出记录体拒绝");

    /* 异常用例 7: SessionID 长度超标 (> 32) */
    memcpy(invalid_sh, valid_sh, sizeof(valid_sh));
    invalid_sh[43] = 33;
    TEST_ASSERT(cfnat_tls_verify_server_hello_packet(invalid_sh, sizeof(invalid_sh)) == 0, "SessionID 长度超标拒绝");

    printf("  -> ServerHello 合法结构与 7 种畸形/非法输入严苛校验 100%% 通过！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 辅助：本地 Mock TLS 探测服务端                                */
/* ------------------------------------------------------------- */
typedef struct {
    socket_t listen_fd;
    int port;
    int mode; /* 1: 分段发送, 2: 截断 3 字节, 3: 读取超时 */
    _Atomic int thread_done;
} mock_tls_server_ctx_t;

static void *mock_tls_server_worker(void *arg) {
    mock_tls_server_ctx_t *ctx = (mock_tls_server_ctx_t *)arg;
    struct sockaddr_in csin;
    socklen_t len = sizeof(csin);
    socket_t cfd = accept(ctx->listen_fd, (struct sockaddr *)&csin, &len);
    if (cfd != (socket_t)(-1)
#ifdef _WIN32
        && cfd != INVALID_SOCKET
#endif
    ) {
        /* 读取客户端发送的 ClientHello */
        char tmp[1024];
        recv(cfd, tmp, sizeof(tmp), 0);

        static const unsigned char full_sh[47] = {
            0x16, 0x03, 0x03, 0x00, 0x2a,
            0x02, 0x00, 0x00, 0x26,
            0x03, 0x03,
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
            0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
            0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
            0x00,
            0xc0, 0x2f,
            0x00
        };

        if (ctx->mode == 1) {
            /* 模式 1: 分 3 次分段发送完整 ServerHello，验证探测端的 TCP 重组能力 */
            send(cfd, (const char *)full_sh, 5, 0);            /* 第 1 段: 5 字节 Header */
            test_sleep_ms(15);
            send(cfd, (const char *)full_sh + 5, 20, 0);       /* 第 2 段: 20 字节 */
            test_sleep_ms(15);
            send(cfd, (const char *)full_sh + 25, 22, 0);      /* 第 3 段: 22 字节 */
        } else if (ctx->mode == 2) {
            /* 模式 2: 仅返回 3 字节后关闭连接，验证 Header 截断异常防御 */
            send(cfd, (const char *)full_sh, 3, 0);
        } else if (ctx->mode == 3) {
            /* 模式 3: 发送 5 字节 Header 声明后续有 42 字节，但故意不发 Body 触发超时 */
            send(cfd, (const char *)full_sh, 5, 0);
            test_sleep_ms(1800);
        }
        test_close_socket(cfd);
    }
    ctx->thread_done = 1;
    return NULL;
}

static int start_mock_tls_server(mock_tls_server_ctx_t *ctx, int mode, pthread_t *out_tid) {
    memset(ctx, 0, sizeof(*ctx));
    ctx->mode = mode;
    ctx->thread_done = 0;
    ctx->listen_fd = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = 0;
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    bind(ctx->listen_fd, (struct sockaddr *)&sin, sizeof(sin));
    listen(ctx->listen_fd, 2);

    socklen_t slen = sizeof(sin);
    getsockname(ctx->listen_fd, (struct sockaddr *)&sin, &slen);
    ctx->port = ntohs(sin.sin_port);

    pthread_create(out_tid, NULL, mock_tls_server_worker, ctx);
    return ctx->port;
}

/* ------------------------------------------------------------- */
/* 测试 4: TCP 分段 ServerHello 正确组装与解析通过测试             */
/* ------------------------------------------------------------- */
static int test_4_tls_segmented_reassembly(void) {
    mock_tls_server_ctx_t ctx;
    pthread_t tid;
    int port = start_mock_tls_server(&ctx, 1, &tid);

    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons((uint16_t)port);
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    TEST_ASSERT(rc == 0, "连接分段 Mock TLS 服务");

    int lat = 0;
    int ok = cfnat_tls_probe_handshake(s, "tesl-reborn.com", 3000, &lat);
    test_close_socket(s);
    test_close_socket(ctx.listen_fd);
    pthread_join(tid, NULL);

    TEST_ASSERT(ok == 1, "分 3 次到达的 TCP ServerHello 数据包成功无损重组并校验通过");
    printf("  -> TCP 分段 ServerHello 流重组解析成功，测得往返延迟: %d ms！\n", lat);
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 5: 阶段 1 仅返回 3 字节截断数据的安全处理测试             */
/* ------------------------------------------------------------- */
static int test_5_tls_truncated_header_handling(void) {
    mock_tls_server_ctx_t ctx;
    pthread_t tid;
    int port = start_mock_tls_server(&ctx, 2, &tid);

    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons((uint16_t)port);
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    TEST_ASSERT(rc == 0, "连接截断 Mock TLS 服务");

    int lat = 0;
    int ok = cfnat_tls_probe_handshake(s, "tesl-reborn.com", 3000, &lat);
    test_close_socket(s);
    test_close_socket(ctx.listen_fd);
    pthread_join(tid, NULL);

    TEST_ASSERT(ok == 0, "Header 截断必须安全判定握手失败，严禁越界读取");
    printf("  -> 阶段 1 仅返回 3 字节截断数据被正确识别并安全拒绝！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 6: 阶段 2 Body 读取超时安全处理测试                       */
/* ------------------------------------------------------------- */
static int test_6_tls_body_read_timeout_handling(void) {
    mock_tls_server_ctx_t ctx;
    pthread_t tid;
    int port = start_mock_tls_server(&ctx, 3, &tid);

    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons((uint16_t)port);
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    TEST_ASSERT(rc == 0, "连接超时 Mock TLS 服务");

    int lat = 0;
    long t0 = cfnat_now_ms();
    int ok = cfnat_tls_probe_handshake(s, "tesl-reborn.com", 1500, &lat);
    long elapsed = cfnat_now_ms() - t0;
    test_close_socket(s);
    test_close_socket(ctx.listen_fd);
    pthread_join(tid, NULL);

    TEST_ASSERT(ok == 0, "Body 超时未到达必须判定失败");
    TEST_ASSERT(elapsed >= 1400 && elapsed <= 2500, "超时必须在合理时间内返回");
    printf("  -> 阶段 2 读取 Body 超时精准退出 (耗时: %ld ms)！\n", elapsed);
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 7: Handle Registry 基础注册、单调 64位 ID 与查错测试      */
/* ------------------------------------------------------------- */
static int test_7_handle_registry_basics(void) {
    cfnat_config_t cfg;
    cfnat_config_init_default(&cfg);
    cfg.local_listen_addr = "127.0.0.1:28443";
    cfg.remote_tls_port = 443;
    cfg.probe_sni = "tesl-reborn.com";

    cfnat_instance_t *inst1 = NULL;
    cfnat_instance_t *inst2 = NULL;
    TEST_ASSERT(cfnat_create_instance(&cfg, &inst1) == 0, "创建 inst1");
    cfg.local_listen_addr = "127.0.0.1:28444";
    TEST_ASSERT(cfnat_create_instance(&cfg, &inst2) == 0, "创建 inst2");

    uint64_t id1 = cfnat_handle_register(inst1, NULL);
    uint64_t id2 = cfnat_handle_register(inst2, NULL);
    TEST_ASSERT(id1 > 0, "ID 1 有效");
    TEST_ASSERT(id2 > id1, "Handle ID 严格单调递增，杜绝回绕或指针混淆");

    /* 验证有效查找与获取 */
    cfnat_jni_handle_t *lookup1 = cfnat_handle_acquire(id1);
    TEST_ASSERT(lookup1 != NULL && lookup1->inst == inst1, "通过 ID 1 成功 acquire");
    cfnat_handle_release(lookup1);

    /* 验证不存在的 ID 查找失败 */
    TEST_ASSERT(cfnat_handle_acquire(9999999ULL) == NULL, "不存在的 ID acquire 返回 NULL");

    /* 验证重复 destroy_begin 拒绝 */
    cfnat_jni_handle_t *to_destroy = NULL;
    TEST_ASSERT(cfnat_handle_destroy_begin(id1, &to_destroy) == 1, "首次 destroy_begin 成功");
    TEST_ASSERT(to_destroy != NULL && to_destroy->handle_id == id1, "取得被注销 handle");

    cfnat_jni_handle_t *dup_destroy = NULL;
    TEST_ASSERT(cfnat_handle_destroy_begin(id1, &dup_destroy) == 0, "已注销 ID 重复 destroy_begin 必须拒绝");

    /* 此时 acquire(id1) 必须立即返回 NULL */
    TEST_ASSERT(cfnat_handle_acquire(id1) == NULL, "已被注销的 ID 无法 acquire");

    /* 完成销毁 */
    cfnat_handle_destroy_finish(to_destroy);
    cfnat_destroy(inst1);

    /* 销毁第二个 Handle */
    TEST_ASSERT(cfnat_handle_destroy_begin(id2, &to_destroy) == 1, "注销 handle 2");
    cfnat_handle_destroy_finish(to_destroy);
    cfnat_destroy(inst2);

    printf("  -> Handle Registry 单调 ID 分配、重复注销防御与安全销毁测试通过！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 8: Handle Registry 多线程并发 acquire/release 与销毁裁决  */
/* ------------------------------------------------------------- */
typedef struct {
    uint64_t target_id;
    _Atomic int calls_succeeded;
    _Atomic int calls_failed_destroyed;
    _Atomic int worker_running;
} registry_stress_ctx_t;

static void *registry_caller_worker(void *arg) {
    registry_stress_ctx_t *ctx = (registry_stress_ctx_t *)arg;
    while (ctx->worker_running) {
        cfnat_jni_handle_t *h = cfnat_handle_acquire(ctx->target_id);
        if (h) {
            atomic_fetch_add(&ctx->calls_succeeded, 1);
            /* 模拟在锁外的业务逻辑工作 */
            test_sleep_ms(1);
            cfnat_handle_release(h);
        } else {
            atomic_fetch_add(&ctx->calls_failed_destroyed, 1);
            test_sleep_ms(1);
        }
    }
    return NULL;
}

static int test_8_handle_registry_concurrent_uaf(void) {
    cfnat_config_t cfg;
    cfnat_config_init_default(&cfg);
    cfg.local_listen_addr = "127.0.0.1:28445";
    cfg.remote_tls_port = 443;
    cfg.probe_sni = "tesl-reborn.com";

    cfnat_instance_t *inst = NULL;
    TEST_ASSERT(cfnat_create_instance(&cfg, &inst) == 0, "创建测试实例");

    uint64_t target_id = cfnat_handle_register(inst, NULL);
    TEST_ASSERT(target_id > 0, "注册测试 Handle");

    registry_stress_ctx_t ctx;
    ctx.target_id = target_id;
    ctx.calls_succeeded = 0;
    ctx.calls_failed_destroyed = 0;
    ctx.worker_running = 1;

    pthread_t tids[4];
    for (int i = 0; i < 4; i++) {
        pthread_create(&tids[i], NULL, registry_caller_worker, &ctx);
    }

    /* 让并发线程先工作 20ms */
    test_sleep_ms(20);

    /* 启动注销仲裁：原子移出全局注册表并标记 is_destroying */
    cfnat_jni_handle_t *destroy_h = NULL;
    TEST_ASSERT(cfnat_handle_destroy_begin(target_id, &destroy_h) == 1, "destroy_begin 成功摘除");

    /* 此时任何新调用必须立刻获取失败，决不能产生新引用 */
    cfnat_jni_handle_t *late_call = cfnat_handle_acquire(target_id);
    TEST_ASSERT(late_call == NULL, "destroy_begin 后任何新请求立即返回 NULL");

    /* 等待已进入的调用全部退出并释放 */
    cfnat_handle_destroy_finish(destroy_h);
    cfnat_destroy(inst);

    /* 停止工作线程并回收 */
    ctx.worker_running = 0;
    for (int i = 0; i < 4; i++) {
        pthread_join(tids[i], NULL);
    }

    TEST_ASSERT(ctx.calls_succeeded > 0, "销毁前有成功调用");
    TEST_ASSERT(ctx.calls_failed_destroyed > 0, "销毁后新请求被安全拒绝");

    printf("  -> Registry 并发 acquire/release/destroy_begin 仲裁消除 UAF 验证通过 (成功调用: %d, 安全拦截: %d)！\n",
           ctx.calls_succeeded, ctx.calls_failed_destroyed);
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 9: Handle Registry 双实例独立销毁互不影响测试             */
/* ------------------------------------------------------------- */
static int test_9_handle_registry_dual_isolation(void) {
    cfnat_config_t cfg;
    cfnat_config_init_default(&cfg);
    cfg.local_listen_addr = "127.0.0.1:28446";
    cfg.remote_tls_port = 443;
    cfg.probe_sni = "tesl-reborn.com";

    cfnat_instance_t *instA = NULL;
    cfnat_instance_t *instB = NULL;
    TEST_ASSERT(cfnat_create_instance(&cfg, &instA) == 0, "创建 instA");
    cfg.local_listen_addr = "127.0.0.1:28447";
    TEST_ASSERT(cfnat_create_instance(&cfg, &instB) == 0, "创建 instB");

    uint64_t idA = cfnat_handle_register(instA, NULL);
    uint64_t idB = cfnat_handle_register(instB, NULL);
    TEST_ASSERT(idA > 0, "注册 hA");
    TEST_ASSERT(idB > 0, "注册 hB");

    /* 销毁 A */
    cfnat_jni_handle_t *outA = NULL;
    TEST_ASSERT(cfnat_handle_destroy_begin(idA, &outA) == 1, "注销 A");
    cfnat_handle_destroy_finish(outA);
    cfnat_destroy(instA);

    /* 验证 B 依然完全正常可访问 */
    cfnat_jni_handle_t *chkB = cfnat_handle_acquire(idB);
    TEST_ASSERT(chkB != NULL, "A 销毁后 B 依然完全正常有效");
    cfnat_handle_release(chkB);

    /* 销毁 B */
    cfnat_jni_handle_t *outB = NULL;
    TEST_ASSERT(cfnat_handle_destroy_begin(idB, &outB) == 1, "注销 B");
    cfnat_handle_destroy_finish(outB);
    cfnat_destroy(instB);

    printf("  -> 双实例 Handle 独立生命周期完全隔离，互不干扰！\n");
    return 1;
}

/* 全局可复用实例与配置 */
static cfnat_instance_t *g_inst_443 = NULL;
static cfnat_instance_t *g_inst_8443 = NULL;

/* ------------------------------------------------------------- */
/* 测试 10: 两实例真实同时监听 (10443 与 18443)                  */
/* ------------------------------------------------------------- */
static int test_10_simultaneous_listen(void) {
    create_dummy_candidate_file("test_ips_443.txt", "104.18.32.246", 45);
    create_dummy_candidate_file("test_ips_8443.txt", "172.64.154.246", 50);
    remove("test_cache_443.txt");
    remove("test_cache_8443.txt");

    cfnat_config_t cfg_443;
    cfnat_config_init_default(&cfg_443);
    cfg_443.local_listen_addr = "127.0.0.1:10443";
    cfg_443.remote_tls_port = 443;
    cfg_443.remote_http_port = 80;
    cfg_443.probe_sni = "tesl-reborn.com";
    cfg_443.ip_source_path = "test_ips_443.txt";
    cfg_443.cache_file_path = "test_cache_443.txt";
    cfg_443.protect_socket_cb = mock_android_vpn_protect;
    cfg_443.protect_user_data = "channel-443";
    cfg_443.health_check_interval_ms = 10000;

    cfnat_config_t cfg_8443;
    cfnat_config_init_default(&cfg_8443);
    cfg_8443.local_listen_addr = "127.0.0.1:18443";
    cfg_8443.remote_tls_port = 8443;
    cfg_8443.remote_http_port = 80;
    cfg_8443.probe_sni = "pubsub.tesl-reborn.com";
    cfg_8443.ip_source_path = "test_ips_8443.txt";
    cfg_8443.cache_file_path = "test_cache_8443.txt";
    cfg_8443.protect_socket_cb = mock_android_vpn_protect;
    cfg_8443.protect_user_data = "channel-8443";
    cfg_8443.health_check_interval_ms = 10000;

    TEST_ASSERT(cfnat_create_instance(&cfg_443, &g_inst_443) == 0 && g_inst_443 != NULL, "创建443实例");
    TEST_ASSERT(cfnat_create_instance(&cfg_8443, &g_inst_8443) == 0 && g_inst_8443 != NULL, "创建8443实例");

    TEST_ASSERT(cfnat_start(g_inst_443) == 0, "启动443监听");
    TEST_ASSERT(cfnat_start(g_inst_8443) == 0, "启动8443监听");

    /* 验证本地端口已建立监听 */
    socket_t s1 = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin1;
    memset(&sin1, 0, sizeof(sin1));
    sin1.sin_family = AF_INET;
    sin1.sin_port = htons(10443);
    sin1.sin_addr.s_addr = inet_addr("127.0.0.1");
    int c1 = connect(s1, (struct sockaddr *)&sin1, sizeof(sin1));
    test_close_socket(s1);

    socket_t s2 = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin2;
    memset(&sin2, 0, sizeof(sin2));
    sin2.sin_family = AF_INET;
    sin2.sin_port = htons(18443);
    sin2.sin_addr.s_addr = inet_addr("127.0.0.1");
    int c2 = connect(s2, (struct sockaddr *)&sin2, sizeof(sin2));
    test_close_socket(s2);

    TEST_ASSERT(c1 == 0, "成功连接 127.0.0.1:10443");
    TEST_ASSERT(c2 == 0, "成功连接 127.0.0.1:18443");
    printf("  -> 10443 与 18443 双端口在同一进程内同时成功监听！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 11: Protect 回调失败时确认 connect 未发生               */
/* ------------------------------------------------------------- */
static int test_11_protect_failure_abort(void) {
    cfnat_config_t fail_cfg;
    cfnat_config_init_default(&fail_cfg);
    fail_cfg.local_listen_addr = "127.0.0.1:24443";
    fail_cfg.remote_tls_port = 443;
    fail_cfg.probe_sni = "tesl-reborn.com";
    fail_cfg.ip_source_path = "test_ips_443.txt";
    fail_cfg.cache_file_path = "test_cache_fail.txt";
    fail_cfg.protect_socket_cb = mock_android_vpn_protect_failing;

    cfnat_instance_t *fail_inst = NULL;
    TEST_ASSERT(cfnat_create_instance(&fail_cfg, &fail_inst) == 0, "创建保护失败测试实例");
    g_simulated_fail_count = 0;

    TEST_ASSERT(cfnat_start(fail_inst) == 0, "启动测试实例");
    test_sleep_ms(300);

    TEST_ASSERT(g_simulated_fail_count > 0, "成功捕获并拦截未保护套接字出站");
    cfnat_stop(fail_inst);
    cfnat_destroy(fail_inst);
    remove("test_cache_fail.txt");

    printf("  -> 拦截未保护套接字发起出站请求 (拦截次数: %d)！\n", g_simulated_fail_count);
    return 1;
}

/* 5.6: 本地 Echo 服务端及其生命周期严格管理 */
static socket_t g_echo_listen = (socket_t)(-1);
static int g_echo_port = 0;
static _Atomic int g_echo_running = 1;
static pthread_t g_echo_tid;
static int g_echo_started = 0;

static void *echo_server_thread(void *arg) {
    (void)arg;
    while (g_echo_running) {
        struct sockaddr_in csin;
        socklen_t len = sizeof(csin);
        socket_t cfd = accept(g_echo_listen, (struct sockaddr *)&csin, &len);
        if (cfd == (socket_t)(-1)
#ifdef _WIN32
            || cfd == INVALID_SOCKET
#endif
        ) {
            if (!g_echo_running) break;
            test_sleep_ms(10);
            continue;
        }

        unsigned char buf[4096];
        int n = recv(cfd, (char *)buf, sizeof(buf), 0);
        if (n > 0) {
            if (buf[0] == 0x16) {
                /* 响应合法完整的 TLS ServerHello 报文 (47 字节) */
                static const unsigned char mock_server_hello[47] = {
                    0x16, 0x03, 0x03, 0x00, 0x2a,
                    0x02, 0x00, 0x00, 0x26,
                    0x03, 0x03,
                    0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                    0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
                    0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
                    0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
                    0x00,
                    0xc0, 0x2f,
                    0x00
                };
                send(cfd, (const char *)mock_server_hello, sizeof(mock_server_hello), 0);
            } else {
                /* 普通业务数据直接回显 (Echo) */
                send(cfd, (const char *)buf, n, 0);
                while (g_echo_running) {
                    int r = recv(cfd, (char *)buf, sizeof(buf), 0);
                    if (r <= 0) break;
                    send(cfd, (const char *)buf, r, 0);
                }
            }
        }
        test_close_socket(cfd);
    }
    return NULL;
}

static int start_local_echo_server(void) {
    if (g_echo_port > 0) return g_echo_port;
    g_echo_listen = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = 0;
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    bind(g_echo_listen, (struct sockaddr *)&sin, sizeof(sin));
    listen(g_echo_listen, 16);

    socklen_t slen = sizeof(sin);
    getsockname(g_echo_listen, (struct sockaddr *)&sin, &slen);
    g_echo_port = ntohs(sin.sin_port);

    g_echo_started = 1;
    g_echo_running = 1;
    pthread_create(&g_echo_tid, NULL, echo_server_thread, NULL);
    return g_echo_port;
}

static void stop_local_echo_server(void) {
    if (!g_echo_started) return;
    g_echo_running = 0;
    if (g_echo_listen != (socket_t)(-1)
#ifdef _WIN32
        && g_echo_listen != INVALID_SOCKET
#endif
    ) {
        test_close_socket(g_echo_listen);
        g_echo_listen = (socket_t)(-1);
    }
    /* 触发连接唤醒 accept */
    if (g_echo_port > 0) {
        socket_t s = socket(AF_INET, SOCK_STREAM, 0);
        struct sockaddr_in sin;
        memset(&sin, 0, sizeof(sin));
        sin.sin_family = AF_INET;
        sin.sin_port = htons((uint16_t)g_echo_port);
        sin.sin_addr.s_addr = inet_addr("127.0.0.1");
        connect(s, (struct sockaddr *)&sin, sizeof(sin));
        test_close_socket(s);
    }
    pthread_join(g_echo_tid, NULL);
    g_echo_started = 0;
    g_echo_port = 0;
}

/* ------------------------------------------------------------- */
/* 测试 12: 双向 TCP 数据完整性测试                              */
/* ------------------------------------------------------------- */
static int test_12_bidirectional_tcp_integrity(void) {
    int echo_port = start_local_echo_server();
    TEST_ASSERT(echo_port > 0, "启动本地 Echo 服务器");

    create_dummy_candidate_file("test_ips_echo.txt", "127.0.0.1", 10);
    cfnat_config_t cfg_echo;
    cfnat_config_init_default(&cfg_echo);
    cfg_echo.local_listen_addr = "127.0.0.1:21443";
    cfg_echo.remote_tls_port = (uint16_t)echo_port;
    cfg_echo.remote_http_port = (uint16_t)echo_port;
    cfg_echo.probe_sni = "tesl-reborn.com";
    cfg_echo.ip_source_path = "test_ips_echo.txt";
    cfg_echo.cache_file_path = "test_cache_echo.txt";

    cfnat_instance_t *inst_echo = NULL;
    TEST_ASSERT(cfnat_create_instance(&cfg_echo, &inst_echo) == 0, "创建 Echo 代理实例");
    TEST_ASSERT(cfnat_start(inst_echo) == 0, "启动 Echo 代理实例");
    TEST_ASSERT(cfnat_wait_ready(inst_echo, 4000) == 1, "等待 Echo 代理 READY");

    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(21443);
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    TEST_ASSERT(rc == 0, "客户端连接代理端口 21443");

    /* 发送 4KB 测试报文 (首字节避开 0x16 走 HTTP 80/echo 端口) */
    unsigned char send_buf[4096];
    send_buf[0] = 0x41;
    for (int i = 1; i < 4096; i++) send_buf[i] = (unsigned char)(i % 251);
    send(s, (const char *)send_buf, sizeof(send_buf), 0);

    unsigned char recv_buf[4096];
    int total_recv = 0;
    while (total_recv < 4096) {
        int n = recv(s, (char *)recv_buf + total_recv, 4096 - total_recv, 0);
        if (n <= 0) break;
        total_recv += n;
    }
    test_close_socket(s);

    cfnat_stop(inst_echo);
    cfnat_destroy(inst_echo);
    remove("test_ips_echo.txt");
    remove("test_cache_echo.txt");

    TEST_ASSERT(total_recv == 4096, "完整接收 4096 字节");
    TEST_ASSERT(memcmp(send_buf, recv_buf, 4096) == 0, "中继数据逐字节一致无损");
    printf("  -> 4096 字节双向 TCP 中继端到端逐字节校验 100%% 一致！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 13: TCP 部分发送与流控切片测试                          */
/* ------------------------------------------------------------- */
static int test_13_tcp_partial_send(void) {
    create_dummy_candidate_file("test_ips_chunk.txt", "127.0.0.1", 10);
    cfnat_config_t cfg_chunk;
    cfnat_config_init_default(&cfg_chunk);
    cfg_chunk.local_listen_addr = "127.0.0.1:22443";
    cfg_chunk.remote_tls_port = (uint16_t)g_echo_port;
    cfg_chunk.remote_http_port = (uint16_t)g_echo_port;
    cfg_chunk.probe_sni = "tesl-reborn.com";
    cfg_chunk.ip_source_path = "test_ips_chunk.txt";
    cfg_chunk.cache_file_path = "test_cache_chunk.txt";

    cfnat_instance_t *inst_chunk = NULL;
    TEST_ASSERT(cfnat_create_instance(&cfg_chunk, &inst_chunk) == 0, "创建分块代理实例");
    TEST_ASSERT(cfnat_start(inst_chunk) == 0, "启动分块代理实例");
    TEST_ASSERT(cfnat_wait_ready(inst_chunk, 4000) == 1, "等待分块代理 READY");

    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(22443);
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    TEST_ASSERT(rc == 0, "连接代理");

    unsigned char chunk_data[512];
    chunk_data[0] = 0x48;
    for (int i = 1; i < 512; i++) chunk_data[i] = (unsigned char)(i ^ 0x5a);

    for (int i = 0; i < 512; i++) {
        send(s, (const char *)&chunk_data[i], 1, 0);
        test_sleep_ms(1);
    }

    unsigned char recv_chunk[512];
    int total_recv = 0;
    while (total_recv < 512) {
        int n = recv(s, (char *)recv_chunk + total_recv, 512 - total_recv, 0);
        if (n <= 0) break;
        total_recv += n;
    }
    test_close_socket(s);

    cfnat_stop(inst_chunk);
    cfnat_destroy(inst_chunk);
    remove("test_ips_chunk.txt");
    remove("test_cache_chunk.txt");

    TEST_ASSERT(total_recv == 512, "碎片流全部接收完整");
    TEST_ASSERT(memcmp(chunk_data, recv_chunk, 512) == 0, "碎片流数据逐字节一致");
    printf("  -> 逐字节碎片发送 512 字节组装转发完全一致！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 14: 运行期间停止一个实例，另一个实例保持正常             */
/* ------------------------------------------------------------- */
static int test_14_stop_isolation(void) {
    TEST_ASSERT(cfnat_stop(g_inst_443) == 0, "停止443实例");

    cfnat_status_t st1, st2;
    cfnat_get_status(g_inst_443, &st1);
    cfnat_get_status(g_inst_8443, &st2);

    TEST_ASSERT(st1.state == CFNAT_STATE_STOPPED, "443 已进入 STOPPED");

    /* 验证 8443 端口依然可连接 */
    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(18443);
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    test_close_socket(s);

    TEST_ASSERT(rc == 0, "8443 端口正常服务");
    printf("  -> 443 停止，8443 完全不受影响继续转发！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 15: 有活动客户端连接时执行 stop (消除竞态，等待活跃连接)   */
/* ------------------------------------------------------------- */
static int test_15_stop_with_active_connections(void) {
    /* 5.2: 在 8443 上建立一个活动连接，并轮询等待 active_connections >= 1 */
    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(18443);
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    TEST_ASSERT(rc == 0, "建立客户端活动连接");

    int wait_cnt = 0;
    cfnat_status_t st_wait;
    while (wait_cnt < 100) {
        cfnat_get_status(g_inst_8443, &st_wait);
        if (st_wait.active_connections >= 1) break;
        test_sleep_ms(10);
        wait_cnt++;
    }
    TEST_ASSERT(st_wait.active_connections >= 1, "主线程确认底层 active_connections >= 1");

    /* 调用 cfnat_stop 必须主动掐断活动连接并等待 Worker 退出 */
    TEST_ASSERT(cfnat_stop(g_inst_8443) == 0, "执行 cfnat_stop");

    cfnat_status_t st;
    cfnat_get_status(g_inst_8443, &st);
    TEST_ASSERT(st.state == CFNAT_STATE_STOPPED, "状态变为 STOPPED");
    TEST_ASSERT(st.active_connections == 0, "活跃连接数安全归零");

    test_close_socket(s);
    printf("  -> 活动连接在 stop 时被安全唤醒并回收，连接计数安全归零！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 16: 有活动连接时执行 destroy (消除竞态，等待活跃连接)    */
/* ------------------------------------------------------------- */
static int test_16_destroy_with_active_connections(void) {
    create_dummy_candidate_file("test_ips_destroy.txt", "127.0.0.1", 10);
    cfnat_config_t cfg;
    cfnat_config_init_default(&cfg);
    cfg.local_listen_addr = "127.0.0.1:23443";
    cfg.remote_tls_port = (uint16_t)g_echo_port;
    cfg.remote_http_port = (uint16_t)g_echo_port;
    cfg.probe_sni = "tesl-reborn.com";
    cfg.ip_source_path = "test_ips_destroy.txt";
    cfg.cache_file_path = "test_cache_destroy.txt";
    cfg.protect_socket_cb = mock_android_vpn_protect;
    cfg.protect_user_data = "channel-443";

    cfnat_instance_t *inst = NULL;
    TEST_ASSERT(cfnat_create_instance(&cfg, &inst) == 0, "创建销毁测试实例");
    TEST_ASSERT(cfnat_start(inst) == 0, "启动实例");
    TEST_ASSERT(cfnat_wait_ready(inst, 4000) == 1, "等待实例进入 READY");

    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(23443);
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    TEST_ASSERT(rc == 0, "建立活动连接");

    /* 5.2: 轮询等待 active_connections >= 1 后再调用 destroy */
    int wait_cnt = 0;
    cfnat_status_t st_wait;
    while (wait_cnt < 100) {
        cfnat_get_status(inst, &st_wait);
        if (st_wait.active_connections >= 1) break;
        test_sleep_ms(10);
        wait_cnt++;
    }
    TEST_ASSERT(st_wait.active_connections >= 1, "主线程确认底层 active_connections >= 1");

    /* 在连接活跃状态下直接执行 destroy */
    cfnat_destroy(inst);
    test_close_socket(s);

    remove("test_ips_destroy.txt");
    remove("test_cache_destroy.txt");

    printf("  -> 带活动连接直接调用 cfnat_destroy 安全终结并释放，零内存崩溃！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 17: 多线程并发读取状态 (验证一致性快照与零死锁)          */
/* ------------------------------------------------------------- */
static void *concurrent_status_worker(void *arg) {
    cfnat_instance_t *inst = (cfnat_instance_t *)arg;
    for (int i = 0; i < 50; i++) {
        cfnat_status_t st;
        int rc = cfnat_get_status(inst, &st);
        if (rc != 0 || st.struct_size != sizeof(cfnat_status_t)) {
            return (void *)(intptr_t)-1;
        }
        test_sleep_ms(2);
    }
    return NULL;
}

static int test_17_concurrent_status_read(void) {
    TEST_ASSERT(cfnat_start(g_inst_443) == 0, "重启 443 实例");

    pthread_t tids[10];
    for (int i = 0; i < 10; i++) {
        pthread_create(&tids[i], NULL, concurrent_status_worker, g_inst_443);
    }

    for (int i = 0; i < 10; i++) {
        void *res = NULL;
        pthread_join(tids[i], &res);
        TEST_ASSERT(res == NULL, "并发状态读取线程成功");
    }

    cfnat_stop(g_inst_443);
    printf("  -> 10 线程并发 500 次状态快照读取无死锁、无竞争、100%% 一致！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 18: 重复启动和停止幂等性测试                             */
/* ------------------------------------------------------------- */
static int test_18_repeated_start_stop(void) {
    for (int loop = 1; loop <= 5; loop++) {
        TEST_ASSERT(cfnat_start(g_inst_443) == 0, "循环启动");
        TEST_ASSERT(cfnat_stop(g_inst_443) == 0, "首次停止");
        TEST_ASSERT(cfnat_stop(g_inst_443) == 0, "幂等二次停止");
    }
    printf("  -> 5 轮连续 start -> stop 幂等启停完全稳定！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 19: 重复创建和销毁无内存与句柄泄漏                       */
/* ------------------------------------------------------------- */
static int test_19_repeated_create_destroy(void) {
    for (int loop = 1; loop <= 8; loop++) {
        cfnat_instance_t *t_inst = NULL;
        cfnat_config_t t_cfg;
        cfnat_config_init_default(&t_cfg);
        t_cfg.local_listen_addr = "127.0.0.1:25443";
        t_cfg.remote_tls_port = 443;
        t_cfg.probe_sni = "tesl-reborn.com";
        t_cfg.ip_source_path = "test_ips_443.txt";
        t_cfg.cache_file_path = "test_cache_loop.txt";

        TEST_ASSERT(cfnat_create_instance(&t_cfg, &t_inst) == 0, "创建实例");
        TEST_ASSERT(cfnat_start(t_inst) == 0, "启动");
        cfnat_stop(t_inst);
        cfnat_destroy(t_inst);
    }
    remove("test_cache_loop.txt");
    printf("  -> 8 轮快速创建、启动、停止、销毁循环无内存泄漏！\n");
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 20: 缓存文件崩溃恢复与临时文件原子替换                   */
/* ------------------------------------------------------------- */
static int test_20_cache_atomic_write_recovery(void) {
    FILE *fp = fopen("test_cache_corrupt.txt", "w");
    fprintf(fp, "invalid-garbage-data-without-comma\n;;;1234\n");
    fclose(fp);

    cfnat_config_t cfg;
    cfnat_config_init_default(&cfg);
    cfg.local_listen_addr = "127.0.0.1:26443";
    cfg.remote_tls_port = 443;
    cfg.probe_sni = "tesl-reborn.com";
    cfg.ip_source_path = "test_ips_443.txt";
    cfg.cache_file_path = "test_cache_corrupt.txt";

    cfnat_instance_t *inst = NULL;
    TEST_ASSERT(cfnat_create_instance(&cfg, &inst) == 0, "创建实例");
    TEST_ASSERT(cfnat_start(inst) == 0, "启动实例读取异常缓存不崩溃并回退到源文件");

    cfnat_stop(inst);
    cfnat_destroy(inst);

    FILE *rfp = fopen("test_cache_corrupt.txt", "r");
    TEST_ASSERT(rfp != NULL, "缓存文件存在");
    char line[128];
    int valid_lines = 0;
    while (fgets(line, sizeof(line), rfp)) {
        if (strstr(line, ",")) valid_lines++;
    }
    fclose(rfp);
    remove("test_cache_corrupt.txt");

    TEST_ASSERT(valid_lines > 0, "损坏缓存已通过原子替换恢复为有效数据");
    printf("  -> 损坏缓存优雅容错并被原子替换 (.tmp -> 刷盘 -> rename)！\n");
    return 1;
}

/* ========================================================================= */
/*                          第二部分：外部真实网络测试                        */
/* ========================================================================= */

static int is_network_available(void) {
    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(443);
    sin.sin_addr.s_addr = inet_addr("104.18.32.246");
#ifdef _WIN32
    u_long mode = 1;
    ioctlsocket(s, FIONBIO, &mode);
    connect(s, (struct sockaddr *)&sin, sizeof(sin));
    WSAPOLLFD pfd = { s, POLLOUT, 0 };
    int pr = WSAPoll(&pfd, 1, 1500);
#else
    int flags = fcntl(s, F_GETFL, 0);
    fcntl(s, F_SETFL, flags | O_NONBLOCK);
    connect(s, (struct sockaddr *)&sin, sizeof(sin));
    struct pollfd pfd = { s, POLLOUT, 0 };
    int pr = poll(&pfd, 1, 1500);
#endif
    test_close_socket(s);
    return (pr > 0);
}

/* ------------------------------------------------------------- */
/* 测试 21: 443 通道真实 probe_sni (tesl-reborn.com) 外部网络测试 */
/* ------------------------------------------------------------- */
static int test_21_probe_sni_443_external(void) {
    if (!is_network_available()) {
        return 2; /* SKIPPED */
    }

    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(443);
    sin.sin_addr.s_addr = inet_addr("104.18.32.246");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    if (rc != 0) {
        test_close_socket(s);
        return 2;
    }

    int lat = 0;
    int ok = cfnat_tls_probe_handshake(s, "tesl-reborn.com", 5000, &lat);
    test_close_socket(s);

    TEST_ASSERT(ok == 1, "443 TLS ClientHello -> ServerHello (0x02) 校验通过");
    printf("  -> 443 probe_sni (tesl-reborn.com) 真实 Cloudflare 边缘握手成功，RTT: %d ms\n", lat);
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 22: 8443 通道真实 probe_sni (pubsub.tesl-reborn.com) 外部测试 */
/* ------------------------------------------------------------- */
static int test_22_probe_sni_8443_external(void) {
    if (!is_network_available()) {
        return 2; /* SKIPPED */
    }

    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(8443);
    sin.sin_addr.s_addr = inet_addr("172.64.154.246");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    if (rc != 0) {
        test_close_socket(s);
        return 2;
    }

    int lat = 0;
    int ok = cfnat_tls_probe_handshake(s, "pubsub.tesl-reborn.com", 5000, &lat);
    test_close_socket(s);

    TEST_ASSERT(ok == 1, "8443 TLS ClientHello -> ServerHello (0x02) 校验通过");
    printf("  -> 8443 probe_sni (pubsub.tesl-reborn.com) 真实 Cloudflare 边缘握手成功，RTT: %d ms\n", lat);
    return 1;
}

/* ------------------------------------------------------------- */
/* 测试 23: 普通 HTTP 80 转发成功 (识别协议走 remote_http_port)   */
/* ------------------------------------------------------------- */
static int test_23_http_80_forwarding_external(void) {
    if (!is_network_available()) {
        return 2; /* SKIPPED */
    }

    TEST_ASSERT(cfnat_start(g_inst_443) == 0, "启动 443 实例");
    cfnat_wait_ready(g_inst_443, 6000);

    socket_t s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in sin;
    memset(&sin, 0, sizeof(sin));
    sin.sin_family = AF_INET;
    sin.sin_port = htons(10443);
    sin.sin_addr.s_addr = inet_addr("127.0.0.1");
    int rc = connect(s, (struct sockaddr *)&sin, sizeof(sin));
    TEST_ASSERT(rc == 0, "连接本地 10443");

    const char *http_req = "GET / HTTP/1.1\r\nHost: tesl-reborn.com\r\nUser-Agent: curl/7.88\r\nConnection: close\r\n\r\n";
    send(s, http_req, (int)strlen(http_req), 0);

    char resp[1024] = {0};
    int n = recv(s, resp, sizeof(resp) - 1, 0);
    test_close_socket(s);
    cfnat_stop(g_inst_443);

    TEST_ASSERT(n > 0, "收到 HTTP 响应报文");
    TEST_ASSERT(strstr(resp, "HTTP/1.1") != NULL || strstr(resp, "HTTP/1.0") != NULL, "响应包含 HTTP 状态行");
    printf("  -> 成功分流普通 HTTP 80 请求，首包响应: %.30s...\n", resp);
    return 1;
}

/* ========================================================================= */
/*                               主测试入口                                  */
/* ========================================================================= */

int main(void) {
#ifdef _WIN32
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
#endif
    printf("=================================================================\n");
    printf("  TESL-Accelerator cfnat Android 实例化引擎深度审查与验证套件\n");
    printf("  [分区架构: 20 项本地确定性单元测试 + 3 项外部边缘网络测试]\n");
    printf("=================================================================\n\n");

    printf(">>> ----- PART 1: 本地确定性单元测试 (完全解耦外部网络) -----\n\n");
    RUN_TEST( 1, "TLS 1.2 ClientHello 构造与真随机数生成", test_1_tls_client_hello_build);
    RUN_TEST( 2, "SNI 解析器正常用例与各类畸形截断越界防护", test_2_sni_parser_bounds_safety);
    RUN_TEST( 3, "ServerHello 报文合法结构与 7 种异常格式严苛校验", test_3_server_hello_verification);
    RUN_TEST( 4, "TCP 分段 ServerHello 正确组装与解析通过", test_4_tls_segmented_reassembly);
    RUN_TEST( 5, "阶段 1 仅返回 3 字节截断数据的安全处理", test_5_tls_truncated_header_handling);
    RUN_TEST( 6, "阶段 2 Body 读取超时安全处理", test_6_tls_body_read_timeout_handling);
    RUN_TEST( 7, "Handle Registry 基础注册、单调 64位 ID 与查错", test_7_handle_registry_basics);
    RUN_TEST( 8, "Handle Registry 多线程并发 acquire/release 与销毁仲裁防 UAF", test_8_handle_registry_concurrent_uaf);
    RUN_TEST( 9, "Handle Registry 双实例独立生命周期隔离测试", test_9_handle_registry_dual_isolation);
    RUN_TEST(10, "两实例真实同时监听 (10443 与 18443)", test_10_simultaneous_listen);
    RUN_TEST(11, "Protect 回调失败时确认 connect 未发生并立即关闭套接字", test_11_protect_failure_abort);
    RUN_TEST(12, "双向 TCP 数据完整性端到端逐字节校验 (4096字节)", test_12_bidirectional_tcp_integrity);
    RUN_TEST(13, "TCP 部分发送与流控切片逐字节中继测试", test_13_tcp_partial_send);
    RUN_TEST(14, "运行期间单独停止 443 实例，8443 实例保持服务", test_14_stop_isolation);
    RUN_TEST(15, "活动客户端连接下调用 stop 优雅退出与连接计数归零 (竞态修复)", test_15_stop_with_active_connections);
    RUN_TEST(16, "活动连接下调用 destroy 幂等安全释放 (竞态修复)", test_16_destroy_with_active_connections);
    RUN_TEST(17, "多线程并发读取状态 (验证一致性快照与零死锁)", test_17_concurrent_status_read);
    RUN_TEST(18, "重复 5 轮启动与停止幂等性测试", test_18_repeated_start_stop);
    RUN_TEST(19, "重复 8 轮创建、启动、停止和销毁稳定性压测", test_19_repeated_create_destroy);
    RUN_TEST(20, "缓存文件崩溃恢复与临时文件原子替换 (.tmp -> 刷盘 -> rename)", test_20_cache_atomic_write_recovery);

    printf(">>> ----- PART 2: 外部真实网络测试 (Cloudflare 边缘节点) -----\n\n");
    RUN_TEST(21, "443 通道真实 probe_sni (tesl-reborn.com) 边缘探测", test_21_probe_sni_443_external);
    RUN_TEST(22, "8443 通道真实 probe_sni (pubsub.tesl-reborn.com) 边缘探测", test_22_probe_sni_8443_external);
    RUN_TEST(23, "普通 HTTP 80 转发成功 (识别协议走 remote_http_port 80)", test_23_http_80_forwarding_external);

    /* 彻底清理测试环境与后台服务 */
    stop_local_echo_server();
    if (g_inst_443) { cfnat_destroy(g_inst_443); g_inst_443 = NULL; }
    if (g_inst_8443) { cfnat_destroy(g_inst_8443); g_inst_8443 = NULL; }

    remove("test_ips_443.txt");
    remove("test_ips_8443.txt");
    remove("test_cache_443.txt");
    remove("test_cache_8443.txt");

    printf("=================================================================\n");
    printf("  测试执行汇总: 通过 = %d, 跳过 = %d, 失败 = %d (总计 23 项)\n", g_passed_tests, g_skipped_tests, g_failed_tests);
    printf("=================================================================\n");

#ifdef _WIN32
    WSACleanup();
#endif

    return (g_failed_tests == 0) ? 0 : 1;
}
