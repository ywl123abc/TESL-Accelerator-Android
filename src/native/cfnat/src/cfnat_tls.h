#ifndef CFNAT_TLS_H
#define CFNAT_TLS_H

#include <stddef.h>
#include <stdint.h>
#include "../include/cfnat_api.h"

#ifdef _WIN32
  #include <winsock2.h>
  #include <ws2tcpip.h>
  typedef SOCKET socket_t;
#else
  #include <sys/types.h>
  #include <sys/socket.h>
  #include <netinet/in.h>
  #include <arpa/inet.h>
  #include <unistd.h>
  typedef int socket_t;
#endif

/**
 * @brief 构造包含指定 SNI 的标准 TLS 1.2 ClientHello 报文 (使用平台安全随机源)
 * @param buf 输出报文缓冲区
 * @param cap 缓冲区总容量
 * @param sni_host 目标主机名 (SNI)
 * @return 报文实际长度，0 表示生成失败或随机源异常
 */
size_t cfnat_build_tls_client_hello(unsigned char *buf, size_t cap, const char *sni_host);

/**
 * @brief 从收到的 TLS ClientHello 数据中解析出客户端实际携带的 SNI (带严格边界校验)
 * @param buf ClientHello 数据缓冲区
 * @param len 数据长度
 * @param sni_out 输出 SNI 字符串缓冲区
 * @param sz 输出缓冲区容量
 * @return 0 成功解析出 SNI，非 0 未找到或报文非法截断
 */
int cfnat_parse_tls_sni(const unsigned char *buf, size_t len, char *sni_out, size_t sz);

/**
 * @brief 校验完整的 TLS ServerHello 数据包结构合法性 (头部、长度、握手类型 0x02 及字段边界)
 * @param pkt 数据包指针 (包含 5 字节 Record 头部及 Body)
 * @param len 数据包长度
 * @return 1 合法有效，0 非法或截断
 */
int cfnat_tls_verify_server_hello_packet(const unsigned char *pkt, size_t len);

/**
 * @brief 向已连接的套接字执行真实 TLS 1.2 ServerHello 响应探测
 *
 * 发送 ClientHello 报文，在超时时间内分段接收完整的 ServerHello 握手记录，
 * 严格校验 TLS Record Header、Handshake Type (0x02) 及必需字段结构。
 *
 * @param fd 已建立 TCP 连接的套接字
 * @param sni_host 目标 SNI 主机名
 * @param timeout_ms 超时毫秒
 * @param out_latency_ms 输出握手往返延迟毫秒数 (可选，可为 NULL)
 * @return 1 表示收到结构完整合法的 ServerHello 响应；0 表示超时、被拒或报文非法
 */
int cfnat_tls_probe_handshake(socket_t fd, const char *sni_host, int timeout_ms, int *out_latency_ms);

/** 获取当前系统单调时钟毫秒数 */
long cfnat_now_ms(void);

#endif /* CFNAT_TLS_H */
