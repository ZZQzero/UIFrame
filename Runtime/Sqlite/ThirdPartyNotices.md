# 第三方组件

## SQLite 3.53.4

来源：https://www.sqlite.org/2026/sqlite-amalgamation-3530400.zip  
声明：https://www.sqlite.org/copyright.html

SQLite为公共领域软件。官方amalgamation中的原始声明完整保留；本模块不修改引擎算法，通过编译配置和强制包含符号映射头隔离链接符号。版本、源码ID、下载包SHA3-256和逐文件SHA-256见 `Native~/third_party/sqlite/source-lock.json`。更新引擎时须同时更新锁文件并重跑验收，不在客户端运行时下载代码。

SQLite原始声明：

> The author disclaims copyright to this source code. In place of a legal notice, here is a blessing:
> May you do good and not evil.
> May you find forgiveness for yourself and forgive others.
> May you share freely, never taking more than you give.

## Android C++运行库

Android目标将NDK自带的libc++ / libc++abi / LLVM运行库静态链接进模块，导出符号由链接器隐藏。组件为Apache License 2.0 with LLVM Exceptions；发行时保留NDK的原始声明，见 `Native~/third_party/android-NOTICE` 与 `Native~/third_party/llvm-NOTICE`。NDK版本及路径由构建清单记录。

## Windows交叉构建运行库

当前Windows x64 DLL使用LLVM-MinGW 20260922 UCRT工具链，静态链接其C++ / unwind / MinGW运行库。LLVM原始声明见 `Native~/third_party/llvm-mingw-NOTICE`；MinGW-w64运行库完整声明见 `Native~/third_party/mingw-w64-runtime-NOTICE`。Windows本机构建另支持MSVC静态运行库。

本次工具链来自 https://github.com/mstorsjo/llvm-mingw/releases/tag/20260922 ，下载文件为 `llvm-mingw-20260922-ucrt-macos-universal.tar.xz`，SHA-256为 `52e5f5a7b131021d0c39a37a38fa380a1da7885cd04bd61afd0cd4ecfb8bc1f3`，与发布资产摘要一致。工具链位于构建环境，不作为Unity插件分发。每个DLL的实际导出和系统库依赖记录在其构建清单。
