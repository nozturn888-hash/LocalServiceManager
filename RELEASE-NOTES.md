# v0.1.0

[简体中文说明](README.md) · [English README](README.en.md)

## 中文

本机服务管家的首个公开版本：Windows x64 便携桌面工具，提供系统托盘服务面板，使用 MIT 许可证。

### 功能

- 空白初始服务列表；工具登录自启与每项服务自启独立，默认都关闭。
- 通用脚本服务：新增、编辑、删除、拖入预填与拖动排序；启动/停止两脚本共用一个条目，可选重启脚本。
- 进程身份、端口和可选 HTTP 检查；区分外部启动，操作串行，重启等待停止。
- ComfyUI 检查模板：手工填写自己的脚本与进程身份，有任务或无法核实队列时保护停止和重启。
- 客户端托管 stdio MCP 登记，不独立启停或记录协议流。
- 后台脚本启动，stdout/stderr 捕获与文件 tail，搜索、复制、暂停滚动、有界缓存和轮转。
- 内置与自定义图标，ICO/EXE/DLL 多资源提取，不执行被提取的程序。
- 网页/应用打开入口、配置导入导出，以及明确的保留服务退出和停止受管理服务退出语义。
- 简体中文和英语即时切换并持久化，覆盖程序自带文案；修正长文本、复选框和底部按钮布局。用户内容保持原样。

### 本机版与公开版

**本机定制安装和原有控制能力继续保留；公开 v0.1.0 是独立的通用便携发行，不是原五项本机服务的完整迁移。**

- 公开核心不包含安装专用的 Gateway 外部接管、Local Codex Bridge profile / 凭据链，以及工作台特有队列的原子关闭接口。这些功能绑定外部运行时、控制器记录或 API，不能仅靠替换路径迁移。
- 另附 [Hermes Gateway 自有控制脚本示例](examples/hermes-gateway/README.md)：保留精确身份和官方进程记录停止守卫，`InstallRoot` / `Action` 必填，无实际路径默认值，不自动使用；不改变 GUI 通用服务对外部进程只读的边界。
- ComfyUI Desktop 个人控制脚本因来源权利尚未核实而不分发；可编辑模板和队列守卫仍保留，需配置自己的脚本。
- 公开版默认空服务列表，并拒绝导入或执行旧私人适配器配置。

### 包与构建

发布资产为 `LocalServiceManager-v0.1.0-win-x64.zip` 和 `SHA256SUMS.txt`，校验步骤见 [中文 README](README.md#运行发布包)。EXE 位于解压目录根部，运行数据保存在旁边的 `config/`、`logs/`、`icons/` 和 `scripts/`。

源码 `build.ps1` 使用系统 .NET Framework C# 编译器，生成 `dist/LocalServiceManager-v0.1.0-win-x64/`。`-Tests` 仅编译隔离测试；随后显式运行 `tests/Tests.exe`。

### 验证与边界

发布验证使用隔离 fixture 和本地界面检查。测试范围包括服务生命周期及其保护、配置交换、编码与路径、日志、GUI 和托盘交互。真实业务服务启停、真实 ComfyUI 队列操作以及 Windows 登录自启持久设置的实际写入未纳入验收。

业务服务、运行环境和模型需另装。开发机器的私人配置及专用适配器不随公开版分发，跨机器路径与脚本需要重新核对。stdio 登记并不证明客户端已经成功调用 MCP。系统原生文字遵循系统或组件语言。

第一版不提供故障自动重启，也不保证固定 CPU 或内存占用。此文件描述 v0.1.0 的内容，不作为远端上传已完成的证明。

## English

The first public version of Local Service Manager is a portable Windows x64 desktop app with a system tray service panel, licensed under MIT.

### Features

- Empty initial service list. App startup at sign-in and per-service startup are independent and disabled by default.
- Generic scripted services: add, edit, delete, drop to prefill, and reorder by dragging. Start and stop scripts share one entry; a restart script is optional.
- Process identity, port, and optional HTTP checks; external-instance detection, serialized operations, and restart after completed stop.
- ComfyUI inspection template: supply your own scripts and process identity. Stop and restart are protected when tasks exist or the queue cannot be verified.
- Registration for client-managed stdio MCP, without independent lifecycle control or protocol-stream logging.
- Background script launch, stdout/stderr capture and file tailing, search, copy, paused scrolling, bounded cache, and rotation.
- Built-in and custom icons; multiple ICO/EXE/DLL resources can be extracted without executing the source program.
- Web/application opening targets, configuration import/export, and explicit exit choices that either keep services running or stop managed services first.
- Immediate, persistent Simplified Chinese/English switching for app-owned text, with long-text, checkbox, and bottom-button layout fixes. User content stays unchanged.

### Local edition versus public edition

**The local customized installation keeps its existing control capabilities. Public v0.1.0 is a separate, generic portable release, not a complete migration of the five local services.**

- The public core excludes installation-specific Gateway external takeover, the Local Codex Bridge profile / credential chain, and the workbench-specific queue atomic shutdown integration. These depend on external runtimes, controller records, or APIs; changing paths alone is insufficient.
- A [Hermes Gateway control script example](examples/hermes-gateway/README.md) is included separately. It retains exact identity and official process-record stop guards. `InstallRoot` / `Action` are required, there is no actual path default, and it is not used automatically. Generic services in the public GUI still treat external instances as read-only.
- Personal ComfyUI Desktop control scripts are not distributed because their source rights remain unverified. The editable template and queue guards remain, using your own scripts.
- The public edition starts with an empty service list and rejects import or execution of old private-adapter configurations.

### Package and build

Assets are `LocalServiceManager-v0.1.0-win-x64.zip` and `SHA256SUMS.txt`. See the [English README](README.en.md#run-the-portable-package) for checksum verification. The EXE is at the extracted directory root, alongside `config/`, `logs/`, `icons/`, and `scripts/`.

Source `build.ps1` uses the system .NET Framework C# compiler and generates `dist/LocalServiceManager-v0.1.0-win-x64/`. The `-Tests` option compiles isolated tests only; run `tests/Tests.exe` explicitly afterward.

### Verification and limits

Release verification uses isolated fixtures and local UI checks. Its scope includes service lifecycle protection, configuration exchange, encoding and paths, logs, GUI, and tray interaction. Acceptance has not operated real business services or real ComfyUI queues, or written actual Windows sign-in startup settings.

Install services, runtimes, and models separately. Private development-machine settings and dedicated adapters are not distributed. Review paths and scripts on each machine. A stdio registration does not prove that the client can successfully call MCP. Native system text follows the system or component language.

This version has no automatic recovery restart and makes no fixed CPU or memory guarantee. These notes describe v0.1.0; they do not confirm that a remote upload has completed.
