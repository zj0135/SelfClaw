# 多 AI 提供商后端 —— 分步实施任务文档

> 配套设计：《ai-provider-system-design.md》v2（2026-07-14）。本文按任务粒度拆解，每个任务可独立提交、独立验证。
> 引用格式：§n 指设计文档章节；文件路径均相对仓根 `e:\git_repo\SelfClaw\`。

## 使用约定

- **完成定义（每个任务通用 DoD）**：`dotnet build` 零警告新增、`dotnet test SelfClaw.Tests` 全绿；涉及前端时 `SelfClaw.TranscriptVue` 内 `npm run build` 通过（脚本名以 package.json 为准）；涉及 UI 的任务附手动验收步骤。
- **提交粒度**：一个任务一个（或一组）提交，提交信息沿用仓内惯例（`feat(providers): …` / `feat(chat): …` / `refactor(data): …`）。
- **规模**：S ≈ 半天内，M ≈ 1 天，L ≈ 2-3 天（含测试）。
- 标注 ⚠ 的步骤是探查中发现的易错点，实施时优先核对。

## 任务总览

| ID | 任务 | 阶段 | 依赖 | 规模 |
|---|---|---|---|---|
| T1 | 领域模型与数据库增量（v20） | P1 | — | M |
| T2 | 内置目录 AiProviderCatalog | P1 | T1 | S |
| T3 | 适配器接口扩展 + OpenAI 系模型列表 + DI 注册 | P1 | T1 | M |
| T4 | AiProviderSettingsService（门面 + 密钥规则 + 合并语义） | P1 | T1-T3 | L |
| T5 | Desktop 桥接与启动接线 | P1 | T4 | M |
| T6 | AIProviders.vue 摘 mock 接真数据 | P1 | T5 | L |
| T7 | P1 手动验收 | P1 | T1-T6 | S |
| T8 | AiProviderHttpClientProvider | P2 | T3 | M |
| T9 | Ollama 适配器 | P2 | T3, T8 | M |
| T10 | Gemini 适配器（兼容层 + 原生列表） | P2 | T3, T8 | M |
| T11 | Azure OpenAI 适配器（手动模型） | P2 | T3, T8 | M |
| T12 | OpenRouter/自定义网关元数据完善 | P2 | T3 | S |
| T13 | （spike，可选）DeepSeek reasoning_content 透出 | P2+ | T3 | M |
| T14 | IAiChatClientFactory | P3 | T3, T8 | M |
| T15 | 工作区工具集 + 审批装饰器 | P3 | — | M |
| T16 | DirectAgentChatRuntime（流式翻译） | P3 | T14, T15 | L |
| T17 | 运行时契约更新与分支接入 | P3 | T16 | M |
| T18 | 审批 UI 恢复 | P3 | — | M |
| T19 | composer 模型选择集成 | P3 | T4, T17 | M |
| T20 | 旧代码删除 + v21 迁移 | P3 | T17, T19 | M |
| T21 | 文档更新与全量回归 | P3 | T20 | S |

依赖主线：T1 → T3 → T4 → T5 → T6（P1 关键路径）；T14+T15 → T16 → T17 → {T19, T20}（P3 关键路径）。T8-T12 彼此独立可并行；T15、T18 可提前做。

---

## P1 —— 设置页打通

### T1 领域模型与数据库增量（v20）

**目标**：枚举扩容、记录加字段、`catalog_id` 列落库、仓储补方法。（§3.1、§3.2、§7）

**改动文件**
- `SelfClaw.Infrastructure\AiProviders\Models\AiProviderKind.cs`：+`GoogleGemini = 4, Ollama = 5, AzureOpenAI = 6`
- `...\Models\AiProviderApiFormat.cs`：+`GeminiGenerateContent = 3, OllamaNative = 4`
- `...\Models\AiProviderAuthKind.cs`：+`None = 1`（⚠ `ApiKey` 保持 0，勿按 v1 稿重编号）
- `...\Models\AiProviderConnection.cs`：+`string CatalogId`（放 `Id` 之后；构造点少，全仓编译错误逐个补）
- `...\Models\AiModelProfile.cs`：+`bool IsEnabled = true`（尾部默认值）
- `...\Models\AiModelDescriptor.cs`：新建（§3.2 形状）
- `SelfClaw.Infrastructure\Data\Sqlite\SqliteDatabase.cs`：`CurrentSchemaVersion` 19 → 20；`EnsureColumnExistsAsync(ai_provider_connections, catalog_id, "... TEXT NOT NULL DEFAULT 'custom'")`
- `...\Data\Sqlite\Repositories\SqliteAiProviderRepository.cs` + `...\SqliteMappings.cs`：连接映射 `catalog_id`、模型档案映射 `is_enabled`（⚠ 列已存在于 v19 表，只缺记录/映射）；新方法 `SetModelProfileEnabledAsync(Guid, bool)`、`SetAllModelProfilesEnabledAsync(Guid connectionId, bool)`、`ListEnabledModelProfilesAsync()`（连接与模型均启用）
- `...\AiProviders\Abstractions\IAiProviderRepository.cs`：上述三方法签名

**测试**
- `SelfClaw.Tests\...\SqliteRepositoriesTests.cs`：⚠ schema 版本断言 `Be(19L)` → `Be(20L)`；catalog_id 读写、is_enabled 读写、启停/枚举新方法、旧库升级（无 catalog_id 列的库跑初始化后默认 `custom`）。

**验收**：`dotnet test` 全绿；用旧版本生成的 dev 数据库启动一次，`ai_provider_connections` 出现 `catalog_id` 列。

---

### T2 内置目录 AiProviderCatalog

**目标**：8 个目录条目的静态数据源。（§3.3）

**改动文件**
- `SelfClaw.Infrastructure\AiProviders\Catalog\AiProviderCatalogEntry.cs`：新建（§3.2 形状）
- `...\Catalog\AiProviderCatalog.cs`：新建静态类，`IReadOnlyList<AiProviderCatalogEntry> Entries` + `GetRequired(string catalogId)`（未知 id 回落 `custom` 条目）

**步骤**
1. 按 §3.3 表逐条填：id/名称/副标题/主题色/Kind/默认端点/协议集/认证/GetApiKeyUrl/SupportsModelListing。
2. ⚠ `azure-openai` 条目 `SupportsModelListing = false`；`ollama` 条目 `AuthKind = None`、默认端点 `http://localhost:11434`。
3. `WellKnownModels` 一律先空列表（§13 开放问题，拉取回填够用）。
4. 单测：条目 id 唯一、`custom` 存在、每条 `DefaultApiFormat ∈ SupportedFormats`。

