/**
 * @file cfnat_api.h
 * @brief TESL-Accelerator cfnat 原生 C 接口定义
 *
 * 核心架构规范：
 * - 单一动态库 (libcfnat.so / cfnat.dll)
 * - 纯 C 实例隔离 (CfnatInstance)，零全局可变状态
 * - 支持 443 与 8443 双通道在同一进程内独立并发运行
 * - 严格解耦健康检查探针 SNI 与实际客户端 TLS SNI (游戏客户端 SNI 原样透传)
 * - 原生 Android VpnService.protect(fd) 套接字防回环保护回调
 */

#ifndef CFNAT_API_H
#define CFNAT_API_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(__CYGWIN__)
  #ifdef CFNAT_BUILD_DLL
    #define CFNAT_API __declspec(dllexport)
  #elif defined(CFNAT_USE_DLL)
    #define CFNAT_API __declspec(dllimport)
  #else
    #define CFNAT_API
  #endif
#else
  #if __GNUC__ >= 4
    #define CFNAT_API __attribute__((visibility("default")))
  #else
    #define CFNAT_API
  #endif
#endif

/** 当前 ABI 规范版本号: 1.0.0 */
#define CFNAT_ABI_VERSION 0x00010000

#if defined(_WIN32)
/** 跨平台 Socket 句柄抽象类型 (Windows SOCKET 为 64位 UINT_PTR/intptr_t) */
typedef intptr_t CfnatSocketHandle;
#else
/** POSIX / Android Socket 文件描述符标准类型 (Linux int32_t fd) */
typedef int32_t CfnatSocketHandle;
#endif

/** 不合法的 Socket 句柄常量 */
#define CFNAT_INVALID_SOCKET ((CfnatSocketHandle)(-1))

/**
 * @brief 实例生命周期状态枚举
 *
 * 状态机流转：
 * STOPPED -> STARTING -> LISTENING -> READY -> STOPPING -> STOPPED
 * 任何阶段发生致命错误均转为 ERROR
 */
typedef enum {
    CFNAT_STATE_STOPPED   = 0,  /**< 已停止，无任何线程或端口占用 */
    CFNAT_STATE_STARTING  = 1,  /**< 启动中，正在读取候选池与初始化网络 */
    CFNAT_STATE_LISTENING = 2,  /**< 本地端口已成功建立监听 (listen 成功) */
    CFNAT_STATE_READY     = 3,  /**< 至少一个有效候选节点已确认，具备实际中继转发能力 */
    CFNAT_STATE_STOPPING  = 4,  /**< 正在停止，正在回收线程与关闭套接字 */
    CFNAT_STATE_ERROR     = 5   /**< 发生致命异常 */
} cfnat_state_t;

/** 日志级别 */
typedef enum {
    CFNAT_LOG_SILENT = 0,
    CFNAT_LOG_ERROR  = 1,
    CFNAT_LOG_WARN   = 2,
    CFNAT_LOG_INFO   = 3,
    CFNAT_LOG_DEBUG  = 4
} cfnat_log_level_t;

/**
 * @brief 平台套接字保护回调函数类型
 *
 * 用于 Android 平台在外部出站 Socket 执行 connect() 或 sendto() 之前，
 * 调用系统的 VpnService.protect(fd)，防止出站连接回流至 VPN TUN 接口引发死循环。
 *
 * @param socket_handle 待保护的底层 OS Socket 文件描述符或句柄
 * @param user_data 调用方传入的自定义上下文指针 (如当前 JNI 保护上下文句柄)
 * @return 1 表示保护成功，允许继续连接；0 表示保护失败，底层必须立即关闭该套接字并终止连接
 */
typedef int (*cfnat_protect_socket_fn)(CfnatSocketHandle socket_handle, void *user_data);

/**
 * @brief 日志输出回调函数类型
 * @param level 日志等级
 * @param msg 日志内容字符串
 * @param user_data 自定义用户指针
 */
typedef void (*cfnat_log_fn)(cfnat_log_level_t level, const char *msg, void *user_data);

/**
 * @brief 实例配置结构体 (持有实例初始化全部参数)
 */
