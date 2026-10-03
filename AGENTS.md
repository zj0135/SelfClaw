> 本文件只收录"无法从代码或 `docs/` 推断、且违反会导致错误行为"的规则。架构叙事、文件/类清单、DI 注册、变更日志、阶段性状态一律不写：细节写进 `docs/`，这里只留一行指针。

# SelfClaw — 项目上下文

Windows 桌面 AI 编程助手：WPF + .NET 10 外壳，WebView2 内托管 Vue 转录。两种执行模式由当前 desktop agent 决定：**Direct**（进程内经 Microsoft.Extensions.AI 调用已配置 provider）与 **CLI**（Claude Code / Codex / OpenCode 子进程）；两者输出同一事件流。

## 构建与运行

```powershell
dotnet restore SelfClaw.slnx --force-evaluate
dotnet build SelfClaw.slnx
dotnet run --project SelfClaw.Desktop/SelfClaw.Desktop.csproj
```

- TranscriptVue：开发 `cd SelfClaw.TranscriptVue && npm install && npm run dev`，测试 `npm test` / `npm run test:e2e`。
- WPF/WebView2 与进程恢复测试需 `SELFCLAW_DESKTOP_SMOKE=1`；合成 live-provider 冒烟需 `SELFCLAW_PROVIDER_SMOKE=1` 且启用一个具备推理能力的模型。

## 不变量与陷阱

1. **模型与凭据**：Direct 用 composer 选中的 enabled 模型，缺省 `desktop-default`；CLI 自行持有认证与模型配置，无可用 CLI 时回合带指引失败；provider 凭据只在 Infrastructure 内解密。
2. **子代理投递**：continuation 的任何工具执行前，必须先提交 durable execution checkpoint。
3. **桌面并发**：run handle 独占回合生命周期；SessionCoordinator 独占选择与快照替换；`ConversationRuntimeState` 只串行化内容变更，不持运行标志。
4. **关闭顺序**：停受理 → 取消并等待活动 → 释放 UI → dispose 服务 → 停 WPF；`OnExit` 只做同步兜底。
5. **定义目录**：Agent 定义在 `{AppData}\agents\`，子代理定义在 `{AppData}\subagents\`；会话绑定的定义缺失时保持不可用并拒绝执行。
6. **插件视图**：一视图一 origin `https://<plugin-id>.plugin.selfclaw.local`，id 非合法 DNS label 在安装期拒绝；`WebViewMessageRouter` 的应用-origin 守卫是唯一信任边界；浮层限定在 `.main-content` 内、除声明的交互矩形外点击穿透、收起必须 `visibility: hidden`（禁用 `display: none`）；权限为 `ui.panel` / `ui.floating` / `network.fetch:<origin>`。
7. **工具审批**：Direct 工具审批（默认 `RequireApproval`）只有 Vue 审批栏或 Windows toast 能裁决；没有超时自动拒绝；订阅者失败、调用方取消或关闭即拒绝。
8. **WPF**：`WindowStyle` 必须保持 `SingleBorderWindow`；插件视图 UI 在 Vue，不在 WPF。
9. **会话删除**：先墓碑 → 停回合 → 取消并限时等待子任务 → 才做 SQLite 级联；超时中止删除；列表与导航只接受 `ConversationKind.Interactive`。
10. **数据库**：schema 版本 **30**；非 v30 或未版本化数据库只读拒绝，绝不迁移、回填或隐式删除。
11. **子代理活动面板**：与主转录、输入区互相独立；子内容不得进入父转录项或影响 composer 忙碌态；摘要页 ≤50 条、详情窗口 ≤64 块、序列化 ≤256 KiB；文本更新不得触发 per-token SQL。

## 代码约定

