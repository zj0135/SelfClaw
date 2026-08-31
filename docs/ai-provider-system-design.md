# 多 AI 提供商后端架构设计（Direct 模式重写基座）

> 状态：设计稿 v2（2026-07-14），主体已于 2026-07-15 实施；结果与剩余限制见 §14。
> 前提：旧 Direct 后端（`ProviderProfile`/`ApiStyle`/`SqliteProfileRepository` 及相关链路）已废弃，不把旧 profile 配置转换为新 provider/model 配置。v21 仅执行必要的 schema 清理迁移，并完整保留 conversation 及其依赖数据。本设计是全新 Direct 模式的地基，同时为设置页 `AIProviders.vue` 提供真实后端。
>
> **v2 变更**（基于对当前代码库的逐文件核对）：
> 1. 类型命名全面对齐已存在的骨架代码（`AiProviderApiFormat`、`IAiProviderRepository` 等），避免无意义重命名；新增 §0 现状基线。
> 2. 修正 `ChatTurnRequest` 契约——v1 稿遗漏了必须保留的 `CliAgent/CliModel/CliReasoningEffort` 字段。
> 3. 修正 Azure OpenAI 模型列表能力（数据面已无法列 deployments，改为手动录入）。
> 4. 标记 DeepSeek `reasoning_content` 经 OpenAI 官方 SDK 不可达的已证实风险（dotnet/extensions#6208）及对策。
> 5. §7 持久化改为"沿用现有三张表 + 增量列"，并纳入 `conversations.profile_id` 外键重建（v1 稿遗漏，删 `profiles` 表会因此失败）。
> 6. 新增 §5.4 组合器（composer）模型选择集成、审批 UI 恢复（当前无人订阅 `ApprovalRequested`）、远端模型合并语义（§6.1）、§13 风险与开放问题。

---

## 0. 现状基线（2026-07-14 核对）

实施前必须知道的事实——本设计**不是从零开始**：

| 部件 | 现状 |
|---|---|
| `SelfClaw.Infrastructure/AiProviders/` 骨架 | **已存在**：枚举 ×3、记录 ×6、`IAiProviderAdapter`/`IAiProviderRegistry`/`AiProviderRegistry`、OpenAI 适配器（partial ×3，CC+Responses 均产出 `IChatClient`）、Anthropic 适配器，均有单测 |
| 持久化 | **已存在**：`IAiProviderRepository` + `SqliteAiProviderRepository`（13 个方法，全 CRUD），三张 `ai_provider_*` 表已建（schema v19），有 `SqliteRepositoriesTests` 覆盖 |
| DI 接线 | **未接入**：仅 `IAiProviderRepository` 注册；适配器与 Registry **未注册**，整个子系统对运行中的应用不可达 |
| Direct 分支 | `AgentExecutionMode.Direct = 0` 已存在；`DispatchingAgentChatRuntime` 的 `_ =>` 分支干净地返回失败事件（"The Direct execution mode is not available yet"） |
| `AIProviders.vue` | 100% 本地 mock，是全仓唯一未接后端的设置面板；所有交互只弹 toast |
| 工具服务 | `IWorkspaceToolService`（5 方法：ListFiles/SearchText/ReadFile/WriteFile/RunShellCommand）已实现并注册，但**无任何运行时消费者**；尚未包装为 `AIFunction`（仅测试里用过 `AIFunctionFactory`） |
| 审批 | `IToolApprovalHandler` + `DesktopToolApprovalHandler`（TCS 字典 + `ApprovalRequested` 事件 + `TryResolve`）已注入，但**前端重构时审批 UI 被移除，当前无人订阅事件**——若今天有工具请求审批将永久挂起 |
| 密钥 | `ISecretProtector`/`DpapiSecretProtector` 完整可用：文件式 DPAPI（`secret:{guid}` → SecretsDirectory 下 `.bin`），API 为 `StoreSecretAsync(secret, existingRef?)` / `RetrieveSecretAsync` / `DeleteSecretAsync` |
| NuGet | `Microsoft.Extensions.AI(.OpenAI)` 10.7.0、`Microsoft.Agents.AI.Anthropic` 1.3.0-preview、OpenAI SDK 2.11.0（传递引用）。**缺**：OllamaSharp、Azure.AI.OpenAI、任何 Gemini 包 |

**命名裁定（v2）**：一律沿用现有代码命名，v1 稿中的新名作废——

| v1 稿命名 | 采用（现有代码） |
|---|---|
| `AiApiFormat` | `AiProviderApiFormat` |
| `AiModelProfile.ModelId` / `DisplayName` | `Model` / `Name` |
| `AiModelProfile.ConnectionId` | `ProviderConnectionId` |
| `AiModelSelection` | `AiModelProfileSelection` |
| `AiClientRequest` | `AiProviderClientRequest` |
| `IAiProviderStore` / `SqliteAiProviderStore` | `IAiProviderRepository` / `SqliteAiProviderRepository` |
| `IAiProviderAdapter.Kind` | `ProviderKind` |
| 表 `ai_model_selections` | `ai_model_profile_selections` |

---

## 1. 目标与非目标

### 目标

1. **多提供商**：OpenAI、Anthropic、Google Gemini、DeepSeek、OpenRouter、Ollama、Azure OpenAI，以及任意 OpenAI 兼容网关（LongCat、Routin 等聚合站以"自定义"方式接入）。
2. **多线协议**：OpenAI Chat Completions、OpenAI Responses、Anthropic Messages、Gemini generateContent、Ollama 原生协议。协议与提供商解耦——同一连接下不同模型可走不同协议。
3. **支撑设置页全部交互**：提供商增删改、启停、API Key 管理、代理地址、连通性检查、远端模型列表拉取、模型级启停与参数配置、默认模型选择。
4. **成为 Direct 执行模式的唯一模型来源**：新的 `DirectAgentChatRuntime` 通过本体系取得可运行的 `IChatClient`，接入现有 `IAgentChatRuntime` / `AgentStreamEvent` 渲染契约。
5. **密钥安全**：DPAPI 加密落盘，明文永不出后端、永不回传前端。

### 非目标

- 不兼容旧 `profiles` 表和 `ChatTurnRequest.Profile/ApiKey` 旧字段（直接删除重定义）。
- 不做多用户/云同步。
- 不覆盖 embedding、图像生成等非对话端点（模型列表允许出现，但执行层 v1 只做 chat）。

---

## 2. 总体架构