typedef struct {
    uint32_t abi_version;              /**< 必须设置为 CFNAT_ABI_VERSION */
    uint32_t struct_size;              /**< 必须设置为 sizeof(cfnat_config_t) */

    /* 本地与远端网络端口配置 */
    const char *local_listen_addr;     /**< 本地监听地址与端口，例如 "127.0.0.1:10443" 或 "127.0.0.1:18443" */
    uint16_t remote_tls_port;          /**< 远端 TLS 目标端口 (如 443 或 8443) */
    uint16_t remote_http_port;         /**< 远端 HTTP 目标端口 (默认 80) */

    /* TLS 与探测参数 (明确区分探针与客户端转发) */
    const char *probe_sni;             /**< 仅用于后台 TLS 1.2 ServerHello 响应探测的 SNI (如 443 为 "tesl-reborn.com", 8443 为 "pubsub.tesl-reborn.com") */
    int connect_timeout_ms;            /**< TCP 连接握手超时时间 (毫秒)，默认 1500 */
    int tls_probe_timeout_ms;          /**< TLS 1.2 ServerHello 探测返回超时时间 (毫秒)，默认 2500 */
    int health_check_interval_ms;      /**< 后台优选节点健康检查周期 (毫秒)，默认 30000 */

    /* 候选池与测速控制 (继承 cfnat 完整能力) */
    int ip_version;                    /**< 4 为 IPv4, 6 为 IPv6 */
    int random_mode;                   /**< 1 为随机采样 CIDR, 0 为顺序读取 */
    int ipnum;                         /**< 保留的最优候选 IP 数量 (默认 20) */
    int num;                           /**< 单节点探测轮次 (默认 1) */
    int task;                          /**< 并发测速工作线程数量 (默认 4) */
    int code;                          /**< 期望 HTTP 状态码 (0 表示不限定) */
    const char *colo;                  /**< 目标机房区域过滤 (例如 "SIN,HKG" 或 NULL) */

    /* 文件与路径配置 (深拷贝存储) */
    const char *ip_source_path;        /**< 候选 IP 数据来源文件路径 (如 "ips-v4.txt") */
    const char *cache_file_path;       /**< 专属优选缓存保存与读取路径 (如 "direct-cache-443.txt") */
    const char *locations_path;        /**< 机房坐标数据文件路径 (可选，可为 NULL) */

    /* 回调与生命周期上下文 */
    cfnat_log_level_t log_level;       /**< 日志过滤级别 */
    cfnat_protect_socket_fn protect_socket_cb; /**< Android Socket 保护回调函数指针 */
    void *protect_user_data;           /**< 传给 protect_socket_cb 的上下文指针 (所有权归调用方) */
    cfnat_log_fn log_cb;               /**< 实例独立日志接收回调 (可选) */
    void *log_user_data;               /**< 传给 log_cb 的上下文指针 */
} cfnat_config_t;

/**
 * @brief 实例运行状态快照结构体
 */
typedef struct {
    uint32_t struct_size;              /**< 必须设置为 sizeof(cfnat_status_t) */
    cfnat_state_t state;               /**< 当前实例生命周期状态 (STOPPED/STARTING/LISTENING/READY/STOPPING/ERROR) */
    uint32_t active_connections;       /**< 当前活跃的转发连接总数 */
    char current_selected_ip[64];      /**< 当前通道选定的最优 IP */
    int current_latency_ms;            /**< 当前最优 IP 实测延迟 (毫秒) */
    uint32_t total_candidates;         /**< 候选节点总数 */
    int last_error_code;               /**< 最近一次错误码 (0 为正常) */
    char last_error_msg[256];          /**< 最近一次错误详细描述 */
} cfnat_status_t;

/** 不透明实例句柄类型 */
typedef struct cfnat_instance_internal cfnat_instance_t;

/* ========================================================================= */
/*                          核心对外 API 声明                                 */
/* ========================================================================= */

/**
 * @brief 初始化默认配置结构
 * @param cfg 待初始化的配置结构体指针
 * @return 0 成功，非 0 失败
 */
