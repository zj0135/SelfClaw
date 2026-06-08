# 多 AI 提供商 v1 设计

## Summary

为 SelfClaw 设计一套多 AI 提供商抽象层，目标是让运行时继续基于 `Microsoft.Agents.AI` 和 `Microsoft.Extensions.AI` 工作，同时把不同 AI 提供商的客户端创建、参数映射、特有参数注入从 `ChatClientAgentExecutionService` 中拆出来。

当前项目引用的 `Microsoft.Extensions.AI.OpenAI 10.5.0` 支持 OpenAI Responses API：包内提供 `OpenAI.Responses.ResponsesClient` 转 `IChatClient` 的扩展，并包含 `OpenAIResponsesChatClient`。因此 v1 的 OpenAI provider 同时支持两种 OpenAI API 格式：

- OpenAI Chat Completions 标准格式
- OpenAI Responses API 格式

v1 设计允许使用全新的 AI provider/profile 表结构。后续正式切换功能时，可以完全抛弃旧 `profiles` 表和旧 `ProviderProfile` 持久化模型，不要求旧表兼容。

## Current State

当前实现中，`ChatClientAgentExecutionService` 直接：

- 根据 `ProviderProfile.Endpoint`、`ProviderProfile.Model`、`ApiKey` 创建 `OpenAI.Chat.ChatClient`
- 调用 `.AsIChatClient()` 转成 `Microsoft.Extensions.AI.IChatClient`
- 构造 `ChatOptions`
- 通过 `RawRepresentationFactory` 写入 OpenAI 特有的 `$.thinking.type`
- 包装 `FunctionInvokingChatClient`
- 交给 `ChatClientAgent`

这导致 OpenAI SDK、OpenAI API 格式、OpenAI-compatible API 风格、通用采样参数和 provider 特有参数混在执行服务里，不利于 DeepSeek、Anthropic、Gemini、本地 OpenAI-compatible 服务等后续扩展。

## Design Goals

- 提供统一 provider 抽象，运行时只依赖 `IChatClient` 和统一请求模型
- 保持 Microsoft Agent Framework 的 `ChatClientAgent` / `IChatClient` 作为核心执行入口
- v1 OpenAI provider 同时支持 Chat Completions 和 Responses API
- 支持 provider 特有参数传递，例如 OpenAI `reasoning_effort`、Responses `store`、DeepSeek reasoning 开关、兼容服务自定义 raw JSON
- 使用全新 provider/profile 表设计，避免旧 `profiles` 表继续限制字段结构
- 后续可以逐步替换 `ChatClientAgentExecutionService` 内部客户端创建逻辑

## Public Types

新增 Core 或 Infrastructure 执行层模型：

```csharp
public enum AiProviderKind
{
    OpenAI = 0,
    OpenAICompatible = 1,
    DeepSeek = 2
}
```

```csharp
public enum AiProviderApiFormat
{
    OpenAIChatCompletions = 0,
    OpenAIResponses = 1
}
```

```csharp
public enum AiProviderAuthKind
{
    ApiKey = 0
}
```

Provider 连接信息与模型配置拆开：

```csharp
public sealed record AiProviderConnection(
    Guid Id,
    string Name,
    AiProviderKind ProviderKind,
    Uri Endpoint,
    AiProviderAuthKind AuthKind,
    IReadOnlyDictionary<string, string> CredentialRefs,
    IReadOnlyDictionary<string, JsonElement> ConnectionOptions,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
```

```csharp
public sealed record AiModelProfile(
    Guid Id,
    Guid ProviderConnectionId,
    string Name,
    AiProviderApiFormat ApiFormat,
    string Model,
    AiSamplingOptions Sampling,
    IReadOnlyDictionary<string, JsonElement> ModelOptions,
    int? ContextWindowTokens,
    int? AutoCompactTokenLimit,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
```

```csharp
public sealed record AiSamplingOptions(
    bool TemperatureEnabled,
    double Temperature,
    bool TopPEnabled,
    double TopP);
```

```csharp
public sealed record AiProviderClientRequest(
    AiProviderConnection Connection,
    AiModelProfile Profile,
    IReadOnlyDictionary<string, string> Secrets,
    bool EnableReasoning,
    IReadOnlyList<AITool> Tools);
```

新增 provider 抽象：

```csharp
public interface IAiProviderAdapter
{
    AiProviderKind ProviderKind { get; }

    bool SupportsApiFormat(AiProviderApiFormat apiFormat);

    IChatClient CreateChatClient(AiProviderClientRequest request);

    ChatOptions CreateChatOptions(AiProviderClientRequest request);
}
```

