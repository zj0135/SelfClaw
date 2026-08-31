# 多 AI 提供商后端实施记录

> 设计依据：`ai-provider-system-design.md`  
> 任务依据：`ai-provider-implementation-tasks.md`  
> 记录规则：任务通过对应自动化验证后标记为“已完成”，并记录新增内容、作用、关键行为和验证证据。

## 任务状态

| 任务 | 状态 | 完成日期 | 说明 |
|---|---|---|---|
| T1 领域模型与数据库增量（v20） | 已完成 | 2026-07-14 | 扩展多提供商领域模型，完成 `catalog_id` 迁移和模型启停仓储能力 |
| T2 内置目录 AiProviderCatalog | 已完成 | 2026-07-14 | 建立 8 个内置提供商条目的静态目录和未知 id 回退规则 |
| T3 适配器接口扩展、模型列表与 DI | 已完成 | 2026-07-14 | OpenAI 系、DeepSeek、Anthropic 支持模型发现并完成适配器注册 |
| T4 AiProviderSettingsService | 已完成 | 2026-07-14 | 设置门面、密钥安全规则、模型合并和连通性检查完整落地 |
| T5 Desktop 桥接与启动接线 | 自动化已完成，待手动验收 | 2026-07-15 | 12 类消息路由、关联响应、错误与取消测试通过；WebView2 DevTools 实机回包并入 T7 验收 |
| T6 AIProviders.vue 接入真实数据 | 已完成 | 2026-07-15 | 设置页全量切换到桥接请求，浏览器环境保留 mock fallback |
| T7 P1 手动验收 | 等待真实桌面与凭据 | - | 自动化边界已完成；需用户在 Windows 桌面用自有凭据验证持久化、真实端点和日志脱敏 |
| T8 AiProviderHttpClientProvider | 已完成 | 2026-07-15 | 连接指纹缓存、流式/非流式超时和 extra headers 统一落地 |
| T9 Ollama 适配器 | 已完成 | 2026-07-15 | OllamaNative、可选 OpenAI CC 和 `/api/tags` 模型发现落地 |
| T10 Gemini 适配器 | 已完成 | 2026-07-15 | OpenAI 兼容聊天与 Gemini 原生模型列表落地 |
| T11 Azure OpenAI 适配器 | 已完成 | 2026-07-15 | Azure SDK Chat Completions、deployment、api-version 和手工模型路径落地 |
| T12 OpenRouter/自定义网关元数据 | 已完成 | 2026-07-15 | OpenRouter 上下文/价格解析与 custom `/models` 可读错误落地 |
| T13 DeepSeek reasoning_content spike（可选） | 未开始 | - | - |
| T14 IAiChatClientFactory | 已完成 | 2026-07-15 | 执行前校验、密钥解析、适配器选择和安全 M.E.AI 管道落地 |
| T15 工作区工具集与审批装饰器 | 已完成 | 2026-07-15 | 五个工作区工具、写入/shell 审批与 FullAccess 旁路落地 |
| T16 DirectAgentChatRuntime | 已完成 | 2026-07-15 | M.E.AI 流式内容完整翻译为现有 AgentStreamEvent，并统一三种终态 |
| T17 运行时契约与 Direct 分支接入 | 已完成 | 2026-07-15 | 请求去明文密钥、Direct dispatcher/DI/Desktop 接入完成 |
| T18 审批 UI 恢复 | 自动化已完成，待手动验收 | 2026-07-15 | handler/toast 契约已测；仍需真实 WPF 弹窗和 Windows toast 交互 |
| T19 composer 模型选择集成 | 自动化已完成，待手动验收 | 2026-07-15 | 模型事件与持久化桥接已测；仍需两个真实提供商完成 Direct 回合 |
| T20 旧代码删除与 v21 迁移 | 已完成 | 2026-07-15 | 删除旧 ProviderProfile 链路，v21 重建 conversations 并保留依赖数据 |
| T21 文档更新与全量回归 | 自动化已完成，待手动验收 | 2026-07-15 | AGENTS/设计/任务记录已同步；174 测试、solution/Vue build 与依赖漏洞审计通过，T7/T18/T19 仍需真实桌面验收 |

## T1 领域模型与数据库增量（v20）

状态：已完成  
完成日期：2026-07-14

### 新增内容

- 扩展 `AiProviderKind`：新增 `GoogleGemini`、`Ollama`、`AzureOpenAI`，为后续独立 SDK/客户端构造路径提供稳定类型标识。
- 扩展 `AiProviderApiFormat`：新增 `GeminiGenerateContent`、`OllamaNative`，使协议选择继续挂在模型档案上。
- 扩展 `AiProviderAuthKind`：新增 `None`，允许 Ollama 等本地服务不配置 API Key。
- `AiProviderConnection` 新增 `CatalogId`，把持久化连接关联到内置目录条目，同时允许同一目录条目创建多个连接实例。
- `AiModelProfile` 新增 `IsEnabled`，模型档案可以独立启停，禁用模型不会进入 Direct/composer 可选集合。
- 新增 `AiModelDescriptor`，统一承载远端 `/models` 和内置目录返回的模型名称、上下文窗口、输出限制与价格元数据。
- SQLite schema 从 v19 升到 v20；新库直接创建 `catalog_id`，旧库通过幂等 `ALTER TABLE` 增加该列并默认归入 `custom`。
- `SqliteAiProviderRepository` 完整读写 `CatalogId` 和 `IsEnabled`，新增单模型启停、连接内全部模型启停、仅列出“连接与模型均启用”档案的方法。
- 常规模型列表和按 id 查询保留禁用记录，供设置页编辑；`ListEnabledModelProfilesAsync` 专门承担执行面过滤职责。

### 作用

- 建立后续八类提供商目录和适配器的稳定领域基础，避免把品牌、认证方式和线协议混成一个枚举。
- 为设置页展示全部模型、执行面只消费启用模型提供明确的数据访问边界。
- 保证现有 v19 数据库无损升级；历史连接没有目录信息时仍能以 `custom` 继续工作。
- 为远端模型发现与合并提供跨提供商的统一数据形状。

### 关键行为

- 已持久化枚举值保持原编号不变，避免破坏现有数据库中的整数值。
- 模型启停更新同步刷新 `updated_at_utc`。
- 启用模型查询同时检查 `ai_model_profiles.is_enabled` 和所属 `ai_provider_connections.is_enabled`。
- `JsonElement` 字典继续使用现有映射中的克隆逻辑，避免 `JsonDocument` 释放后的悬空引用。

### 验证证据

