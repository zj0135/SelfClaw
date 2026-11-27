# SelfClaw

**基于 .NET 10、WPF 与 Vue 3 的 Windows 桌面 AI 编程助手。**

SelfClaw 将模型对话、代码读写、终端、Git 工作树和后台任务放在同一个桌面工作区中。你可以通过 **Direct** 模式直接连接模型服务，让 SelfClaw 执行工具与管理上下文；也可以通过 **CLI** 模式调用本机的 Claude Code、Codex 或 OpenCode，在统一的对话界面中查看执行过程。

## 功能概览

| 功能 | 当前能力 |
| --- | --- |
| 模型与服务商 | 接入 OpenAI、Anthropic 及自定义兼容端点；管理模型、推理等级与多模态能力；支持 OpenAI Responses、Chat Completions 和 Anthropic Messages 协议 |
| Direct 编程代理 | 浏览与搜索项目、读写文件、执行命令、维护任务清单；流式展示回复、思考过程、工具调用与 Token 用量 |
| 连续交互 | 消息排队、执行中的补充指令（Steer）、结构化提问与工具审批；支持回合开始前及执行过程中的上下文压缩 |
| 代理与扩展 | 自定义代理和子代理，按代理绑定 Skills、插件与 MCP 服务；Direct 子代理可在后台执行，独立面板展示进度与结果 |
| 项目工作区 | 按项目组织会话、自动生成标题、归档历史；支持 Git 工作树、嵌入式终端和工作区 Git diff 查看 |
| 多模态输入 | 输入框提供文件、指令与技能快捷选择；Direct 多模态模型支持图片附件及通过文件工具读取图片 |
| 自动化 | 个人待办、手动 AI 任务、单次及每日／每周／每月／Cron 定时任务；提供日历视图、运行记录与历史清理 |
| 图像生成 | 独立生图工作台，通过 OpenAI Images 协议生成或编辑图片；按模型能力提供尺寸、质量、背景等参数与取消操作 |
| 桌面体验 | 桌面宠物、状态气泡、系统托盘与 Windows 通知；应用用量仪表盘展示使用趋势；支持本地、系统与自定义网络代理设置 |

工具审批与模型提问是两种交互：审批决定工具能否执行，提问用于补充任务信息。即使选择完全访问，Direct 交互回合中的模型提问仍会等待你的回答。

## Direct 与 CLI

执行模式由当前选择的代理决定，可在「设置 → 代理助手」中配置。

| 对比项 | Direct | CLI |
| --- | --- | --- |
| 执行位置 | SelfClaw 进程内的 Agent Loop | 本机 Claude Code、Codex 或 OpenCode 子进程 |
| 模型配置 | 使用 SelfClaw 中启用并选中的模型 | 使用对应 CLI 的模型与配置 |
| 身份认证 | 在 SelfClaw 中配置服务商凭据 | 由 CLI 自行登录或配置凭据 |
| 工具与扩展 | SelfClaw 的工作区工具、审批、Skills、插件、MCP 与子代理 | 由对应 CLI 提供与管理 |
| 图片附件 | 支持，需要启用多模态模型 | 暂不支持 |

两种模式共用对话展示界面，但模型配置与认证相互独立。在 SelfClaw 中填写 API Key 后，使用 CLI 仍需完成该 CLI 自身的配置。

## 从源码运行

### 环境要求

| 依赖 | 用途 |
| --- | --- |
| Windows 10 / 11 x64 | 运行 WPF 桌面程序；项目 Windows 目标版本为 `10.0.19041.0` |
| .NET 10 SDK | 还原、构建与运行 .NET 项目 |
| Node.js 22.x（至少 22.12）或 24.x，附带 npm | 构建 Vue 前端与运行前端测试 |
| Microsoft Edge WebView2 Runtime | 在桌面窗口中承载前端界面 |
| Git | 使用仓库、分支与工作树功能 |

使用 CLI 模式时，还需自行安装并配置至少一个受支持的 CLI。

### 构建并启动

在仓库根目录打开 PowerShell，先构建前端，再运行桌面项目：

```powershell
cd SelfClaw.TranscriptVue
npm ci
npm run build
cd ..

dotnet restore SelfClaw.slnx --force-evaluate
dotnet build SelfClaw.slnx
dotnet run --project SelfClaw.Desktop/SelfClaw.Desktop.csproj
```

前端产物输出到 `SelfClaw.Desktop/Assets/TranscriptVue/`。该目录不随 Git 提交，桌面构建也不会自动执行 npm，因此首次运行及修改前端后都需要执行 `npm run build`。

### 首次配置

**使用 Direct：**

1. 打开「设置 → AI 服务商」，添加服务商，配置 API Key 与所需的接口地址。
2. 在「设置 → 模型管理」中添加或选择模型并启用；需要图片理解时，配置模型的多模态能力。
3. 在「设置 → 代理助手」中选择或创建 Direct 代理，按需绑定技能、插件、MCP 服务及允许使用的子代理。
4. 返回对话，选择工作目录、代理和模型，输入任务开始执行。

**使用 CLI：**

1. 在本机终端中确认 Claude Code、Codex 或 OpenCode 已安装且可以正常使用。
2. 打开「设置 → 编程助手」，扫描本地 CLI，选择并测试所需工具。
3. 在「设置 → 代理助手」中创建或选择 CLI 模式的代理，再从对话中选择该代理执行任务。

图像生成使用单独的生图模型配置，需要服务商支持 OpenAI Images 协议；聊天模型的多模态能力并不代表它能够生成图片。

## 扩展与插件