---

### T3 适配器接口扩展 + OpenAI 系模型列表 + DI 注册

**目标**：`IAiProviderAdapter` 增 2 成员；OpenAI 系（OpenAI/DeepSeek/OpenRouter/custom）能拉模型；Anthropic 能拉模型；适配器与 Registry 进 DI。（§4.1、§4.3、§9）

**改动文件**
- `...\Abstractions\IAiProviderAdapter.cs`：+`bool SupportsModelListing { get; }`、+`ListModelsAsync(connection, secrets, ct)`
- `...\OpenAi\OpenAiProviderAdapter.cs`：ctor 守卫放开 `AiProviderKind.DeepSeek`（⚠ 现只允许 OpenAI/OpenAICompatible；`AiProviderRegistryTests` 里"DeepSeek 未注册即抛"的用例要改）；实现 `SupportsModelListing => true` + 委托 `OpenAiModelListClient`
- `...\OpenAi\OpenAiModelListClient.cs`：新建。`GET {endpoint}/models`，`Authorization: Bearer`；解析 OpenAI 形状（`data[].id`）；字段尽力（OpenRouter 的 `context_length/pricing` 在 T12 完善，此处先兼容不炸）
- `...\Anthropic\AnthropicProviderAdapter.cs`：`SupportsModelListing => true`；`GET {endpoint}/v1/models`，头 `x-api-key` + `anthropic-version: 2023-06-01`，解析 `data[].id/display_name` 及 ctx/max_tokens 字段（有则回填 descriptor），`after_id` 分页循环
- `SelfClaw.Infrastructure\DependencyInjection\ServiceCollectionExtensions.cs`：按 §9 注册 3 个 OpenAI 实例（OpenAI/OpenAICompatible/DeepSeek）+ Anthropic + `IAiProviderRegistry`

**测试**
- fixture JSON：OpenAI 裸列表、DeepSeek、Anthropic（带分页）各一份；解析断言。
- Registry：现在 DeepSeek 应可解析到 adapter（改写原"未注册"用例，改用 `Ollama` 之类未注册 Kind）。
- `ServiceCollectionExtensionsTests`：`IAiProviderRegistry` 可解析、adapter 计 4 实例。

---

### T4 AiProviderSettingsService