CFNAT_API int cfnat_config_init_default(cfnat_config_t *cfg);

/**
 * @brief 创建一个新的独立 cfnat 实例
 *
 * 函数内部将对所有配置字符串执行深拷贝，验证端口、路径与超时参数。
 * protect_user_data 作为外部上下文指针直接保留，不执行字符串拷贝。
 *
 * @param cfg 初始配置参数
 * @param out_instance 输出创建的实例指针
 * @return 0 成功，非 0 失败 (如参数非法、内存分配不足)
 */
CFNAT_API int cfnat_create_instance(const cfnat_config_t *cfg, cfnat_instance_t **out_instance);

/**
 * @brief 启动实例运行
 *
 * 绑定 local_listen_addr，启动监听线程、健康巡检线程及优选探测引擎。
 * 状态流转: STOPPED -> STARTING -> LISTENING -> READY
 *
 * @param inst 目标实例指针
 * @return 0 成功启动，非 0 失败
 */
CFNAT_API int cfnat_start(cfnat_instance_t *inst);

/**
 * @brief 等待实例完全达到 READY 状态
 *
 * 阻塞等待实例确认至少一个可用候选 IP 且本地监听建立，具备完整转发能力。
 *
 * @param inst 目标实例指针
 * @param timeout_ms 超时毫秒数 (<=0 表示无限等待)
 * @return 1 达到 READY，0 超时，-1 发生致命错误或实例停止
 */
CFNAT_API int cfnat_wait_ready(cfnat_instance_t *inst, int timeout_ms);

/**
 * @brief 查询实例当前状态 (线程安全快照)
 * @param inst 目标实例指针
 * @param out_status 输出状态结构体指针
 * @return 0 成功，非 0 失败
 */
CFNAT_API int cfnat_get_status(cfnat_instance_t *inst, cfnat_status_t *out_status);

/**
 * @brief 获取最近一次错误描述信息
 * @param inst 目标实例指针
 * @param buf 存储错误信息的缓冲区
 * @param buf_sz 缓冲区长度
 * @return 0 成功，非 0 失败
 */
CFNAT_API int cfnat_get_last_error(cfnat_instance_t *inst, char *buf, size_t buf_sz);

/**
 * @brief 触发一次重新优选测速 (非阻塞后台刷新)
 * @param inst 目标实例指针
 * @return 0 成功触发，非 0 失败
 */
CFNAT_API int cfnat_rescan(cfnat_instance_t *inst);

/**
 * @brief 停止实例运行 (具备并发同步与幂等性)
 *
 * 关闭本地监听套接字，主动掐断并等待所有活跃连接 Worker 退出，
 * 阻塞等待核心后台线程安全退出，原子保存优选缓存到磁盘。
 * 支持并发调用：当某线程已在执行停止时，其他并发调用者将同步等待直至彻底达到 STOPPED。
 * 状态流转: RUNNING/READY -> STOPPING -> STOPPED
 *
 * @param inst 目标实例指针
 * @return 0 成功停止，非 0 失败
 */
CFNAT_API int cfnat_stop(cfnat_instance_t *inst);

/**
 * @brief 销毁实例并释放所有内部内存资源
 *
 * 若实例仍在运行，将内部调用 cfnat_stop 确保所有工作线程与连接 Worker 已终止退出。
 * 【纯 C API 所有权边界约束】：
 * 调用方必须拥有该 inst 裸指针的独占销毁所有权。纯 C 层不对多个线程同时使用同一裸指针
 * 执行并发 destroy 提供隐式仲裁；跨线程并发调用与句柄销毁保护由上层机制 (如 JNI 全局 Registry) 保证。
 *
 * @param inst 待销毁的实例指针
 */
CFNAT_API void cfnat_destroy(cfnat_instance_t *inst);

/**
 * @brief 查询引擎版本信息
 * @return 版本字符串 (如 "cfnat-core-v2.0.0-multi-inst")
 */
CFNAT_API const char *cfnat_version(void);

#ifdef __cplusplus
}
#endif

#endif /* CFNAT_API_H */
