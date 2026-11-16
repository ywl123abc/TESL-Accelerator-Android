#include "cfnat_tls.h"
#include <string.h>
#include <stdlib.h>
#include <stdio.h>
#include <time.h>

#ifdef _WIN32
  #include <windows.h>
  #include <wincrypt.h>
#else
  #include <sys/select.h>
  #include <poll.h>
  #include <errno.h>
#endif

long cfnat_now_ms(void) {
#ifdef _WIN32
    static LARGE_INTEGER freq;
    static int init = 0;
    if (!init) { QueryPerformanceFrequency(&freq); init = 1; }
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return (long)((c.QuadPart * 1000) / freq.QuadPart);
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (long)(ts.tv_sec * 1000 + ts.tv_nsec / 1000000);
#endif
}

/**
 * @brief 使用系统平台安全随机源生成高强度随机字节 (禁止使用固定伪随机模式)
 * @param buf 输出随机缓冲区
 * @param len 随机字节长度 (通常为 32 字节)
 * @return 1 成功，0 失败
 */
static int cfnat_secure_random(unsigned char *buf, size_t len) {
    if (!buf || len == 0) return 0;
#ifdef _WIN32
    /* 优先使用 Windows CNG BCryptGenRandom */
    HMODULE hBcrypt = LoadLibraryA("bcrypt.dll");
    if (hBcrypt) {
        typedef LONG (NTAPI *PFN_BCryptGenRandom)(PVOID, PUCHAR, ULONG, ULONG);
        PFN_BCryptGenRandom pfn = (PFN_BCryptGenRandom)GetProcAddress(hBcrypt, "BCryptGenRandom");
        if (pfn) {
            LONG status = pfn(NULL, (PUCHAR)buf, (ULONG)len, 0x00000002 /* BCRYPT_USE_SYSTEM_PREFERRED_RNG */);
            FreeLibrary(hBcrypt);
            if (status == 0) return 1;
        } else {
            FreeLibrary(hBcrypt);
        }
    }
    /* 回退使用 CryptoAPI CryptGenRandom */
    HCRYPTPROV hProv = 0;
    if (CryptAcquireContextA(&hProv, NULL, NULL, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT | CRYPT_SILENT)) {
        BOOL ok = CryptGenRandom(hProv, (DWORD)len, (BYTE *)buf);
        CryptReleaseContext(hProv, 0);
        return ok ? 1 : 0;
    }
    return 0;
#else
    FILE *fp = fopen("/dev/urandom", "rb");
    if (!fp) return 0;
    size_t r = fread(buf, 1, len, fp);
    fclose(fp);
    return (r == len) ? 1 : 0;
#endif
}

static size_t append_tls_ext_sni(unsigned char *buf, size_t pos, size_t cap, const char *host) {
    size_t host_len = strlen(host);
    size_t ext_len = 2 + 1 + 2 + host_len;
    if (pos + 4 + ext_len > cap || host_len > 255) return 0;
    buf[pos++] = 0x00; buf[pos++] = 0x00;
    buf[pos++] = (unsigned char)(ext_len >> 8); buf[pos++] = (unsigned char)ext_len;
    size_t list_len = 1 + 2 + host_len;
    buf[pos++] = (unsigned char)(list_len >> 8); buf[pos++] = (unsigned char)list_len;
    buf[pos++] = 0x00;
    buf[pos++] = (unsigned char)(host_len >> 8); buf[pos++] = (unsigned char)host_len;
    memcpy(buf + pos, host, host_len);
    return pos + host_len;
}

