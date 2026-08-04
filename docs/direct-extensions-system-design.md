# Direct 模式 Plugins / MCP / Skills 全栈接入设计

> 状态：设计稿 v1.1（2026-07-26；v1.1 按当前代码逐链路核对后补充事实修正与缺口：现状行为 §1.2–§1.4、ToolPolicy §5.4、权限确认持久化 §6.2、跨回合与 token 规则 §8.4、stdio 环境事实修正 §9.1、结果映射 §9.5、审批载体 §9.4、投影链 §10.2、桥接推送与超时 §11.2、DI 接线 §15）
>
> 基线：以 `docs/runtime-execution-flow.md` 和当前仓库代码为准。本设计只扩展 **Direct** 执行模式；CLI 继续由各 CLI 自身管理插件、MCP、Skill 与权限。
>
> 基线勘误：`runtime-execution-flow.md`（2026-07-25）相对当前代码已有两处漂移——workspace tools 实际为 7 个（新增 `glob_files`、`edit_file`），且 `ChatTurnRequest` 已拆分为抽象基类 + `DirectChatTurnRequest` / `CliChatTurnRequest` 子类型。本文以代码为准；P4 更新该文档时一并勘误。
>
> 术语说明：本文的三类能力是 **Plugins、MCP Servers、Skills**。现有 `WorkspaceAgentToolset` 仍是 Direct 的内建 workspace tools，不属于本次新增的三类扩展。

---

## 0. 核心结论

本次接入不应把“读插件包、加载 Skill、启动 MCP、拼 system prompt、工具重命名、审批和释放资源”继续塞进 `DirectAgentChatRuntime`。应新增一个深模块：

```csharp
internal interface IDirectTurnCapabilityResolver
{
    Task<DirectTurnCapabilityLease> ResolveAsync(
        DirectChatTurnRequest request,
        CancellationToken cancellationToken = default);
}
```

它是 Direct runtime 与扩展系统之间唯一的 seam。调用方只需要知道：给出本轮请求，得到一份不可变、可释放的能力快照。

```text
DirectTurnCapabilityLease
  |- SystemInstructions       本轮追加的插件/Skill 指令
  |- Tools                    workspace + Skill loader + MCP AITool
  |- ToolDescriptors          provider tool name -> 来源/显示名/风险
  |- MessageAdjustments       剥离 skill token 后的消息文本改写（见 §8.4）
  |- Diagnostics              本轮能力降级信息
  `- DisposeAsync()           释放本轮持有的 MCP client lease
```

扩展设置和 Agent 绑定采用两层开关：

```text
最终生效 = 已安装且全局启用 ∩ 当前 Agent 已绑定 ∩ 本轮环境满足要求
```

- **全局注册表**由设置页维护：安装、启停、配置、测试、删除。
- **Agent 绑定**由 Agent markdown 维护：`plugins`、`skills`、`mcpServers`。
- 空绑定表示不启用任何外部能力，不能解释为“全部启用”，避免升级后静默扩大权限。
- Plugin 是声明式能力包，可贡献 instructions、Skills 和 MCP 定义；v1 不允许把第三方 DLL 动态加载到 WPF 进程。
- Skills 采用“显式激活 + 按需加载”混合模式，避免把所有 `SKILL.md` 全量塞进每轮上下文。
- MCP 基于仓库已引用的 `ModelContextProtocol.Core 1.4.0`，其 `McpClientTool` 本身就是 `AIFunction`，可直接进入现有 M.E.AI function invocation pipeline。

---

## 1. 当前实现基线

### 1.1 已有可复用基础

| 部件 | 当前状态 | 本设计中的用途 |
|---|---|---|
| `DispatchingAgentChatRuntime` | Direct / CLI 分发已稳定 | 不改分发契约 |
| `DirectAgentChatRuntime` | 已能装配 `AITool`、调用 `IChatClient`、翻译工具事件 | 接收能力快照，不直接理解扩展配置 |
| `AiChatClientFactory` | 已通过 `.UseFunctionInvocation()` 自动执行函数 | MCP/Skill loader 工具直接复用 |
| `WorkspaceAgentToolset` | 当前实际提供 7 个 workspace tools | 作为内建工具来源并入统一能力快照 |
| `DesktopToolApprovalHandler` | 已有 WPF/Vue/toast 审批闭环 | 扩展到 MCP 工具审批 |
| `ISecretProtector` / DPAPI | 已有 SecretRef 存储 | 保存 MCP env/header 密钥 |
| `ModelContextProtocol.Core 1.4.0` | 已引用但仓库中没有消费者 | 提供 stdio、HTTP transport、client、tool discovery |
| `DesktopAgentStore` | 已解析 `skills`、`disabledSkills`、`mcpServers`、`disabledMcpServers` | 迁移为纯 Agent 绑定来源 |
| `hostBridge.js` | 已有 `request/requestLatest/on/post` 与 requestId 关联 | 扩展设置页统一走该通道 |
| `SettingsView.vue` | 已对设置页做路由级异步加载 | 保持懒加载 |

### 1.2 当前断点

1. `MainWindowViewModel.ResolveRuntimeAgent()` 会传递 `EnabledSkills`，但把 `McpServers` 和 `ConfiguredMcpServers` 强制设为空。
2. `DirectAgentChatRuntime` 完全不读取 `Agent.ToolPolicy`、`Agent.Skills`、`Agent.McpServers`。
3. `AgentMcpServerDefinition` 把 command/args/env 放进 Core runtime DTO；若继续沿用，会让明文配置甚至密钥跨过 Infrastructure seam。
4. `%LocalAppData%/SelfClaw/skills/**/SKILL.md`（`StoragePaths.CreateDefault()` 的根是 `LocalApplicationData`）目前只被 `DesktopAgentStore.DiscoverInstalledSkillIds()` 扫描来生成 Agent warning，没有真正加载到模型上下文。
5. `SelfClaw.Desktop/Assets/skills` 有构建复制规则，但当前没有可运行的 Skill 装配链。
6. `Plugins.vue` 共 1593 行，三类数据、弹窗、抽屉和所有交互均为组件内 mock；启停、导入、连接都不会进入宿主。
7. Composer 只会把已经存在于消息文本中的 `[/skill]` 渲染成 chip，没有 Skill 列表、插入入口或后端激活语义。
8. MCP tool 的来源、原始名称和 server id 无法写入现有 `ToolExecutionRecord`，工具卡片无法说明调用来自哪个扩展。
9. `ToolApprovalRequest` 只有 `ToolExecutionId/ToolName/DisplayName/Description/ArgumentsJson/ConversationId` 六个字段；Vue 确认栏消费的 `toolApprovalRequest` payload 与之一一对应，Windows toast 与宠物审批队列同源。三个审批展示面都无法区分 workspace 工具与外部扩展工具。
10. `TranscriptRenderState` 只有 `Items/AutoScroll/Conversations/SelectedConversationId/IsBusy/ActivityText/AgentMode`，前端没有任何 agent 身份信息（无 agent 选择器，agent 跟随会话）；SkillPicker 需要的 `selectedAgentId` 与能力 revision 目前不存在。

### 1.3 邻接清理

实施本设计时应一并修正以下现有约束违例，而不是在其上继续叠逻辑：

- `DesktopAgentDefinition` 当前包含 `HasWarnings`、`EnabledSkills`、`EnabledMcpServers`、`ToRuntimeDefinition()` 和过滤逻辑。它应恢复为纯 DTO，过滤和映射移入 Agent 定义模块。
- `AgentMcpServerDefinition.EffectiveDisplayName` 在 DTO 内包含派生逻辑，且该类型在新设计中不再需要，应删除。
- `Plugins.vue` 必须拆分，不能在 1593 行单文件中继续接入业务逻辑。
- 接入 `SkillPicker` 时会触碰 `ComposerPanel.vue`；其当前非 scoped 样式也应按前端约束收口为 scoped 样式或已有全局主题样式。
- `TranscriptToolRunPresenter` 的 summary/detail switch 仍使用旧工具名（`read_workspace_file`、`write_workspace_file`、`list_workspace_files`、`search_workspace_text`），与当前实际注册的 7 个工具名（`read_file` 等）不匹配，现在所有内建工具都在走 `HumanizeToolName` 兜底。接入来源展示（§10.2）时一并修正，不在漂移名称上叠加 MCP 分支。
- `DirectAgentChatRuntime.MapToolKind()` 按工具名硬编码猜测 `ToolCallKind`；MCP namespace 名（`mcp__*`）会全部落到 `Other`。kind 判定应随 §9.3 的 `DirectToolDescriptor` 走，不再按名称猜。
- 现有 Agent markdown 序列化总是写出空的 `skills:/disabledSkills:/mcpServers:/disabledMcpServers:` 列表头，且 `WriteAgentFile()` 是直接 `File.WriteAllText`，不是原子写；§5.2 的迁移必须同时收口这两点。

### 1.4 与本设计相关的现状行为（实施依赖）

以下是核对代码确认的行为，后续章节的设计直接建立在其上：

1. **消息组装**：`DirectAgentChatRuntime.BuildMessages()` 跳过 `Failed` **和 `Cancelled`** 消息与空文本，只把 `User/Assistant` 的 `MarkdownContent` 映射为文本 `ChatMessage`。上一轮的 function call/result **不会**重放进下一轮请求——跨回合语义见 §8.4。
2. **审批发生在 function 内部**：`UseFunctionInvocation()` 执行工具时才走 `IsApprovedAsync()`，此时 `ToolCallStartedEvent` 已发出、工具记录状态保持 `Running`（`ToolExecutionStatus.AwaitingApproval` 目前无人写入）。拒绝不是异常：function 正常返回 `WorkspaceAgentToolset.DeniedResult`（"User denied this tool call."），模型拿到该文本继续。MCP 审批 wrapper 沿用同一形状。
3. **工具结果摘要**：`DescribeToolResult()` 对 workspace 类型化结果逐一 Summarize/Describe；未知类型落到 `JsonElement`/`ToString()` 兜底（"Tool call completed."）。MCP 工具结果是序列化的 `CallToolResult`，直接走兜底会让摘要与卡片不可读，见 §9.5。
4. **活动文案**：`AgentActivityCoordinator` 把 `AgentRunStatus`（Initializing/Requesting/Thinking/Running）映射为四条固定中文文案；`RunStatusEvent` 目前只有 `Status` 一个字段。§10.3 的 `Detail` 要在该映射处消费。
5. **hostBridge 语义**：`request()` 默认 30 秒超时、错误经 `payload.error` reject、回包 `type` 允许与请求 `type` 不同（按 requestId 关联）；`on(type, handler, { replayLast })` 支持 sticky 重放。前端 skill chip 正则为 `\[\/([^\]\r\n]{1,80})\]`（允许 `/`，因此与 plugin 命名空间 id 兼容）。
6. **skill id 归一化**：`DesktopAgentStore.NormalizeSkillId()` 已支持多段路径（`a/b` 形式，剔除 `.`/`..`），嵌套目录里的 `SKILL.md` 会得到多段 id；plugin 贡献 Skill 的 `<plugin-id>/<skill-id>` 规范 id 与现有归一化天然一致。
7. **启动/退出**：`App.OnStartup()` 依次 `IConversationRepository.InitializeAsync()` → `IAiProviderRepository.InitializeAsync()` → `ProgrammingAssistantSettingsService.GetOrInitializeAsync()` → 显示 MainWindow → `PetHost.InitializeAsync()`；`OnExit()` 统一 `_host.StopAsync()` + `DisposeAsync()`。扩展仓储初始化、reconcile 与 `McpClientManager` 关闭钩子的落点见 §15「DI 与启动接线」。

---

## 2. 目标与非目标

### 2.1 目标

1. 在 Direct 回合中真实启用 Agent 绑定的 Plugins、MCP Servers、Skills。
2. 设置页完整支持安装/新增、启停、配置、测试、查看状态、Agent 绑定和删除。
3. 保持 provider-neutral：扩展最终统一变成 system instructions 和 `AITool`，provider adapter 不感知来源。
4. 保持回合快照一致：设置在回合中变更只影响下一轮，不让正在执行的工具集合中途变化。
5. MCP 密钥不以明文进入 SQLite、Agent markdown、`ChatTurnRequest`、Vue state 或日志。
6. 统一工具命名、来源、审批、事件、落库和前端展示。
7. 对包导入、路径、子进程环境、HTTP endpoint、取消和释放建立明确安全规则。

### 2.2 非目标

- 不给 CLI 注入 SelfClaw 的 Plugins/MCP/Skills。
- v1 不加载第三方 .NET assembly，不提供 WPF 进程内插件代码执行。
- v1 的 MCP 只向模型暴露 **tools**；MCP prompts/resources/sampling/elicitation 不进入 Direct 主链。
- v1 的 HTTP MCP 只支持静态 header 认证；SDK `HttpClientTransportOptions.OAuth` 存在但不启用，不做 OAuth 授权流。MCP progress notifications（`WithProgress`）也不消费，工具卡片只有开始/完成两态。
- 不做在线 Marketplace、自动下载、自动更新和发布者签名基础设施；v1 只做本地导入。
- 不做 Windows AppContainer/低权限账户级进程隔离；stdio MCP 仍是当前用户权限，UI 必须明确披露。
- 不把扩展配置复制进 conversation 表；历史回合继续保存实际工具调用记录，而非可变配置副本。

---

## 3. 三类能力的语义

### 3.1 Skill

Skill 是受信任的指令与参考资料包，入口为 `SKILL.md`。它本身不执行代码。

- 独立 Skill 安装在 `%LocalAppData%/SelfClaw/skills/<skill-id>/`。
- Plugin 内 Skill 使用规范 id：`<plugin-id>/<skill-id>`。
- Agent 的 `skills` 是允许使用的 Skill 集合。
- 用户在 composer 中选择 Skill 时插入 `[/<skill-id>]`，仅显式激活本轮。
- 未显式激活的已绑定 Skill 只以紧凑目录暴露，模型可调用 `activate_skill` 按需加载。
- Skill 引用的附加文件通过 `read_skill_resource` 分页读取，路径被限制在该 Skill 根目录内。

### 3.2 MCP Server

MCP Server 是外部工具提供者。

- 独立 MCP 由设置页创建并保存配置。
- Plugin 可贡献 MCP 配置模板；模板缺少必填值时保持“待配置”，不会启动。
- v1 支持 `stdio` 与 `http`。HTTP transport 使用 SDK 的 Streamable HTTP/SSE 自动协商能力。
- 只在设置页测试或某个 Direct 回合真正需要时连接，不在应用启动时批量拉起所有 server。
- MCP 返回的 annotations 只作为 UI 提示，不能作为绕过审批的可信依据。

### 3.3 Plugin

Plugin 是声明式分发和激活单元，不等同于进程内代码插件。

```text
Plugin
  |- Direct instructions（可选）
  |- Skills（0..N）
  |- MCP server templates（0..N）
  `- assets / reference files（可选）
```