SelfClaw 支持 Skills、本地插件和 MCP 服务，并按代理绑定所需能力。输入框可通过 `@` 选择文件、`/` 选择指令、`$` 选择技能；技能也可以显式写为 `[/技能ID]`。

仓库提供以下插件包，可按需打包导入：

| 插件 | 用途 |
| --- | --- |
| [csharp-dev](plugins/csharp-dev/README.md) | C# 开发、重构、Web API、EF Core 查询与测试编写 |
| [csharp-diag](plugins/csharp-diag/README.md) | .NET 构建诊断、性能分析、转储分析、基准测试与覆盖率 |
| [plugin-dev](plugins/plugin-dev/README.md) | 创建、检查并打包 SelfClaw 插件 |

例如，在仓库根目录打包插件开发工具包：

```powershell
Compress-Archive -Path plugins/plugin-dev/* -DestinationPath plugin-dev.zip -Force
```

在「设置 → 扩展 → 插件」中导入并启用，然后到「设置 → 代理助手」将其绑定给目标 Direct 代理，即可在对话中使用 `[/plugin-dev/create-plugin]`。

## 开发与测试

### 前端开发

```powershell
cd SelfClaw.TranscriptVue
npm ci
npm run dev
```

Vite 开发服务器适合浏览器中的界面开发。文件操作、模型调用和终端等功能依赖 WebView2 宿主桥，完整联调需要构建前端后运行桌面项目。

### 测试

在仓库根目录执行：

```powershell
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj
npm --prefix SelfClaw.TranscriptVue test
npm --prefix SelfClaw.TranscriptVue run test:e2e
```

前端单元测试使用 Vitest，端到端测试使用 Playwright。端到端配置默认启动开发服务器，并使用本机安装的 Microsoft Edge。

部分集成冒烟测试按环境启用：WPF、WebView2 与进程恢复测试需设置 `SELFCLAW_DESKTOP_SMOKE=1`；真实模型服务测试需设置 `SELFCLAW_PROVIDER_SMOKE=1`，并配置、启用具备推理能力的模型。

### 发布

先完成前端构建，再在仓库根目录执行：

```powershell
dotnet publish SelfClaw.Desktop/SelfClaw.Desktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

**分发时必须包含整个 `publish` 文件夹，包括 `Assets/` 与 `runtimes/`。** 内置 ripgrep 和宠物 WebP 解码所需的原生文件需要随程序一起交付，仅复制 exe 会导致相关功能不可用。自包含发布包含 .NET 运行时，目标机器仍需安装 WebView2 Runtime。

## 项目结构

```text
SelfClaw.Core/           领域模型、契约与运行时规则
SelfClaw.Infrastructure/ Direct / CLI 执行、模型接入、工具、扩展与持久化
SelfClaw.Desktop/        WPF 外壳、会话协调、宿主桥与桌面服务
SelfClaw.TranscriptVue/  Vue 界面、对话渲染、设置页与前端测试
SelfClaw.Tests/          .NET 单元测试与集成测试
plugins/                可导入的插件包源码
docs/                   架构与功能设计文档
```

桌面端通过 WebView2 与 Vue 界面交换消息；Direct 和 CLI 将执行过程转换为统一的事件流，用于展示回复、工具活动、状态与用量。对话与任务等数据使用本地 SQLite 存储。

## 本地数据与使用边界

- 默认数据目录为 `%LOCALAPPDATA%\SelfClaw\`，包含 `selfclaw.db`、代理定义、凭据文件及工作树等数据；日志位于程序目录下的 `logs/`。
- 服务商凭据使用 Windows DPAPI 按当前用户加密。模型请求会发送到所配置的服务商，CLI 的认证由对应工具管理。
- 当前数据库不执行自动版本迁移；遇到不兼容版本会拒绝打开，不会自动删除或改写旧数据。
- 图片附件仅支持 Direct 交互式对话与多模态模型，每条最多 6 张、每张不超过 10 MiB；CLI 和子代理暂不支持附件输入。
- 自动化 AI 任务使用 Direct，定时调度依赖 SelfClaw 进程运行；运行历史由用户手动清理。
- 应用代理设置不覆盖所有进程：WebView2、终端命令、Git 命令与技能脚本有各自的网络行为，详见[网络代理设计](docs/network-proxy-design.md)。
- 翻译等部分页面目前仍为前端演示，尚未接入完整宿主功能。

## 深入阅读

| 主题 | 文档 |
| --- | --- |
| 整体架构与执行流程 | [管线架构](docs/pipeline-architecture.md)、[运行时调用链](docs/runtime-execution-flow.md) |
| 输入与交互 | [消息队列与 Steer](docs/direct-message-input-design.md)、[提问工具](docs/direct-ask-user-design.md)、[图片附件](docs/input-attachment-design.md) |
| 代理与扩展 | [子代理系统](docs/direct-subagent-system-design.md)、[Skills / 插件 / MCP](docs/direct-extensions-system-design.md) |
| 上下文管理 | [上下文压缩](docs/direct-context-compaction-design.md)、[回合内压缩](docs/direct-in-turn-compaction-design.md) |
| 工作区与界面 | [右侧功能面板](docs/side-dock-design.md)、[Git diff 审查面板](docs/git-review-pane-design.md)、[对话渲染](docs/message-segments-system-design.md) |
| 自动化与图像 | [自动化系统](docs/automation-system-design.md)、[图像生成](docs/image-generation-design.md) |
| 桌面与统计 | [宠物系统](docs/pet-system-design.md)、[用量仪表盘](docs/app-usage-dashboard-design.md) |
| 开发约定 | [AGENTS.md](AGENTS.md) |

## 许可证

本项目采用 [MIT License](LICENSE)。