```
┌────────────────────────────────────────────────────────────────────┐
│ Vue: settings/AIProviders.vue（现 mock → 接桥接）                    │
│      chat composer 模型选择器（P3，Direct 模型接入）                  │
└───────────────▲────────────────────────────────────────────────────┘
                │ WebView2 JSON 消息（type + requestId，沿用现有模式）
┌───────────────┴────────────────────────────────────────────────────┐
│ Desktop                                                             │
│   AiProviderSettingsBridge   —— 消息路由 + DTO 映射（密钥只出掩码）    │
│   （MainWindow switch 只按 "ai-providers/" 前缀转发一条）              │
└───────────────▲────────────────────────────────────────────────────┘
                │
┌───────────────┴──────────────── Infrastructure ────────────────────┐
│                                                                     │
│  设置面                          执行面                              │
│  ┌───────────────────────┐     ┌─────────────────────────────────┐ │
│  │IAiProviderSettings-   │     │ DirectAgentChatRuntime           │ │
│  │Service                │     │   : IAgentChatRuntime            │ │
│  │ CRUD/启停/检查/拉模型   │     │   （接入 Dispatching 的 Direct 分支）│ │
│  └──────────┬────────────┘     └───────────────┬─────────────────┘ │
│             │                                  │                    │
│             │            ┌─────────────────────▼──────────────────┐│
│             │            │ IAiChatClientFactory                    ││
│             │            │  profileId → 读库 → 解密 → 选适配器      ││
│             │            │  → IChatClient + ChatOptions（含中间件） ││
│             │            └─────────────────────┬──────────────────┘│
│             ▼                                  ▼                    │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │ IAiProviderRegistry ──► IAiProviderAdapter（按 ProviderKind）   │ │
│  │   OpenAI（CC+Responses） Anthropic（Messages）                  │ │
│  │   Gemini（P2 借 OpenAI 兼容层） Ollama（原生） AzureOpenAI       │ │
│  └──────────────────────────────────────────────────────────────┘ │
│                                                                     │
│  统一执行抽象：Microsoft.Extensions.AI 的 IChatClient                │
│                                                                     │
│  AiProviderCatalog（内置目录，静态代码）                              │
│  IAiProviderRepository（SQLite，沿用现有三张表 + 增量列）             │
│  ISecretProtector（DPAPI，复用现有实现，零改动）                      │
│  AiProviderHttpClientProvider（按连接缓存 HttpClient/代理/超时）      │
└─────────────────────────────────────────────────────────────────────┘
```

**基石决策：以 `Microsoft.Extensions.AI` 的 `IChatClient` 为统一执行抽象**（10.7.0 已引入，现有两个适配器已产出 `IChatClient`）。消息模型、流式增量、工具调用、用量统计由它统一承担。自研代码只负责：凭据/端点装配、方言差异（thinking、raw options）、协议选择。

---

## 3. 核心领域模型

四个概念分层，协议挂在**模型**上而非提供商上：

```
AiProviderCatalogEntry（内置目录，静态代码）——【新增类型】
    "OpenAI / Anthropic / Gemini / …"，默认端点、支持的协议、认证方式、
    获取 Key 的 URL、logo/主题色、预置模型元数据
        │ 1 : N（同一目录条目可建多个连接，如"Routin AI"与"Routin AI（套餐）"）
        ▼
AiProviderConnection（连接，持久化）= UI 左侧列表的一行 ——【已存在，+CatalogId】
    目录归属、显示名、endpoint（= UI"API 代理地址"）、认证方式、
    密钥引用（SecretRef，非明文）、连接级选项、启用开关
        │ 1 : N
        ▼
AiModelProfile（模型档案，持久化）= UI 模型列表的一行 ——【已存在，+IsEnabled】
    模型 id、【线协议 AiProviderApiFormat】、采样参数、模型级选项、启用开关
        │
AiModelProfileSelection（scope → 模型档案）——【已存在，零改动】
    "desktop-default" / "pet" 等场景各自记住选中的模型
```

### 3.1 枚举（增量修改，不重命名）

```csharp
/// 决定由哪个 Adapter 构造客户端。只有"SDK 或构造方式不同"才新增成员；
/// 纯粹换域名的提供商一律 OpenAICompatible + 目录默认值。
public enum AiProviderKind
{
    OpenAI = 0,            // 已存在
    OpenAICompatible = 1,  // 已存在
    DeepSeek = 2,          // 已存在（枚举有、adapter 无）——OpenAI 兼容 + thinking 方言，见 §4.3/§13
    Anthropic = 3,         // 已存在
    GoogleGemini = 4,      // 新增
    Ollama = 5,            // 新增
    AzureOpenAI = 6        // 新增
}

/// 线协议。挂在 AiModelProfile 上，一个 Adapter 可支持多种。
public enum AiProviderApiFormat
{
    OpenAIChatCompletions = 0,  // 已存在
    OpenAIResponses = 1,        // 已存在
    AnthropicMessages = 2,      // 已存在
    GeminiGenerateContent = 3,  // 新增（P2 之后才有 adapter 支持）
    OllamaNative = 4            // 新增
}

public enum AiProviderAuthKind
{
    ApiKey = 0,   // 已存在，保持 0 不重编号（库里已有存量 int）
    None = 1      // 新增：Ollama 本地
}
```

### 3.2 记录类型

已存在的记录只做增量（新增字段放尾部带默认值，减少构造点改动）：

```csharp
// 已存在 → 新增 CatalogId
public sealed record AiProviderConnection(
    Guid Id,
    string CatalogId,                              // 新增："openai" | "anthropic" | … | "custom"
    string Name,
    AiProviderKind ProviderKind,
    Uri Endpoint,
    AiProviderAuthKind AuthKind,
    IReadOnlyDictionary<string, string> CredentialRefs,        // 名称 → SecretRef（非明文）
    IReadOnlyDictionary<string, JsonElement> ConnectionOptions, // 超时/extra_headers/azure api-version 等
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsEnabled = true);

// 已存在 → 新增 IsEnabled（is_enabled 列在表里已有，记录/映射缺失，补齐即可）
public sealed record AiModelProfile(
    Guid Id,
    Guid ProviderConnectionId,
    string Name,                                   // UI 显示名
    AiProviderApiFormat ApiFormat,
    string Model,                                  // 发给 API 的模型名（Azure 下为 deployment 名）
    AiSamplingOptions Sampling,
    IReadOnlyDictionary<string, JsonElement> ModelOptions,   // 开放袋：reasoning、max_tokens、display.* 等
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsEnabled = true);

// 已存在，零改动。每项带显式启用位，未启用的参数不上请求
// （顺带规避 Anthropic 新款模型收到 temperature/top_p 即 400 的问题——默认不启用就不发送）。
public sealed record AiSamplingOptions(
    bool TemperatureEnabled, double Temperature,
    bool TopPEnabled, double TopP);

// 已存在，零改动
public sealed record AiModelProfileSelection(
    string Scope,                                  // "desktop-default" / "pet" / …
    Guid ModelProfileId,
    DateTimeOffset UpdatedAtUtc);

// 新增：目录条目（静态代码，不落库）
public sealed record AiProviderCatalogEntry(
    string CatalogId,                              // "openai" | "anthropic" | … | "custom"
    string DisplayName,
    string Subtitle,                               // UI 副标题，如 "OpenAI Chat Completions 兼容"
    string AccentColor,
    AiProviderKind ProviderKind,
    Uri DefaultEndpoint,
    AiProviderApiFormat DefaultApiFormat,
    IReadOnlyList<AiProviderApiFormat> SupportedFormats,
    AiProviderAuthKind AuthKind,
    string? GetApiKeyUrl,                          // UI"获取 API Key"链接
    bool SupportsModelListing,
    IReadOnlyList<AiModelDescriptor> WellKnownModels); // 预置模型 + 展示元数据（P1 可为空列表）

// 新增：远端模型描述（/models 拉取结果 & 目录预置共用）。字段尽力而为：
// OpenAI /v1/models 只有 id；OpenRouter /models 带 context_length 与 pricing；
// Anthropic /v1/models 自 2026-03 起带 max_input_tokens / max_tokens，可回填 ctx/out。
public sealed record AiModelDescriptor(
    string ModelId,
    string? DisplayName,
    long? ContextLength,
    long? MaxOutputTokens,
    decimal? PriceInPerMTok, decimal? PriceOutPerMTok,
    decimal? PriceCacheWritePerMTok, decimal? PriceCacheReadPerMTok);
```