- Plugin 被 Agent 绑定后，其 instructions 和已就绪贡献项才进入有效能力集。
- Plugin 子项在 Skills/MCP tab 中可查看来源，但安装、升级、卸载生命周期由所属 Plugin 管理。
- Plugin 可以声明一个 stdio MCP 进程，从而提供可执行能力；该进程仍在 SelfClaw 进程外运行。
- v1 禁止 Plugin 声明任意 DLL 入口、反射类型名或 WPF UI 注入点。

---

## 4. 总体架构

```text
Vue Extension Settings / SkillPicker
  -> hostBridge.request("extensions/*")
  -> MainWindow.OnTranscriptWebMessageReceived()
  -> ExtensionSettingsBridge
  -> IExtensionSettingsService
       |- IExtensionPackageRepository / IMcpServerRepository (SQLite)
       |- ExtensionPackageInstaller (filesystem staging + validation)
       |- ISecretProtector (DPAPI)
       |- McpClientManager (test / health)
       `- DesktopAgentDefinitionService (Agent 绑定)

Direct turn
  -> MainWindowViewModel.ResolveRuntimeAgent()
       `- 只携带 PluginIds / SkillIds / McpServerIds
  -> DispatchingAgentChatRuntime
  -> DirectAgentChatRuntime
  -> IDirectTurnCapabilityResolver.ResolveAsync(request)
       |- 计算有效能力集
       |- 解析显式 [/skill] token
       |- 拼装 plugin/skill system instructions
       |- 创建 workspace + skill loader tools
       |- McpClientManager.AcquireAsync()
       |- McpClient.ListToolsAsync()
       |- 重命名、包审批、生成来源描述
       `- 返回 DirectTurnCapabilityLease
  -> IAiChatClientFactory.CreateAsync(..., capabilityLease.Tools)
  -> IChatClient.GetStreamingResponseAsync()
  -> AgentStreamEvent
  -> SQLite tool_runs + Vue transcript
  -> dispose provider client
  -> dispose capability lease
```

### 4.1 seam 的职责

`IDirectTurnCapabilityResolver` 隐藏以下复杂度：

- 全局启用状态与 Agent 绑定求交集；
- Plugin contribution 展开；
- Skill token 解析、内容限制和 prompt 拼接；
- workspace 是否存在、Plugin/MCP 是否要求 workspace；
- MCP config revision、secret 解密、transport 创建和 client 复用；
- provider tool name 归一化与碰撞处理；
- MCP tool 审批包装；
- diagnostics 与所有 MCP 资源释放。

删除该模块后，上述逻辑会重新散落到 runtime、ViewModel、设置模块和测试中，因此该 seam 有足够深度。`DirectAgentChatRuntime` 只保留流式协议翻译和终态纪律。

### 4.2 能力快照

`DirectTurnCapabilityLease` 是资源所有者，不是 DTO。它创建后不可变：

```csharp
internal sealed class DirectTurnCapabilityLease : IAsyncDisposable
{
    public string SystemInstructions { get; }
    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyDictionary<string, DirectToolDescriptor> ToolDescriptors { get; }
    public IReadOnlyDictionary<Guid, string> MessageAdjustments { get; }
    public IReadOnlyList<CapabilityDiagnostic> Diagnostics { get; }