**目标**：设置页门面全量落地：状态视图、增删改、启停、密钥规则、连通检查、拉取合并。（§6、§6.1）

**改动文件**
- `...\AiProviders\Abstractions\IAiProviderSettingsService.cs`：新建（§6 签名）
- `...\AiProviders\Models\Views\`：`AiProviderSettingsState` / `AiProviderView` / `AiModelView` / `EnabledModelView` / `SaveProviderCommand` / `UpsertModelCommand` / `ConnectivityCheckResult`
- `...\AiProviders\AiProviderSettingsService.cs`：新建实现

**步骤**
1. `GetStateAsync`：目录条目 × 现有连接做左连接视图；未建连接的条目返回灰显占位（`connectionId: null`）。
2. 密钥规则（集中此处）：`ApiKey == null` 不动；`""` → `DeleteSecretAsync` + 移除 `CredentialRefs["api_key"]`；非空 → `StoreSecretAsync(plain, existingRef)` 原地覆盖。出参只给 `HasApiKey` + 尾四位掩码。
3. `DeleteProviderAsync`：⚠ 先枚举 `CredentialRefs` 逐个 `DeleteSecretAsync`（行级联删不到 `.bin` 文件），再删行（FK 级联删模型与选择）。
4. `FetchAndMergeRemoteModelsAsync`：按 §6.1 三规则合并（新增默认 **禁用**、更新只回填缺失的 `display.*`、绝不删除）。
5. `CheckConnectivityAsync`：与执行面同路径构造 `IChatClient`（临时、即弃），发 `MaxOutputTokens=1` 的 `"ping"`，`Stopwatch` 计时；异常消息原样进 `ErrorMessage`。
6. `SaveProviderAsync`：`CatalogId` → 目录条目定 `ProviderKind/AuthKind`；custom 亦然。

**测试**（fake repository + fake protector）：密钥三态、掩码、删连接先删密钥、合并三规则、灰显视图、检查成功/失败路径（fake adapter 返回可控 client）。

---

### T5 Desktop 桥接与启动接线

**目标**：`ai-providers/*` 消息全通；启动初始化。（§8、§9）

**改动文件**
- `SelfClaw.Desktop\Services\AiProviders\AiProviderSettingsBridge.cs`：新建。方法 `TryHandleAsync(string type, JsonElement payload)`：内部 switch 各 `ai-providers/*` 消息 → 调 `IAiProviderSettingsService` → `PostWebMessage(new { type = 请求type, requestId, ...结果或 error })`
- `SelfClaw.Desktop\MainWindow.xaml.cs`：switch 前置一条 `if (type.StartsWith("ai-providers/")) { await _aiProviderBridge.TryHandleAsync(...); break; }`（⚠ 响应用未门控的 `PostWebMessage`，与 pet/programming 一致）
- `SelfClaw.Desktop\App.xaml.cs`：注册桥接单例；启动序列加 `IAiProviderRepository.InitializeAsync()`（与其他仓一致，消除设置页首开竞态）

**验收**：临时用 WebView2 devtools `window.chrome.webview.postMessage({type:'ai-providers/get-state', requestId:'x'})` 收到状态回包。

---

### T6 AIProviders.vue 摘 mock 接真数据

**目标**：设置页全部交互走桥接；mock 仅作浏览器 dev fallback。（§8）

**改动文件**
- `SelfClaw.TranscriptVue\src\components\settings\AIProviders.vue`
- （可选）`src\utils\modelDisplay.ts`：`formatTokens(400000) → '400K'/'1M'`、`formatPrice(1.75) → '$1.75'`、null → `'—'`

**步骤**
1. 照抄 `ProgrammingAssistant.vue` 的四件套：`postToHost` / `defineExpose({ handleMessage })` / requestId 生成与 stale 守卫 / 非 WebView2 环境 dev fallback（保留现 mock 数组作 fallback 数据）。
2. `onMounted` → `ai-providers/get-state`；渲染目录灰显条目（未建连接）与真实连接。
3. 各交互改发消息：保存 key/base（`save-provider`）、启停（`set-provider-enabled`）、删除、拉模型（`fetch-models`，响应替换该 provider 的 models）、检查（`check`，取 `{ok, latencyMs, error}` 展示）、模型启停/全部启停、添加提供商（弹出选目录条目 → `save-provider`）。
4. ⚠ 模型元数据 DTO 是原始数字：模板处渲染改走格式化函数；`cacheW/cacheR` 为 null 时保持现隐藏逻辑；`total` 用 DTO 字段。
5. API key 输入框：显示掩码（`keyMask`），用户输入新值才上行明文，清空上行 `""`。

**验收**：见 T7。

---

### T7 P1 手动验收

在真实桌面应用逐条过：
1. 新建 OpenAI 连接（真实 key）→ 拉模型列表 → 列表入库且重启仍在（默认禁用态）。
2. 连通性检查：正确 key 显示延迟；错 key 显示可读错误。
3. 改 key（掩码回显）、清 key、删除连接（`%SecretsDirectory%` 下对应 `.bin` 同步消失）。
4. custom 条目接一个聚合站（Routin/LongCat）：CC 协议检查通过。
5. Anthropic 连接拉模型（应带 display_name 与 ctx 元数据）。
6. 重启应用：全部状态保持；日志无密钥明文。

---

## P2 —— 提供商与协议补齐

### T8 AiProviderHttpClientProvider

**目标**：按连接指纹缓存 `HttpClient`，统一超时/extra_headers。（§4.4）

**要点**
- 指纹 = endpoint + timeout_seconds + extra_headers 摘要；`ConcurrentDictionary` 缓存。
- `SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }`。
- ⚠ 聊天流式用的 client：`Timeout = Timeout.InfiniteTimeSpan`；列模型/检查用的 client：`Timeout = timeout_seconds ?? 100s`。两类可用同一 HttpClient + 每请求 CTS 实现，或缓存两把——实现自选，测试钉住行为。
- extra_headers 经 `DelegatingHandler` 注入。
- 接入点：`OpenAIClientOptions.Transport = new HttpClientPipelineTransport(http)`；`OpenAiModelListClient`/Anthropic 列表直接用该 HttpClient。

### T9 Ollama 适配器

- `Directory.Packages.props` + Infrastructure 引用 **OllamaSharp**（自带 IChatClient）。
- `...\AiProviders\Ollama\OllamaProviderAdapter.cs`：`ProviderKind = Ollama`；`OllamaNative` 必支持，`OpenAIChatCompletions` 可选（走 `{endpoint}/v1` 复用 OpenAI SDK）；`AuthKind.None` → 跳过 `api_key` 校验（⚠ 工厂/服务层的"缺密钥即错"逻辑要按 AuthKind 分流）。
- `ListModelsAsync`：`GET /api/tags` → `models[].name`。
- DI 注册 + registry 测试 + tags fixture 测试。

### T10 Gemini 适配器（兼容层）

- `...\AiProviders\Gemini\GeminiProviderAdapter.cs`：`ProviderKind = GoogleGemini`；聊天走其 OpenAI 兼容层（endpoint 规整为 `https://generativelanguage.googleapis.com/v1beta/openai/`，`Authorization: Bearer {key}`，复用 OpenAI SDK CC 路径）；`SupportsApiFormat`：CC ✔，`GeminiGenerateContent` 暂 ✖（留 §13-R3 spike）。
- `ListModelsAsync`：原生 `GET /v1beta/models`（头 `x-goog-api-key`），解析 `models[].name/displayName/inputTokenLimit/outputTokenLimit`，过滤含 `generateContent` 的 `supportedGenerationMethods`。
- ⚠ 目录默认协议临时调整为 CC（拿到原生 adapter 后再改回 `GeminiGenerateContent`）。

### T11 Azure OpenAI 适配器

- 引用 **Azure.AI.OpenAI**。
- `...\AiProviders\Azure\AzureOpenAiProviderAdapter.cs`：`AzureOpenAIClient(endpoint, ApiKeyCredential)` → `GetChatClient(deploymentName).AsIChatClient()`；`Model` 字段即 deployment 名；`api-version` 从 `ConnectionOptions` 读（SDK 有默认值时可选）。
- `SupportsModelListing => false`（§3.3 修正项）；`ListModelsAsync` 抛 `NotSupportedException`。
- UI 路径验证：手动"添加模型"（T6 已有 upsert-model 通道）→ 检查连通。

### T12 OpenRouter/自定义网关元数据完善

- `OpenAiModelListClient` 识别 OpenRouter 形状：`data[].{id, name, context_length, pricing{prompt,completion,input_cache_read,input_cache_write}}`（价格是每 token 美元字符串，×1M 换算 PerMTok）。
- custom 端点解析失败时给可读错误（"该网关未实现 /models 或返回了非 OpenAI 形状"）。
- fixture：OpenRouter 真实响应样例。

### T13（spike，可选）DeepSeek reasoning_content 透出

按 §4.3/§13-R1：验证 SSE 重写 `DelegatingHandler` 方案（把 `choices[].delta.reasoning_content` 挪到 SDK 可达位置）vs 轻量自研 CC `IChatClient`。产出：可行性结论 + 原型 + 决策记录进设计文档。**不阻塞 P2/P3 验收**。

---

## P3 —— Direct 执行链路

### T14 IAiChatClientFactory

**改动文件**：`...\AiProviders\Abstractions\IAiChatClientFactory.cs`、`...\AiProviders\AiChatClientFactory.cs`、`...\Models\AiChatRuntimeInputs.cs`、`...\Models\AiChatClientLease.cs`、`...\Models\AiModelSelectionScopes.cs`

**步骤**（§5.1）
1. `CreateAsync`：读档案+连接 → 双 `IsEnabled` 校验 → 按 `AuthKind` 解密（`None` 跳过；缺 `api_key` 抛可读异常）→ `SupportsApiFormat` 校验 → adapter 造 client/options → 包 `UseFunctionInvocation().UseLogging(脱敏)` → lease。
2. `CreateForScopeAsync`：selection 缺失 → 抛"请在设置中为 Direct 模式选择默认模型"。
3. 单测覆盖全部错误路径（§11 Factory 行）。

### T15 工作区工具集 + 审批装饰器

**改动文件**：`SelfClaw.Infrastructure\Agents\Runtime\WorkspaceAgentToolset.cs`（新，或放 Tools 目录）

**步骤**（§5.2 工具与审批）
1. `AIFunctionFactory.Create` 包装 `IWorkspaceToolService` 5 方法（名称 `list_files/search_text/read_file/write_file/run_shell_command`，中文描述给模型看英文即可，参数描述完整；`WorkspaceRoot` 经闭包绑定）。
2. 审批装饰器包 `write_file`/`run_shell_command`：`FullAccess` 旁路；`RequireApproval` → `ToolApprovalRequest(Guid.NewGuid(), …, argsJson, conversationId)` → `RequestApprovalAsync`；拒绝返回 `"User denied this tool call."`；`ToolApprovalHandler == null` 视为拒绝。
3. 单测：批准执行、拒绝不执行且返回 denied、FullAccess 旁路、null handler。

### T16 DirectAgentChatRuntime

**改动文件**：`SelfClaw.Infrastructure\Agents\Runtime\DirectAgentChatRuntime.cs`（新）

**步骤**（§5.2、§5.3 翻译表逐行实现）
1. 迭代器纪律：`[EnumeratorCancellation]`；早期失败 `yield RunCompleted(Failed, msg)` 后 break；`runCompletedEmitted` 兜底；⚠ 绝不让异常穿出迭代器。
2. 进流即发 `RunStarted(sessionId: $"direct-{guid}", model, agentKind: null)` + `RunStatus(Requesting)`。
3. 消息组装（§5.3）+ 工具组装（T15；`WorkspaceRoot == null` 不带工具）。
4. `await foreach (update in client.GetStreamingResponseAsync(...))`：按翻译表映射；`UsageContent` 累加流末统一发；文本累积作 `finalText`。
5. 终态三分支：正常 / `OperationCanceledException` → Cancelled / 其他异常 → Failed。
6. `finally` 处置 lease。
7. 单测：脚本化 fake `IChatClient`（预置 update 序列）断言全翻译表 + 三终态 + 事件序（§11）。

### T17 运行时契约更新与分支接入

**改动文件**
- `SelfClaw.Core\Runtime\Requests\ChatTurnRequest.cs`：删 `Profile`/`ApiKey`，加 `Guid? ModelProfileId`（⚠ 保留 `CliAgent/CliModel/CliReasoningEffort`）
- `SelfClaw.Core\Runtime\Agent\RunStartedEvent.cs`：`CliAgentKind` → `CliAgentKind?`（全仓只写不读，安全）
- `SelfClaw.Infrastructure\Agents\Runtime\DispatchingAgentChatRuntime.cs`：ctor + `AgentExecutionMode.Direct => _directRuntime.StreamTurnAsync(...)`
- `SelfClaw.Infrastructure\DependencyInjection\ServiceCollectionExtensions.cs`：`AddSingleton<DirectAgentChatRuntime>()`
- `SelfClaw.Desktop\ViewModels\MainWindowViewModel.cs:766`：⚠ 唯一构造点按新位置参数重排（此时先传 `ModelProfileId: null` 占位，T19 接真值；`requestProfile/apiKey` 两个局部变量与赋值一并删）

**验收**：选 Direct 模式 Agent 发消息 → 若已设默认模型则整轮可跑（无工具亦可），否则收到可读失败提示。

### T18 审批 UI 恢复

**改动文件**：`SelfClaw.Desktop\MainWindow.xaml.cs` 或 `MainWindowViewModel`（+ 必要的前端 toast/对话）

**步骤**（§5.2 前置条件；⚠ 当前 `ApprovalRequested` 无订阅者，不做此任务则 RequireApproval 下工具调用永久挂起）
1. 订阅 `DesktopToolApprovalHandler.ApprovalRequested`：将请求（工具名/描述/参数摘要）经现有通知/桥接通道呈现"允许 / 拒绝"。
2. 用户选择 → `TryResolve(toolExecutionId, approved)`；超时（如 5 分钟）自动拒绝并提示。
3. 手动验收：Direct 轮内触发 `write_file` → 弹审批 → 允许成功写、拒绝返回 denied 且对话继续。

### T19 composer 模型选择集成

**改动文件**：`AiProviderSettingsBridge`（+`list-enabled-models`、`set-default-model` 已有）、composer 相关 Vue 组件、`MainWindowViewModel`

**步骤**（§5.4）
1. 桥接实现 `ai-providers/list-enabled-models`。
2. Direct 类 Agent 激活时，composer 模型选择器数据源切换为 enabled 档案列表（对齐现有 CLI 选择器交互与持久化方式）。
3. 选中 → `ChatTurnRequest.ModelProfileId`；同时 `set-default-model(scope: desktop-default)` 持久化；启动回读。
4. 手动验收：两个不同提供商的模型来回切换发消息，各自走通。

### T20 旧代码删除 + v21 迁移

**实施状态（2026-07-15）**：已完成。旧 profile 类型、仓储、VM/DI/启动链路已删除；v21 迁移测试确认 conversation、message、tool run、CLI session 数据保留，新库不再含 `profiles/profile_id`。

**严格按设计 §10 清单执行**，要点：
1. `SqliteDatabase.cs`：版本 → 21；新增 `conversations` 重建（照抄 `EnsureConversationProfileIdNullableAsync` 模式，去掉 `profile_id` 列与其 FK）；`DROP TABLE IF EXISTS profiles`；删 profiles DDL 与 4 处 EnsureColumnExists。
2. 删 4 个文件（`ProviderProfile/ApiStyle/IProfileRepository/SqliteProfileRepository`）+ 死接口 `IWorkspaceMemoryInitializationService` + `SqliteMappings.ReadProfile`。
3. `MainWindowViewModel` 旧 profile 面清理（§10 行 8）；`App.xaml.cs:60`、`ServiceCollectionExtensions.cs:30`。
4. 测试：删 profiles round-trip；版本断言 → 21；⚠ 新增迁移测试——预置 v19/v20 形状库文件（带 conversations 数据 + profiles 表）跑初始化：conversations 行数不变、profiles 消失。

### T21 文档更新与全量回归

**实施状态（2026-07-15）**：自动化与文档更新已完成，真实桌面手动验收待执行。全量 174 个 .NET 测试通过，solution build 0 警告/0 错误，TranscriptVue 73 modules build 通过，完整测试依赖树无已知漏洞；T7/T18/T19 的真实 API Key、WPF/toast 与跨提供商 Direct 回合不得视为已验收。

1. 更新 `AGENTS.md`（DI/运行时描述——设计 §10 末行）与本任务文档状态。
2. 全量：`dotnet test` + 前端 build + P1 手动清单抽查 + P3 验收（Direct 全链路：选模型 → 对话 → 工具 → 审批 → 用量/终态渲染与 CLI 分支一致）。

---

## 里程碑验收（对齐设计 §12）

- **P1 完成**：设置页对 OpenAI/Anthropic/custom 三类真实可用（增删改查、启停、检查、拉取持久化、密钥加密与掩码），重启不丢。
- **P2 完成**：目录 8 条目全部可配置可检查；除 Azure 外可拉列表；Azure 手动模型可检查。
- **P3 完成**：Direct Agent 在 composer 选任意已启用模型，完成带工具调用与审批的完整对话轮；旧 profiles 链路全删且既有会话无损；`RunStartedEvent.AgentKind` 可空化后 CLI 分支回归通过。