**模型展示元数据（上下文窗口/价格）** 不建列，存 `ModelOptions` 的 `display.*` 键（数值存原始数字，不存格式化字符串；`ctx="391K"`、`inp="$1.75"` 这类格式化由前端完成，见 §8）。来源优先级：用户手动编辑 > 远端 `/models` 返回 > 目录 `WellKnownModels` 预置。

### 3.3 内置目录

| CatalogId | ProviderKind | 默认协议 | 可选协议 | 认证 | 拉模型列表 |
|---|---|---|---|---|---|
| `openai` | OpenAI | Responses | CC、Responses | ApiKey | ✔ `GET /v1/models`（仅 id） |
| `anthropic` | Anthropic | AnthropicMessages | — | ApiKey | ✔ `GET /v1/models`（带 display_name、ctx、max output；`after_id` 分页） |
| `google-gemini` | GoogleGemini | GeminiGenerateContent | +CC（其 OpenAI 兼容层 `…/v1beta/openai/`） | ApiKey | ✔ `GET /v1beta/models`（带 token 限制） |
| `deepseek` | DeepSeek | CC | — | ApiKey | ✔ `GET /models` |
| `openrouter` | OpenAICompatible | CC | — | ApiKey | ✔ `GET /api/v1/models`（含 ctx/价格元数据，最丰富） |
| `ollama` | Ollama | OllamaNative | +CC | None | ✔ `GET /api/tags` |
| `azure-openai` | AzureOpenAI | CC | — | ApiKey | ✖ **手动录入**（v1 稿有误：数据面列 deployments 的端点已随 2023-03-15-preview 之后的 API 版本退役，列 deployments 需管理面 + Entra 凭据，纯 api-key 做不到；数据面 `/openai/models` 返回的是基础模型而非部署，对 UI 无用） |
| `custom` | OpenAICompatible | CC | CC、Responses | ApiKey | ✔（尽力，按 OpenAI 形状解析，失败给出可读错误） |

LongCat、Routin 等聚合站 = `custom` 目录条目的连接实例（或后续按需求补目录项，零代码：只加目录数据）。

---

## 4. 协议适配层

### 4.1 适配器接口（现有 4 成员 + 新增 2 成员）

```csharp
/// 一个 Adapter 拥有一个 AiProviderKind，可支持多种 AiProviderApiFormat。
/// 封装 SDK 选择、凭据/端点装配、采样映射、方言 raw options 注入、模型列表拉取。
public interface IAiProviderAdapter
{
    AiProviderKind ProviderKind { get; }                     // 已存在
    bool SupportsApiFormat(AiProviderApiFormat apiFormat);   // 已存在
    IChatClient CreateChatClient(AiProviderClientRequest request);   // 已存在
    ChatOptions CreateChatOptions(AiProviderClientRequest request);  // 已存在

    bool SupportsModelListing { get; }                       // 新增

    /// 新增：从提供商 API 拉取可用模型（/v1/models、/v1beta/models、/api/tags…）。
    /// SupportsModelListing == false 时抛 NotSupportedException。
    Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
        AiProviderConnection connection,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken);
}
```

`AiProviderClientRequest`（已存在，零改动）：`(Connection, Profile, Secrets, EnableReasoning, Tools)`——`Secrets` 为已解密明文字典（`"api_key"` → 明文，仅内存、即用即弃）。

### 4.2 注册表

`IAiProviderRegistry` / `AiProviderRegistry` 已存在（DI 收集全部 adapter、按 Kind 索引、重复注册 fail-fast、未注册抛 `KeyNotFoundException`），零改动；缺的是把 adapter 实例**注册进 DI**（见 §9）。

### 4.3 各适配器实现要点

| Adapter | Formats | 实现 | 要点 |
|---|---|---|---|
| `OpenAiProviderAdapter`（已存在） | CC、Responses | OpenAI 官方 SDK + M.E.AI.OpenAI | 保留现有三 partial 结构；同类多实例注册服务 `OpenAI` / `OpenAICompatible` / `DeepSeek` 三个 Kind（ctor 已支持传 Kind，需放开 DeepSeek 守卫）；新增 `OpenAiModelListClient`（`GET /v1/models` 解析；OpenRouter 形状带 context_length/pricing 时回填 descriptor） |
| `AnthropicProviderAdapter`（已存在） | AnthropicMessages | Microsoft.Agents.AI.Anthropic（preview） | 现有实现保留（注意它经 `AsAIAgent` 回调捕获 `IChatClient`，是绕行 hack，见 §13-R4）；新增 `ListModelsAsync` 走 `GET /v1/models`（`x-api-key` + `anthropic-version` 头；响应含 display_name/ctx/max_tokens，回填 descriptor；`after_id` 分页） |
| `GeminiProviderAdapter`（新增，P2） | GeminiGenerateContent（P3+ 才做原生）、CC | P2 先借其 OpenAI 兼容端点（`…/v1beta/openai/`）复用 OpenAI SDK 走 CC 快速可用；模型列表走原生 `GET /v1beta/models?key=`（`x-goog-api-key` 或 query key） | 原生 generateContent（thinkingBudget 等能力）推迟到 P2 之后按需求做；届时二选一：社区 IChatClient 包 / 轻量 REST 自实现（spike 决定） |
| `OllamaProviderAdapter`（新增，P2） | OllamaNative（+CC） | OllamaSharp（自带 IChatClient，需新增包引用） | `AuthKind.None`（跳过密钥校验）；模型列表走 `GET /api/tags`；本地默认端点 `http://localhost:11434` |
| `AzureOpenAiProviderAdapter`（新增，P2） | CC | Azure.AI.OpenAI（继承 OpenAI SDK，需新增包引用） | `Model` 字段即 deployment 名；`api-version`、资源端点存 `ConnectionOptions`；**SupportsModelListing = false**，UI 手动添加模型；Responses 待有明确运行时需求再接入 |