1. DTO 是纯数据载体（`record` + 主构造器），无方法、逻辑或副作用；一个文件只放一个 DTO 或一个服务类，绝不混放。
2. 领域契约放 `SelfClaw.Core/Interfaces/<Feature>/`；仅 Infrastructure 内部使用的抽象放 `SelfClaw.Infrastructure/<Feature>/Abstractions/`；接口保持 1–5 个方法。
3. 非为继承设计的类一律 `sealed`；DI 注册的内部类型用 `internal sealed`；依赖以 private `_camelCase` 字段构造注入，字段列在构造函数之前；类内顺序：字段 → 构造 → 主公开方法 → 私有辅助。
4. 公开方法超 40–50 行即拆分；无状态辅助用 `private static`。
5. 异步方法一律 `Async` 后缀；公开名不用缩写（`Conversation` 而非 `Conv`）。
6. Infrastructure 全量 `ConfigureAwait(false)`，Desktop ViewModel 不加；`OperationCanceledException` 必须重抛、绝不转成失败结果；流式用 `IAsyncEnumerable<T>` + `Channel<T>`。
7. 启用 NRT；公开方法用 `ArgumentNullException.ThrowIfNull`；除编译器不可追踪的可证明空检查外禁用 `!`。
8. 优先朴素控制流（`if` / `return` / `switch`）；不为单一调用方造泛型包装或基类；只被调用一次的 3 行辅助函数应内联。
9. 注释只写 why；不加显而易见的注释；私有成员与平凡属性不加 XML 文档注释。
10. 改动时顺带检查相邻死代码可否删除；新实现完整替代旧实现时删除旧实现，不留 "retained"。
11. 见到违反以上约定的代码（DTO 混逻辑、超大方法、缺 `ConfigureAwait(false)`、死代码）主动指出并重构。

## 前端约定（SelfClaw.TranscriptVue）

1. 一组件一职责；超过 ~300 行即考虑拆到 `components/<Feature>/`。`views/` 只做编排；领域逻辑进 `composables/`（`use<Feature>()`），`<script setup>` 内除 trivial 本地状态外不写逻辑；纯渲染逻辑进 `renderers/`。
2. 组件样式一律 `<style scoped>`；全局布局/重置/主题变量只放 `App.vue` 的未作用域 `<style>`；设置页共用 `styles/settings-console.css`（`sc-page` / `sc-page-head` / `sc-page-body`，页面根 `sc-root` / `sc-stage`），在 scoped 块内 `@import` 引入。
3. 图标统一 `lucide-vue-next`；界面禁止 emoji 和手写内联 SVG 图标表。
4. 设计标准：对标 Awwwards 每日最佳水准（先锋视觉、实验性排版、流畅物理动效、沉浸式体验）。
5. SFC 顺序 `<script setup>` → `<template>` → `<style scoped>`；路由级/设置面板用 `defineAsyncComponent` 懒加载。
6. 组件变大、逻辑混入组件、重复模式出现时，见到就拆或抽（composable / renderer util）。

## 已移除 / 停用（不要复活或顺手启用）

- **Plan mode**：已删除；只有 `AgentExecutionMode.Direct` / `Cli`。
- **Channel conversations**：数据模型保留；UI 与调度只接受 `ConversationKind.Interactive`。
- **Legacy provider profiles**（`ProviderProfile`、`IProfileRepository`、`profiles` 表、`ChatTurnRequest.Profile/ApiKey`）：已删除；Direct 只用 `ModelProfileId`。
- **Direct Queue 默认关闭**（`SELFCLAW_QUEUE_ENABLED`）；**Steer 已停用**（枚举存在，受理/执行不可用）。
- **附件**：只保留持久化/转录渲染/虚拟主机（`message_attachments`、`https://attachments.selfclaw.local`）；输入入口一律拒绝（`ConversationInputReason.AttachmentsUnsupported`），不要再引用已不存在的 6 张 / 10 MB / 30 MB 限制。
- **设置页**：AI 提供商 / 模型管理 / 编程助手 / 代理助手 / 插件 / 宠物已接宿主；其余页面（自动化、图像生成、翻译等）是前端 mock。

## 深入阅读

- 端到端主干：`docs/pipeline-architecture.md`（全局图）→ `docs/runtime-execution-flow.md`（当前调用链、回合/准入/消费基线）。
- Direct 证据链：`docs/direct-agent-architecture-review.md`、`docs/direct-message-input-p{1,2}-implementation-review.md`、`docs/direct-message-input-p3-review-fixes.md`。
- 桌面/前端：`docs/desktop-architecture-review.md`（§5 剩余验证限制、§7 窗口动画、§8 关闭延迟）、`docs/plugin-view-system-design.md`、`docs/message-segments-system-design.md`。
- 子系统：`docs/direct-subagent-system-design.md`、`docs/direct-extensions-system-design.md`、`docs/direct-hooks-system-design.md`、`docs/ai-provider-system-design.md`、`docs/pet-system-design.md`、`docs/usage-tracking-design.md`。
- 规则：spec/issue 写 `.scratch/<feature-slug>/`（git 忽略）；耐久结论写 `docs/`；被取代的设计要么更新要么删除，不留"历史保留"。