- 相关测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter "FullyQualifiedName~SqliteRepositoriesTests|FullyQualifiedName~OpenAiProviderAdapterTests|FullyQualifiedName~AnthropicProviderAdapterTests"`
- 结果：26 个测试通过，0 失败，覆盖 v20 版本、`catalog_id` 旧库迁移、连接/模型读写、单个与批量启停、连接禁用过滤及现有适配器构造回归。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，84 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。

## T10 Gemini 适配器（兼容层）

状态：已完成  
完成日期：2026-07-15

### 新增内容

- 新增 `GeminiProviderAdapter`，`ProviderKind = GoogleGemini`，聊天阶段支持 `OpenAIChatCompletions`，原生 `GeminiGenerateContent` 暂明确不支持。
- 聊天连接将根 endpoint 规范为 `/v1beta/openai/`，复用 OpenAI SDK、T8 streaming transport 和 Bearer API Key。
- 模型列表使用原生 `GET /v1beta/models` 和 `x-goog-api-key`，通过 T8 非流式客户端执行。
- 解析 `name/displayName/inputTokenLimit/outputTokenLimit`，只保留 `supportedGenerationMethods` 包含 `generateContent` 的模型，并剥离 id 的 `models/` 前缀。
- Gemini 目录默认协议按任务要求临时调整为 OpenAI CC，仍保留原生协议在支持协议目录中供后续 spike。
- DI 注册 Gemini adapter，Registry 可解析适配器数增加到 6。
- 新增 `gemini-models.json` fixture 和端点/认证/过滤/元数据测试。

### 作用

- Google Gemini 在原生 generateContent 选型未定期间即可通过官方 OpenAI 兼容层参与设置、连通性检查和后续 Direct 对话。
- 模型发现仍走原生 API，因此能获得上下文和输出 token 限制，而不是退化成只有 id。
- embedding 等不支持生成的模型不会污染聊天模型列表。

### 关键行为

- 根 endpoint、`/v1beta` endpoint 和已规范的 `/v1beta/openai` endpoint 均不会重复拼接版本路径。
- 列表请求仅使用 `x-goog-api-key`；聊天请求由 OpenAI SDK 使用 Bearer key。
- 缺少 key 时在发送列表请求前失败；原生协议在客户端构造前返回可读 `NotSupportedException`。
- 新拉取模型默认采用 CC 协议，避免生成当前 adapter 无法执行的原生档案。

### 验证证据

- 针对性测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter "FullyQualifiedName~GeminiProviderAdapterTests|FullyQualifiedName~AiProviderCatalogTests|FullyQualifiedName~AiProviderRegistryTests|FullyQualifiedName~ServiceCollectionExtensionsTests"`，21 个测试通过，0 失败。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，124 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。

## T9 Ollama 适配器

状态：已完成  
完成日期：2026-07-15

### 新增内容

- 中央包版本新增 `OllamaSharp 5.4.25`，Infrastructure 引用该包；该版本原生实现 Microsoft.Extensions.AI `IChatClient`。
- 新增 `OllamaProviderAdapter`，`ProviderKind = Ollama`，支持必需的 `OllamaNative` 和可选的 `OpenAIChatCompletions`。
- OllamaNative 使用 T8 流式 `HttpClient` 构造 `OllamaApiClient`，映射模型 id、显式启用的 Temperature/TopP 和工具集合。
- OpenAI CC 路径将 endpoint 规范为 `/v1/`，复用 OpenAI SDK 适配器；内部使用本地占位 credential，不要求用户配置 API Key。
- 模型列表使用 T8 非流式客户端调用 OllamaSharp `ListLocalModelsAsync`（底层 `/api/tags`），映射 `models[].name` 为 descriptor。
- DI 注册 Ollama adapter，Registry/DI 期望适配器数增加到 5。
- 新增真实形状 `ollama-tags.json` fixture 和适配器测试。

### 作用

- 本机 Ollama 可以在完全无密钥的情况下拉取模型、检查连通性并进入后续 Direct 执行链路。
- 原生协议保留 OllamaSharp 的完整流式和工具集成；兼容协议为用户已有 `/v1` 配置提供迁移路径。
- 模型列表、聊天和额外 headers 复用统一 HttpClient 生命周期与超时策略。

### 关键行为

- `AuthKind.None` 不触发 `api_key` 校验，空 secrets 可成功构造客户端和拉取模型。
- endpoint 已含 `/v1` 时不重复追加；根 endpoint 自动转换为 `{endpoint}/v1/`。
- 模型列表使用非流式默认 100 秒超时，且可消费连接级 `extra_headers`。
- Responses 等未支持协议在发请求前抛出包含协议和档案名的可读异常。

### 验证证据

- 依赖恢复：`dotnet restore SelfClaw.slnx --force-evaluate` 成功（沙箱网络限制后经批准执行）。
- 针对性测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter "FullyQualifiedName~OllamaProviderAdapterTests|FullyQualifiedName~AiProviderRegistryTests|FullyQualifiedName~ServiceCollectionExtensionsTests"`，7 个测试通过，0 失败。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，121 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。

## T4 AiProviderSettingsService

状态：已完成  
完成日期：2026-07-14

### 新增内容

- 新增 `IAiProviderSettingsService`，统一提供状态读取、连接 CRUD/启停、远端模型拉取合并、模型 CRUD/启停、连通性检查、默认选择和启用模型列表。
- 新增 `AiProviderSettingsState`、`AiProviderView`、`AiModelView`、`EnabledModelView`、`SaveProviderCommand`、`UpsertModelCommand`、`ConnectivityCheckResult` DTO。
- 新增 `AiProviderSettingsService` 并注册到 Infrastructure DI。
- `GetStateAsync` 将静态目录和持久化连接做左连接；没有连接的目录条目仍以灰显占位返回，配置连接则附带全部启用/禁用模型。
- API Key 实现三态语义：null 保持、空字符串删除、非空值加密写入；更新复用原 SecretRef，返回只包含 `HasApiKey` 和尾四位掩码。
- 删除连接前逐个删除所有密钥文件，再删除数据库行，避免级联删除后残留 DPAPI 文件。
- 远端模型按 model id 合并：新增默认禁用；已有档案仅补缺失的 `display.*` 元数据；远端缺失的本地模型不删除。
- 连通性检查通过对应适配器构造真实 `IChatClient/ChatOptions`，发送 `ping` 且限制 `MaxOutputTokens=1`，返回耗时或原始异常消息。
- 手工模型保存校验目录支持的协议；默认模型只能指向“模型和连接均启用”的档案。

### 作用

- 为 Desktop 桥接和设置页提供单一后端门面，UI 不再直接组合仓储、目录、密钥和适配器逻辑。
- 将明文密钥限制在一次服务入参内，状态读取和 WebView 下行数据不会泄露明文。
- 明确区分设置面“展示全部档案”和执行面“只消费双重启用档案”的查询语义。
- 远端模型刷新可重复执行且不覆盖用户配置，避免启停、采样、协议和手工元数据被刷新破坏。

### 关键行为

- 目录决定 `ProviderKind` 和 `AuthKind`；保存 Ollama 时不会存储 API Key，切换到无认证目录会清理旧 key。
- API Key 掩码只保留可识别前缀（如 `sk-`）和尾四位，序列化状态中不含明文。
- 合并支持历史库中重复 model id，不会因字典构造异常中断刷新。
- 取消令牌触发的 `OperationCanceledException` 继续向上传播；普通连接异常转换为失败结果并保留可读消息。
- Azure 等目录或适配器声明不支持模型列表时明确抛出 `NotSupportedException`。

### 验证证据

- 针对性测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter "FullyQualifiedName~AiProviderSettingsServiceTests|FullyQualifiedName~ServiceCollectionExtensionsTests"`，8 个测试通过，0 失败。
- 测试覆盖灰显占位、密钥掩码与三态、删除顺序、合并三原则、连通性成功/失败、CRUD 启停、协议校验和默认选择过滤。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，109 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。