size_t cfnat_build_tls_client_hello(unsigned char *buf, size_t cap, const char *sni_host) {
    if (!buf || cap < 256 || !sni_host || !*sni_host) return 0;
    unsigned char body[512];
    size_t p = 0;

    /* 明确限定为 TLS 1.2 ClientHello (0x0303) */
    body[p++] = 0x03; body[p++] = 0x03;

    /* 使用平台安全随机源生成 32 字节 Client Random */
    unsigned char random_bytes[32];
    if (!cfnat_secure_random(random_bytes, sizeof(random_bytes))) {
        return 0; /* 随机源异常直接终止，拒绝发送固定伪随机值 */
    }
    memcpy(body + p, random_bytes, sizeof(random_bytes));
    p += sizeof(random_bytes);

    /* Session ID Length = 0 */
    body[p++] = 0x00;

    /* TLS 1.2 标准 ECDHE 与兼容密码套件 (已彻底移除 TLS 1.3 专用套件) */
    static const unsigned char ciphers[] = {
        0xc0, 0x2f, /* TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 */
        0xc0, 0x2b, /* TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256 */
        0xc0, 0x30, /* TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384 */
        0xc0, 0x2c, /* TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384 */
        0x00, 0x2f  /* TLS_RSA_WITH_AES_128_CBC_SHA */
    };
    body[p++] = (unsigned char)(sizeof(ciphers) >> 8);
    body[p++] = (unsigned char)(sizeof(ciphers) & 0xff);
    memcpy(body + p, ciphers, sizeof(ciphers));
    p += sizeof(ciphers);

    /* Compression Methods (null compression) */
    body[p++] = 0x01;
    body[p++] = 0x00;

    /* Extensions 组装 */
    unsigned char exts[384];
    size_t e = 0;

    /* 1. SNI Extension (0x0000) */
    e = append_tls_ext_sni(exts, e, sizeof(exts), sni_host);
    if (e == 0) return 0;

    /* 2. Supported Groups (0x000a): X25519 (0x001d), secp256r1 (0x0017) */
    static const unsigned char ext_groups[] = {
        0x00, 0x0a, 0x00, 0x06, 0x00, 0x04, 0x00, 0x1d, 0x00, 0x17
    };
    if (e + sizeof(ext_groups) <= sizeof(exts)) {
        memcpy(exts + e, ext_groups, sizeof(ext_groups));
        e += sizeof(ext_groups);
    }

    /* 3. EC Point Formats (0x000b): uncompressed (0x00) */
    static const unsigned char ext_points[] = {
        0x00, 0x0b, 0x00, 0x02, 0x01, 0x00
    };
    if (e + sizeof(ext_points) <= sizeof(exts)) {
        memcpy(exts + e, ext_points, sizeof(ext_points));
        e += sizeof(ext_points);
    }

    /* 4. Signature Algorithms (0x000d): rsa_pss_rsae_sha256(0x0804), ecdsa_secp256r1_sha256(0x0403), rsa_pkcs1_sha256(0x0401) */
    static const unsigned char ext_sig[] = {
        0x00, 0x0d, 0x00, 0x08, 0x00, 0x06, 0x08, 0x04, 0x04, 0x03, 0x04, 0x01
    };
    if (e + sizeof(ext_sig) <= sizeof(exts)) {
        memcpy(exts + e, ext_sig, sizeof(ext_sig));
        e += sizeof(ext_sig);
    }

    /* Extensions 总长度 */
    if (p + 2 + e > sizeof(body)) return 0;
    body[p++] = (unsigned char)(e >> 8);
    body[p++] = (unsigned char)(e & 0xff);
    memcpy(body + p, exts, e);
    p += e;

    /* Handshake Record 头部与载荷组装 */
    size_t hs_len = p;
    size_t rec_len = 4 + hs_len;
    if (5 + rec_len > cap || hs_len > 0xffffff) return 0;
    size_t o = 0;
    buf[o++] = 0x16;              /* ContentType: Handshake (0x16) */
    buf[o++] = 0x03; buf[o++] = 0x01; /* Legacy Record Version: TLS 1.0 */
    buf[o++] = (unsigned char)(rec_len >> 8);
    buf[o++] = (unsigned char)(rec_len & 0xff);

    buf[o++] = 0x01;              /* HandshakeType: ClientHello (0x01) */
    buf[o++] = (unsigned char)(hs_len >> 16);
    buf[o++] = (unsigned char)(hs_len >> 8);
    buf[o++] = (unsigned char)(hs_len & 0xff);
    memcpy(buf + o, body, hs_len);
    return o + hs_len;
}