    public ValueTask DisposeAsync();
}
```

`MessageAdjustments` 是 message id -> 改写后文本的映射，承载 §8.2/§8.4 的 skill token 剥离结果（最新 user message 与历史中匹配有效 Skill 的 token）。没有这个输出，token 剥离就只能散落回 runtime 或 prompt composer 里重复解析——lease 是 resolver 对外的唯一产物，改写必须随之携带。`DirectPromptComposer` 组装消息时以该映射覆盖对应 `MessageRecord.MarkdownContent`；数据库与 Vue 中的原始消息不变。

配置启停、更新或删除时：

- 新回合读取新 revision；
- 已经取得 lease 的回合继续使用旧快照；
- 删除动作先标记为 pending delete，等旧 lease 引用归零后清理旧目录/连接；
- 不在进行中的 provider function loop 里热替换工具。

---

## 5. Core 与 Agent 契约调整

### 5.1 `AgentRuntimeDefinition`

改为只携带稳定引用，不携带 MCP 启动配置或密钥：

```csharp
public sealed record AgentRuntimeDefinition(
    string Id,
    string Name,
    string Description,
    AgentExecutionMode Mode,
    string ToolPolicy,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> McpServerIds,
    string Instructions);
```

相应删除：

- `AgentMcpServerDefinition`；
- `AgentRuntimeDefinition.ConfiguredMcpServers`；
- Desktop 到 runtime 的明文 MCP config 映射。

这与当前 Direct 模型选择只携带 `ModelProfileId`、凭据仅在 Infrastructure 解密的原则一致。

### 5.2 Agent markdown

目标格式：

```yaml
---
name: build
description: 通用代理
mode: direct
tools: system
plugins:
  - office-workflows
skills:
  - code-review
mcpServers:
  - github
---
Agent instructions...
```

迁移规则：

1. 新增 `plugins` 解析与序列化。
2. `skills` / `mcpServers` 保持兼容。
3. `disabledSkills` / `disabledMcpServers` 只做一次兼容读取：用“selected - disabled”得到有效值。
4. Agent 下次由设置页保存时只写 `plugins/skills/mcpServers`，不再写 `disabled*`。
5. `DesktopAgentDefinition` 改为纯记录；过滤、校验、序列化由 `DesktopAgentDefinitionService` 承担。
6. 设置页变更绑定时必须原子写临时文件后 replace，不能直接截断 Agent markdown。
7. 删除 `DesktopAgentStore.DiscoverInstalledSkillIds()` 的文件系统旁路；安装状态、broken/missing 引用统一由 extension catalog 与 capability resolver 判定，避免 Desktop 和 Infrastructure 各维护一套目录真相。

### 5.3 有效集合计算

```text
有效 Plugin
  = Agent.PluginIds
  ∩ 已安装 Plugin
  ∩ 全局已启用 Plugin

有效 standalone Skill
  = Agent.SkillIds
  ∩ 已安装 Skill
  ∩ 全局已启用 Skill

有效 standalone MCP
  = Agent.McpServerIds
  ∩ 已保存 MCP
  ∩ 全局已启用 MCP
  ∩ 配置完整

最终 Skill / MCP
  = standalone 有效项
  + 有效 Plugin 的 contribution
```

缺失绑定不会被静默扩展为全局项。设置页必须明确显示“全局启用”和“已绑定到 N 个 Agent”是两个状态。

### 5.4 `ToolPolicy` 在 v1 的语义

新 `AgentRuntimeDefinition` 保留了 `ToolPolicy` 字段，但当前代码只接受 `system`（`DesktopAgentStore` 对其他值告警并回退），且 Direct runtime 从不读取它。为避免留下一个“看起来可配置、实际无人消费”的字段，v1 明确：

- `ToolPolicy` 继续只有 `system` 一个合法值：有 workspace 时注入全部 7 个内建工具，无 workspace 时不注入，行为与当前一致。
- Plugins/Skills/MCP **不通过** `ToolPolicy` 控制；三类扩展的开关只有 §5.3 的两层交集。
- capability resolver 读取该字段仅用于前向校验（非 `system` 值在能力解析时产生 diagnostic，不改变行为）。
- 更细粒度的工具策略（如按 Agent 关闭 shell）留给后续版本，届时才扩展该字段的取值。

---

## 6. 持久化与文件布局

### 6.1 文件布局

```text
%LocalAppData%/SelfClaw/
  agents/
    build.md
  plugins/
    <plugin-id>/
      current.json                 当前激活版本指针/摘要
      versions/<version-hash>/
        plugin.json
        instructions/
        skills/
        server/
        assets/
  skills/
    <skill-id>/
      SKILL.md
      ...referenced files
  staging/extensions/<operation-id>/
  secrets/
    <dpapi-secret>.bin
  selfclaw.db
```

独立 Skill 延续当前 `DesktopAgentStore` 已扫描的 `%LocalAppData%/SelfClaw/skills` 根目录（`StoragePaths` 的应用数据根即 `LocalApplicationData`，本文所有 `%LocalAppData%/SelfClaw` 均指该根），避免制造第二个 Skill 来源。Plugin 使用版本化不可变目录，使正在运行的 lease 可以安全引用旧版本。

### 6.2 SQLite schema v22

当前 schema version 为 21；实施时升到 22，新增：

```sql
CREATE TABLE extension_packages (
    kind INTEGER NOT NULL,                 -- Plugin / Skill
    id TEXT NOT NULL,
    display_name TEXT NOT NULL,
    version TEXT NOT NULL,
    description TEXT NOT NULL,
    install_path TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    manifest_json TEXT NOT NULL,
    source_plugin_id TEXT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 0,
    acknowledged_permissions_json TEXT NULL,   -- 用户已确认的权限快照
    acknowledged_at_utc TEXT NULL,
    installed_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(kind, id)
);