**DeepSeek thinking 方言（已证实风险）**：请求侧注入 `thinking.type` 等 raw options 现有代码已支持；但**响应侧** DeepSeek 的非标 `reasoning_content` 字段会被 OpenAI 官方 SDK 反序列化时丢弃，无法经 M.E.AI 透出为 `TextReasoningContent`（[dotnet/extensions#6208](https://github.com/dotnet/extensions/issues/6208)）。对策分级：
- **P1/P2**：接受"思考内容不显示"，正文正常（不阻塞任何链路）；
- **P3 或之后**（若需要思考流）：在 DeepSeek/OpenAICompatible 路径挂一个 **SSE 重写 `DelegatingHandler`**，把增量 JSON 里的 `delta.reasoning_content` 改写进自定义可达字段，或干脆为 CC 方言自写轻量 `IChatClient`（协议简单，仅 chat + SSE）。任务化为独立 spike，不做进主干承诺。

**连通性检查不属于适配器**：设置服务用与执行面相同的装配路径构造该（连接, 所选模型档案）的 `IChatClient`，发一条 `MaxOutputTokens = 1` 的 ping（消息 `"ping"`）并计时——一次验证 endpoint、密钥、模型名、协议四件事，正好对应 UI"下拉选模型 + 检查"的交互。（对 embedding 模型会失败，属预期：检查请选对话模型。）错误信息原样透传（SDK 的 401/404/超时消息足够可读）。

### 4.4 HttpClient 管理

`AiProviderHttpClientProvider`（新增）：按连接指纹（endpoint + 代理 + 超时 + extra headers 摘要）缓存 `HttpClient`，注入各 SDK 的 transport。统一落点：

- `ConnectionOptions.timeout_seconds` → **仅用于非流式操作**（列模型、连通性检查）的请求超时；聊天流式请求 `HttpClient.Timeout` 设为 `Timeout.InfiniteTimeSpan`，靠 `CancellationToken` 终止（否则长生成会被整体超时误杀）
- `ConnectionOptions.extra_headers` → OpenRouter 的 `HTTP-Referer`/`X-Title` 等（以 `DelegatingHandler` 注入，或 SDK 自带 header 选项）
- 中转/代理地址即 `Endpoint` 本身（如 `https://zyapi.example.com/v1`），无需额外代理概念；系统代理走 `HttpClient` 默认行为
- 底层用 `SocketsHttpHandler { PooledConnectionLifetime = 5min }`，避免长寿单例 HttpClient 的 DNS 失效问题

各 SDK 注入点：OpenAI/Azure `OpenAIClientOptions.Transport = new HttpClientPipelineTransport(httpClient)`；OllamaSharp 构造函数直接收 `HttpClient`；Anthropic（Microsoft.Agents.AI.Anthropic）如无注入口则退化为仅超时/头配置（记录到 §13-R4）。

`IChatClient` 本身轻量，**每轮新建**（配置变更立即生效），重的 `HttpClient` 靠 provider 缓存。

---

## 5. 执行层：工厂与 Direct 运行时

### 5.1 IAiChatClientFactory（新增）

```csharp
public interface IAiChatClientFactory
{
    /// 按模型档案构造可运行客户端：读库 → 校验（连接与模型均 IsEnabled）→
    /// ISecretProtector 解密 CredentialRefs → Registry 选 Adapter →
    /// SupportsApiFormat 校验 → CreateChatClient/Options → 包 M.E.AI 管道。
    /// 任一环节失败抛带用户可读消息的异常（由运行时翻译为 RunCompletedEvent(Failed)）。
    Task<AiChatClientLease> CreateAsync(
        Guid modelProfileId, AiChatRuntimeInputs inputs, CancellationToken ct);

    /// 按场景默认选择（AiModelProfileSelection）构造；scope 无选择时抛可读异常
    ///（"请在设置中为 Direct 模式选择默认模型"）。
    Task<AiChatClientLease> CreateForScopeAsync(
        string scope, AiChatRuntimeInputs inputs, CancellationToken ct);
}

public sealed record AiChatRuntimeInputs(
    bool EnableReasoning,
    IReadOnlyList<AITool> Tools);

/// Dispose 只处置 IChatClient 管道，不触碰缓存的 HttpClient。
/// 由 DirectAgentChatRuntime 在流结束（含异常/取消）后负责 Dispose。
public sealed record AiChatClientLease(
    IChatClient Client, ChatOptions Options, AiModelProfile Profile) : IDisposable;
```

管道组装（自内向外）：`SDK 原生 client` → `UseFunctionInvocation()`（自动工具环）→ `UseLogging()`（脱敏：不打 headers/key/消息正文）。

场景常量：`public static class AiModelSelectionScopes { public const string DesktopDefault = "desktop-default"; }`（后续加 `"pet"` 等）。

### 5.2 DirectAgentChatRuntime（新增）

补上 `DispatchingAgentChatRuntime` 预留的 `AgentExecutionMode.Direct` 分支（该 dispatcher 按具体类型注入，需给 ctor 加 `DirectAgentChatRuntime` 参数并注册具体类型，同 `CliAgentChatRuntime` 的既有做法）：

```csharp
public sealed class DirectAgentChatRuntime : IAgentChatRuntime
{
    // ChatTurnRequest → 组装 ChatMessage 列表（Agent.Instructions 为系统消息 +
    //   Messages 历史映射：MessageRole.User/Assistant → ChatRole，MarkdownContent 为文本）
    // → 组装 Tools（见下"工具与审批"；WorkspaceRoot == null 则不带工具）
    // → factory.CreateAsync(request.ModelProfileId!.Value, ...) 或
    //   request.ModelProfileId == null 时 CreateForScopeAsync(DesktopDefault, ...)
    // → client.GetStreamingResponseAsync(messages, options, ct)
    // → 将流式增量翻译为 AgentStreamEvent
}
```

**ChatTurnRequest 契约更新**（删 2 字段、加 1 字段；**保留 CLI 三字段**——v1 稿遗漏了它们，CLI 分支正在使用）：

```csharp
public sealed record ChatTurnRequest(
    Guid ConversationId,
    Guid? ModelProfileId,              // 新增：Direct 模式指定模型档案；null 时用 scope 默认
    WorkspaceRoot? WorkspaceRoot,      //（删除原 ProviderProfile? Profile 与 string? ApiKey——
    ConversationMode Mode,             //  明文密钥不再经过请求对象，由工厂内部解析）
    AgentRuntimeDefinition Agent,
    CliAgentKind? CliAgent,            // 保留（CLI 分支）
    string? CliModel,                  // 保留（CLI 分支）
    string? CliReasoningEffort,        // 保留（CLI 分支）
    ToolPermissionMode ToolPermissionMode,
    IToolApprovalHandler? ToolApprovalHandler,
    IReadOnlyList<MessageRecord> Messages);
```

改动面已核实：`ChatTurnRequest` **仅有一处按位置构造**（`MainWindowViewModel.cs:766`），且 `Profile`/`ApiKey` 在全仓是只写字段（无任何运行时读取），删除安全。

**迭代器纪律**（与 `CliAgentChatRuntime` 完全一致，UI 依赖此契约）：
- `async IAsyncEnumerable` + `[EnumeratorCancellation]`；**绝不向外抛异常**——任何早期失败 `yield return RunCompletedEvent(Failed, null, 可读消息)` 后 `yield break`；
- 全程**恰好一个**终态 `RunCompletedEvent`（跟踪 `runCompletedEmitted`，流意外结束时兜底合成）；
- 事件顺序：`RunStartedEvent` → `RunStatusEvent` → 增量事件交错 → `UsageReportedEvent` → `RunCompletedEvent`。

**M.E.AI 流式增量 → AgentStreamEvent 翻译表**（渲染契约不动，`MainWindowViewModel.AgentStream.cs` 按事件子类型分发、与运行时无关，Vue 零改动）：

| 时机 / M.E.AI 流式内容 | AgentStreamEvent |
|---|---|
| 进入流（发首个请求前，勿等首个增量） | `RunStartedEvent(sessionId: $"direct-{Guid:N}", model: profile.Model, agentKind: null)` + `RunStatusEvent(Requesting)` |
| `TextContent` 增量 | `AssistantTextDeltaEvent(blockId, delta)`（blockId 用 `ChatResponseUpdate.MessageId ?? 序号` 分组） |
| `TextReasoningContent` 增量 | `AssistantThinkingDeltaEvent(blockId, delta)` |
| `FunctionCallContent` | `ToolCallStartedEvent(callId, name, argsJson, MapToolKind(name))`（参数字典 JSON 序列化；按 callId 去重） |
| `FunctionResultContent` | `ToolCallCompletedEvent(callId, status, summary, content)`（summary 用 `WorkspaceToolSummaries`） |
| `UsageContent`（工具环内每次模型往返都可能出现一次） | **累加**，不逐条发；流结束前发一条汇总 `UsageReportedEvent(inputTokens, outputTokens)` |
| 流正常结束 | `RunCompletedEvent(Succeeded, finalText: 累积文本, null)` |
| `OperationCanceledException` | `RunCompletedEvent(Cancelled, 累积文本, null)` |
| 其余异常 | `RunCompletedEvent(Failed, 累积文本或 null, ex.Message)` |

**契约微调**：`RunStartedEvent.AgentKind` 由 `CliAgentKind` 改为 `CliAgentKind?`，Direct 传 `null`。已核实该属性全仓**只写不读**（两个 CLI parser 赋值、VM 匹配 `case RunStartedEvent:` 不绑定值），改动零风险。

**工具与审批**：`IWorkspaceToolService` 的 5 个方法经 `AIFunctionFactory.Create(...)` 包装为 `AIFunction` 进入 `Tools`（工厂化为 `WorkspaceAgentToolset`，绑定当轮 `WorkspaceRoot`；适配器测试里已有同款包装写法可参考）。需审批的工具（`WriteFile`、`RunShellCommand`）再包一层审批装饰器：

- `ToolPermissionMode.FullAccess` → 直接执行；
- `ToolPermissionMode.RequireApproval` → 构造 `ToolApprovalRequest(ToolExecutionId: Guid.NewGuid(), 工具名, 显示名, 描述, argsJson, conversationId)` 经 `request.ToolApprovalHandler.RequestApprovalAsync(...)` 等待；拒绝则返回字符串结果 `"User denied this tool call."` 而不执行（模型可继续对话）。
- **前置条件（P3 必做，v1 稿遗漏）**：审批 UI 已在前端重构中移除，`DesktopToolApprovalHandler.ApprovalRequested` 当前**无订阅者**、`TryResolve` 无调用方——需在 Desktop 恢复"弹窗/toast + 允许/拒绝 → `TryResolve`"的订阅链，否则 RequireApproval 下首个写/Shell 工具调用即永久挂起。

### 5.3 系统提示与历史组装

- 系统消息：`Agent.Instructions`（非空时）；
- 历史：`request.Messages` 中 `MessageRole.User/Assistant` 且非失败态的记录，按序映射为 `ChatMessage(ChatRole, MarkdownContent)`；附件 v1 忽略（后续版本再上多模态）；
- 本轮用户输入已包含在 `Messages` 尾部（沿用 CLI 分支的现状约定）。

### 5.4 组合器（composer）与模型选择集成（P3，v1 稿遗漏）

P3 验收"Direct Agent 可用设置页配置的任意模型完成对话"隐含要求：用户能在聊天界面选模型。对齐现有 CLI 选择器模式（`persist composer model selection` 已有先例）：

- 新桥接消息 `ai-providers/list-enabled-models`（轻量，供 composer 用）：返回 `[{ modelProfileId, name, model, providerName }]`（仅 连接与模型均启用 的档案）；
- Direct 类 Agent 激活时，composer 模型选择器数据源切换为上表；选中项 → `ChatTurnRequest.ModelProfileId`；
- 选择持久化：写 `AiModelProfileSelection(scope: "desktop-default")`（经 `ai-providers/set-default-model`），启动时回读作为初始选中；
- 设置页可不做"设为默认"按钮（composer 的选择即默认），或后续补——两处写同一张 selections 表。

---

## 6. 设置服务层（面向设置页的门面，新增）

```csharp
public interface IAiProviderSettingsService
{
    /// 目录 + 连接 + 模型合并视图（未建连接的目录条目也返回，UI 灰显）。
    Task<AiProviderSettingsState> GetStateAsync(CancellationToken ct);

    Task<AiProviderView> SaveProviderAsync(SaveProviderCommand cmd, CancellationToken ct);
    Task SetProviderEnabledAsync(Guid connectionId, bool enabled, CancellationToken ct);
    Task DeleteProviderAsync(Guid connectionId, CancellationToken ct);   // 先删 DPAPI 密钥文件再删行（行级联删模型/选择，密钥文件不会级联，须显式 DeleteSecretAsync）

    /// 拉取远端模型并按 §6.1 语义合并入库，返回合并后的模型视图列表。
    Task<IReadOnlyList<AiModelView>> FetchAndMergeRemoteModelsAsync(Guid connectionId, CancellationToken ct);
    Task<ConnectivityCheckResult> CheckConnectivityAsync(Guid connectionId, Guid modelProfileId, CancellationToken ct);

    Task<AiModelView> UpsertModelAsync(UpsertModelCommand cmd, CancellationToken ct);
    Task SetModelEnabledAsync(Guid modelProfileId, bool enabled, CancellationToken ct);
    Task SetAllModelsEnabledAsync(Guid connectionId, bool enabled, CancellationToken ct); // UI"全部启用/禁用"
    Task DeleteModelAsync(Guid modelProfileId, CancellationToken ct);

    Task SetDefaultModelAsync(string scope, Guid modelProfileId, CancellationToken ct);
    Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(CancellationToken ct); // composer 用
}

public sealed record ConnectivityCheckResult(bool Ok, long LatencyMs, string? ErrorMessage);

public sealed record SaveProviderCommand(
    Guid? Id,                      // null = 新建
    string CatalogId,              // 决定 ProviderKind/AuthKind/默认协议（custom 亦然）
    string Name,
    Uri Endpoint,
    string? ApiKey,                // null = 不变；"" = 清除；非空 = 重新加密存储
    IReadOnlyDictionary<string, JsonElement>? ConnectionOptions);
```

**密钥规则集中于此层**：入参明文 → `ISecretProtector.StoreSecretAsync(plain, existingRef)`（DPAPI，复用已有 ref 原地覆盖）→ 只存 SecretRef；`""` 清除 → `DeleteSecretAsync` + 移除 ref；出参一律 `HasApiKey: bool` + 尾四位掩码字符串（如 `sk-****abcd`），**明文永不回传**。删除连接时先枚举 `CredentialRefs` 逐个 `DeleteSecretAsync`（防 `.bin` 文件泄留）再删行。

### 6.1 远端模型合并语义（v1 稿未定义，UI 隐含要求持久化）

`FetchAndMergeRemoteModelsAsync` 以 `Model`（model id）为键与该连接现有档案合并：

- **新增**：远端有、库里无 → 插入新档案，`ApiFormat` 取目录默认协议，`IsEnabled = false`（**默认禁用**，避免一次拉取 87 个模型污染 composer 选择器；用户用"全部启用"/逐个开关），descriptor 元数据写入 `ModelOptions.display.*`；
- **更新**：两边都有 → 仅回填 `display.*` 中用户未手工覆盖过的键（记 `display.userEdited` 标志或仅在键缺失时写入——取后者，简单），用户的启停/采样/协议配置一律不动；
- **不删除**：库里有、远端无 → 保留（用户可能配了聚合站上暂时下架的模型）；
- 全量结果不截断入库；UI `total` 字段 = 该连接档案总数。

---

## 7. 持久化（沿用现有表 + 增量迁移；v1 稿"全新建表"作废）

现状机制（已核实）：**无迁移框架**。`SqliteDatabase.EnsureInitializedAsync` 每次启动幂等执行：`CREATE TABLE IF NOT EXISTS` + `EnsureColumnExistsAsync`（查 `PRAGMA table_info` 补 `ALTER TABLE ADD COLUMN`）+ 手写"重建-拷贝"处理非增量变更；`CurrentSchemaVersion`（现 = 19）只是记账戳（`schema_versions` 表 `INSERT OR IGNORE`），不驱动迁移。测试 `SqliteRepositoriesTests` 断言版本 = 19，改版本必须同步改断言。

三张 `ai_provider_*` 表 **v19 已存在**，与本设计几乎一致（采样为离散列而非 JSON 列——保留现状，少动为妙）。增量如下：

**P1（版本 → 20）**：
```sql
-- EnsureColumnExistsAsync：
ALTER TABLE ai_provider_connections ADD COLUMN catalog_id TEXT NOT NULL DEFAULT 'custom';
```
（`ai_model_profiles.is_enabled` 列已存在，只需把记录/映射/Upsert 补上该字段；开发期存量行值不重要。）

**P3（版本 → 21，与旧代码删除同批）**：
- `conversations` 表**重建**去掉 `profile_id` 列——它有 `FOREIGN KEY(profile_id) REFERENCES profiles(id)`，不重建则 `profiles` 表删不掉；照抄既有 `EnsureConversationProfileIdNullableAsync` 的重建-拷贝模式（v19 加的），**conversations 是用户数据，必须完整保留**；
- `DROP TABLE IF EXISTS profiles;`（开发期配置数据可丢弃的只有 profiles / ai_provider_*，会话消息不在此列）。

现有仓储 `IAiProviderRepository`/`SqliteAiProviderRepository` 保留，增量补：连接映射 `catalog_id`、模型档案映射 `is_enabled`、`SetModelProfileEnabledAsync`、`SetAllModelProfilesEnabledAsync(connectionId)`、`ListEnabledModelProfilesAsync`（执行面/composer 用）。列类型沿用仓内约定：Guid=TEXT("D")、时间=TEXT("O")、bool=INTEGER、枚举=INTEGER、字典=JSON TEXT。

**实现提示**：从 `JsonDocument` 读出的 `JsonElement` 入records 前必须 `Clone()`（现有 `SqliteMappings.ReadJsonElementDictionary` 若已如此则沿用），否则文档释放后元素失效。

启动初始化：`App.xaml.cs` 已显式调用各仓 `InitializeAsync`；`IAiProviderRepository.InitializeAsync()` 目前靠首用惰性初始化，P1 顺手加进启动序列（与设置页首开竞态更稳）。

---

## 8. WebView 桥接协议

沿用现有 `type` + `requestId` 请求-响应模式（参考 `get-pet-settings` / `programming-assistant-settings` 的成熟写法：响应带 `requestId` 回显 + `error` 字段；`PostWebMessage` 序列化 camelCase、忽略 null、不做 `_webViewReady` 门控）。新增消息统一由 `AiProviderSettingsBridge`（Desktop 新服务）处理，`MainWindow` 的 switch 只按 `ai-providers/` 前缀转发一条，避免继续膨胀。

> 命名说明：现有消息均为扁平 kebab-case（无斜杠命名空间）。`ai-providers/` 前缀是**有意为之的新约定**（换取 MainWindow 单一转发口），响应 `type` 与请求 `type` 相同（回显），前端靠 `requestId` 关联并用"仅接受最新 requestId"的 stale 守卫（`ProgrammingAssistant.vue` 已有同款写法可抄）。

| 消息 type | 上行载荷 | 下行响应（同 type + requestId） |
|---|---|---|
| `ai-providers/get-state` | requestId | 全量状态（目录+连接+模型+默认选择；密钥仅 `hasKey` + 掩码） |
| `ai-providers/save-provider` | requestId, 连接字段, apiKey?（明文仅上行一跳） | 保存后的 ProviderView |
| `ai-providers/set-provider-enabled` | requestId, id, enabled | ok / error |
| `ai-providers/delete-provider` | requestId, id | ok / error |
| `ai-providers/fetch-models` | requestId, providerId | 合并后的 `AiModelView[]`（见 §6.1） |
| `ai-providers/check` | requestId, providerId, modelProfileId | `{ok, latencyMs, error?}` |
| `ai-providers/upsert-model` | requestId, 模型字段 | ModelView |
| `ai-providers/set-model-enabled` | requestId, modelProfileId, enabled | ok / error |
| `ai-providers/set-all-models-enabled` | requestId, providerId, enabled | ok / error |
| `ai-providers/delete-model` | requestId, modelProfileId | ok / error |
| `ai-providers/set-default-model` | requestId, scope, modelProfileId | ok / error |
| `ai-providers/list-enabled-models` | requestId | `EnabledModelView[]`（composer 用，P3） |

**DTO 形状**：对齐 `AIProviders.vue` 现有渲染字段（`name/sub/color/enabled/keyMask/base/models[]/total`），但模型元数据（ctx/out/价格）**传原始数字**（`contextLength: 400000`、`priceInPerMTok: 1.75`），由前端新增小工具函数格式化为 `'391K'`/`'$1.75'`/`'—'`（mock 里的字符串形状是显示层职责，不进后端）。`sub`/`color` 来自目录条目（custom 用默认色）。前端在非 WebView2 环境（浏览器 dev）保留 mock 数据作为 fallback，沿用 `ProgrammingAssistant.vue` 的 dev fallback 写法。

安全注意：明文密钥只在 postMessage 上行一跳出现（本机进程内通信），入服务即加密；日志层不打印 headers 与密钥。

---

## 9. 目录结构（对齐现有文件，新增标注 ★）

```
SelfClaw.Infrastructure/AiProviders/
├── Abstractions/
│   ├── IAiProviderAdapter.cs            （改：+SupportsModelListing/ListModelsAsync）
│   ├── IAiProviderRegistry.cs           （不动）
│   ├── IAiProviderRepository.cs         （改：+启停/枚举方法）
│   ├── IAiProviderSettingsService.cs  ★
│   └── IAiChatClientFactory.cs        ★
├── Catalog/
│   ├── AiProviderCatalog.cs           ★ // 内置目录数据（含 WellKnownModels，可先为空）
│   └── AiProviderCatalogEntry.cs      ★
├── Models/
│   ├── AiProviderKind.cs / AiProviderApiFormat.cs / AiProviderAuthKind.cs （改：+枚举成员）
│   ├── AiProviderConnection.cs（改：+CatalogId） / AiModelProfile.cs（改：+IsEnabled）
│   ├── AiModelProfileSelection.cs / AiSamplingOptions.cs / AiProviderClientRequest.cs（不动）
│   ├── AiModelDescriptor.cs           ★ / AiChatRuntimeInputs.cs ★ / AiChatClientLease.cs ★
│   └── Views/                         ★ （ProviderView / ModelView / SettingsState / Commands / ConnectivityCheckResult / EnabledModelView）
├── Http/
│   └── AiProviderHttpClientProvider.cs ★
├── OpenAi/
│   ├── OpenAiProviderAdapter.cs         （改：放开 DeepSeek Kind、接 HttpClientProvider）
│   ├── OpenAiProviderAdapter.ChatCompletions.cs / .Responses.cs（微调）
│   └── OpenAiModelListClient.cs       ★ // GET /v1/models（OpenAI/DeepSeek/OpenRouter/custom 形状）
├── Anthropic/AnthropicProviderAdapter.cs（改：+ListModelsAsync）
├── Gemini/GeminiProviderAdapter.cs    ★（P2）
├── Ollama/OllamaProviderAdapter.cs    ★（P2）
├── Azure/AzureOpenAiProviderAdapter.cs ★（P2）
├── AiProviderRegistry.cs                （不动）
├── AiProviderSettingsService.cs       ★
└── AiChatClientFactory.cs             ★

SelfClaw.Infrastructure/Agents/Runtime/
├── DispatchingAgentChatRuntime.cs       （改：ctor + Direct 分支）
└── DirectAgentChatRuntime.cs          ★（含 WorkspaceAgentToolset、审批装饰器，可拆文件）

SelfClaw.Infrastructure/Data/Sqlite/
├── SqliteDatabase.cs                    （改：v20/v21 增量、conversations 重建、删 profiles DDL）
└── Repositories/SqliteAiProviderRepository.cs（改：新列/新方法映射）

SelfClaw.Desktop/
├── Services/AiProviders/AiProviderSettingsBridge.cs ★
├── MainWindow.xaml.cs                   （改：+"ai-providers/" 前缀 case）
├── App.xaml.cs                          （改：DI + InitializeAsync 序列）
└── （P3）审批订阅：MainWindow/VM 订阅 ApprovalRequested → UI → TryResolve

SelfClaw.TranscriptVue/src/components/settings/AIProviders.vue（改：摘 mock 接桥接 + 格式化工具）
```

### DI 注册（`AddSelfClawInfrastructure` 增量）

```csharp
services.AddSingleton<AiProviderHttpClientProvider>();
services.AddSingleton<IAiProviderAdapter>(sp => new OpenAiProviderAdapter(AiProviderKind.OpenAI, …));
services.AddSingleton<IAiProviderAdapter>(sp => new OpenAiProviderAdapter(AiProviderKind.OpenAICompatible, …));
services.AddSingleton<IAiProviderAdapter>(sp => new OpenAiProviderAdapter(AiProviderKind.DeepSeek, …));
services.AddSingleton<IAiProviderAdapter, AnthropicProviderAdapter>();
services.AddSingleton<IAiProviderAdapter, GeminiProviderAdapter>();      // P2
services.AddSingleton<IAiProviderAdapter, OllamaProviderAdapter>();      // P2
services.AddSingleton<IAiProviderAdapter, AzureOpenAiProviderAdapter>(); // P2
services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();
// IAiProviderRepository → SqliteAiProviderRepository 已注册
services.AddSingleton<IAiProviderSettingsService, AiProviderSettingsService>();
services.AddSingleton<IAiChatClientFactory, AiChatClientFactory>();
services.AddSingleton<DirectAgentChatRuntime>();                         // P3，具体类型（同 CliAgentChatRuntime）
// DispatchingAgentChatRuntime ctor 增加 DirectAgentChatRuntime 参数 + Direct 分支
```

Desktop 侧：`AiProviderSettingsBridge` 注册单例并注入 `MainWindow`（同 `ProgrammingAssistantSettingsService` 的接法）。

---

## 10. 旧代码处置清单（v2 扩充为精确清单）

| 对象 | 处置 | 位置 |
|---|---|---|
| `ProviderProfile.cs`、`ApiStyle.cs` | **删除** | `SelfClaw.Core\Models\Profiles\` |
| `IProfileRepository.cs` | **删除** | `SelfClaw.Core\Interfaces\Profiles\` |
| `SqliteProfileRepository.cs` | **删除** | `SelfClaw.Infrastructure\Data\Sqlite\Repositories\` |
| `IWorkspaceMemoryInitializationService.cs` | **删除**（死代码：零实现零调用，仅它还引用 ProviderProfile） | `SelfClaw.Core\Interfaces\Workspace\` |
| `SqliteMappings.ReadProfile` | **删除** | `SqliteMappings.cs:11-24` |
| `profiles` 建表 DDL + 4 处 `EnsureColumnExists` | **删除**，改为 `DROP TABLE IF EXISTS profiles`（v21） | `SqliteDatabase.cs:76-89, 131-157` |
| `conversations.profile_id` 列 + 外键 | **重建表移除**（照抄 v19 重建模式；用户数据完整保留） | `SqliteDatabase.cs:182, 438` |
| `ChatTurnRequest.Profile` / `.ApiKey` | **删除**，加 `ModelProfileId`（§5.2） | `ChatTurnRequest.cs` + 唯一构造点 `MainWindowViewModel.cs:766` |
| `MainWindowViewModel` 旧 profile 面：ctor 注入 `IProfileRepository`、`_profiles/_selectedProfile/_selectedProfileModelOverride`、`SelectProfile`、`ReloadProfilesAsync`、`requestProfile.SecretRef → apiKey` 流程 | **删除**（模型选择改走 §5.4） | `MainWindowViewModel.cs:679-680, 744-778 等` |
| `App.xaml.cs` 的 `IProfileRepository...InitializeAsync()` | **删除** | `App.xaml.cs:60` |
| DI 注册 `IProfileRepository` | **删除** | `ServiceCollectionExtensions.cs:30` |
| 旧 profile 相关测试 | **删除/改写**（`SqliteRepositoriesTests` 22-88 行 round-trip；schema 版本断言 19 → 21；`ServiceCollectionExtensionsTests` 的 `IProfileRepository` 断言） | `SelfClaw.Tests` |
| `RunStartedEvent.AgentKind: CliAgentKind` | 改 `CliAgentKind?`（已核实全仓只写不读，零风险） | `RunStartedEvent.cs` + 两处 parser 赋值点 |
| AGENTS.md 中过时的 DI/运行时描述 | 落地时更新 | 仓根 |

---

## 11. 测试策略（`SelfClaw.Tests`，沿用镜像目录惯例）

- **Adapter 单测**（现有 OpenAI/Anthropic/Registry 测试保留扩展）：ChatOptions 映射（采样启用位、thinking 方言、raw options）、不支持协议 fail-fast、`ListModelsAsync` 响应解析（各家 fixture JSON：OpenAI 裸 id / OpenRouter 带价格 / Anthropic 带 ctx / Ollama tags / Gemini models）、DeepSeek Kind 放行。
- **SettingsService**：fake repository + fake protector 验证密钥"null 不变 / 空清除 / 非空重加密"、掩码出参、删除连接先删密钥文件、级联删除、目录合并视图、**远端模型合并语义**（新增默认禁用/更新不动用户配置/不删除）。
- **Factory**：禁用连接/禁用模型/缺密钥/协议不匹配/scope 无默认选择的错误路径（异常消息可读）。
- **DirectAgentChatRuntime**：脚本化 fake `IChatClient` 驱动，断言翻译表全行（文本/思考/工具/用量累加/三种终态）、迭代器纪律（不抛异常、恰一个终态）、审批装饰器（批准执行、拒绝返回 denied、FullAccess 旁路）、无 WorkspaceRoot 不带工具。
- **SqliteAiProviderRepository**：现有 CRUD 测试扩展 catalog_id/is_enabled/新方法 + 级联；schema 版本断言随版本号更新。
- **迁移**：v19 库文件（带 conversations 数据 + profiles 表）跑 `EnsureInitializedAsync` 后：conversations 行数不变、profiles 表消失、catalog_id 默认 `custom`。

---

## 12. 实施阶段

> 任务级拆解见《ai-provider-implementation-tasks.md》。

**P1 —— 设置页打通（先见效）**
枚举/记录增量 + `catalog_id` 列（v20）+ 仓储补方法 + 目录 + adapter 接口扩展与 OpenAI 系 `ListModelsAsync` + `AiProviderSettingsService`（含合并语义）+ Desktop 桥接 + `AIProviders.vue` 摘 mock 接真数据 + 连通性检查（OpenAI 系走通即可）+ DI 注册 + 启动初始化。
*验收：设置页可增删改查提供商与模型、检查连通、拉模型列表并持久化，重启后配置仍在，密钥加密落盘且回显仅掩码。*

**P2 —— 提供商与协议补齐**
Ollama（OllamaSharp、None 认证、`/api/tags`）、Gemini（OpenAI 兼容层 CC + 原生列模型）、Azure OpenAI（手动模型、deployment 名、api-version）、DeepSeek 注册与方言 spike、OpenRouter 元数据解析、`AiProviderHttpClientProvider`（extra_headers/超时/流式无限超时）落地。
*验收：目录内每类提供商均可配置、检查；除 Azure 外均可拉列表；Azure 可手动建模型并检查通过。*

**P3 —— Direct 执行链路（本体系的最终目的）**
`IAiChatClientFactory` + `DirectAgentChatRuntime`（翻译 + 迭代器纪律）+ 工具包装与审批装饰器 + **审批 UI 恢复** + `ChatTurnRequest` 契约更新 + `RunStartedEvent.AgentKind` 可空化 + Dispatching 接分支 + **composer 模型选择集成（§5.4）** + 旧代码删除清单执行（含 conversations 重建，v21）。
*验收：Agent 选 Direct 模式后，可在 composer 选任意已启用模型完成带工具调用与审批的完整对话轮，转写渲染与 CLI 分支一致；旧 profiles 链路全部移除且既有会话数据无损。*

---

## 13. 风险与开放问题

| # | 风险 | 影响 | 对策 |
|---|---|---|---|
| R1 | **DeepSeek `reasoning_content` 经 OpenAI SDK 不可达**（dotnet/extensions#6208 已证实） | DeepSeek/部分聚合站的思考内容无法显示（正文不受影响） | P1/P2 接受；需要时按 §4.3 做 SSE 重写 handler 或轻量自研 CC 客户端（独立 spike） |
| R2 | **Azure 无法用 api-key 列 deployments**（数据面端点已退役） | Azure 无"拉模型列表" | 目录标 `SupportsModelListing=false`，UI 手动录入 deployment 名（§3.3 已修正） |
| R3 | Gemini 原生 generateContent 的 .NET 侧实现选型未定（社区包 vs 自研 REST） | 仅影响 thinkingBudget 等原生能力的时点 | P2 先走 OpenAI 兼容层；原生协议独立 spike 后再排期 |
| R4 | `Microsoft.Agents.AI.Anthropic` 为 preview 包，且现有 adapter 靠 `AsAIAgent` 回调捕获 `IChatClient`（脆弱 hack）；T8 已核实当前公开 API 无 HttpClient 注入口 | Anthropic 路径升级包可能破坏；聊天请求的 `extra_headers` 无法注入 | T8 已将模型列表接入统一 HttpClient provider，连通性检查用关联 CTS 执行 `timeout_seconds`；聊天链路保留现状并以单测钉住，备选为官方 Anthropic C# SDK 的 M.E.AI 集成替换 spike |
| R5 | `UseFunctionInvocation` 工具环内 `UsageContent` 多次出现、`FunctionCallContent/ResultContent` 的流内呈现细节随 M.E.AI 版本演进 | 翻译层偶发漏事件/重事件 | fake IChatClient 单测钉死翻译表；升级 M.E.AI 时跑全套 |
| R6 | 采样参数与新款模型不兼容（如 Anthropic 4.7+ 收到 temperature 即 400） | 用户误开参数 → 请求失败 | 参数显式启用才发送（现状即此）；连通性检查可暴露；错误消息原样透传 |
| R7 | `JsonElement` 生命周期（`JsonDocument` 释放后失效） | 偶发 ObjectDisposedException | 入记录前 `Clone()`（§7 实现提示；review 时检查） |
| R8 | 流式超时语义：连接级 `timeout_seconds` 误杀长生成 | 长回答中断 | §4.4：流式请求无限超时 + CancellationToken；超时仅用于列模型/检查 |
| 开放 | 目录 `WellKnownModels` 预置数据维护成本（价格/ctx 会过时） | 显示信息陈旧 | P1 先空列表（拉取回填），预置数据作为后续增强按需补 |
| 开放 | 设置页“模型参数/查看详情”两个占位按钮的交互（采样/协议/reasoning 编辑面板） | UpsertModelCommand 已预留字段 | UI 细化时另行设计，不阻塞本架构 |

---

## 14. 实施结果（2026-07-15）

### 已落地

- P1 后端与前端桥接已实现：8 条目目录、provider/model CRUD 与启停、DPAPI 密钥三态更新/掩码、连通性检查、远端模型合并、默认模型选择和 AIProviders.vue 真实数据源。
- P2 适配器已实现：OpenAI、OpenAI-compatible、DeepSeek、Anthropic、Ollama、Gemini、Azure OpenAI；OpenRouter 由 custom/OpenAI-compatible 路径提供扩展元数据解析。
- P3 自动化范围已实现：`IAiChatClientFactory`、Direct runtime、workspace 工具与审批包装、Desktop WPF/toast 审批入口、composer Direct 模型选择、CLI/Direct dispatcher、统一 transcript 事件与终态。
- 旧 `ProviderProfile` 体系已删除；schema v21 重建 conversation 表以移除 `profile_id`，并删除 `profiles` 表。自动化迁移覆盖 conversation、message、tool run 和 CLI session 数据保留。
- 自动化回归结果：174 个 .NET 测试通过（含 27 个 Desktop 桥接/审批/通知契约测试）；solution build 0 警告/0 错误；TranscriptVue production build 转换 73 个模块；完整测试依赖树无已知漏洞。

### 已知限制与未验收项

- T7/T18/T19 仍需真实 Windows 桌面与真实 provider credential 手动验收；当前“实现完成/自动化通过”不等于 P1/P3 最终验收完成。
- MCP server front matter 仍未接入 runtime，`MainWindowViewModel.Agents.cs` 继续传空列表。
- Azure OpenAI 不支持列 deployment，必须手工录入 deployment 名；可选 `api-version` 从 connection options 读取。
- Gemini 聊天当前走 OpenAI-compatible Chat Completions，原生 `generateContent` 尚未实现。
- DeepSeek `reasoning_content` 受当前 OpenAI SDK 抽象限制，T13 spike 未执行；正文响应不受影响。
- 图片附件已持久化并在 transcript 展示，但 Direct runtime 按本设计阶段暂不把历史附件映射到 provider chat messages。