新增 resolver/factory：

```csharp
public interface IAiProviderRegistry
{
    IAiProviderAdapter GetRequiredAdapter(AiProviderKind providerKind);
}
```

## OpenAI v1 Behavior

OpenAI provider adapter 负责：

- 根据 `AiModelProfile.ApiFormat` 创建不同 OpenAI SDK client
- 使用 `request.Secrets["api_key"]` 创建 `ApiKeyCredential`
- 使用 `AiProviderConnection.Endpoint` 创建 `OpenAIClientOptions`
- 返回 Microsoft.Extensions.AI `IChatClient`
- 根据统一 `AiSamplingOptions` 设置 `ChatOptions.Temperature`、`TopP`
- 根据工具数量设置 `ChatToolMode.Auto` 或 `ChatToolMode.None`
- 通过 `RawRepresentationFactory` 写入 OpenAI 特有参数

### Chat Completions Format

当 `ApiFormat = OpenAIChatCompletions`：

- 使用 `OpenAI.Chat.ChatClient`
- 调用 `.AsIChatClient()`
- raw options 类型为 `OpenAI.Chat.ChatCompletionOptions`
- 保留当前项目行为：默认写入 `$.thinking.type = enabled/disabled`

支持的 model-specific options：

| Option Key | Type | Behavior |
| --- | --- | --- |
| `thinking.type` | string | 写入 `$.thinking.type`，默认由 `EnableReasoning` 推导为 `enabled` / `disabled` |
| `reasoning_effort` | string | 如果存在，写入 raw chat completion options |
| `parallel_tool_calls` | bool | 如果存在，写入 raw chat completion options |
| `store` | bool | 如果存在，写入 raw chat completion options |

### Responses API Format

当 `ApiFormat = OpenAIResponses`：

- 使用 `OpenAI.Responses.ResponsesClient`
- 调用 `ResponsesClient.AsIChatClient(profile.Model)`
- raw options 类型为 `OpenAI.Responses.CreateResponseOptions`
- 继续由 `ChatClientAgent` 通过 `IChatClient` 运行，不绕过 Microsoft Agent Framework

支持的 model-specific options：

| Option Key | Type | Behavior |
| --- | --- | --- |
| `reasoning.effort` | string | 写入 Responses `ReasoningOptions` 或等价 raw patch |
| `reasoning.summary` | string | 写入 Responses reasoning summary 配置 |
| `store` | bool | 写入 Responses `StoredOutputEnabled` |
| `max_output_tokens` | int | 写入 Responses `MaxOutputTokenCount` |
| `truncation` | string | 写入 Responses truncation mode |
| `parallel_tool_calls` | bool | 如果 SDK 当前类型未直接暴露，则通过 raw patch 写入 |

Responses API 不使用 `thinking.type` 作为默认 reasoning 开关；`EnableReasoning` 在 v1 中只影响 Chat Completions 的 `thinking.type` 默认值。Responses reasoning 由 `ModelOptions` 显式控制，避免给不支持 reasoning 的 Responses 模型注入无效参数。

## Execution Layer Integration Design

v1 可以先不接入现有系统，但后续接入时应按以下方式改造：

- `ChatClientAgentExecutionService` 不再直接引用 `OpenAI.Chat.ChatClient`
- 执行服务从 `IAiProviderRegistry` 获取 adapter
- adapter 创建 `IChatClient` 和 `ChatOptions`
- 执行服务继续负责：
  - `FunctionInvokingChatClient`
  - tool approval
  - `ChatClientAgent`
  - streaming aggregation
  - usage extraction
  - error logging

推荐后续接入后的执行层结构：

```csharp
var providerRequest = new AiProviderClientRequest(
    connection,
    profile,
    secrets,
    request.EnableReasoning,
    request.Tools);

var adapter = _providerRegistry.GetRequiredAdapter(connection.ProviderKind);
var leafClient = adapter.CreateChatClient(providerRequest);
var chatOptions = adapter.CreateChatOptions(providerRequest);
```

如果 adapter 不支持指定 `ApiFormat`，应抛出包含 provider kind、api format、profile name 的 `NotSupportedException`。

## Options Policy

- `ConnectionOptions` 保存 provider 连接级选项，例如 organization、project、timeout、proxy、base path 兼容策略
- `ModelOptions` 保存模型/API 格式级选项，例如 reasoning、store、max output tokens、parallel tool calls
- 通用采样参数只放入 `AiSamplingOptions`
- API 格式选择放入 `AiProviderApiFormat`
- 未识别 option 在 v1 中忽略，但 adapter 应记录 debug 日志
- 类型不匹配的 option 不抛出运行时异常，应忽略并记录 warning
- Secret 仍只保存 secret ref，真实 API key 继续由 `ISecretProtector` 管理