## T3 适配器接口扩展、模型列表与 DI 注册

状态：已完成  
完成日期：2026-07-14

### 新增内容

- `IAiProviderAdapter` 新增 `SupportsModelListing` 和 `ListModelsAsync`，把模型发现纳入所有提供商适配器的统一契约。
- 新增 `OpenAiModelListClient`：调用连接端点下的 `/models`，使用 Bearer API Key，解析 OpenAI `data[].id` 形状。
- `OpenAiProviderAdapter` 扩展为同时承载 `OpenAI`、`OpenAICompatible`、`DeepSeek` 三种 Kind，并把 OpenAI 系模型列表委托给统一客户端。
- 新增 `AnthropicModelListClient`：调用 `/v1/models`，发送 `x-api-key` 和固定 `anthropic-version: 2023-06-01`，按 `has_more/last_id/after_id` 拉取所有分页。
- Anthropic 模型解析回填 `display_name`、输入上下文和最大输出 token；缺失元数据保持 null。
- Infrastructure DI 注册 3 个 OpenAI 系适配器实例、1 个 Anthropic 适配器以及 `IAiProviderRegistry`。
- 新增 OpenAI、DeepSeek、Anthropic 两页分页 JSON fixtures；新增伪 HTTP 测试捕获 URL、认证头、分页游标和解析结果。
- Registry 和 DI 测试改为确认 DeepSeek 可解析，未注册 Kind 改用 Ollama，适配器实例总数固定为 4。

### 作用

- 设置服务可以通过统一适配器接口发现远端模型，无需知道各提供商 URL、认证头和响应结构。
- DeepSeek 正式进入 OpenAI-compatible 执行/模型发现链路，为后续 reasoning 方言扩展保留独立 Kind。
- Anthropic 分页会完整拉取模型而不是只保存第一页，避免模型列表静默缺失。
- DI 注册表在应用启动时检查重复 Kind，并为后续设置服务和 Direct 工厂提供唯一解析入口。

### 关键行为

- 缺失 `api_key` 时在发送 HTTP 请求前抛出可读异常。
- OpenAI 形状缺少 `data` 数组、Anthropic 分页游标缺失或重复时明确失败，避免返回不完整结果或无限循环。
- T3 的模型列表客户端支持注入 `HttpClient` 以便测试；连接级缓存、额外 headers 和超时统一将在 T8 收口。
- OpenAI `/v1/models` 只有 id 时其余 descriptor 字段保持 null；不伪造展示元数据。

### 验证证据

- 针对性测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter "FullyQualifiedName~AiProviderModelListingTests|FullyQualifiedName~AiProviderRegistryTests|FullyQualifiedName~ServiceCollectionExtensionsTests"`，8 个测试通过，0 失败。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，102 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。

## T2 内置目录 AiProviderCatalog

状态：已完成  
完成日期：2026-07-14

### 新增内容

- 新增 `AiProviderCatalogEntry`，统一描述目录 id、展示名称、副标题、主题色、适配器类型、默认端点、默认/支持协议、认证方式、Key 获取地址、模型拉取能力和预置模型。
- 新增静态 `AiProviderCatalog`，内置 `openai`、`anthropic`、`google-gemini`、`deepseek`、`openrouter`、`ollama`、`azure-openai`、`custom` 8 个条目。
- `GetRequired` 支持大小写无关查找；空值或未知 id 统一回退 `custom`，保证历史/外部数据不会导致设置页崩溃。
- P1 阶段所有 `WellKnownModels` 保持空列表，后续以远端模型拉取和持久化合并为主。
- 新增目录契约测试，覆盖条目数量与唯一性、展示字段、协议闭包、完整 Kind/默认协议/认证/拉取能力映射及特殊提供商约束。

### 作用

- 将产品内置提供商元数据集中到单一静态数据源，设置服务无需按品牌散落硬编码。
- 为连接创建提供可信默认值，同时允许同一目录条目对应多个持久化连接。
- 提前固定 Ollama 无认证、Azure 手动录入模型、Gemini 双协议等后续适配器和 UI 依赖的契约。

### 关键行为

- 每个条目的默认协议必定包含在 `SupportedFormats` 中。
- `ollama` 使用 `AuthKind.None` 和 `http://localhost:11434/`，支持拉取本地模型。
- `azure-openai` 的 `SupportsModelListing` 为 `false`，避免错误使用已退役或语义不匹配的部署列表端点。
- `custom` 使用 OpenAI-compatible 适配器，并作为未知目录 id 的兼容回退。

### 验证证据