CREATE TABLE mcp_server_configs (
    id TEXT NOT NULL PRIMARY KEY,
    display_name TEXT NOT NULL,
    transport INTEGER NOT NULL,            -- Stdio / Http
    settings_json TEXT NOT NULL,           -- 不含秘密值
    credential_refs_json TEXT NOT NULL,    -- setting path -> SecretRef
    source_plugin_id TEXT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 0,
    config_revision INTEGER NOT NULL DEFAULT 1,
    discovered_tools_json TEXT NOT NULL DEFAULT '[]',
    last_status INTEGER NOT NULL DEFAULT 0,
    last_error TEXT NULL,
    last_checked_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
```

同时给 `tool_runs` 增加可空审计列：

```sql
ALTER TABLE tool_runs ADD COLUMN source_kind INTEGER NULL;
ALTER TABLE tool_runs ADD COLUMN source_id TEXT NULL;
ALTER TABLE tool_runs ADD COLUMN display_name TEXT NULL;
```

约束：

- `settings_json` 只放 command、arguments array、endpoint、非敏感选项和密钥字段名。
- env/header 的秘密值先调用 `ISecretProtector.StoreSecretAsync()`（其 `existingSecretRef` 参数天然支持原位替换），表中只保存 SecretRef。
- 删除或替换密钥时，数据库提交成功后再调用 `DeleteSecretAsync()` 清理不再引用的旧 SecretRef；失败时保留旧引用，避免配置被破坏。
- Vue state 只返回 `configured: true/false` 或掩码，不返回 SecretRef 和明文。
- `acknowledged_permissions_json` 记录用户在 `PermissionReviewDialog` 中确认过的权限快照：启用前必须满足 `manifest.permissions ⊆ acknowledged`；升级后出现新增权限即不满足，`set-enabled` 被拒绝并要求重新确认（支撑 §13.2 “权限扩大不能沿用旧确认”）。没有该列，重新确认规则无处落地。

### 6.3 文件与数据库一致性

安装流程使用“文件 staging + 校验 + 原子移动 + 数据库 upsert”：

1. 在 `staging/extensions/<operation-id>` 解包。
2. 校验格式、大小、路径、manifest、引用文件、hash 和贡献项。
3. 移动到新的不可变 `versions/<version-hash>`。
4. 在 SQLite transaction 中切换 package metadata/current version。
5. 失败时删除 staging；旧版本仍有效。
6. 启动时运行轻量 reconcile：数据库指向的目录丢失则标记 broken，孤立 staging/无引用旧版本延迟清理。

---

## 7. Plugin 包格式

v1 接受 `.selfclaw-plugin`（ZIP 容器）和普通 `.zip`；包根必须有 `plugin.json`。现有 mock 中的 `.odplugin/.odskill` 不是仓库现存协议，不应在没有兼容规范的情况下固化到后端。

示例：

```json
{
  "schemaVersion": 1,
  "id": "office-workflows",
  "name": "Office Workflows",
  "version": "1.0.0",
  "description": "Presentation and document workflows.",
  "publisher": "example",
  "permissions": ["workspace.read", "workspace.write", "process.execute"],
  "contributes": {
    "directInstructions": "instructions/direct.md",
    "skills": [
      { "id": "presentation", "path": "skills/presentation" }
    ],
    "mcpServers": [
      {
        "id": "office-renderer",
        "name": "Office Renderer",
        "transport": "stdio",
        "command": "node",
        "arguments": ["${pluginRoot}/server/index.js"],
        "requiresWorkspace": true,
        "requiredSettings": [
          { "key": "LICENSE_KEY", "target": "env", "secret": true }
        ]
      }
    ]
  }
}
```

规则：

- id 只允许小写 ASCII、数字、`-`，贡献项最终规范 id 带 Plugin namespace。
- manifest 中的参数必须是 string array，禁止把整条 shell command 作为字符串执行。
- `${pluginRoot}`、`${workspaceRoot}` 只允许作为受控模板值展开，不执行 shell expansion。
- instruction、Skill、server entry path 必须 resolve 在已验证的 package root 内。
- 包内进程只能通过声明的 MCP stdio 入口启动，不能被 WPF `Assembly.Load`。
- 导入后默认 **disabled**；用户完成权限审查并显式启用后才可在测试/回合中启动进程。

### 7.1 包限制

初始建议值：

| 限制 | 值 |
|---|---:|
| 压缩包大小 | 100 MB |
| 解压后总大小 | 300 MB |
| 文件数 | 5,000 |
| 单文件大小 | 50 MB |
| `plugin.json` | 256 KB |
| 单个 `SKILL.md` | 256 KB |
| 单个 instruction 文件 | 256 KB |

必须拒绝 absolute path、`..` 逃逸、NTFS alternate data stream、reparse point/symlink、重复大小写路径和解压后越界，防止 Zip Slip 与 Windows 路径混淆。

---

## 8. Skill 运行时设计

### 8.1 安装与目录发现

独立 Skill 支持：

- 选择一个 `SKILL.md`；
- 导入包含 `SKILL.md` 的 `.zip` / `.selfclaw-skill`；
- 首版不接受前端上传 base64 内容，由 Desktop 原生文件选择器返回路径给 installer。

`SkillPackageReader` 完整读取并校验 `SKILL.md`，提取 name、description、version（可选）、triggers（可选），其余正文保持原样。引用文件按需读取，不在扫描时递归拼接。

### 8.2 激活策略

每轮分两种路径：

#### 显式激活

最新 user message 中的 `[/skill-id]`：

1. 只允许匹配本轮有效 Skill。
2. 最多激活 3 个，重复 id 去重。
3. resolver 把对应 `SKILL.md` 作为带边界标记的 system section 注入。
4. 发给模型的最新 user text 去掉 token；数据库和 Vue 中的原始消息不变。
5. token 不存在、Skill 未安装/停用/未绑定时，本轮在调用 provider 前失败并给出可操作错误，不能悄悄忽略显式请求。

**token 语法**：`[/<skill-id>]`，其中 `<skill-id>` 为 `[a-z0-9-]+`，plugin 贡献 Skill 允许一个 `/` 分隔（`<plugin-id>/<skill-id>`），总长 ≤ 64。前端 chip 正则（`\[\/([^\]\r\n]{1,80})\]`，见 §1.4）比后端宽松是有意的：渲染宽松、激活严格，二者以“后端语法是前端语法的子集”为兼容边界，后端不得使用 `]`、换行或超过 80 字符的 id。

#### 按需激活

对所有有效但未显式激活的 Skill，仅注入紧凑目录：id、name、description、triggers；同时注册两个只读工具：

```text
activate_skill(skillId)
read_skill_resource(skillId, relativePath, startLine?, lineCount?)
```

- `activate_skill` 返回完整 `SKILL.md`，使模型能在同一 function loop 中继续执行。
- `read_skill_resource` 只能访问已激活且有效 Skill 的根目录，做路径归一化、分页和文本大小限制。
- 两个工具只读本地已安装内容，不触发桌面审批，但仍记录普通 tool run 以便审计。
- **每轮激活上限**：显式 + 按需合计不超过 5 个；超限时 `activate_skill` 返回“本轮激活数已达上限”的说明文本而不是内容。对已激活（含显式激活）的 Skill 再次调用 `activate_skill` 幂等返回“已激活”提示，不重复注入全文。
- Agent 本轮没有任何有效 Skill 时，两个工具与紧凑目录都不注册，不给模型制造空目录噪音。

### 8.3 Prompt 顺序

Direct system message 使用稳定顺序：

```text
1. Agent.Instructions
2. SelfClaw capability usage policy
3. 有效 Plugin directInstructions（按 plugin id 排序）
4. 显式激活 Skill instructions（按用户 token 顺序）
5. 可用 Skill compact catalog + activate/read 使用说明
6. 能力降级摘要（如可选 MCP 暂不可用）
```

每个第三方 section 带来源和不可伪造的边界标题。MCP `ServerInstructions` 默认不注入 system prompt，因为 remote server 不应自行获得 system-level 指令权。

### 8.4 跨回合语义与历史 token

`BuildMessages()` 只重放 user/assistant 文本（§1.4），因此 Skill 激活天然是**每轮重置**的，必须作为明确语义写死，避免实现时臆造“会话级激活状态”：

1. **激活不跨回合**。上一轮 `activate_skill` 拿到的 `SKILL.md` 与显式激活注入的 system section 都不会出现在下一轮请求里；下一轮回到紧凑目录，模型需要时可再次 activate（目录与工具说明会告知这一点）。不引入“会话已激活集合”这类可变状态。
2. **历史消息中的 token**。激活语义只由最新 user message 触发。组装请求时，历史 user 消息中匹配“本轮有效 Skill”的 token 一并从文本中剥离（它们是当轮已消费的指令，留在历史里只会诱导模型模仿该记号）；无法匹配任何有效 Skill 的历史 token 保留原文、不失败——只有最新消息里的未知 token 才按显式激活规则失败。数据库与 Vue 中的原始消息始终不变。
3. **CLI 回合原样透传**。composer 模式覆盖允许同一会话临时切到 CLI（`ResolveComposerExecutionMode()`）。CLI 分支的 `ExtractPrompt()` 只取最新 user 文本，SelfClaw 不解析、不剥离、不失败，token 作为字面文本交给 CLI；`SkillPicker` 在生效模式为 CLI 时隐藏（§12.6），但用户手打的 token 不做拦截。
4. **审计**。显式激活在能力解析阶段完成、没有 function call，本轮激活的 Skill 列表记入 `DirectTurnCapabilityLease.Diagnostics`（信息级），保证事后能从日志还原“这轮注入了什么”。

---

## 9. MCP 运行时设计

### 9.1 配置模型

`McpServerConfiguration` 是纯 DTO，按 transport 分支校验：

```text
Stdio
  command
  arguments[]
  workingDirectoryMode: workspace | plugin | appData
  environment: key -> plain value / SecretRef
  requiresWorkspace

Http
  endpoint
  transportMode: auto | streamableHttp | sse
  headers: key -> plain value / SecretRef
  connectionTimeout