## New Persistence Design

采用全新表，不扩展旧 `profiles` 表。后续切换时，旧 `profiles` 可以保留为历史数据，也可以直接废弃；新功能只读取新表。

### `ai_provider_connections`

保存 provider 连接、鉴权方式和连接级配置。

```sql
CREATE TABLE ai_provider_connections (
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    provider_kind INTEGER NOT NULL,
    endpoint TEXT NOT NULL,
    auth_kind INTEGER NOT NULL,
    credential_refs_json TEXT NOT NULL DEFAULT '{}',
    connection_options_json TEXT NOT NULL DEFAULT '{}',
    is_enabled INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
```

### `ai_model_profiles`

保存具体模型、API 格式、采样参数和模型级配置。

```sql
CREATE TABLE ai_model_profiles (
    id TEXT NOT NULL PRIMARY KEY,
    provider_connection_id TEXT NOT NULL,
    name TEXT NOT NULL,
    api_format INTEGER NOT NULL,
    model TEXT NOT NULL,
    temperature_enabled INTEGER NOT NULL DEFAULT 0,
    temperature REAL NOT NULL DEFAULT 0.7,
    top_p_enabled INTEGER NOT NULL DEFAULT 0,
    top_p REAL NOT NULL DEFAULT 0.7,
    model_options_json TEXT NOT NULL DEFAULT '{}',
    context_window_tokens INTEGER NULL,
    auto_compact_token_limit INTEGER NULL,
    is_enabled INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(provider_connection_id) REFERENCES ai_provider_connections(id) ON DELETE CASCADE
);
```

### `ai_model_profile_selections`

保存桌面当前默认模型选择。这个表让默认模型选择独立于 profile 本身，后续可以按 workspace、agent 或 channel 扩展。

```sql
CREATE TABLE ai_model_profile_selections (
    scope TEXT NOT NULL PRIMARY KEY,
    model_profile_id TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(model_profile_id) REFERENCES ai_model_profiles(id) ON DELETE CASCADE
);
```

默认 scope：

- `desktop.default`
- `compaction.default`

### Indexes

```sql
CREATE INDEX ix_ai_provider_connections_kind ON ai_provider_connections(provider_kind);
CREATE INDEX ix_ai_model_profiles_connection ON ai_model_profiles(provider_connection_id);
CREATE INDEX ix_ai_model_profiles_updated ON ai_model_profiles(updated_at_utc DESC);
```

## DI Design

新增注册：

```csharp
services.AddSingleton<IAiProviderAdapter, OpenAiProviderAdapter>();
services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();
```

`AiProviderRegistry` 构造时收集所有 `IAiProviderAdapter`，按 `ProviderKind` 建索引；重复注册同一 kind 时启动即失败。

## Tests

v1 需要覆盖：

- OpenAI adapter 支持 `OpenAIChatCompletions`
- OpenAI adapter 支持 `OpenAIResponses`
- OpenAI Chat Completions adapter 能创建 `IChatClient`
- OpenAI Responses adapter 能创建 `IChatClient`
- OpenAI adapter 能把 temperature/top_p 映射到 `ChatOptions`
- tools 非空时 `ToolMode = Auto`
- tools 为空时 `ToolMode = None`
- Chat Completions 下 `EnableReasoning` 能映射到 raw OpenAI option
- Chat Completions model-specific options 能写入 raw chat completion options
- Responses model-specific options 能写入 raw response options
- adapter 收到不支持的 `ApiFormat` 时抛出明确异常
- 未知 provider kind 时 registry 抛出明确异常
- 重复 provider kind 注册时 registry 抛出明确异常
- 新表 repository 能保存和读取 provider connection
- 新表 repository 能保存和读取 model profile
- 删除 provider connection 时级联删除 model profiles

## Assumptions

- v1 可以只实现独立 provider 抽象和新表 repository，不替换现有 `ProviderProfile`
- v1 不新增 WPF 设置 UI
- v1 不迁移旧 `profiles` 数据
- v1 后续接入时可以完全停止读取旧 `profiles` 表
- v1 继续以 Microsoft Agent Framework 的 `ChatClientAgent` 和 `IChatClient` 为运行时核心
- OpenAI Responses API 支持基于当前包版本 `Microsoft.Extensions.AI.OpenAI 10.5.0` 和 `OpenAI 2.10.0`
- DeepSeek v1 不实现，只预留 `AiProviderKind.DeepSeek` 和 adapter 扩展位置