- 目录测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter "FullyQualifiedName~AiProviderCatalogTests"`，14 个测试通过，0 失败。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，98 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。

## T5 Desktop 桥接与启动接线

状态：自动化已完成，待 T7 手动验收  
实现日期：2026-07-15

### 新增内容

- 新增 `AiProviderSettingsBridge` Desktop 单例服务，统一处理所有 `ai-providers/*` 请求。
- 桥接支持状态读取、连接保存/启停/删除、模型拉取、连通性检查、模型新增/启停/批量启停/删除、默认模型设置和启用模型列表。
- 每个响应回显原请求 `type` 和 `requestId`；成功响应按命令返回 `state/provider/models/model/ok`，异常统一返回 `error`。
- `MainWindow` 在原消息 switch 前按 `ai-providers/` 前缀转交桥接，并通过 `ResponseReady` 事件调用现有未门控的 `PostWebMessage`。
- `MainWindow` 构造时订阅桥接响应、关闭时解除订阅，避免窗口生命周期结束后的事件引用。
- `App.xaml.cs` 注册桥接单例，并在显示主窗口前显式调用 `IAiProviderRepository.InitializeAsync()`。

### 作用

- WebView 设置页只需使用稳定的 request/response 协议，不直接依赖 WPF、仓储、密钥或适配器实现。
- AI 提供商消息不再继续膨胀 `MainWindow` 的 switch，新增命令集中在独立桥接中维护。
- 响应绕过 `_webViewReady` 推送门控；收到请求本身已经证明页面监听器就绪，避免启动阶段请求永久等待。
- 启动时预先初始化 AI 仓储，消除设置页首次打开与 SQLite schema 初始化之间的竞态。

### 关键行为

- 桥接接受 `providerId/connectionId/id` 等协议别名，并对 GUID、布尔值、枚举和对象载荷做显式校验。
- `apiKey` 读取保留 null 与空字符串的区别，以便 T4 密钥三态语义正确执行。
- 普通异常转换为同 requestId 的错误响应；显式取消继续向上传播。
- 桥接通过事件把纯响应对象交给宿主，不直接引用 WebView2，也不会与 `MainWindow` 形成 DI 循环。

### 验证证据

- 新增桥接契约测试，逐项覆盖 12 类 `ai-providers/*` 消息、requestId 回显、unsupported/invalid payload 错误响应、非本前缀忽略和 caller cancellation 透传。
- `list-enabled-models` 与 `set-default-model` 测试确认只有成功的 desktop-default 选择会发布权威 `ModelSelectionChanged`。
- Desktop 构建：`dotnet build SelfClaw.Desktop/SelfClaw.Desktop.csproj --no-restore`，构建成功，0 警告、0 错误。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，174 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。
- 待验收：真实桌面应用中通过 WebView2 DevTools 发送 `ai-providers/get-state` 并核对同 requestId 回包；该项并入 T7 P1 手动清单，在完成前不把 T5 标记为“已完成”。

## T6 AIProviders.vue 接入真实数据

状态：已完成；实机业务验收归 T7  
完成日期：2026-07-15

### 新增内容

- 新增 `useAiProviderHost` composable，集中管理 WebView 请求、requestId 关联、30 秒超时、响应错误和组件卸载清理。
- `AIProviders.vue` 挂载时请求 `ai-providers/get-state`，将后端 Provider/Model DTO 规范化为现有页面渲染形状；浏览器开发环境继续使用原 mock 数据。
- 状态请求带最新 requestId 守卫，迟到响应不会覆盖较新的设置状态。
- API Key 改为独立输入草稿：初始只显示后端掩码；未修改不上传，输入新值才上传明文，清空时明确上传空字符串。
- 连接保存、启停、删除、模型刷新、连通性检查、单模型/全部模型启停、模型删除全部切换为对应 `ai-providers/*` 消息。
- 新增服务商选择对话框，选择未配置目录条目后调用 `save-provider`；新增手工模型对话框，协议选项完全来自后端目录视图并调用 `upsert-model`。
- 新增 `AiProviderDialogs.vue`，为新增服务商和手工模型提供克制的操作对话框、键盘可用表单和请求中状态。
- 模型元数据以原始数字接收并在前端格式化：token 使用 K/M，价格使用美元每百万 token，null 统一显示 `—`。
- Provider DTO 补充 `GetApiKeyUrl`、`DefaultApiFormat`、`SupportedFormats`，前端不再按品牌硬编码控制台地址或协议。

### 作用

- 设置页的连接、密钥、模型和默认能力现在都来自 SQLite/适配器真实状态，重启后可恢复，不再依赖页面内 mock。
- requestId 和超时机制防止异步响应乱序、丢包后永久 loading，以及组件卸载后的悬挂回调。
- 密钥输入与掩码分离，避免把掩码误当成新密钥回传，也避免明文在页面状态中长期保留。
- 后端目录成为协议、认证、模型拉取能力和控制台链接的单一事实来源。

### 关键行为

- Ollama 等 `AuthKind.None` 条目不显示 API Key 字段；Azure 等不支持列表的条目禁用“获取模型列表”。
- 未配置目录条目保持灰显，可从左侧选择后直接创建；删除后重新回到目录占位状态。
- 模型列表拉取响应会完整替换当前连接的视图列表，但实际持久化合并仍遵循 T4 的“不覆盖用户配置、不删除本地模型”规则。
- 连通性结果展示真实延迟或后端错误；破坏性删除先要求用户确认。
- 前端沿用现有视觉层级，仅增加必要的 loading/error/disabled 状态和两个交互对话框。

### 验证证据

- 前端生产构建：`cd SelfClaw.TranscriptVue && npm run build`，73 个模块转换成功，Vite 构建通过。
- 格式化抽查输出：`400000 → 400K`、`1000000 → 1M`、`1.75 → $1.75`、null → `—`。
- 前后端消息审计：连接/模型 CRUD、启停、刷新和检查所用消息均能在 `AiProviderSettingsBridge` 找到同名处理分支。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，109 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。

## T7 / T18 / T19 真实桌面验收清单

状态：等待具备 Windows 交互会话和用户自有 provider credential 的人工验收  

> 安全要求：不要把完整 API Key、`secrets/*.bin` 内容或未脱敏日志粘贴进任务记录；截图需遮挡输入框和账号信息。开始前备份 `%LOCALAPPDATA%\SelfClaw\selfclaw.db` 与 `secrets` 目录。

### T7 设置页与真实提供商

1. 运行 `dotnet run --project SelfClaw.Desktop/SelfClaw.Desktop.csproj`，在“设置 → AI 提供商”创建 OpenAI 连接并输入用户自有 key。
2. 拉取模型列表，确认新模型默认禁用；启用一个模型后重启应用，连接、模型及启用状态仍在。
3. 分别用正确 key 和故意错误的 key 执行连通性检查：前者显示延迟，后者显示可读认证错误且 UI 不挂起。
4. 更新 key 后确认只回显掩码；清空 key 后检查 `%LOCALAPPDATA%\SelfClaw\secrets` 对应文件消失；删除连接后其剩余 secret 文件同步消失。
5. custom 连接接用户可用的 OpenAI-compatible 网关并走通 Chat Completions；Anthropic 连接拉取后核对 `display_name` 与 context metadata。
6. 再次重启核对全部状态；检查应用输出目录 `logs/selfclaw-*.log`，确认搜索完整 key 无命中。

### T18 审批 UI

1. 使用 `mode: direct` Agent、已选择 workspace、`RequireApproval` 权限，请模型调用 `write_file` 写一个可删除的验收标记文件。
2. 窗口可见时分别执行一次“允许”和“拒绝”：允许时文件写入；拒绝时文件不产生且 transcript 收到 denied 后继续完成回合。
3. 最小化窗口后分别通过 Windows toast 的 Confirm/Cancel 执行一次，核对结果与前台一致且同一 execution id 不能二次处理。
4. 再触发一项写入但不操作，等待 5 分钟；确认自动拒绝、出现过期提示且回合解除等待。
5. 有 pending approval 时关闭主窗口，确认应用退出且没有遗留写入或挂起进程。

### T19 composer 与 Direct 完整回合

1. 准备两个不同 provider connection，各启用至少一个可聊天模型；Direct composer 不应混入 CLI 模型。
2. 选择模型 A 发一轮，再选择模型 B 发一轮；两轮均应完成文本、usage 与唯一成功/失败终态渲染。
3. 在模型 B 回合触发一次已批准的 workspace 工具，确认 tool call/result 锚点、最终文本和 running 状态解除。
4. 重启应用，composer 应恢复模型 B；再发送一轮确认显式/default `ModelProfileId` 都指向当前启用档案。
5. 切回 `mode: cli` Agent，确认选择器恢复 CLI 数据源且现有 CLI 回合不受 Direct 默认模型影响。

### 验收记录模板

| 项目 | 结果 | 脱敏证据/备注 |
|---|---|---|
| T7 OpenAI 拉取、检查、重启持久化 | 待执行 | - |
| T7 key 更新/清空/删除与日志脱敏 | 待执行 | - |
| T7 custom + Anthropic | 待执行 | - |
| T18 WPF 允许/拒绝 | 待执行 | - |
| T18 toast 允许/拒绝 | 待执行 | - |
| T18 timeout/关闭拒绝 | 待执行 | - |
| T19 双 provider 模型切换/重启 | 待执行 | - |
| T19 Direct 工具/usage/终态 + CLI 回归 | 待执行 | - |

## T8 AiProviderHttpClientProvider

状态：已完成  
完成日期：2026-07-15

### 新增内容

- 新增 `AiProviderHttpClientProvider` 单例，按 endpoint、非流式超时和排序后的 `extra_headers` 摘要生成 SHA-256 连接指纹。
- 每个指纹分别缓存流式与非流式 `HttpClient`；使用 `Lazy<HttpClient>` 避免并发 `GetOrAdd` 重复构造和 handler 泄漏。
- 底层默认使用 `SocketsHttpHandler`，`PooledConnectionLifetime` 固定 5 分钟，降低长寿进程 DNS 变更失效风险。
- 流式客户端 `Timeout = InfiniteTimeSpan`；非流式客户端使用 `ConnectionOptions.timeout_seconds`，缺省 100 秒，合法范围 1-3600 秒。
- 新增 extra-header handler，在每次请求时注入 `ConnectionOptions.extra_headers`，但不会覆盖请求已经显式设置的 Authorization 等同名头。
- OpenAI Chat Completions 和 Responses SDK options 注入 `HttpClientPipelineTransport`，两种协议共享同一流式 transport。
- OpenAI/Anthropic 模型列表客户端改用统一 provider 的非流式客户端。
- 连通性检查使用关联 CTS 应用同一非流式超时，同时保留调用方主动取消的传播语义。
- Infrastructure DI 注册统一 provider，所有 OpenAI 系适配器、模型列表客户端和设置服务共享该实例。

### 作用

- 避免每次请求创建 socket/handler，同时使 endpoint、超时或 headers 配置变化立即切换到新缓存实例。
- 长时间流式生成不会被连接级超时误杀；模型列表和连通性检查仍有明确超时上限。
- OpenRouter 的 `HTTP-Referer`、`X-Title` 等连接级请求头可在模型列表和 OpenAI SDK 聊天链路统一生效。
- 指纹只保存 header 内容摘要，不把可能敏感的 header 值放进缓存键、日志或异常文本。

### 关键行为

- header JSON 顺序和 header 名大小写不影响指纹；endpoint 路径保持大小写敏感，避免错误复用不同 API 路径。
- 流式与非流式客户端即使配置相同也不会共享实例，因为 `HttpClient.Timeout` 语义不同。
- `timeout_seconds` 或 `extra_headers` 形状错误时在创建请求前给出包含配置名的可读异常。
- Anthropic preview 聊天集成没有公开 HttpClient 注入口：模型列表和检查超时已统一，聊天 `extra_headers` 暂不能注入；结论已回写设计文档 R4。

### 验证证据

- 针对性测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter "FullyQualifiedName~AiProviderHttpClientProviderTests|FullyQualifiedName~AiProviderModelListingTests|FullyQualifiedName~AiProviderSettingsServiceTests|FullyQualifiedName~ServiceCollectionExtensionsTests"`，21 个测试通过，0 失败。
- 测试覆盖指纹复用/分离、两类 timeout、默认 timeout、header 注入与 Authorization 优先级、无效配置、OpenAI 双协议 transport 共享及既有模型列表/设置服务回归。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，118 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。

## T11 Azure OpenAI 适配器

状态：已完成  
完成日期：2026-07-15

### 新增内容

- 中央包版本新增 `Azure.AI.OpenAI 2.5.0-beta.1`，Infrastructure 引用官方 Azure 扩展 SDK。
- 新增 `AzureOpenAiProviderAdapter`，仅支持 `OpenAIChatCompletions`；`AiModelProfile.Model` 直接作为 Azure deployment 名传给 `GetChatClient`。
- Azure SDK transport 接入 T8 流式 HttpClient，连接级 extra headers 和无限流式超时继续生效。
- 可选读取 `ConnectionOptions["api-version"]`，把 SDK 支持的日期版本转换为 `AzureOpenAIClientOptions.ServiceVersion`；未知版本在发请求前给出可读错误。
- `SupportsModelListing = false`，列表调用明确提示使用设置页手工添加 deployment；目录支持协议同步收窄为 CC，避免宣称未实现的 Responses。
- DI/Registry 注册 Azure adapter，并补齐密钥、协议、版本、采样、工具与手工模型行为测试。

### 关键行为

- 缺少 `api_key`、协议不匹配或 SDK 不支持指定 api-version 时均在网络请求前失败。
- 未配置 api-version 时使用 SDK 当前默认服务版本；配置值必须为非空字符串。
- adapter 每轮创建轻量 SDK client，共享 HttpClient 的生命周期仍由 T8 provider 管理。

### 验证证据

- Azure、目录和 DI 针对性测试：19 个通过，0 失败。
- 本批次全量回归见 T15：142 个测试通过，解决方案构建 0 警告、0 错误。

## T12 OpenRouter/自定义网关元数据

状态：已完成  
完成日期：2026-07-15

### 新增内容

- `OpenAiModelListClient` 解析 OpenRouter `data[].name/context_length/pricing` 扩展字段。
- `prompt/completion/input_cache_write/input_cache_read` 从每 token 美元乘以一百万，统一写入 descriptor 的 PerMTok 字段；同时接受 JSON 字符串和数字。
- 新增真实形状 `openrouter-models.json` fixture，覆盖名称、上下文、输入输出价格与缓存价格。
- custom 网关返回非 JSON 或缺少 OpenAI `data` 数组时，错误明确说明网关可能未实现 `/models` 或返回了非 OpenAI 形状。

### 验证证据

- OpenRouter、custom 和既有模型列表针对性测试：11 个通过，0 失败。
- 本批次全量回归见 T15。

## T14 IAiChatClientFactory

状态：已完成  
完成日期：2026-07-15

### 新增内容

- 新增 `IAiChatClientFactory`、`AiChatRuntimeInputs`、`AiChatClientLease` 和 `AiModelSelectionScopes.DesktopDefault`。
- `AiChatClientFactory` 按档案读取连接，校验连接/模型启用状态，按认证类型解析 DPAPI 密钥，再由 Registry 选择 adapter 并验证协议。
- SDK 原生客户端外依次包装自动函数调用和日志管道；lease 释放整个本轮管道，但不会处置 T8 缓存 HttpClient。
- 日志工厂包装器强制屏蔽 Trace；即使全局误开 Trace，M.E.AI 也不会记录消息正文和完整 ChatOptions。
- scope 未选择模型时返回“请在设置中为 Direct 模式选择默认模型”的可读错误。

### 验证证据

- Factory/DI 专项测试：7 个通过，覆盖成功装配、Dispose、None 认证、缺密钥、禁用/缺失记录、协议不匹配和 scope 默认选择。
- 本批次全量回归见 T15。

## T15 工作区工具集与审批装饰器

状态：已完成  
完成日期：2026-07-15

### 新增内容

- 新增 `WorkspaceAgentToolset`，把 `list_files/search_text/read_file/write_file/run_shell_command` 五个英文描述完整的 AIFunction 绑定到当轮 workspace root。
- `write_file` 和 `run_shell_command` 在 RequireApproval 下构造带唯一 execution id、参数 JSON 与 conversation id 的 `ToolApprovalRequest`。
- 用户拒绝或 approval handler 缺失时返回 `User denied this tool call.`，底层写入/shell 服务不执行；FullAccess 完全旁路审批。
- DI 注册 toolset，供后续 Direct runtime 按有无 WorkspaceRoot 组装工具。

### 验证证据

- 工具与 DI 专项测试：6 个通过，覆盖五工具契约、批准、拒绝、null handler 和 FullAccess。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，142 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。

## T16 DirectAgentChatRuntime

状态：已完成  
完成日期：2026-07-15

### 新增内容

- 新增 `DirectAgentChatRuntime`，按显式 `ModelProfileId` 或 `AiModelSelectionScopes.DesktopDefault` 从 T14 工厂取得本轮 client lease。
- 有 WorkspaceRoot 时通过 T15 绑定五个工作区工具；无工作区时向 provider 传空工具集合。
- 系统消息来自 Agent instructions；历史仅映射 User/Assistant 且非 Failed 的消息，附件暂按设计忽略。
- 进入 provider 流前发送 `RunStartedEvent(direct-{guid}, model, null)` 和 `RunStatusEvent(Requesting)`。
- 完整翻译 `TextContent`、`TextReasoningContent`、`FunctionCallContent`、`FunctionResultContent` 和 `UsageContent`；工具 call id 去重，工具类别与工作区摘要沿用统一映射。
- 工具环内多次 usage 在流末汇总成一条；异常或取消前已经收到的 usage 也会先汇总输出。
- 成功、取消和失败都转换为唯一 `RunCompletedEvent`；异常不穿出异步枚举，lease 在所有路径释放，Dispose 自身失败也不会让 transcript 挂起。
- 使用单写者 Channel 把异常捕获与异步迭代 yield 解耦，满足 C# iterator 限制并保证终态纪律。

### 作用

- Direct 模式与 CLI 模式现在共享完全相同的 `AgentStreamEvent` 和 transcript 渲染链路。
- provider SDK、自动工具环和审批结果不需要 Desktop 理解 M.E.AI 的具体内容类型。
- 长流取消、provider 异常和配置错误都能可靠结束 UI running 状态。

### 验证证据

- 脚本化 fake IChatClient 覆盖完整翻译表、call id 去重、usage 累加、历史过滤、workspace 工具、scope、成功/取消/失败和 lease Dispose。
- T16/T17/DI 专项测试：6 个通过，0 失败。
- 全量测试：147 个通过，0 失败；解决方案构建 0 警告、0 错误。

## T17 运行时契约与 Direct 分支接入

状态：已完成  
完成日期：2026-07-15

### 新增内容

- `ChatTurnRequest` 删除旧 `ProviderProfile? Profile` 和明文 `ApiKey`，新增 `Guid? ModelProfileId`；CLI kind/model/reasoning 三字段保持不变。
- `RunStartedEvent.AgentKind` 改为 `CliAgentKind?`，Direct 传 null，两个 CLI parser 继续传具体 kind。
- `DispatchingAgentChatRuntime` 注入具体 `DirectAgentChatRuntime`，按 `AgentExecutionMode.Direct` 实际分发，不再返回“Direct unavailable”。
- Infrastructure DI 注册 Direct runtime；集成测试从 dispatcher 发 Direct 请求并验证进入默认模型错误路径。
- Desktop 唯一 `ChatTurnRequest` 构造点删除旧 profile/key 解析，暂传 `ModelProfileId: null` 使用 scope 默认；T19 再接 composer 显式选择。

### 作用

- 明文提供商密钥不再经过 Desktop 请求对象，凭据只在 Infrastructure 工厂内部按需解密。
- Direct agent 已能端到端进入新后端；尚未完成 composer 选择时仍可使用设置中保存的默认档案。
- CLI 分支的请求字段和 parser 行为保持兼容。

### 验证证据

- 全仓审计仅剩新的三个 `ChatTurnRequest` 构造点，均使用 `ModelProfileId`；Agent runtime 不再读取请求 Profile/API Key。
- dispatcher 集成、Direct runtime 与 CLI parser 回归测试通过。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，147 个测试通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，构建成功，0 警告、0 错误。

## T18 审批 UI 恢复

状态：已实现，待真实桌面手动验收  
完成日期：2026-07-15

### 新增内容

- `MainWindow` 订阅 `DesktopToolApprovalHandler.ApprovalRequested`；窗口可见时显示默认拒绝的 WPF Yes/No 对话框，展示工具描述与截断后的参数摘要。
- 窗口隐藏或最小化时复用 Windows toast 的 Confirm/Cancel 按钮；`DesktopNotificationActivationService` 重新解析 approve/reject action 并调用 `TryResolve`。
- handler 为每项审批增加 5 分钟超时，超时自动返回拒绝并触发前台/系统通知提示。
- 补上 timeout callback 先于 pending item 可见时的竞态闭合；即使超短 timeout 落在注册窗口内也会自动拒绝，不会永久等待。
- 主窗口关闭时取消订阅并 `RejectAll`，避免后台 Direct 回合永久等待；调用方取消仍保持 CancellationToken 语义。
- UI 订阅异常会安全退化为拒绝，不会留下悬挂的 pending approval。

### 作用

- T15 的 RequireApproval 路径现在有真实用户决策入口，写文件与 shell 不再永久挂起。
- 前台、后台、超时和应用关闭四种状态都有明确终止行为，默认安全策略始终是拒绝。

### 验证证据

- 新增 handler 测试覆盖允许/拒绝、caller cancellation、超时与单次 expired 事件、subscriber 异常安全拒绝、重复 execution id 和 `RejectAll`。
- 新增 toast 参数往返测试，确认 approve/reject action 与 execution id 经过 URI 转义后仍能被 activation 解析。
- Desktop 项目和解决方案构建成功，0 警告、0 错误。
- 待手动验收：真实 Direct 回合触发 `write_file`/`run_shell_command`，分别核对弹窗与 toast 的允许、拒绝、超时行为。

## T19 composer 模型选择集成

状态：已实现，待真实桌面手动验收  
完成日期：2026-07-15

### 新增内容

- Desktop Agent store 恢复 `cli`/`direct` mode 解析与序列化，`ResolveRuntimeAgent` 不再强制改写成 CLI。
- `TranscriptRenderState` 新增 `agentMode`，ChatView 把 mode 传入现有 `ModelSelector`。
- Direct 模式调用 `ai-providers/list-enabled-models`，展示“模型名 + 提供商 + model id”，并从响应恢复 `desktop-default` 选择；无默认值时明确显示未选择，不伪选第一项。
- 选择 Direct 模型后调用 `set-default-model` 持久化；桥接只在服务成功后触发 `ModelSelectionChanged`，VM 将 id 放进下一轮 `ChatTurnRequest.ModelProfileId`。
- CLI 模式保留原本的 CLI 扫描、模型与 reasoning 选择逻辑；Direct 模式隐藏无关的 CLI Agent/推理设置。
- 对 list-enabled 回包增加 requestId stale guard，迟到响应不会覆盖新状态；保存失败会显示错误并重新拉取权威状态。

### 作用

- composer 可在 Direct Agent 下选择任意已启用提供商模型，选择同时成为后续 scope 默认值。
- UI 不再把本地 CLI 模型与 Direct API 模型混在同一数据源，未来 CLI Agent 仍能沿用原交互。

### 验证证据

- `npm run build`：Vite 73 个模块转换成功。
- 桥接测试确认 enabled model 列表会回传并发布持久化默认 id，desktop-default 保存成功才更新 VM，其他 scope 不污染 composer 选择。
- 全量 .NET 测试：174 个通过，0 失败；解决方案构建 0 警告、0 错误。
- 待手动验收：在两个不同提供商模型间切换并各发送一轮，确认请求使用所选 profile 且重启后恢复默认。

## T20 旧代码删除与 v21 迁移

状态：已完成  
完成日期：2026-07-15

### 新增内容

- 删除 `ProviderProfile`、`ApiStyle`、`IProfileRepository`、`SqliteProfileRepository` 和零实现零调用的 `IWorkspaceMemoryInitializationService`。
- `ConversationRecord` 与 `SqliteConversationRepository` 删除 `ProfileId/profile_id`；`SqliteMappings` 删除旧 profile 映射并调整 conversation 列索引。
- `MainWindowViewModel` 删除 profile repository/secret protector 注入、profile 列表与选择、模型 override、加载和持久化逻辑；新的 `_selectedModelProfileId` 与 `ChatTurnRequest.ModelProfileId` 保持不变。
- `App.xaml.cs` 和 Infrastructure DI 删除旧 profile repository 初始化/注册。
- SQLite schema 升至 v21；新库不再创建 `profiles` 或 `conversations.profile_id`。
- 旧库初始化时先补齐 legacy conversation 的历史增量列，再重建 `conversations`，完整复制 workspace、mode、permission、agent、channel 和时间字段，最后删除 `profiles`；消息、工具记录和 CLI session 仍指向同 id 会话。
- 仓储 round-trip 测试改为 conversation/message/tool/workspace；新库测试断言无 `profiles` 表且无 `profile_id` 列。
- 新增真实 v20 形状迁移测试，预置 profile、workspace、conversation、message、tool run 和 CLI session 后执行初始化，逐项确认业务字段与依赖数据保留。

### 作用

- 完成旧提供商配置体系与新 AI provider/model profile 体系的最终切割，避免两套模型选择和凭据路径并存。
- Conversation 不再绑定提供商；Direct 的模型选择属于每轮请求/desktop default，CLI 继续使用自身配置。
- 既有用户数据库能无损移除旧外键和表，不会因 schema 清理丢失 transcript、工具历史或 CLI resume id。

### 验证证据

- 全仓旧链路审计：除 v21 迁移实现/fixture 中的 legacy `profile_id` 外，不再存在 `ProviderProfile`、`ApiStyle`、`IProfileRepository`、`SqliteProfileRepository` 或 conversation `ProfileId` 引用。
- SQLite 专项测试：8 个通过，0 失败，包含新库结构、v20→v21 数据保留和历史增量迁移。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore`，147 个通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx --no-restore`，0 警告、0 错误。

## T21 文档更新与全量回归

状态：自动化已完成，待真实桌面手动验收  
完成日期：2026-07-15

### 新增内容

- 更新根 `AGENTS.md`：运行时描述从 CLI-only 改为 Direct/CLI 双分支，补充 AI provider settings/factory、Direct 工具审批、composer 模型选择、DI 注册与 v21 表结构。
- 更新设计文档与任务文档的实施状态，明确已落地范围、自动化证据和仍未完成的真实桌面验收。
- 最终审计确认 MCP server 定义仍未进入 runtime（VM 继续传空列表），DeepSeek `reasoning_content` spike 与 Gemini 原生 generateContent 仍是开放项。
- 测试项目纳入 Desktop 引用，新增 27 个 Desktop 契约测试；测试 TFM 与 Desktop 统一为 `net10.0-windows10.0.19041.0`。
- 显式提升通知包带入的 `System.Drawing.Common` 至 10.0.9，并把 `SQLitePCLRaw.bundle_e_sqlite3` 从易受高危公告影响的 2.1.11 提升到 2.1.12。
- 修正浏览器 fallback 的 Gemini 默认协议为当前实际支持的 OpenAI Chat Completions，并移除像真实凭据的 mock key；更新 composer 的 Direct 已接线注释。

### 作用

- 让后续开发者看到的项目上下文与当前代码一致，不再误判 Direct、AI provider settings、审批 UI 或旧 profiles 为未接线状态。
- 将自动化完成和人工验收严格区分，避免把未运行的真实 API Key/GUI 流程写成已通过。

### 验证证据

- Desktop 专项测试：27 个通过，覆盖 T5/T18/T19 可自动化边界。
- 全量 .NET 测试：174 个通过，0 失败。
- 解决方案构建：0 警告、0 错误。
- TranscriptVue production build：Vite 73 个模块转换成功。
- `dotnet list ... package --vulnerable --include-transitive`：当前 Tests 完整依赖树无已知易受攻击包。
- 待手动验收：T7 的真实提供商 CRUD/密钥/重启流程、T18 的 WPF/toast 允许拒绝超时、T19 的跨提供商模型切换与完整 Direct 工具回合。因此 T21/P1/P3 不标记为最终验收完成。

## 代码审查修复（2026-07-16）

状态：已完成  
完成日期：2026-07-16

针对全量代码审查发现的问题所做的修复与优化，不改变任何任务的对外契约。

### 缺陷修复

- **设置页 API Key 掩码/草稿分离**（审查 H1）：输入框不再预填掩码，改为空草稿 + 掩码 placeholder；只有用户实际输入的内容才会上传，杜绝掩码串被局部编辑后当作新密钥覆盖真实密钥。清空语义保留：输入后删空并失焦即上传空字符串删除密钥。
- **v21 conversations 重建原子化**（审查 H2）：重建-拷贝-删表-改名包进单个 `BEGIN IMMEDIATE` 事务，进程中断不再可能留下"无 conversations 表"或"下次启动建表冲突"的库；事务前先 `DROP TABLE IF EXISTS conversations_new` 以从历史中断状态自恢复。
- **JsonElement 数值读取健壮性**（审查 M1）：`AiProviderSettingsService.ReadInt64/ReadDecimal` 与 `AnthropicModelListClient.ReadOptionalInt64` 先检查 `ValueKind == Number`，字符串形态的 `display.*` 或远端字段不再使整个状态读取/列表解析抛异常，而是按缺失处理。
- **upsert-model 编辑不再隐式重启用**（审查 M2）：`UpsertModelCommand.Enabled` 改为 `bool?`；桥接缺省该字段时传 null，服务端回退保留现有 `IsEnabled`，仅创建路径默认启用。
- **短密钥掩码边界**（审查 M6）：长度不足 8 的 API Key 只回显 `****`，不再完整泄露尾部。

### 优化

- **list-enabled-models 轻量化**（审查 M3）：`IAiProviderSettingsService` 新增 `GetDefaultModelAsync(scope)`，桥接不再为拿默认模型 id 而调用 `GetStateAsync`（后者会解密全部连接的 API Key）。composer 打开时不再触发任何 DPAPI 解密。
- **Direct 流孤儿 producer 防护**（审查 M5）：`DirectAgentChatRuntime.StreamCoreAsync` 用链接 CTS 包裹 producer，消费方提前放弃枚举时会取消 provider 流并等待 producer 收尾，lease 确定释放。
- **删除静态 HttpClientProvider 兜底**：`OpenAiModelListClient`/`AnthropicModelListClient` 移除无参构造与 `static` 共享 provider；`OpenAiProviderAdapter`/`AnthropicProviderAdapter` 兜底改为实例级构造（生产路径始终由 DI 注入统一单例）。
- **密钥解析收敛**：新增 `AiProviderSecrets.RequireApiKey`，5 处重复的 `ResolveApiKey` 逻辑与 `api_key` 常量统一到单点。
- **模型列表非 200 错误可读化**：新增 `AiProviderHttpResponses.EnsureSuccessAsync`，OpenAI/Anthropic/Gemini 列表请求失败时错误消息带状态码与截断后的响应体（上限 500 字符），不再丢弃提供商返回的错误详情。
- **前端 requestId 传递**：`useAiProviderHost.request()` 直接在返回的 promise 上暴露 `requestId`，`loadState` 不再用 `[...pendingRequests.keys()].at(-1)` 反查。

### 审查结论中确认无需修改的项

- text/thinking 增量共用 blockId：桌面渲染层（`MainWindowViewModel.AgentStream.cs`）不按 blockId 分块，思考内容经 `WrapThinking` 标记区分，无冲突。
- OllamaSharp 5.4.25 `OllamaApiClient.Dispose()` 实测不会处置注入的 HttpClient，`ListModelsAsync` 中 `using` 缓存客户端安全。
- `CreateForScopeAsync` 的中文错误文案为设计文档 §5.1 明确规定的用户可见文案，保留。

### 验证证据

- 新增 6 个测试：v21 残留 `conversations_new` 自恢复、upsert-model 编辑保留禁用态、短密钥掩码、非数值 display 元数据容错、模型列表 401 带响应体错误、Direct 消费方提前退出取消 producer。
- 全量测试：`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj`，180 个通过，0 失败。
- 解决方案构建：`dotnet build SelfClaw.slnx`，0 警告、0 错误。
- TranscriptVue production build：`npm run build` 通过。

## composer 模式切换（2026-07-16）

状态：已完成（自动化）；实机核对并入 T19 手动验收  
完成日期：2026-07-16

### 背景

T19 的 composer Direct 模型选择依赖"当前 Agent 的 front matter 为 direct 模式"才会激活；内置 Agent 均为 CLI 模式，导致设置页启用的提供商模型在 composer 无入口可选。

### 新增内容

- ModelSelector 弹层"模式"段由只读指示改为可选分段控件：本地 CLI / 提供商；选择"提供商"后即复用既有 Direct 数据源（`ai-providers/list-enabled-models`，展示启用连接下的启用模型并持久化 desktop-default）。
- 新增 WebView 消息 `select-composer-mode`（`mode: 'cli' | 'direct'`），`MainWindow` 路由到 `MainWindowViewModel.SelectComposerModeAsync`。
- VM 新增 composer 级模式覆盖 `_composerModeOverride`：持久化到 `desktop-settings.json` 的 `composer.executionMode` 节点，启动时回读；有效模式 = 覆盖值 ?? Agent front matter 模式。
- 发送回合时把有效模式写回 `AgentRuntimeDefinition.Mode`，CLI Agent 可直接以提供商模型跑 Direct 回合（反之亦然），无需另建 Direct Agent。
- 有效模式纳入 shell 渲染指纹与 `TranscriptRenderState.AgentMode`，仅模式变化也会推送前端刷新选择器数据源。

### 关键行为

- 覆盖值为全局 composer 偏好，跨会话与 Agent 生效；未选择过时完全跟随 Agent 定义。
- 提供商模式下未选默认模型时发送回合，仍按 T14 语义得到"请在设置中为 Direct 模式选择默认模型"的可读失败。
- CLI 字段（CliAgent/CliModel/CliReasoningEffort）与 `ModelProfileId` 继续同时进请求，由对应运行时各取所需。

### 验证证据

- 全量测试：180 个通过，0 失败；解决方案构建 0 警告、0 错误。
- TranscriptVue production build 通过。
- 待实机核对（并入 T19）：切到"提供商"选模型发送一轮、重启后模式与模型恢复、切回"本地 CLI"后 CLI 回合不受影响。
