#!/usr/bin/env bash
# Android ARM64 原生动态库 (libcfnat.so) NDK 构建脚本
set -e

if [ -z "$ANDROID_NDK_HOME" ]; then
    echo "【错误】请设置 ANDROID_NDK_HOME 环境变量！"
    exit 1
fi

TOOLCHAIN="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt"
# 自动探测宿主开发环境 (Linux / Windows / macOS)
if [ -d "$TOOLCHAIN/linux-x86_64" ]; then
    HOST_TAG="linux-x86_64"
elif [ -d "$TOOLCHAIN/windows-x86_64" ]; then
    HOST_TAG="windows-x86_64"
elif [ -d "$TOOLCHAIN/darwin-x86_64" ]; then
    HOST_TAG="darwin-x86_64"
else
    echo "未找到匹配的 NDK LLVM 工具链！"
    exit 1
fi

# 采用 API 24+ (Android 7.0+ 覆盖主流移动终端)
CC="$TOOLCHAIN/$HOST_TAG/bin/aarch64-linux-android24-clang"
OUT_DIR="dist/arm64-v8a"
mkdir -p "$OUT_DIR"

echo ">>> 正在使用 Android NDK aarch64-linux-android24-clang 构建 libcfnat.so..."
$CC -shared -fPIC -O2 -pipe -std=c11 \
    -D__ANDROID__ \
    -Iinclude -Isrc \
    src/cfnat_tls.c \
    src/cfnat_api.c \
    src/cfnat_jni.c \
    -o "$OUT_DIR/libcfnat.so" \
    -llog

echo ">>> 构建完成: $OUT_DIR/libcfnat.so"
