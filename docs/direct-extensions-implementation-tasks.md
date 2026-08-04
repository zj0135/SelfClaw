# Direct 扩展系统（Plugins / MCP / Skills）—— 分步实施任务文档

> 配套设计：《direct-extensions-system-design.md》v1.1（2026-07-26）。本文按任务粒度拆解，每个任务可独立提交、独立验证。
> 引用格式：§n 指设计文档章节；文件路径均相对仓根。

## 使用约定

- **完成定义（每个任务通用 DoD）**：`dotnet build SelfClaw.slnx` 零新增警告、`dotnet test SelfClaw.Tests` 全绿；涉及前端时 `SelfClaw.TranscriptVue` 内 `npm run build` 通过；涉及 UI 的任务附手动验收步骤。
- **提交粒度**：一个任务一个（或一组）提交，提交信息沿用仓内惯例（`feat(extensions): …` / `feat(mcp): …` / `feat(skills): …` / `refactor(agents): …`）。
- **规模**：S ≈ 半天内，M ≈ 1 天，L ≈ 2-3 天（含测试）。
- 标注 ⚠ 的步骤是设计核对（v1.1，§1.2–§1.4）中确认的易错点，实施时优先核对。
- 阶段编号沿用设计 §17：P0 契约与持久化地基 → P1 Skills → P2 MCP → P3 Plugins → P4 收尾。

## 实施状态（2026-07-26）

| 范围 | 自动化状态 | 真实桌面验收 |
| --- | --- | --- |
| T1-T7 | 已完成，后端测试与前端编译覆盖 | T8 待手工验收 |
| T9-T13 | 已完成，Skill 解析、能力求解与 bridge 测试覆盖 | T14 待手工验收 |
| T15-T19 | 已完成，真实 HTTP/stdio MCP fixture 与来源/审批测试覆盖 | T20 待手工验收 |
| T21-T23 | 代码与自动化验证已完成，包括权限门控、升级、lease、drain、delete、reconcile | P3 导入/确认/绑定/升级/活动回合待手工验收 |
| T24 | 自动化收尾已完成：377 项 .NET 测试、前端生产编译、solution build 与 `git diff --check` 通过 | §18 中的桌面操作待手工验收 |

本状态只区分可由自动化证明的实现与真实 WPF/WebView2 行为。前端执行 `npm run build` 编译验证；视觉和桌面交互由手工验收覆盖，不把未执行的前端测试标为通过。

### §18 验收审计

| §18 | 自动化证据 | 状态 |
| --- | --- | --- |
| 1 | 空绑定的 Direct capability resolver 等价路径 + 全量 runtime 回归 | 自动化通过 |
| 2 | 全局启用、Agent 未绑定时的能力求交测试 | 自动化通过 |
| 3 | 显式 Skill token 激活、剥离、顺序与限制测试 | 解析自动化通过；chip 与真实回合待手工 |
| 4 | MCP provider namespace、稳定 hash、原始调用名与来源 DTO 测试 | 后端自动化通过；工具卡片显示待手工 |
| 5 | `ApprovedAIFunction` 放行/审批/拒绝及来源传播测试 | 自动化通过；真实审批窗口与模型续跑待手工 |
| 6 | MCP config revision lease、共享连接取消隔离、drain 与 Plugin 多版本 drain/delete 测试 | 自动化通过；活动真实回合待手工 |
| 7 | secret 三态保存、WebView 屏蔽、解密失败脱敏测试 | 自动化通过；桌面日志抽查待手工 |
| 8 | 真实 Windows stdio 父子进程退出 fixture | dispose 与初始化期间取消自动化通过；关窗/退出待手工 |
| 9 | 原 `Plugins.vue` 已删除，组件拆分、composable、scoped style 与生产编译检查 | 结构与编译通过；视觉待手工 |
| 10 | Direct/CLI 统一事件契约的全量回归 | 自动化通过 |
| 11 | 真实 SDK `isError`、文本/结构化结果映射和截断测试 | 自动化通过；真实模型回合待手工 |
| 12 | 文件选择取消的 correlated result 与 bridge 无超时路径测试 | 自动化通过；原生对话框长时间停留待手工 |
| 13 | mutation/回合期 health 共用 notifier、并发 revision、订阅异常隔离与 bridge 测试 | 宿主自动化通过；打开页面实时刷新待手工 |
| 14 | 历史 token 剥离、最新消息激活和 per-turn capability lease 测试 | 自动化通过 |

## 任务总览

| ID  | 任务                                                                           | 阶段 | 依赖          | 规模 |
| --- | ------------------------------------------------------------------------------ | ---- | ------------- | ---- |
| T1  | Core 扩展契约 + `AgentRuntimeDefinition` 改形                                  | P0   | —             | M    |
| T2  | SQLite schema v22 + `SqliteExtensionRepository`                                | P0   | T1            | M    |
| T3  | `DesktopAgentDefinitionService`（纯 DTO / plugins / 迁移 / 原子写）            | P0   | T1            | M    |
| T4  | `ExtensionSettingsService`（state / 启停 / 删除 / save-mcp / 密钥规则）        | P0   | T1-T3         | L    |
| T5  | `ExtensionSettingsBridge` + MainWindow/App 接线 + state-changed 推送           | P0   | T4            | M    |
| T6  | `Plugins.vue` 拆分：组件骨架 + `useExtensionSettings` 接真数据                 | P0   | T5            | L    |
| T7  | `McpServerDialog` 结构化表单（args / env / secret 语义）                       | P0   | T6            | M    |
| T8  | P0 手动验收                                                                    | P0   | T1-T7         | S    |
| T9  | 包导入管线：installer + `SkillPackageReader` + import-package                  | P1   | T2, T5        | L    |
| T10 | `IDirectTurnCapabilityResolver` 骨架 + Direct runtime 等价改造                 | P1   | T1            | L    |
| T11 | Skill 有效集 + 显式 token + `MessageAdjustments` + prompt 组装                 | P1   | T9, T10       | M    |
| T12 | `SkillRuntimeToolset`（activate_skill / read_skill_resource）                  | P1   | T11           | M    |
| T13 | Composer `SkillPicker` + replaceState 扩展                                     | P1   | T5, T11       | M    |
| T14 | P1 手动验收                                                                    | P1   | T9-T13        | S    |
| T15 | `McpConfigurationResolver` + `McpTransportFactory`（env 基线 / 解密 / stderr） | P2   | T2, T4        | M    |
| T16 | （spike 前置）stdio 进程树验证 + `McpClientManager` + test-mcp 实装            | P2   | T15           | L    |
| T17 | 事件 / 审批 / 落库契约扩展（来源字段贯通 Desktop）                             | P2   | T2            | M    |
| T18 | `ApprovedAIFunction` + MCP 工具装配 + §9.5 结果映射                            | P2   | T10, T16, T17 | L    |
| T19 | 前端 provenance + 审批来源 + activity `Detail`                                 | P2   | T17, T18      | M    |
| T20 | P2 手动验收                                                                    | P2   | T15-T19       | S    |
| T21 | Plugin manifest + 版本化安装（versions / current.json）                        | P3   | T9            | L    |
| T22 | Plugin 贡献展开 + 权限确认门控（acknowledged）                                 | P3   | T21, T11, T16 | L    |
| T23 | Plugin 生命周期（update / delete / drain / reconcile）+ P3 验收                | P3   | T22           | M    |
| T24 | 文档勘误 + dead code 清理 + 全量回归                                           | P4   | T1-T23        | M    |