int cfnat_parse_tls_sni(const unsigned char *buf, size_t len, char *sni_out, size_t sz) {
    if (!buf || len < 44 || !sni_out || sz == 0) return -1;
    sni_out[0] = '\0';

    if (buf[0] != 0x16) return -1;
    if (len < 5) return -1;

    uint16_t rec_len = (uint16_t)(((uint16_t)buf[3] << 8) | buf[4]);
    if (5 + (size_t)rec_len > len) return -1; /* Record 报文截断 */

    size_t p = 5;
    if (p + 4 > len) return -1;
    if (buf[p] != 0x01) return -1; /* 必须为 ClientHello */

    size_t hs_len = ((size_t)buf[p + 1] << 16) | ((size_t)buf[p + 2] << 8) | buf[p + 3];
    p += 4;
    if (p + hs_len > len) return -1; /* Handshake 报文截断 */
    size_t hs_end = p + hs_len;

    /* Version (2) + Random (32) = 34 字节 */
    if (p + 34 > hs_end) return -1;
    p += 34;

    /* Session ID */
    if (p + 1 > hs_end) return -1;
    size_t sid_len = buf[p++];
    if (p + sid_len > hs_end) return -1;
    p += sid_len;

    /* Cipher Suites */
    if (p + 2 > hs_end) return -1;
    size_t cs_len = ((size_t)buf[p] << 8) | buf[p + 1];
    p += 2;
    if (p + cs_len > hs_end) return -1;
    p += cs_len;

    /* Compression Methods */
    if (p + 1 > hs_end) return -1;
    size_t comp_len = buf[p++];
    if (p + comp_len > hs_end) return -1;
    p += comp_len;

    /* Extensions */
    if (p == hs_end) return -1; /* 无扩展字段 */
    if (p + 2 > hs_end) return -1;
    size_t ext_total_len = ((size_t)buf[p] << 8) | buf[p + 1];
    p += 2;
    if (p + ext_total_len > hs_end) return -1;
    size_t ext_end = p + ext_total_len;

    while (p + 4 <= ext_end) {
        uint16_t ext_type = (uint16_t)(((uint16_t)buf[p] << 8) | buf[p + 1]);
        size_t ext_len = ((size_t)buf[p + 2] << 8) | buf[p + 3];
        p += 4;
        if (p + ext_len > ext_end) return -1; /* 扩展长度超出扩展区边界 */

        if (ext_type == 0x0000) { /* server_name */
            if (ext_len >= 5) {
                size_t list_len = ((size_t)buf[p] << 8) | buf[p + 1];
                if (list_len + 2 > ext_len) return -1;
                size_t sp = p + 2;
                if (sp + 3 <= p + ext_len) {
                    uint8_t name_type = buf[sp];
                    if (name_type == 0) { /* host_name */
                        size_t hlen = ((size_t)buf[sp + 1] << 8) | buf[sp + 2];
                        sp += 3;
                        if (sp + hlen <= p + ext_len) {
                            size_t copy_len = hlen < (sz - 1) ? hlen : (sz - 1);
                            memcpy(sni_out, buf + sp, copy_len);
                            sni_out[copy_len] = '\0';
                            return 0;
                        }
                    }
                }
            }
        }
        p += ext_len;
    }
    return -1;
}

int cfnat_tls_verify_server_hello_packet(const unsigned char *pkt, size_t len) {
    if (!pkt || len < 5) return 0;
    if (pkt[0] != 0x16) return 0; /* ContentType 必须为 Handshake (0x16) */
    if (pkt[1] != 0x03 || pkt[2] > 0x03) return 0; /* Version: TLS 1.0 ~ 1.2 */
    uint16_t rec_len = (uint16_t)(((uint16_t)pkt[3] << 8) | pkt[4]);
    if (rec_len < 4 || rec_len > 4096) return 0;
    if (len < 5 + (size_t)rec_len) return 0; /* 记录体未收齐 */

    const unsigned char *body = pkt + 5;
    if (body[0] != 0x02) return 0; /* Handshake Type 必须为 ServerHello (0x02) */
    uint32_t hs_len = ((uint32_t)body[1] << 16) | ((uint32_t)body[2] << 8) | (uint32_t)body[3];
    if (4 + hs_len > rec_len) return 0; /* 握手长度超出记录长度 */
    if (hs_len < 38) return 0;
    if (body[4] != 0x03) return 0; /* Server Version 必须为 0x03xx */
    size_t sid_len = body[4 + 34];
    if (sid_len > 32) return 0;
    if (4 + 35 + sid_len + 3 > 4 + hs_len) return 0; /* 字段不完整 */
    return 1;
}