```

- stdio 使用 `StdioClientTransport`。
- HTTP 使用 `HttpClientTransport`，默认 `HttpTransportMode.AutoDetect`。
- stdio 显式设置 `InheritEnvironmentVariables = false`。注意 SDK 1.4.0 的默认值是 **true**，且设为 false 后子进程环境是**完全空**的（XML 文档原文：empty environment and only the variables explicitly provided）——SDK 没有“安全默认环境集合”。因此 `McpTransportFactory` 必须自己维护 Windows 最小基线集合（`SystemRoot`、`windir`、`ComSpec`、`PATHEXT`、`TEMP`/`TMP`，以及受控的 `PATH`），再叠加用户显式配置；否则 node/python 等常见 server 进程在空环境下无法启动。不能把 SelfClaw 全进程环境原样传给第三方进程。
- stdio 的 stderr 通过 SDK 的 `StdioClientTransportOptions.StandardErrorLines` 回调接入 §13.3 的限长诊断缓冲；`ShutdownTimeout`（SDK 默认 5 秒）即 §9.2 graceful shutdown 的“短超时”。
- 有 workspace 时只把所选 workspace 作为工作目录/roots；没有 workspace 而 server 标记 `requiresWorkspace` 时，该 server 本轮不可用。
- v1 不实现 MCP sampling、elicitation；client capabilities 不注册对应 handler。

### 9.2 `McpClientManager`

使用应用级 singleton 管理连接，但对 runtime 暴露引用计数 lease：

```csharp
internal interface IMcpClientManager
{
    Task<McpClientLease> AcquireAsync(
        ResolvedMcpServerConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<McpHealthResult> TestAsync(
        ResolvedMcpServerConfiguration configuration,
        CancellationToken cancellationToken = default);
}
```

pool key：

```text
server id + config revision + resolved workspace path（stdio/workspace 模式）
```

生命周期：

- 第一次 `AcquireAsync` 懒连接（`McpClient.CreateAsync(transport, options, loggerFactory)`）并 `ListToolsAsync()`。
- `TestAsync` 走连接 + `PingAsync()` + `ListToolsAsync()`，同时刷新 `discovered_tools_json` 与 health 列。
- 同 config revision 的并发回合共享连接；lease 保证使用期间不释放。
- 引用归零后进入 idle，建议 5 分钟后关闭。
- 修改/停用配置使旧 entry 进入 draining；新回合只拿新 revision。
- 应用退出由 Host 统一 `IAsyncDisposable`，给 stdio graceful shutdown 一个短超时，之后终止进程树。
- 外部 cancellation 始终传播 `OperationCanceledException`。

实现前必须用真实 Windows stdio fixture 验证 SDK 1.4.0 的 dispose 是否会终止完整子进程树；若不能，`McpTransportFactory` 内的 stdio adapter 改为由 Windows Job Object 托管的 `IClientTransport` 实现。该差异保持在 transport 内部，不改变 capability resolver interface。

**T16 spike 结论（2026-07-26）**：在 Windows 上使用 Node stdio fixture，由 server 父进程再启动一个保持运行的子进程；完成真实 `initialize` / `PingAsync()` / `ListToolsAsync()` 后释放 SDK 1.4.0 `McpClient`，以及在 `initialize` 挂起期间取消连接。两条路径的父、子 PID 均在 5 秒内退出，证明当前 SDK stdio dispose/初始化取消会终止该进程树。实现继续使用 `StdioClientTransport`，无需 Job Object adapter；回归 fixture 保留在 `SelfClaw.Tests/Infrastructure/Extensions/Fixtures/`。

生产使用 MCP SDK transport adapter；测试使用 in-memory fake transport。该 seam 有真实的 production/test 两个 adapter。

### 9.3 Tool discovery 与命名

`McpClient.ListToolsAsync()` 返回 `McpClientTool`，它已经继承 M.E.AI `AIFunction`。注册前统一重命名，机制直接用 SDK 自带的 `McpClientTool.WithName()`（必要时配合 `WithDescription()` 截断超长描述）——它返回改名后的克隆实例，调用时仍以原始 MCP tool name 请求 server，无需自写代理：

```text
mcp__<server-slug>__<tool-slug>
```

规则：

1. workspace 内建工具名保留，作为 reserved names。
2. 外部工具始终 namespace，不能覆盖 `read_file`、`run_shell_command` 等内建工具。
3. 只使用 provider 普遍接受的 `[A-Za-z0-9_-]`，超长时截断并附加 8 位稳定 hash。
4. 同一快照内最终名称仍冲突时能力解析失败，不能让后注册项静默覆盖。
5. `DirectToolDescriptor` 保留 provider name、原始 MCP tool name、server id、Plugin source、显示名、风险等级和一个 `ToolCallKind` 提示（内建工具沿用现值；skill loader 工具为 `Read`；MCP 工具默认 `Other`，可按 `readOnlyHint` 提示为 `Read`——仅影响卡片图标，不影响审批）。`DirectAgentChatRuntime.MapToolKind()` 的按名猜测随之退役（§1.3）。

### 9.4 审批策略

MCP annotations 是“不可信提示”。有效审批策略：

```text
配置 hard deny
  -> 不向模型暴露

conversation.ToolPermissionMode == FullAccess
  -> 允许调用（仍受 hard deny）

conversation.ToolPermissionMode == RequireApproval
  -> 所有 MCP tool 默认逐次审批
```

未来可增加“用户显式信任某 server 的某个只读工具”，但不能仅根据 server 自报 `readOnlyHint=true` 自动免审。审批内容必须显示：

- 显示名与原始 tool name；
- 来源 server / Plugin；
- transport 与远端 host 或 stdio command；
- 参数 JSON；
- 声明权限和 annotations（标记为 server-provided hint）。

**载体**：以上内容的运载 DTO 是现有 `ToolApprovalRequest`（当前只有 `ToolExecutionId/ToolName/DisplayName/Description/ArgumentsJson/ConversationId`），为其增加带默认值的可空来源字段（`SourceKind`、`SourceId`、`TransportSummary`、`AnnotationsJson`），保持 workspace 工具构造点不变。它有三个既有展示面会自动受益：Vue 确认栏（`toolApprovalRequest` payload 按字段透传）、Windows toast（受版面限制只加“来源”一行短文案）、宠物审批队列（`AgentActivityCoordinator` 监听 `DesktopToolApprovalHandler` 的现有链路，气泡文案带来源）。

**拒绝语义与 workspace 工具一致**：审批 wrapper 在拒绝时正常返回 `DeniedResult` 字符串（不是抛异常），模型收到否决结果后继续本轮（对应验收 §18.5）；审批期间工具记录保持 `Running`，与当前 workspace 审批行为相同（§1.4）。

审批 wrapper 应是通用 `ApprovedAIFunction`，不要复制 `WorkspaceAgentToolset.BoundWorkspaceTools.IsApprovedAsync()`。workspace write/edit/shell 也可迁移到该 wrapper，统一审批行为。

### 9.5 MCP 工具结果的内容映射

`McpClientTool` 的 AIFunction 调用结果是序列化后的 `CallToolResult`（`JsonElement`），包含 `content[]`（text / image / audio / resource_link / embedded resource）、可选 `structuredContent` 和 `isError`。当前 `DescribeToolResult()` 的 `JsonElement` 兜底只会给出“Tool call completed.” + 原始 JSON（§1.4），必须补一条 MCP 专用路径，放在 `McpToolAdapter`（依据 `DirectToolDescriptor` 判定该 call 是 MCP 工具）：

- **给模型的结果**：M.E.AI function loop 回传什么由 SDK 决定，不改写；但对超长结果设置上限（建议 64 KiB，截断时附截断说明），避免单次 tool result 撑爆上下文。
- **`isError = true`**：映射为 `ToolCallStatus.Failed` 的 `ToolCallCompletedEvent`（工具卡片显示失败），模型照常拿到错误文本继续，本轮不失败——与 §14.2 “MCP tool call 失败”行一致。
- **摘要（ResultSummary）**：取第一个 text block 的首行（截断），无 text block 时给出内容类型计数（如 `1 image, 2 resources`）。
- **详情（ResultContent）**：拼接全部 text block；image/audio/blob 以占位符描述（类型 + mimeType + 大小），不把 base64 落库或送进 prompt；`structuredContent` 存在时以 pretty JSON 附加。
- 摘要/详情映射只服务事件与落库展示，不改写给模型的结果；唯一触碰模型侧的是第一条的上限截断，它在审批 wrapper 内、映射之前统一发生。

---

## 10. Direct 回合改造

### 10.1 新调用链

```text
DirectAgentChatRuntime.ProduceEventsAsync()
  -> capabilityResolver.ResolveAsync(request)
       -> DirectTurnCapabilityLease
  -> emit initializing/degraded status
  -> AiChatRuntimeInputs(false, capabilityLease.Tools)
  -> IAiChatClientFactory.CreateAsync()
  -> DirectPromptComposer.BuildMessages(
       request.Messages,
       request.Agent.Instructions,
       capabilityLease.SystemInstructions,
       capabilityLease.MessageAdjustments)
  -> lease.Client.GetStreamingResponseAsync()
  -> FunctionCallContent
       -> capabilityLease.ToolDescriptors[call.Name]
       -> ToolCallStartedEvent（带来源）
  -> FunctionResultContent
       -> ToolCallCompletedEvent
  -> dispose AiChatClientLease
  -> dispose DirectTurnCapabilityLease
```

释放顺序很重要：先让 provider pipeline 完整结束并 dispose，再释放 MCP lease，防止 function invocation middleware 还在收尾时连接被关闭。

`DirectPromptComposer` 继承现有 `BuildMessages()` 的全部过滤行为：跳过 `Failed` 与 `Cancelled` 消息、跳过空文本、只映射 User/Assistant 角色（§1.4）；在此之上应用 `MessageAdjustments` 覆盖文本，并按 §8.3 顺序拼接 system sections。它是纯函数，不做 IO。

### 10.2 工具事件与落库

扩展 `ToolCallStartedEvent`，新增带默认值的可空来源字段，保持现有构造点兼容：

```csharp
public sealed record ToolCallStartedEvent(
    string ToolCallId,
    string ToolName,
    string ArgumentsJson,
    ToolCallKind Kind,
    ToolSourceKind SourceKind = ToolSourceKind.BuiltIn,
    string? SourceId = null,
    string? DisplayName = null) : AgentStreamEvent;
```

`ConversationTurnEngine.StartToolRunAsync()` 把来源写入 v22 新列。前端工具卡片显示：

```text
Git Status
MCP · git
```

而不是只显示经过 namespace 的 provider function name。

来源要贯通整条投影链，缺一环卡片就退回 provider name：

```text
DirectToolDescriptor
  -> ToolCallStartedEvent(SourceKind/SourceId/DisplayName)
  -> ToolExecutionRecord 增加同名可空字段（现有构造点默认 BuiltIn/null）
  -> ConversationTurnEngine.StartToolRunAsync() 写入 v22 列
  -> TranscriptToolRunPresenter.BuildToolSegment() 投影进 TranscriptRenderSegment 新增来源字段
  -> ToolCard.vue / ToolGroup.vue 渲染来源副标题
```

`BuildToolSegment()` 对 MCP 工具优先使用 `DisplayName`（原始 tool name 的人类可读形式），summary/detail 由 §9.5 的映射提供；同步修正该 presenter 中漂移的旧工具名 switch（§1.3）。`activate_skill` / `read_skill_resource` 的来源显示为 `Skill · <skill-id>`。历史会话从 v22 列恢复来源；v22 之前的旧记录来源列为 NULL，按内建展示。

### 10.3 降级规则

- 显式 `[/skill]` 无法激活：本轮失败，错误说明未安装/停用/未绑定中的具体原因。
- Plugin manifest、路径、工具名称出现安全错误：该 Plugin 不进入快照，并把安装状态标记 broken；若用户本轮显式依赖它则失败。
- 单个可选 MCP 连接失败：跳过该 server，其他能力和模型回合继续；错误写日志、更新设置页 health，并通过带 detail 的 `RunStatusEvent` 告知当前 UI。
- MCP 全部不可用但用户没有显式要求某项：模型仍可使用基础对话和 workspace tools。
- capability resolver 自身出现未知错误：Direct runtime 输出失败终态，不创建 provider client。

`RunStatusEvent` 可增加可空 `Detail` 字段，`AgentActivityCoordinator` 和 `activityText` 展示短提示；设置页保存完整 `lastError`。不把连接错误伪装成 assistant 文本。`Detail` 的消费点就是 `AgentActivityCoordinator` 现有的 `AgentRunStatus` -> 中文文案映射（当前为四条固定文案，§1.4）：`Detail` 非空时以其覆盖默认文案，同一条链路自然到达 Vue 活动区与宠物气泡，无需新增事件类型。

---

## 11. 设置服务与 Desktop bridge

### 11.1 设置模块 interface

设置页只依赖一个面向用例的深 interface：

```csharp
public interface IExtensionSettingsService
{
    Task<ExtensionSettingsState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<ExtensionPackageView> ImportPackageAsync(
        ExtensionPackageKind kind,
        string selectedPath,
        CancellationToken cancellationToken = default);
    Task SetEnabledAsync(ExtensionItemKey key, bool enabled, CancellationToken cancellationToken = default);
    Task DeleteAsync(ExtensionItemKey key, CancellationToken cancellationToken = default);
    Task<McpServerView> SaveMcpServerAsync(SaveMcpServerCommand command, CancellationToken cancellationToken = default);
    Task<McpHealthResult> TestMcpServerAsync(string id, CancellationToken cancellationToken = default);
}
```

Agent 绑定涉及 AppData Agent markdown，由 Desktop 的 `DesktopAgentDefinitionService` 负责；`ExtensionSettingsBridge` 协调设置服务和 Agent 定义模块，避免 Infrastructure 反向依赖 Desktop。

所有 state/command/view record 都是纯 DTO，每个文件只放一个 DTO；解析、验证、遮罩、迁移和文件操作全部在类模块中。

### 11.2 `ExtensionSettingsBridge`

沿用 `AiProviderSettingsBridge` 的模式：

- 消息前缀：`extensions/`；
- 原样回显 `requestId`；
- `OperationCanceledException` 重抛；
- 其他异常转换为 `{ type, requestId, error }`；
- bridge 直接返回带 `requestId` 的响应对象，由 `WebViewMessageRouter` 统一调用 `WebViewHostChannel.PostResponse()`。

集成点：`WebViewMessageRouter.RouteAsync()` 按固定顺序调用各 feature bridge；bridge 返回非空响应后立即回包，不把 extensions message 塞进 shell intent switch。

除 request/response 外，任何成功 mutation、以及回合期 health 变化（§10.3）都会通过 `IExtensionStateChangeNotifier.StateChanged(revision)` 发布。`WebViewMessageRouter` 直接订阅该 notifier 后做两件事——向 Vue 推送 `extensions/state-changed`（打开中的设置页据此刷新，而不是靠轮询），并通知 `MainWindowViewModel` 更新 `capabilityRevision` 后重新发布 transcript（composer 的 SkillPicker 缓存据此失效，§12.6）。

导入不能依赖 WebView `<input type=file>` 暴露绝对路径，也不应把大文件 base64 塞进 JSON。`extensions/import-package` 由 Desktop 打开原生 `OpenFileDialog`，将用户选择的本地路径直接交给 installer。

**超时注意**：hostBridge 的 `request()` 默认 30 秒超时（§1.4），而 import 流程要等用户在原生文件对话框里挑文件，随后还有解包校验。前端对 `extensions/import-package` 必须显式传大超时（如 10 分钟）；用户取消文件选择时后端立即回 `{ ok: false, cancelled: true }`，不留悬挂请求。`extensions/test-mcp` 的后端执行必须自行限时（连接超时 + ping/list 上限），保证在默认 30 秒内回包或返回明确超时错误。

### 11.3 消息契约

| type | 方向 | 关键字段 | 返回 |
|---|---|---|---|
| `extensions/get-state` | Vue -> Desktop | `requestId` | 完整设置 state |
| `extensions/import-package` | Vue -> Desktop | `requestId`, `kind` | 导入项 + 最新 revision；Desktop 弹原生文件选择器 |
| `extensions/set-enabled` | Vue -> Desktop | `kind`, `id`, `enabled` | `ok`, `revision` |
| `extensions/delete` | Vue -> Desktop | `kind`, `id` | `ok`, `revision` |
| `extensions/save-mcp` | Vue -> Desktop | MCP command DTO | 脱敏 MCP view |
| `extensions/test-mcp` | Vue -> Desktop | `id` | latency、status、error、tools |
| `extensions/set-agent-binding` | Vue -> Desktop | `agentId`, `kind`, `id`, `enabled` | 最新 Agent bindings |
| `extensions/list-effective-skills` | Vue -> Desktop | 可选 `agentId` | composer 可选 Skill 列表 |
| `extensions/state-changed` | Desktop -> Vue（推送） | `revision` | 无回包；设置页与 composer 据此重新拉取 |

`save-mcp` 要求：

- `arguments` 是 array，不是以空格分隔的单字符串；
- env/header 以结构化行提交；
- secret value 留空表示保留旧值，`clearSecret=true` 才删除；
- response 只返回 `hasSecret=true`；
- URL、command、参数、环境键名均由后端二次校验，不能相信 Vue 校验。

### 11.4 State 示例

```json
{
  "type": "extensions/get-state",
  "requestId": "host-...",
  "state": {
    "revision": 12,
    "activeAgentId": "build",
    "agents": [
      { "id": "build", "name": "build", "pluginIds": [], "skillIds": ["code-review"], "mcpServerIds": ["github"] }
    ],
    "plugins": [],
    "skills": [
      {
        "id": "code-review",
        "name": "Code Review",
        "version": "1.0.0",
        "enabled": true,
        "sourcePluginId": null,
        "assignedAgentIds": ["build"],
        "status": "ready"
      }
    ],
    "mcpServers": [
      {
        "id": "github",
        "name": "GitHub",
        "transport": "http",
        "enabled": true,
        "assignedAgentIds": ["build"],
        "status": "ready",
        "lastError": null,
        "tools": ["search_issues"]
      }
    ]
  }
}
```

---

## 12. 前端设计

### 12.1 组件拆分

删除 `Plugins.vue` 中的 mock 和业务逻辑，将其拆为：

```text
components/settings/extensions/
  ExtensionSettingsPanel.vue        薄页面编排
  ExtensionCategoryTabs.vue
  ExtensionToolbar.vue
  ExtensionList.vue
  ExtensionListItem.vue
  ExtensionDetailDrawer.vue
  ExtensionStatusBadge.vue
  AgentBindingsEditor.vue
  McpServerDialog.vue
  McpSecretFields.vue
  PackageImportDialog.vue
  PermissionReviewDialog.vue

composables/
  useExtensionSettings.js           state、加载、mutation、错误/并发控制
  useMcpServerForm.js               表单归一化和校验
```

`SettingsView.vue` 继续用 `defineAsyncComponent`。每个组件 `<style scoped>` 并导入 `settings-console.css`；跨组件视觉规则可放独立共享 CSS，但业务状态不放组件脚本。

### 12.2 页面状态

三类 tab 沿用当前信息架构，但一行状态改为真实含义：

- package/config 状态：ready、disabled、needs-config、connecting、degraded、broken；
- 全局启用 switch；
- `已绑定 N 个 Agent`；
- Plugin contribution 显示 `由 <plugin> 管理`，不能从子项 tab 单独卸载；
- MCP 显示 transport、最近检查时间、tool 数量和可展开错误；
- mutation 中禁用对应行，成功后采用后端 response/revision，失败回滚，不做假成功 toast。

### 12.3 MCP 表单

替换当前的 raw textarea：

- stdio command 单字段；
- arguments 使用可增删的逐项列表，保留包含空格的单个参数；
- environment/header 使用 key/value 行；
- 每行可标记 secret，已有 secret 只显示“已配置”；
- HTTP endpoint 只允许 `http/https`，非 HTTPS 远端地址显示高风险提示；
- 保存后不自动假定连接成功；可选择“保存并测试”，状态来自后端。

### 12.4 Plugin / Skill 导入

当前 drag/drop File 仅保留为视觉 mock，没有安全可靠的 Desktop 路径传输。v1 改为：

1. 点击导入；
2. Vue 请求 `extensions/import-package`；
3. WPF 原生文件选择器选择文件；
4. 后端 staging/校验；
5. Vue 收到 manifest、hash、permissions 和 contribution 摘要；
6. 首次启用前展示 `PermissionReviewDialog`；
7. 用户确认后才 `set-enabled`。

后续若要恢复 WebView drag/drop，应通过 WebView2 专用文件句柄能力设计，不走 base64 JSON。

### 12.5 Agent 绑定维护

详情抽屉加入“适用 Agent”：

- Plugin：绑定后启用其全部有效 contributions；
- standalone Skill/MCP：可独立绑定；
- contribution 子项显示继承自 Plugin 的绑定，v1 不提供子项覆盖；
- 修改调用 `extensions/set-agent-binding`，由 Desktop 原子写 Agent markdown；
- built-in Agent 也允许修改其 AppData markdown，但 UI 标识内建模板，不能删除 Agent 本身。

### 12.6 Composer SkillPicker

在 `ComposerPanel.vue` 的 Direct 模式工具栏新增 `SkillPicker.vue`：

- 打开时调用 `extensions/list-effective-skills`，只列当前 Agent 的有效 Skill；
- 选择后在 textarea 当前光标处插入 `[/skill-id] `；
- 已存在的 `renderSkillTokensInUserHtml()` 继续负责发送后消息 chip 渲染；
- CLI 模式隐藏该入口，避免暗示 SelfClaw 会给 CLI 注入 Skill；注意生效模式是 per-send 的 composer 覆盖结果（`ResolveComposerExecutionMode()`），用户切到 CLI 后已输入的 token 原样透传（§8.4）；
- `replaceState` 增加 `selectedAgentId`、`selectedAgentName`、`capabilityRevision`，用于 picker cache 失效。落点是既有链路的三处扩展：`TranscriptRenderState` 增加对应字段（当前只有 `AgentMode` 一个模式信号，没有任何 agent 身份，§1.2.10）-> `MainWindowViewModel.PublishShell()` 填充 -> `ChatView.replaceState()` 读入 state。`capabilityRevision` 由 §11.2 的 `StateChanged` 通知 ViewModel 后递增；
- 插入和提交仍由 `ComposerPanel` 管理，不把 draft 状态上提到设置页。

---

## 13. 安全设计

### 13.1 信任层级

```text
SelfClaw 内建 workspace/skill-loader tools
  > 用户手工配置且启用的 MCP
  > 用户导入并确认权限的 Plugin
  > MCP server 返回的 tool metadata / instructions
```

下层不能仅凭自报信息提升到上层权限。

### 13.2 安装安全

- 所有包先进入 staging，完全校验后再激活。
- 拒绝路径逃逸、链接/reparse point、ADS、超限、重复规范路径。
- 保存 package content hash、publisher 文本和 manifest；v1 明确标记“未签名”。
- 更新时重新展示新增权限 diff；权限扩大不能沿用旧确认。
- 删除采用 disable -> drain -> remove，不杀死仍被活动回合持有的资源。

### 13.3 进程与网络安全

- stdio 不经 shell，command 与 argument list 分开。
- 默认只转发安全环境集合，不继承整个 SelfClaw 环境。
- MCP stderr 进入限长诊断缓冲，不进入 assistant 消息；日志不得打印 secret 值。
- 远端 endpoint 必须是绝对 HTTP(S) URI；公网 HTTP 默认拒绝，localhost HTTP 可在 UI 警告后允许。
- HTTP header secret 在每次连接时于 Infrastructure 内解密，client/lease 释放后不保留到 view DTO。
- Plugin stdio server 仍拥有当前 Windows 用户权限；首版 UI 必须直说“进程未经过 OS sandbox”。

### 13.4 Prompt 安全

- Plugin instructions 和 Skill 被视为用户安装的高权限指令，只在对应 Agent 有效时注入。
- MCP `ServerInstructions` 默认忽略。
- tool descriptions 做长度限制并保留来源；不能把 tool description 拼入宿主自己的安全 policy section。
- Skill resource 只允许文本白名单和分页读取；二进制文件不直接放入 prompt。
- 系统固定 policy 明确：扩展内容不能修改 SelfClaw 的审批规则、secret 处理和 workspace 限制。

---

## 14. 取消、错误与资源释放

### 14.1 取消

取消 token 贯穿：

```text
Desktop CTS
  -> DirectAgentChatRuntime
  -> IDirectTurnCapabilityResolver
  -> McpClientManager.Acquire/ListTools
  -> provider stream / function invocation / MCP call
```

任何 `OperationCanceledException` 都继续抛出，不转换成失败结果。取消后：

1. provider stream 停止；
2. `AiChatClientLease.Dispose()`；
3. `DirectTurnCapabilityLease.DisposeAsync()` 释放 MCP 引用；
4. ref count 归零的专属 stdio connection 可关闭，共享 connection 保留到 idle timeout；
5. Desktop turn finalizer 按现有流程把未完成 tool run 标记 Cancelled。

### 14.2 错误分类

| 类别 | 行为 |
|---|---|
| package/config 校验错误 | 设置 mutation 失败，不改变旧状态 |
| secret 解密失败 | 对应 MCP unavailable；不把 secret/ref 写入错误文本 |
| MCP connect/list tools 失败 | 更新 health；可选 server 降级，显式依赖失败 |
| MCP tool call 失败 | 作为 `FunctionResultContent.Exception` -> 失败工具卡片，模型可继续处理 |
| provider 不接受工具 schema/name | 本轮失败，错误包含 server/tool 标识但不含 secret |
| capability lease dispose 失败 | 记录 error；不覆盖已经锁定的回合终态 |

---

## 15. 建议代码落点

### Core

```text
SelfClaw.Core/
  Interfaces/Extensions/
    IExtensionPackageRepository.cs
    IMcpServerRepository.cs
    IExtensionSettingsService.cs
  Models/Extensions/
    ExtensionKind.cs
    ExtensionItemKey.cs
    ExtensionPackageRecord.cs
    McpServerConfiguration.cs
    ...每个 DTO 单独文件
  Runtime/
    AgentRuntimeDefinition.cs              增加 PluginIds，删除内嵌 MCP config
    Agent/ToolCallStartedEvent.cs           增加来源
    Agent/ToolSourceKind.cs
```

`IExtensionRepository` 被 Infrastructure 设置模块和 Direct 能力模块共同消费，因此放 Core；仅 MCP manager 内部使用的 seam 放 Infrastructure `Abstractions/`。

### Infrastructure

```text
SelfClaw.Infrastructure/Extensions/
  ExtensionSettingsService.cs
  ExtensionPackageInstaller.cs
  ExtensionCatalog.cs
  Repositories/SqliteExtensionRepository.cs
  Skills/SkillPackageReader.cs
  Skills/SkillRuntimeToolset.cs
  Mcp/McpConfigurationResolver.cs
  Mcp/McpTransportFactory.cs
  Mcp/McpClientManager.cs
  Mcp/McpToolAdapter.cs
  Runtime/DirectTurnCapabilityResolver.cs
  Runtime/DirectTurnCapabilityLease.cs
  Runtime/DirectPromptComposer.cs
  Abstractions/IDirectTurnCapabilityResolver.cs
  Abstractions/IMcpClientManager.cs
```

`DirectAgentChatRuntime` 删除直接依赖 `WorkspaceAgentToolset`，改为依赖 `IDirectTurnCapabilityResolver`。`AiChatClientFactory` 和 provider adapters 不需要知道工具来自哪里。

### Desktop

```text
SelfClaw.Desktop/Services/Extensions/
  ExtensionSettingsBridge.cs
  ExtensionPackagePicker.cs
SelfClaw.Desktop/Services/Agents/
  DesktopAgentDefinition.cs                纯 DTO
  DesktopAgentDefinitionService.cs         解析/保存/绑定/迁移
```

`MainWindow` 只新增一个 `ExtensionSettingsBridge.TryHandleAsync()` 前缀分发和 response 订阅，不把每个 extensions message case 塞进现有大 switch。

### TranscriptVue

使用 §12 的组件/composable 拆分；原 `Plugins.vue` 可保留为薄 route wrapper，或由 `SettingsView.vue` 直接 lazy-load `ExtensionSettingsPanel.vue`。

### DI 与启动接线

新模块全部走既有装配点，不新增初始化机制：

```text
AddSelfClawInfrastructure() 追加注册（均为 singleton，与现有 runtime 服务一致）：
  IExtensionPackageRepository  -> SqliteExtensionRepository
  IMcpServerRepository         -> SqliteExtensionRepository（同一实现类可分接口注册）
  ExtensionCatalog / ExtensionPackageInstaller / SkillPackageReader
  IExtensionSettingsService    -> ExtensionSettingsService
  IMcpClientManager            -> McpClientManager（实现 IAsyncDisposable）
  IDirectTurnCapabilityResolver-> DirectTurnCapabilityResolver
  DirectAgentChatRuntime 构造依赖从 WorkspaceAgentToolset 换成 IDirectTurnCapabilityResolver

App.xaml.cs（Desktop）：
  OnStartup: 在 IAiProviderRepository.InitializeAsync() 之后追加
    IExtensionPackageRepository.InitializeAsync()（v22 迁移随现有 schema 管道执行）
    ExtensionCatalog.ReconcileAsync()（§6.3 的启动 reconcile，轻量、可容错）
  DI: 注册 ExtensionSettingsBridge 与 WebViewMessageRouter（singleton），router 负责 bridge 调用、
    响应回包以及 StateChanged / ModelSelectionChanged 订阅
  OnExit: 无需新增代码——Generic Host DisposeAsync 会释放实现 IAsyncDisposable 的
    McpClientManager singleton，由它执行 §9.2 的 graceful shutdown + 进程树终止
```

AGENTS.md 的 DI Registration 清单与 schema version 记录在 P4 一并更新。

---

## 16. 测试策略

### 16.1 Capability seam 测试

测试主要穿过 `IDirectTurnCapabilityResolver` 的 interface，断言可观察结果，不锁定内部类结构：

- 全局启用与 Agent 绑定求交集；
- Plugin contribution 展开与 namespace；
- 空绑定不意外启用全局项；
- 显式 Skill 激活、未知 token 失败、最多 3 个、resource 路径逃逸；
- `MessageAdjustments`：最新消息 token 剥离、历史消息中有效 Skill token 剥离、无法解析的历史 token 保留且不失败（§8.4）；
- 激活上限（显式 + 按需合计）与 `activate_skill` 幂等；无有效 Skill 时不注册 loader 工具与目录；
- workspace 缺失时 `requiresWorkspace` 的行为；
- workspace/Skill/MCP tool 命名唯一；
- RequireApproval / FullAccess / hard deny；
- 一个 MCP 失败时其他能力仍可用；
- cancellation 与 lease dispose 恰好一次。

### 16.2 MCP 测试

- in-memory fake transport 覆盖 initialize、list tools、call tool、disconnect；
- config revision 与 workspace 隔离 pool key；
- 并发 Acquire 只创建一个 client；
- idle/draining 与 app shutdown；
- Windows Job Object/SDK dispose 的进程树退出验证；
- env/header secret resolution 与日志/state 脱敏；
- stdio 空环境 + 自维护 Windows 基线集合（SystemRoot 等）能启动真实 node/python fixture（§9.1）；
- `CallToolResult` 内容映射：text 摘要、非文本占位、`isError` -> Failed 卡片、超长截断（§9.5）；
- `McpClientTool` 重命名后仍调用原始 MCP tool name。

至少增加一个真实 stdio test fixture（测试进程）验证 Windows 启动、JSON-RPC、取消和 kill-tree，不依赖外网。

### 16.3 安装与持久化测试

- Zip Slip、absolute path、ADS、reparse point、大小/数量上限；
- invalid manifest 不改变旧版本；
- update 权限扩大；
- staging 失败恢复与 startup reconcile；
- schema v21 -> v22 保留 conversation/messages/tool_runs；
- secret 更新失败时旧 secret 仍可用，删除配置后无引用 secret 被清理。

### 16.4 Direct runtime 测试

给 `DirectAgentChatRuntimeTests` 注入 fake capability resolver：

- tools 和 system instructions 进入 client；
- tool descriptor 正确投影到来源事件；
- provider dispose 先于 capability dispose；
- resolver 失败时不创建 provider client；
- cancellation 不转失败终态。

旧的 workspace toolset 细粒度测试保留其路径/审批职责；重复验证能力装配的测试移到新 seam，避免测试层层叠加。

### 16.5 Desktop / Vue

- `ExtensionSettingsBridgeTests` 覆盖每个 requestId、错误、secret 遮罩、Agent 绑定，以及 `StateChanged` -> `extensions/state-changed` 推送与 capabilityRevision 递增。
- 增加 Vitest + Vue Test Utils（当前前端没有 test script），至少覆盖：加载、mutation 回滚、MCP secret 保留/清除、Agent binding、Skill token 插入、import 请求使用加长超时。
- `npm run build` 验证懒加载 chunk 和 scoped style。

---

## 17. 分阶段实施

### P0：契约与持久化地基

- schema v22、repository、settings service；
- Agent DTO 纯化、`plugins` 字段、`disabled*` 兼容迁移；
- 删除 `AgentMcpServerDefinition/ConfiguredMcpServers`；
- `ExtensionSettingsBridge` 与真实 settings state（含 `extensions/state-changed` 推送）；
- 拆分 `Plugins.vue` 并删除 mock。

验收：设置页重启后状态保持；Vue/日志/Agent markdown 中没有 secret 明文。

### P1：Skills 端到端

- Skill installer/catalog；
- capability resolver 与 Skill tools；
- 显式 token 解析与 `MessageAdjustments`；
- composer `SkillPicker` 与 `replaceState` 扩展（`selectedAgentId`/`capabilityRevision`）；
- Direct prompt composition。

验收：绑定 Skill 后，显式和按需两种路径都能在同一 Direct 回合生效；未绑定 Skill 无法调用。

### P2：MCP 端到端

- stdio/HTTP config、test、health；
- `McpClientManager`、tool discovery、name mapping；
- 通用审批 wrapper（含 `ToolApprovalRequest` 来源字段）、tool provenance、§9.5 结果映射、schema v22 tool audit；
- cancellation/draining。

验收：一个 stdio fixture 和一个 HTTP fixture 可被 Direct 模型调用；RequireApproval 下每个 MCP call 可允许/拒绝，取消无残留进程。

### P3：Plugins

- manifest/import/update/delete；
- instructions、Skill、MCP contribution；
- permission review 和 Agent binding；
- package version lease 与旧版本清理。

验收：导入一个同时贡献 Skill 和 MCP 的 Plugin，绑定到 `build` 后下一回合生效；停用/解绑后新回合不可见，活动回合不被破坏。

### P4：收尾

- health/diagnostic UX；
- Vitest 和真实 transport fixture；
- 更新 `docs/runtime-execution-flow.md`、根 `AGENTS.md` 的运行边界、schema version 和 DI 列表；
- 删除迁移完成后的 dead fields、旧 mock 与重复测试。

---

## 18. 验收标准

1. Agent 未绑定任何扩展时，Direct 行为与当前版本一致。
2. Plugin/Skill/MCP 全局启用但未绑定 Agent 时，不进入模型上下文或工具列表。
3. `[/skill]` 在 UI 保留 chip，Direct 只在本轮激活对应有效 Skill。
4. MCP tool 经过 namespace 后无碰撞，工具卡片仍显示原始名称和来源。
5. RequireApproval 下 MCP 调用默认需要确认；拒绝后模型拿到拒绝结果，回合不挂死。
6. 修改/停用/删除配置只影响新回合；活动回合的 lease 可正常收尾。
7. MCP secret 不出现在 SQLite 明文、Agent markdown、WebView JSON、异常文本或日志。
8. stdio MCP 在取消、窗口关闭和应用退出时无孤儿进程。
9. `Plugins.vue` 不再包含 mock 数据，扩展设置业务进入 composable，组件保持单一职责与 scoped style。
10. Direct、CLI 继续输出统一 `AgentStreamEvent`；CLI 行为没有变化。
11. MCP 工具返回 `isError=true` 时，工具卡片显示失败、模型收到错误文本、回合继续（不产生失败终态）。
12. 导入流程在原生文件对话框停留任意时长都不产生前端超时错误；用户取消选择后设置页回到原状态。
13. 设置 mutation 或回合期 health 变化后，打开中的设置页收到 `extensions/state-changed` 刷新，composer SkillPicker 缓存按 `capabilityRevision` 失效。
14. Skill 激活不跨回合：上一轮 activate 过的 Skill 在新回合只出现在紧凑目录，历史消息中的 token 不重复触发激活。

---

## 19. 明确不采用的方案

### 19.1 在 `ResolveRuntimeAgent()` 中把完整 MCP config 塞进 Core DTO

不采用。它会让 Desktop 负责配置解析和密钥处理，也让每轮请求携带易泄漏的 env/header。运行时只传 id，Infrastructure 在本轮解析，和 provider `ModelProfileId` 的现有做法保持一致。

### 19.2 每轮把所有 Skill 全文塞进 system prompt

不采用。它会造成稳定的 token 浪费、多个 Skill 指令冲突，并破坏渐进披露。采用显式激活 + 紧凑目录 + `activate_skill/read_skill_resource`。

### 19.3 让所有已启用扩展自动对所有 Agent 生效

不采用。安装/启用是应用级信任，Agent 绑定是运行时最小权限，两者必须分开。

### 19.4 把 Plugin DLL 动态加载到 SelfClaw 进程

不采用。它破坏进程稳定性和卸载边界，也无法用现有审批体系控制。v1 的可执行扩展统一走进程外 MCP。

### 19.5 每个 Direct 回合无条件新建所有 MCP 进程

不采用。实现简单但 stdio 启动延迟和资源抖动明显。使用按 config revision/workspace 隔离、引用计数和 idle timeout 的 manager，同时仍以 per-turn lease 保持正确释放语义。

### 19.6 依赖 MCP annotations 自动免审批

不采用。SDK 文档明确说明 annotations 只是 hints，不能用于对不可信 server 做安全决策。

---

## 20. 实施后的目标调用链摘要

```text
Vue composer / extension settings
  -> WebView2 host bridge
  -> Desktop Agent binding + Infrastructure extension registry

SendAsync()
  -> AgentRuntimeDefinition(PluginIds, SkillIds, McpServerIds)
  -> DirectAgentChatRuntime
  -> DirectTurnCapabilityResolver
       -> plugin instructions
       -> explicit/on-demand skills
       -> workspace tools
       -> MCP clients/tools
       -> approval + provenance + diagnostics
  -> AiChatClientFactory + UseFunctionInvocation
  -> provider stream
  -> unified AgentStreamEvent
  -> ConversationTurnEngine
  -> tool_runs(source kind/id/display name)
  -> TranscriptRenderState
  -> Vue tool cards / activity
```

该结构把扩展系统的复杂度集中在一个可测试的深模块中，同时保持现有 provider adapter、dispatcher、conversation reducer 和 CLI 链路稳定。