依赖主线：T1 → {T2, T3} → T4 → T5 → T6 → T7（P0 关键路径）；**T10 只依赖 T1，可与 P0 的设置页任务并行**；T9+T10 → T11 → {T12, T13}（P1）；T15 → T16 → T18 → T19（P2）；T21 → T22 → T23（P3）。T16 的 spike（进程树验证）应在 P2 开工首日先做，其结论决定 transport 实现形态。

---

## P0 —— 契约与持久化地基

### T1 Core 扩展契约 + `AgentRuntimeDefinition` 改形

**目标**：Core 层扩展契约成型；`AgentRuntimeDefinition` 换新形；删除 `AgentMcpServerDefinition`。（§5.1、§11.1、§15）

**改动文件**

- `SelfClaw.Core\Models\Extensions\`：新建 `ExtensionKind.cs`、`ExtensionItemKey.cs`、`ExtensionPackageRecord.cs`、`McpServerConfigRecord.cs`、`McpTransportKind.cs` 及 §11.1 的 state/command/view record（每文件一个 DTO，遵守 AGENTS.md DTO 约束）
- `SelfClaw.Core\Interfaces\Extensions\`：`IExtensionPackageRepository.cs`、`IMcpServerRepository.cs`、`IExtensionSettingsService.cs`（§11.1 签名）
- `SelfClaw.Core\Runtime\AgentRuntimeDefinition.cs`：`Skills/McpServers/ConfiguredMcpServers` → `PluginIds/SkillIds/McpServerIds`（§5.1）
- 删除 `SelfClaw.Core\Runtime\AgentMcpServerDefinition.cs`

**步骤**

1. ⚠ `AgentRuntimeDefinition` 的构造点只有两处：`SelfClaw.Desktop\Services\Agents\DesktopAgentDefinition.cs` 的 `ToRuntimeDefinition()` 与 `SelfClaw.Desktop\ViewModels\MainWindowViewModel.Agents.cs:38` 的 `ResolveRuntimeAgent()`。本任务只做最小修补（`PluginIds` 传 `[]`，沿用现有 `EnabledSkills`/`EnabledMcpServers`），完整重构留给 T3。
2. `McpServerConfigRecord.settings_json` 形状按 §9.1 的 Stdio/Http 分支建模；秘密只允许出现为 SecretRef 字段名（§6.2 约束）。
3. `IExtensionSettingsService.TestMcpServerAsync` 本阶段允许实现方抛 `NotSupportedException`（T16 实装），接口一次定全，避免 P2 改签名。

**测试**：无新行为；编译 + 既有测试全绿。

---

### T2 SQLite schema v22 + `SqliteExtensionRepository`

**目标**：schema 21 → 22；`extension_packages`、`mcp_server_configs` 两张新表 + `tool_runs` 三列；扩展仓储。（§6.2）

**改动文件**

- `SelfClaw.Infrastructure\Data\Sqlite\SqliteDatabase.cs`：`CurrentSchemaVersion` 21 → 22（⚠ 文件头 `private const int`，另有 `SelfClaw.Tests\Infrastructure\Data\Sqlite\Repositories\SqliteRepositoriesTests.cs` 的版本断言要同步 21 → 22）；两表 DDL 按 §6.2 全列落地（含 `acknowledged_permissions_json` / `acknowledged_at_utc`，P3 才消费但列一次到位）；`EnsureColumnExistsAsync(tool_runs, source_kind / source_id / display_name)`
- `SelfClaw.Infrastructure\Data\Sqlite\Repositories\SqliteExtensionRepository.cs`：新建，同一实现类分别实现 `IExtensionPackageRepository` 与 `IMcpServerRepository`（§15 DI）
- `SelfClaw.Infrastructure\Data\Sqlite\SqliteMappings.cs`：新记录映射
- `SelfClaw.Infrastructure\DependencyInjection\ServiceCollectionExtensions.cs`：注册仓储（singleton，两接口指向同实例）

**步骤**

1. `tool_runs` 三列为 additive 可空列，无需重建表（现有 FK/级联不动）。
2. `mcp_server_configs.config_revision` 默认 1；仓储在 upsert 时对配置内容变化自动 +1（pool key 依赖它，§9.2）。
3. 仓储提供 `InitializeAsync()`，模式与 `SqliteAiProviderRepository` 一致。

**测试**

- `SqliteRepositoriesTests`：版本断言 22；两表 round-trip、`config_revision` 递增、tool_runs 三列读写。
- 迁移测试：预置带 conversations/messages/tool_runs 数据的 v21 库跑初始化 → 数据保留、新表出现、旧行三列为 NULL。（§16.3）

---

### T3 `DesktopAgentDefinitionService`（纯 DTO / plugins / 迁移 / 原子写）

**目标**：Agent 定义模块重构为"纯 DTO + 服务"，新增 `plugins`，`disabled*` 一次性兼容迁移，删除文件系统旁路。（§5.2、§1.3）

**改动文件**

- `SelfClaw.Desktop\Services\Agents\DesktopAgentDefinition.cs`：删 `HasWarnings` / `EnabledSkills` / `EnabledMcpServers` / `ToRuntimeDefinition()` / `FilterEnabledServices`，字段改为 `PluginIds/SkillIds/McpServerIds`（纯 record）
- `SelfClaw.Desktop\Services\Agents\DesktopAgentStore.cs` → `DesktopAgentDefinitionService.cs`：改名，承担解析 / 序列化 / 保存 / 绑定变更（§15）
- `SelfClaw.Desktop\ViewModels\MainWindowViewModel.Agents.cs`：`ResolveRuntimeAgent()` 直接投影新 DTO
- `SelfClaw.Desktop\App.xaml.cs`：DI 注册随改名更新

**步骤**

1. 解析器新增 `plugins` 列表 key。⚠ 现 parser 对未知 key 记 warning（`Ignoring unsupported front matter key`），先加解析分支，否则旧版本写出的 `plugins` 会被当告警。
2. `disabledSkills` / `disabledMcpServers` 兼容读取：存在即求 `selected − disabled` 作为有效值（§5.2.3）；下次保存只写 `plugins/skills/mcpServers`。
3. ⚠ 序列化只在列表非空时写 key（现实现总是写空列表头，§1.3）；写入改为"同目录临时文件 + `File.Replace`"（同卷保证原子性，§5.2.6）。
4. 删除 `DiscoverInstalledSkillIds()` 与"Skill 未安装"warning（安装状态归 catalog / resolver，§5.2.7）；`Warnings` 仅保留解析类告警。
5. 内建 `build` agent 播种与 `BuildAgentId` 常量保持不变；⚠ 全仓 grep `DesktopAgentStore` 引用（`MainWindowViewModel`、App DI）随改名更新。

**测试**：新增 `SelfClaw.Tests\Desktop\Services\Agents\DesktopAgentDefinitionServiceTests.cs`（Desktop 已被测试工程引用）：`plugins` 解析、`disabled*` 迁移语义、只写非空列表、多段 skill id（`a/b`）round-trip、写失败不破坏原文件、`build` 播种。

---

### T4 `ExtensionSettingsService`

**目标**：设置门面落地：状态视图、启停、删除、MCP 配置保存与密钥规则。（§11.1、§6.2 约束、§5.3）

**改动文件**

- `SelfClaw.Infrastructure\Extensions\ExtensionSettingsService.cs`：新建实现
- `SelfClaw.Infrastructure\Extensions\ExtensionCatalog.cs`：新建（本阶段只做"数据库记录 + 安装目录存在性"的只读目录视图；staging/导入在 T9）
- `SelfClaw.Infrastructure\DependencyInjection\ServiceCollectionExtensions.cs`：注册

**步骤**

1. `GetStateAsync`：按 §11.4 形状聚合 `revision`、agents（含绑定）、plugins / skills / mcpServers 三列表；`assignedAgentIds` 由 Agent 绑定反查。状态取值 ready / disabled / needs-config / broken（connecting / degraded 属 P2 起）。
2. 密钥规则（集中此处，对齐 `AiProviderSettingsService` 先例）：secret 留空不动；`clearSecret=true` → `DeleteSecretAsync` + 移除 ref；新值 → `StoreSecretAsync(plain, existingRef)` 原位替换。⚠ 删除配置先枚举 `credential_refs_json` 逐个 `DeleteSecretAsync` 再删行（数据库级联删不到 `.bin` 文件）。
3. `SaveMcpServerAsync` 后端二次校验（§11.3）：`arguments` 必须 array；endpoint 仅 `http/https` 绝对 URI，公网 http 拒绝、localhost http 放行；环境键名合法性。⚠ 不信任 Vue 校验。
4. 视图脱敏：secret 只出 `hasSecret=true`；`settings_json` 原文不下发（§6.2）。
5. `revision`：任何成功 mutation +1，随所有响应返回。
6. `SetEnabledAsync` 对 Plugin 的 acknowledged 门控在 T22 补；本阶段 Skill/MCP 直接启停。

**测试**（fake repository + fake protector，对齐 `AiProviderSettingsServiceTests` 模式）：密钥三态与原位替换、删除先删密钥、endpoint/args 校验拒绝、state 聚合与 `assignedAgentIds`、revision 递增。

---

### T5 `ExtensionSettingsBridge` + 接线 + state-changed

**目标**：`extensions/*` 消息全通；启动初始化；Desktop → Vue 推送。（§11.2、§11.3、§15「DI 与启动接线」）

**改动文件**

- `SelfClaw.Desktop\Services\Extensions\ExtensionSettingsBridge.cs`：新建，逐条对照 `AiProviderSettingsBridge`（前缀守卫、requestId 回显、`OperationCanceledException` 重抛、异常 → `{ type, requestId, error }`）
- `SelfClaw.Desktop\Services\WebView\WebViewMessageRouter.cs`：按固定顺序调用 `_extensionSettingsBridge.TryHandleAsync()`；非空响应通过 `WebViewHostChannel.PostResponse()` 回包
- `SelfClaw.Desktop\App.xaml.cs`：注册 bridge 单例；启动序列在 `IAiProviderRepository.InitializeAsync()` 之后追加扩展仓储 `InitializeAsync()`（§1.4.7；reconcile 在 T9 加）

**步骤**

1. 本阶段实现消息：`get-state` / `set-enabled` / `delete` / `save-mcp` / `set-agent-binding`；`test-mcp` 转发服务层 `NotSupportedException` 为可读错误；`import-package` / `list-effective-skills` 留 T9 / T13。
2. `set-agent-binding` 由 bridge 协调 `IExtensionSettingsService`（校验目标存在）与 `DesktopAgentDefinitionService`（原子写 markdown），Infrastructure 不反向依赖 Desktop（§11.1）。
3. `IExtensionStateChangeNotifier.StateChanged(revision)` 事件：任何成功 mutation 触发；`WebViewMessageRouter` 直接订阅 notifier 后 (a) 推送 `{ type: "extensions/state-changed", revision }`，(b) 通知 `MainWindowViewModel` 记录 `capabilityRevision`。（§11.2）

**测试**：`SelfClaw.Tests\Desktop\Services\Extensions\ExtensionSettingsBridgeTests.cs`（对齐 `AiProviderSettingsBridgeTests`）：每消息 requestId 回显、错误 shape、secret 不出现在响应、set-agent-binding 写通 markdown、StateChanged 触发。

**验收**：WebView2 devtools 手发 `{type:'extensions/get-state', requestId:'x'}` 收到状态回包。

---

### T6 `Plugins.vue` 拆分 + `useExtensionSettings` 接真数据

**目标**：删 mock，按 §12.1 拆组件，三 tab 状态 / 启停 / 删除 / 绑定走桥接。（§12.1、§12.2、§12.5）

**改动文件**

- `SelfClaw.TranscriptVue\src\components\settings\extensions\`：`ExtensionSettingsPanel.vue`、`ExtensionCategoryTabs.vue`、`ExtensionToolbar.vue`、`ExtensionList.vue`、`ExtensionListItem.vue`、`ExtensionDetailDrawer.vue`、`ExtensionStatusBadge.vue`、`AgentBindingsEditor.vue`
- `SelfClaw.TranscriptVue\src\composables\useExtensionSettings.js`：state、加载、mutation、错误 / 并发控制（`requestLatest` 防抖，`SupersededError` 静默）
- `SelfClaw.TranscriptVue\src\views\SettingsView.vue`：asyncComponents 映射 `plugins` → `ExtensionSettingsPanel.vue`（nav id/label 不动）；删除或清空旧 `Plugins.vue`

**步骤**

1. `useExtensionSettings`：`onMounted` → `extensions/get-state`；订阅 `extensions/state-changed` 重新拉取；mutation 期间禁用对应行，成功采用响应 revision，失败回滚 + 错误条（§12.2 不做假成功 toast）。
2. 状态徽章映射 ready / disabled / needs-config / broken；`已绑定 N 个 Agent` 与全局启用 switch 分开展示（§5.3）。
3. `AgentBindingsEditor`：详情抽屉内按 agent 列 checkbox → `extensions/set-agent-binding`；内建 `build` 可改绑定但标识内建、不可删（§12.5）。
4. ⚠ mock 数据、`.odplugin/.odskill` 文案、drag/drop 逻辑全部删除（§7：该扩展名不是仓库协议；导入交互 T9 重建）。
5. 每组件 `<style scoped>` + `@import settings-console.css`；lucide 图标（AGENTS.md 前端约束）。

**验收**：设置页三 tab 显示真数据（空态正常）；启停 / 删除 / 绑定即时生效且重启保持；`npm run build` 通过。

---

### T7 `McpServerDialog` 结构化表单

**目标**：替换 raw textarea，落地 §12.3 表单与 §11.3 save-mcp 语义。

**改动文件**

- `SelfClaw.TranscriptVue\src\components\settings\extensions\McpServerDialog.vue`、`McpSecretFields.vue`
- `SelfClaw.TranscriptVue\src\composables\useMcpServerForm.js`：表单归一化和校验

**步骤**

1. stdio：command 单字段 + arguments 可增删逐项列表（保留含空格参数）+ workingDirectoryMode + `requiresWorkspace`；HTTP：endpoint + transportMode + connectionTimeout。
2. env/header key/value 行，每行可标记 secret；已有 secret 显示"已配置"，留空保留、显式清除才发 `clearSecret=true`（§11.3）。
3. 非 HTTPS 远端地址显示高风险提示；保存不假定连接成功，"保存并测试"按钮在 P0 禁用（tooltip 说明 P2 可用）。
4. 前端校验只做即时反馈，提交后以后端错误为准回显。

**验收**：新建 / 编辑 stdio 与 HTTP 配置各一条，重启后配置保持、secret 显示"已配置"且 devtools 网络消息中无明文。

---

### T8 P0 手动验收

对照设计 §17-P0 验收，真实桌面逐条过：

1. 新建 MCP 配置（带 env secret）→ 重启 → 状态保持、secret 掩码；`%LocalAppData%\SelfClaw\secrets\` 出现对应 `.bin`；删除配置后 `.bin` 消失。
2. Agent 绑定 Skill/MCP → `agents\build.md` 只出现 `plugins/skills/mcpServers` key（无 `disabled*`、无空列表头）；手工构造含 `disabledSkills` 的旧 markdown → 载入后有效值正确、再保存后 `disabled*` 消失。
3. 日志 / WebView JSON / markdown 全程无 secret 明文。
4. 既有对话、Direct/CLI 回合完全不受影响（本阶段 runtime 未动）。

---

## P1 —— Skills 端到端

### T9 包导入管线：installer + `SkillPackageReader` + import-package

**目标**：staging → 校验 → 原子落位 → 落库的通用导入管线（本阶段消费方为 Skill 包）；原生文件选择器导入闭环；启动 reconcile。（§6.3、§7.1、§8.1、§12.4）

**改动文件**

- `SelfClaw.Infrastructure\Extensions\ExtensionPackageInstaller.cs`：staging 解包（`%LocalAppData%\SelfClaw\staging\extensions\<operation-id>`）、防护、原子移动、事务 upsert、失败清理
- `SelfClaw.Infrastructure\Extensions\Skills\SkillPackageReader.cs`：`SKILL.md` 读取校验（name/description/version/triggers，正文原样，§8.1）
- `SelfClaw.Infrastructure\Extensions\ExtensionCatalog.cs`：+`ReconcileAsync()`（目录丢失标 broken、孤立 staging 清理，§6.3.6）
- `SelfClaw.Desktop\Services\Extensions\ExtensionPackagePicker.cs`：原生 `OpenFileDialog`（过滤 `.zip` / `.selfclaw-skill` / `SKILL.md`）
- bridge / `App.xaml.cs`：`extensions/import-package` 实装；启动序列加 `ReconcileAsync()`
- `SelfClaw.TranscriptVue`：`PackageImportDialog.vue`（导入结果摘要：manifest、hash、文件数）

**步骤**

1. 防护清单逐条实现并逐条测试（§7.1）：Zip Slip / absolute path / `..`、NTFS ADS、reparse point/symlink、大小与数量上限、重复大小写路径、解压后越界。
2. 安装布局：`skills\<skill-id>\SKILL.md`（§6.1）；skill id 归一化沿用 `NormalizeSkillId` 语义（多段允许）。
3. ⚠ 前端 `import-package` 请求必须显式传大超时（hostBridge 默认 30s，用户挑文件会超时，§11.2）；用户取消 → `{ ok:false, cancelled:true }`。
4. 导入后默认 disabled（§7）；响应携带最新 revision。

**测试**（§16.3）：全部防护路径、invalid `SKILL.md` 不落库不留残目录、staging 失败恢复、reconcile 标 broken / 清 staging、导入成功 round-trip。

---

### T10 `IDirectTurnCapabilityResolver` 骨架 + Direct runtime 等价改造

**目标**：seam 先行落地：resolver / lease / composer 三件套 + `DirectAgentChatRuntime` 换依赖，**行为与现状严格等价**（只产出 workspace 工具与 Agent instructions），Skill/MCP 后续叠加。（§0、§4、§10.1）

**改动文件**

- `SelfClaw.Infrastructure\Extensions\Runtime\DirectTurnCapabilityResolver.cs`、`DirectTurnCapabilityLease.cs`、`DirectPromptComposer.cs`
- `SelfClaw.Infrastructure\Extensions\Abstractions\IDirectTurnCapabilityResolver.cs`
- `SelfClaw.Infrastructure\Agents\Runtime\DirectAgentChatRuntime.cs`：ctor 依赖 `WorkspaceAgentToolset` → `IDirectTurnCapabilityResolver`；消息组装换 `DirectPromptComposer`；finally 中先 dispose provider lease 再 `DisposeAsync()` capability lease（§10.1 释放顺序）
- `SelfClaw.Infrastructure\DependencyInjection\ServiceCollectionExtensions.cs`：注册 resolver

**步骤**

1. lease 形状按 §4.2（`SystemInstructions` / `Tools` / `ToolDescriptors` / `MessageAdjustments` / `Diagnostics` / `DisposeAsync`）；本阶段 `SystemInstructions` 为空、`MessageAdjustments` 为空、descriptor 覆盖 7 个内建工具。
2. ⚠ `DirectPromptComposer` 逐行继承现 `BuildMessages()` 行为：跳过 `Failed` **和 `Cancelled`**、跳过空文本、仅 User/Assistant（§1.4.1）；纯函数无 IO。
3. resolver 失败 → runtime 输出失败终态、不创建 provider client（§10.3）。
4. ⚠ `DirectAgentChatRuntimeTests` 改注入 fake resolver（§16.4）；`WorkspaceAgentToolsetTests` 原样保留（路径 / 审批职责不动）。

**测试**：fake resolver 下工具与 instructions 进入 client、resolver 失败不建 client、cancellation 不转失败、dispose 顺序（provider 先于 capability，可用记录式 fake 断言）。

**验收**：Direct 全链路手动回归一轮（对话 + 工具 + 审批），行为与改造前一致。

---

### T11 Skill 有效集 + 显式 token + `MessageAdjustments` + prompt 组装

**目标**：resolver 真正装配 Skill：有效集计算、显式激活、token 剥离、system section 顺序。（§5.3、§8.2-§8.4）

**改动文件**

- `DirectTurnCapabilityResolver.cs`：有效集 = `Agent.SkillIds ∩ 已安装 ∩ 全局启用`；显式 token 解析
- `DirectPromptComposer.cs`：§8.3 六段顺序拼装（capability policy 固定文案 + plugin 段留 T22）
- 新 `SelfClaw.Infrastructure\Extensions\Skills\SkillTokenParser.cs`（或并入 resolver 私有方法，按体量定）

**步骤**

1. token 语法（§8.2）：`[a-z0-9-]+`，最多一个 `/`，总长 ≤ 64；⚠ 后端语法必须是前端 chip 正则 `\[\/([^\]\r\n]{1,80})\]` 的子集，不得使用 `]`、换行。
2. 显式激活：只匹配最新 user message；≤ 3 个去重；未知 / 未安装 / 停用 / 未绑定 → 回合在 provider 调用前失败，错误指明具体原因（§8.2.5）。
3. `MessageAdjustments`：最新 user message 剥离已消费 token；历史 user 消息剥离"匹配本轮有效 Skill"的 token，无法匹配的历史 token 保留且不失败（§8.4.2）。⚠ 数据库与 Vue 原文不动，只改发给模型的副本。
4. 激活 Skill 的 `SKILL.md` 注入为带来源边界标题的 system section，按 token 顺序（§8.3.4）；激活列表记入 `Diagnostics`（§8.4.4）。
5. 跨回合语义按 §8.4.1：不引入会话级激活状态。

**测试**（§16.1）：交集计算、空绑定不启用全局项、token 边界（3 个上限 / 重复去重 / 未知失败 / 语法拒绝）、`MessageAdjustments` 三类剥离行为、section 顺序与边界标题、CLI 请求不受影响（dispatcher 分支回归）。

---

### T12 `SkillRuntimeToolset`（activate_skill / read_skill_resource）

**目标**：按需激活双工具 + 紧凑目录注入。（§8.2 按需激活）

**改动文件**

- `SelfClaw.Infrastructure\Extensions\Skills\SkillRuntimeToolset.cs`：两个 `AIFunctionFactory.Create` 工具
- `DirectTurnCapabilityResolver.cs`：目录 section + 工具注册

**步骤**

1. 紧凑目录只含 id/name/description/triggers；Agent 无有效 Skill 时目录与两工具都不注册（§8.2）。
2. `activate_skill`：返回全文；每轮显式 + 按需合计 ≤ 5，超限返回说明文本；对已激活 id 幂等返回"已激活"（§8.2）。
3. `read_skill_resource`：路径归一化限制在该 Skill 根目录内（复用 installer 的路径防护语义）、文本扩展名白名单、分页与大小上限（§13.4）。
4. 两工具不走审批，但作为普通 function call 自然产生 tool run 记录（§8.2）；工具名进入 reserved names 清单（§9.3.1）。

**测试**：路径逃逸拒绝、白名单、分页、上限与幂等、未激活 Skill 的 resource 访问拒绝、无 Skill 时零注入。

---

### T13 Composer `SkillPicker` + replaceState 扩展

**目标**：composer 插入入口 + agent 身份 / 能力 revision 贯通前端。（§12.6、§1.2.10）

**改动文件**

- `SelfClaw.Desktop\Services\Transcript\TranscriptRenderState.cs`：+`SelectedAgentId` / `SelectedAgentName` / `CapabilityRevision`
- `SelfClaw.Desktop\ViewModels\MainWindowViewModel.cs`：`PublishShell()` 填充（T5 已存 revision 字段）
- bridge：`extensions/list-effective-skills` 实装（入参可选 agentId，缺省用当前选中 agent）
- `SelfClaw.TranscriptVue\src\views\ChatView.vue`：`replaceState()` 读入三字段并下传
- `SelfClaw.TranscriptVue\src\components\Chat\SkillPicker.vue`：新建；`ComposerPanel.vue` 工具栏接入

**步骤**

1. picker 打开时 `requestLatest('effective-skills', 'extensions/list-effective-skills', { agentId })`；缓存按 `(agentId, capabilityRevision)` 失效（§12.6）。
2. 选择后在 textarea 光标处插入 `[/skill-id] `；发送 / 渲染链路不改（chip 渲染已存在）。
3. 生效模式为 CLI 时隐藏入口（`agentMode` 已在 state；⚠ 模式是 per-send 覆盖结果，切 CLI 后已输入 token 原样透传，§8.4.3，不做拦截）。
4. ⚠ 顺手把 `ComposerPanel.vue` 的非 scoped 样式收口（§1.3）。

**验收**：Direct 模式下 picker 只列当前 agent 有效 Skill；设置页改绑定后（state-changed → revision 变化）picker 下次打开即刷新；CLI 模式无入口。

---

### T14 P1 手动验收

对照设计 §17-P1 验收：

1. 绑定 Skill 后：picker 插入 `[/skill-id]` 发送 → 本轮回答可见 Skill 指令生效；同轮模型可 `activate_skill` 另一个目录内 Skill。
2. 未绑定 / 已停用 Skill：picker 不列出；手打 token → 回合失败且错误可读；历史消息中的旧 token 不影响后续回合。
3. 下一轮不带上一轮激活内容（观察 usage 输入 token 回落）。
4. CLI 覆盖模式：token 原样到达 CLI，无解析报错。

---

## P2 —— MCP 端到端

### T15 `McpConfigurationResolver` + `McpTransportFactory`

**目标**：配置 → 可连接 transport 的解析层：秘密解密、workspace 解析、stdio 环境基线、stderr 接线。（§9.1、§13.3）

**改动文件**

- `SelfClaw.Infrastructure\Extensions\Mcp\McpConfigurationResolver.cs`：`McpServerConfigRecord` → `ResolvedMcpServerConfiguration`（解密后的 env/header 只存在于本轮内存；`requiresWorkspace` 无 workspace → 标记不可用）
- `SelfClaw.Infrastructure\Extensions\Mcp\McpTransportFactory.cs`：Stdio/Http 分支

**步骤**

1. ⚠ stdio 环境（§9.1 事实修正）：`InheritEnvironmentVariables = false` 后子进程环境**完全为空**（SDK 1.4.0 无"安全默认集合"）。工厂自维护 Windows 基线：`SystemRoot`、`windir`、`ComSpec`、`PATHEXT`、`TEMP`/`TMP` + 受控 `PATH`，再叠加用户显式配置。
2. stderr 经 `StdioClientTransportOptions.StandardErrorLines` 回调进限长诊断缓冲（复用 CLI 侧 64 KiB 环形语义）；`ShutdownTimeout` 用 SDK 默认 5s。
3. HTTP：`HttpClientTransportOptions` 填 endpoint / TransportMode（AutoDetect 默认）/ AdditionalHeaders / ConnectionTimeout；OAuth 字段不触碰（§2.2）。
4. 解密失败 → 该 server 标不可用，错误文本不含 secret/ref（§14.2）。

**测试**：基线环境集合内容、用户配置覆盖顺序、解密失败降级文本脱敏、requiresWorkspace 判定。

---

### T16 （spike 前置）stdio 进程树验证 + `McpClientManager` + test-mcp 实装

**目标**：连接池 / lease / 生命周期 / 健康检查。（§9.2）

**⚠ spike 先行（0.5 天，产出决定实现）**：真实 Windows stdio fixture（测试用子进程再拉孙进程）验证 SDK 1.4.0 dispose 是否终止完整进程树；不能则 stdio adapter 改为 Windows Job Object 托管的 `IClientTransport` 实现，差异封在 transport 内（§9.2）。结论写回设计文档 §9.2。

**改动文件**

- `SelfClaw.Infrastructure\Extensions\Mcp\McpClientManager.cs`（实现 `IMcpClientManager` + `IAsyncDisposable`）
- `SelfClaw.Infrastructure\Extensions\Abstractions\IMcpClientManager.cs`
- `ExtensionSettingsService.TestMcpServerAsync`：移除 T4 stub，走 `TestAsync`
- DI：singleton 注册（⚠ Generic Host `DisposeAsync` 自动释放 IAsyncDisposable 单例，App.xaml.cs 无需新增代码，§15）

**步骤**

1. pool key = `server id + config revision + resolved workspace path`；首次 `AcquireAsync` 懒连接（`McpClient.CreateAsync`）+ `ListToolsAsync()`；lease 引用计数，归零后 idle 5 分钟关闭（§9.2）。
2. 配置变更 → 旧 entry draining，新回合拿新 revision；`TestAsync` = 连接 + `PingAsync()` + `ListToolsAsync()`，并把 health / `discovered_tools_json` 落库、触发 `StateChanged`。
3. cancellation 全链传播 `OperationCanceledException`（§14.1）；应用退出 graceful shutdown 短超时后 kill-tree。
4. 前端"保存并测试"按钮解禁（T7 预留），health / tool 数量 / 最近检查时间上屏（§12.2）。

**测试**（§16.2）：in-memory fake transport 全生命周期、pool key 隔离、并发 Acquire 单连接、idle/draining/shutdown、真实 stdio fixture 的启动 / JSON-RPC / 取消 / kill-tree（不依赖外网）。

---

### T17 事件 / 审批 / 落库契约扩展

**目标**：来源三元组（SourceKind/SourceId/DisplayName）+ `Detail` 贯通 Core 事件 → Desktop 落库 → 审批载体。（§10.2、§10.3、§9.4 载体）

**改动文件**

- `SelfClaw.Core\Runtime\Agent\ToolSourceKind.cs`：新建（BuiltIn / Mcp / Skill / Plugin）
- `SelfClaw.Core\Runtime\Agent\ToolCallStartedEvent.cs`：+3 个带默认值可空字段（§10.2 形状）
- `SelfClaw.Core\Runtime\Agent\RunStatusEvent.cs`：+`string? Detail = null`
- `SelfClaw.Core\Runtime\Approvals\ToolApprovalRequest.cs`：+`SourceKind` / `SourceId` / `TransportSummary` / `AnnotationsJson`（均默认值）
- `SelfClaw.Core\Models\Tooling\ToolExecutionRecord.cs`：+3 字段（默认值，⚠ 该 record 已有两个构造重载，同步补齐）
- `SelfClaw.Desktop\Services\Runtime\ConversationTurnEngine.cs`：`StartToolRunAsync` 透传来源
- `SelfClaw.Infrastructure\Data\Sqlite\Repositories\SqliteConversationRepository.cs` + `SqliteMappings.cs`：三列读写
- `SelfClaw.Desktop\MainWindow.xaml.cs`：`toolApprovalRequest` payload 透传新字段

**步骤**

1. ⚠ 全部新字段带默认值，现有构造点（Direct runtime、三个 CLI parser、workspace 审批）零改动编译通过。
2. `DirectAgentChatRuntime`：`FunctionCallContent` → 查 `ToolDescriptors` 填来源；descriptor 未命中回落 BuiltIn/null。
3. 历史回合读取：v22 前旧行三列 NULL → 按内建展示（§10.2）。

**测试**：`ConversationTurnEngineTests` 来源透传与落库 round-trip；事件默认值兼容（既有测试不改即绿）。

---

### T18 `ApprovedAIFunction` + MCP 工具装配 + §9.5 结果映射

**目标**：resolver 装配 MCP 工具全链：重命名、descriptor、审批包装、结果映射、降级。（§9.3-§9.5、§10.3）

**改动文件**

- `SelfClaw.Infrastructure\Extensions\Runtime\ApprovedAIFunction.cs`：通用审批 wrapper（AIFunction 装饰器）
- `SelfClaw.Infrastructure\Extensions\Mcp\McpToolAdapter.cs`：重命名 + descriptor + 结果映射
- `DirectTurnCapabilityResolver.cs`：MCP 有效集（§5.3）→ `AcquireAsync` → `ListTools` → 装配；lease `DisposeAsync` 释放全部 MCP lease

**步骤**

1. 重命名用 SDK 原生 `McpClientTool.WithName()` / `WithDescription()`（克隆实例仍调原始 tool name，§9.3）；命名规则 `mcp__<server-slug>__<tool-slug>`，超长截断 + 8 位稳定 hash；与 reserved names（7 内建 + 2 skill loader）冲突或彼此冲突 → 能力解析失败（§9.3.4）。
2. `ApprovedAIFunction`：hard deny 不注册；FullAccess 放行；RequireApproval 逐次审批，请求携带 T17 来源字段；⚠ 拒绝返回 `WorkspaceAgentToolset.DeniedResult` 同款字符串而非抛异常（§9.4 拒绝语义）。workspace `write_file`/`edit_file`/`run_shell_command` 同步迁移到该 wrapper，`BoundWorkspaceTools.IsApprovedAsync()` 删除（§9.4）。
3. §9.5 结果映射在 wrapper 内：先做 64 KiB 截断（模型侧），再从 `CallToolResult` 提取 text 摘要 / 非文本占位 / `structuredContent` 附加 / `isError` → Failed 事件；⚠ 现 `DescribeToolResult()` 的 `JsonElement` 兜底不适用于 MCP，映射结果经 descriptor 通道交给 runtime 事件（不在 runtime 里加 MCP switch）。
4. 降级（§10.3）：单个可选 server 连接失败 → 跳过 + `Diagnostics` + `RunStatusEvent(Detail)` + health 落库；MCP 全挂不影响 workspace 工具。
5. `MapToolKind()` 退役：kind 全部来自 descriptor（§1.3、§9.3.5）。

**测试**（§16.1、§16.2）：审批三态与来源、拒绝字符串、重命名后调用原始名、冲突失败、结果映射四分支（text / 非文本 / isError / 截断）、单 server 失败其余可用、lease dispose 恰好一次、workspace 工具迁移后 `WorkspaceAgentToolsetTests` 相应收缩。

---

### T19 前端 provenance + 审批来源 + activity `Detail`

**目标**：工具卡片、审批确认栏、活动文案吃到来源与降级信息。（§10.2 投影链、§10.3、§9.4）

**改动文件**

- `SelfClaw.Desktop\Services\Transcript\TranscriptRenderSegment.cs`：+来源字段；`TranscriptToolRunPresenter.BuildToolSegment()` 投影
- `SelfClaw.Desktop\Services\Transcript\TranscriptToolRunPresenter.cs`：⚠ 同步修正漂移的旧工具名 switch（`read_workspace_file` 等 → 实际 7 工具名，§1.3）
- `SelfClaw.Desktop\Services\AgentActivity\AgentActivityCoordinator.cs`：`RunStatusEvent.Detail` 非空时覆盖默认文案（§10.3）
- `SelfClaw.TranscriptVue\src\components\Chat\transcript\ToolCard.vue` / `ToolGroup.vue`：来源副标题（`MCP · git` / `Skill · code-review`）
- `SelfClaw.TranscriptVue\src\views\ChatView.vue`：审批确认栏渲染来源行（toolApprovalRequest 新字段）

**步骤**

1. MCP 卡片主标题用 `DisplayName`（原始 tool name 人类可读形式），副标题来源；summary/detail 来自 §9.5 映射。
2. toast 加一行来源短文案（版面受限，§9.4 载体）。
3. 历史（NULL 来源列）按内建渲染。

**验收**：MCP 工具卡片显示"原始名 + 来源"；审批确认栏能看出来自哪个 server / 命令摘要；MCP 连接失败时活动区出现短提示、对话继续。

---

### T20 P2 手动验收

对照设计 §17-P2 验收：

1. 一个 stdio fixture + 一个 HTTP fixture 被 Direct 模型真实调用。
2. RequireApproval：每次 MCP 调用可允许 / 拒绝；拒绝后模型收到否决结果，回合继续。
3. 取消回合 / 关窗 / 退出应用：任务管理器确认无孤儿 server 进程。
4. 停用 / 修改配置只影响新回合；活动回合正常收尾。
5. secret 全链无明文（含 stderr 日志）；`isError` 工具卡片显示失败但回合成功结束。

---

## P3 —— Plugins

### T21 Plugin manifest + 版本化安装

**目标**：`plugin.json` 解析校验 + `versions/<hash>` 不可变目录 + `current.json` 切换。（§6.1、§6.3、§7）

**改动文件**

- `SelfClaw.Infrastructure\Extensions\Plugins\PluginManifestReader.cs`（新；schema、id 规则、permissions、contributes）
- `ExtensionPackageInstaller.cs`：+plugin 分支（`.selfclaw-plugin` / `.zip`，包根必须有 `plugin.json`）
- `ExtensionPackagePicker.cs`：过滤器扩展

**步骤**

1. manifest 规则逐条落（§7）：id 字符集、arguments 必须 string array、`${pluginRoot}`/`${workspaceRoot}` 仅受控模板展开、全部 entry path resolve 在包根内、禁止 DLL 入口。
2. 版本化：解包校验后移入 `plugins\<id>\versions\<version-hash>\`，SQLite 事务内更新 metadata + `current.json`；失败保旧版本（§6.3）。
3. 导入默认 disabled；响应带 permissions 与 contribution 摘要（供 T22 弹权限确认）。

**测试**（§16.3）：manifest 每条规则的拒绝路径、invalid manifest 不动旧版本、版本切换原子性、模板展开不执行 shell expansion。

---

### T22 Plugin 贡献展开 + 权限确认门控

**目标**：绑定 Plugin 的 instructions / Skills / MCP 进入有效能力集；启用前权限确认。（§3.3、§5.3、§8.3、§13.2、§12.4）

**改动文件**

- `DirectTurnCapabilityResolver.cs` / `DirectPromptComposer.cs`：有效 Plugin 求交（§5.3）→ directInstructions section（按 plugin id 排序，§8.3.3）、贡献 Skill 以 `<plugin>/<skill>` 规范 id 并入、贡献 MCP 模板缺必填值保持 needs-config 不启动（§3.2）
- `ExtensionSettingsService.SetEnabledAsync`：⚠ Plugin 启用门控——`manifest.permissions ⊆ acknowledged_permissions_json` 才放行；不满足返回需确认错误（§6.2 约束）
- `ExtensionSettingsBridge`：确认动作写 `acknowledged_permissions_json/acknowledged_at_utc`
- `SelfClaw.TranscriptVue`：`PermissionReviewDialog.vue`（首次启用与升级新增权限 diff 场景，§12.4/§13.2）；Skills/MCP tab 中贡献项显示"由 `<plugin>` 管理"、不可单独卸载（§12.2）

**步骤**

1. 贡献 Skill 安装状态随 Plugin 版本目录（不复制进独立 skills 目录）；`SkillPicker` / token 校验对贡献 Skill 同样生效。
2. Plugin manifest / 路径 / 工具名安全错误 → 该 Plugin 不进快照并标 broken；本轮显式依赖它则失败（§10.3）。
3. 升级出现新增权限 → `is_enabled` 保持但下一次能力解析前强制重新确认（acknowledged 不满足 → 贡献不进快照 + 设置页黄条）。

**测试**（§16.1）：contribution 展开与 namespace、需 workspace 的贡献 MCP 降级、门控三态（未确认 / 已确认 / 升级后权限扩大）、broken plugin 不进快照。

---

### T23 Plugin 生命周期 + P3 验收

**目标**：update / delete / drain / 启动清理闭环。（§6.3、§13.2、§9.2 draining）

**步骤**

1. update：新版本走 T21 管线 → 权限 diff 确认 → 事务切 `current.json`；活动回合 lease 仍引用旧版本目录，安全。
2. delete：disable → 等待引用归零（MCP drain 复用 T16）→ 删目录与行 → 清理无引用 secret（§13.2）。
3. `ReconcileAsync` 扩展：清理无引用旧 versions 目录与孤立 staging（延迟清理，§6.3.6）。
4. **P3 手动验收**（对照 §17-P3）：导入同时贡献 Skill + MCP 的 Plugin → 权限确认 → 绑定 `build` → 下一回合两类贡献生效；停用 / 解绑后新回合不可见；活动回合不被破坏；升级带新增权限的版本 → 强制重新确认。

---

## P4 —— 收尾

### T24 文档勘误 + dead code 清理 + 全量回归

**目标**：文档对齐实现；删除迁移遗留。（§17-P4）

**步骤**

1. `docs/runtime-execution-flow.md`：修正 7 工具 / 请求拆分两处漂移（设计文档头部"基线勘误"）；补 Direct 能力解析链、审批与来源事件、`extensions/*` 消息族。
2. `AGENTS.md`：schema version 21 → 22、DI Registration 清单（extensions / mcp / resolver / bridge）、"MCP server wiring"从 Dead/Retained 移除、设置页接线状态更新。
3. 设计文档 §9.2 写入 T16 spike 结论；本任务文档标注各任务实施状态。
4. 清理：旧 `Plugins.vue` 残留、`DesktopAgentStore` 旧名遗留引用、被 seam 测试取代的重复用例（§16.4 末段）、`MapToolKind` / 旧工具名 switch 已删的确认。
5. 全量回归：`dotnet test` + `npm run build` + `dotnet build` + `git diff --check`；§18 验收清单 1-14 逐条审计，真实 WPF/WebView2 行为保留为手工验收。

---

## 里程碑验收（对齐设计 §17 / §18）

- **P0 完成**：设置页三类扩展的启停 / 删除 / MCP 配置 / Agent 绑定真实可用且重启保持；secret 全链无明文；Agent markdown 新格式落地且旧 `disabled*` 可迁移；Direct/CLI 现有行为零变化。
- **P1 完成**：绑定 Skill 后显式 token 与按需 activate 在同一 Direct 回合生效；未绑定不可用；token 剥离与跨回合语义符合 §8.4；SkillPicker 随能力 revision 刷新。
- **P2 完成**：stdio + HTTP fixture 可被模型调用；审批 / 拒绝 / 取消 / 降级符合 §9.4-§10.3；无孤儿进程；工具卡片与审批栏显示来源。
- **P3 完成**：Plugin 导入 → 权限确认 → 绑定 → 贡献生效全链路；升级权限扩大强制重确认；停用 / 删除不破坏活动回合。
- **P4 完成**：§18 验收标准 1-14 全绿；文档与 DI / schema 记录一致；无 mock 与迁移遗留。