int cfnat_tls_probe_handshake(socket_t fd, const char *sni_host, int timeout_ms, int *out_latency_ms) {
    if (timeout_ms < 1500) timeout_ms = 1500;
    unsigned char hello[512];
    size_t hello_len = cfnat_build_tls_client_hello(hello, sizeof(hello), sni_host && *sni_host ? sni_host : "tesl-reborn.com");
    if (hello_len == 0) return 0;

    long start_t = cfnat_now_ms();
    long deadline = start_t + timeout_ms;

    /* 1. 完整发送 ClientHello 报文 (处理 partial send) */
    size_t sent = 0;
    while (sent < hello_len && cfnat_now_ms() < deadline) {
        int left = (int)(deadline - cfnat_now_ms());
        if (left <= 0) break;
#ifdef _WIN32
        WSAPOLLFD pfd = { fd, POLLOUT, 0 };
        int pr = WSAPoll(&pfd, 1, left);
        if (pr <= 0) break;
        int n = send(fd, (const char *)(hello + sent), (int)(hello_len - sent), 0);
        if (n <= 0) {
            int err = WSAGetLastError();
            if (n < 0 && (err == WSAEWOULDBLOCK || err == WSAEINTR)) continue;
            break;
        }
#else
        struct pollfd pfd = { fd, POLLOUT, 0 };
        int pr = poll(&pfd, 1, left);
        if (pr <= 0) break;
        ssize_t n = send(fd, hello + sent, hello_len - sent, MSG_NOSIGNAL);
        if (n <= 0) {
            if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) continue;
            break;
        }
#endif
        sent += (size_t)n;
    }
    if (sent < hello_len) return 0;

    /* 2. 读取完整的 TLS Record Header (5 字节) */
    unsigned char hdr[5];
    size_t hdr_read = 0;
    while (hdr_read < 5 && cfnat_now_ms() < deadline) {
        int left = (int)(deadline - cfnat_now_ms());
        if (left <= 0) break;
#ifdef _WIN32
        WSAPOLLFD pfd = { fd, POLLIN, 0 };
        int pr = WSAPoll(&pfd, 1, left);
        if (pr <= 0) break;
        int n = recv(fd, (char *)hdr + hdr_read, (int)(5 - hdr_read), 0);
        if (n <= 0) {
            int err = WSAGetLastError();
            if (n < 0 && (err == WSAEWOULDBLOCK || err == WSAEINTR)) continue;
            break;
        }
#else
        struct pollfd pfd = { fd, POLLIN, 0 };
        int pr = poll(&pfd, 1, left);
        if (pr <= 0) break;
        ssize_t n = recv(fd, hdr + hdr_read, 5 - hdr_read, 0);
        if (n <= 0) {
            if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) continue;
            break;
        }
#endif
        hdr_read += (size_t)n;
    }
    if (hdr_read < 5) return 0;

    /* 3. 严格校验 TLS Record Header 结构 */
    if (hdr[0] != 0x16) return 0; /* ContentType 必须为 Handshake (0x16) */
    if (hdr[1] != 0x03 || hdr[2] > 0x03) return 0; /* Version: TLS 1.0 ~ 1.2 */
    uint16_t rec_len = (uint16_t)(((uint16_t)hdr[3] << 8) | hdr[4]);
    if (rec_len < 4 || rec_len > 4096) return 0; /* ServerHello Record 合法长度范围 */

    /* 4. 分段接收完整的 Record Body 载荷 (处理 TCP 流分段到达) */
    unsigned char body[4096];
    size_t body_read = 0;
    while (body_read < rec_len && cfnat_now_ms() < deadline) {
        int left = (int)(deadline - cfnat_now_ms());
        if (left <= 0) break;
#ifdef _WIN32
        WSAPOLLFD pfd = { fd, POLLIN, 0 };
        int pr = WSAPoll(&pfd, 1, left);
        if (pr <= 0) break;
        int n = recv(fd, (char *)body + body_read, (int)(rec_len - body_read), 0);
        if (n <= 0) {
            int err = WSAGetLastError();
            if (n < 0 && (err == WSAEWOULDBLOCK || err == WSAEINTR)) continue;
            break;
        }
#else
        struct pollfd pfd = { fd, POLLIN, 0 };
        int pr = poll(&pfd, 1, left);
        if (pr <= 0) break;
        ssize_t n = recv(fd, body + body_read, rec_len - body_read, 0);
        if (n <= 0) {
            if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR)) continue;
            break;
        }
#endif
        body_read += (size_t)n;
    }
    if (body_read < rec_len) return 0;

    /* 5. 校验完整的 ServerHello 结构合法性 */
    unsigned char full_packet[5 + 4096];
    memcpy(full_packet, hdr, 5);
    memcpy(full_packet + 5, body, body_read);
    if (!cfnat_tls_verify_server_hello_packet(full_packet, 5 + body_read)) {
        return 0;
    }

    long end_t = cfnat_now_ms();
    int latency = (int)(end_t - start_t);
    if (latency <= 0) latency = 1;
    if (out_latency_ms) *out_latency_ms = latency;
    return 1;
}
