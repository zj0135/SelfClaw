# 多 AI 提供商 v1 任务文档

## Summary

实现一个未接入主流程的多 AI 提供商抽象 v1。第一阶段实现 OpenAI provider adapter、provider registry、参数模型、新表 schema/repository 和单元测试，为后续替换 `ChatClientAgentExecutionService` 内部 OpenAI 直连逻辑做准备。

v1 OpenAI provider 必须同时支持：

- OpenAI Chat Completions 标准格式
- OpenAI Responses API 格式

当前项目引用的 `Microsoft.Extensions.AI.OpenAI 10.5.0` 已支持 `OpenAI.Responses.ResponsesClient` 转 `IChatClient`，因此 Responses API 可以继续通过 `ChatClientAgent` 运行。

本版本使用全新 AI provider/profile 表设计。后续切换功能时可以完全抛弃旧 `profiles` 表，不要求兼容旧表结构。

## Tasks

### 1. 新增 provider 抽象模型 ✅ 已完成

- [x] 添加 `AiProviderKind`
- [x] 添加 `AiProviderApiFormat`
- [x] 添加 `AiProviderAuthKind`
- [x] 添加 `AiProviderConnection`
- [x] 添加 `AiModelProfile`
- [x] 添加 `AiSamplingOptions`
- [x] 添加 `AiProviderClientRequest`
- [x] `ConnectionOptions` 和 `ModelOptions` 使用 `IReadOnlyDictionary<string, JsonElement>`
- [x] `AiModelProfile.ApiFormat` 用于选择 Chat Completions 或 Responses API
- [x] `AiProviderClientRequest.Secrets` 使用已解密 secret 字典，OpenAI v1 读取 `api_key`

> 实现位置：`SelfClaw.Infrastructure/AiProviders/Abstractions/`（新增独立文件夹，命名空间 `SelfClaw.Infrastructure.AiProviders.Abstractions`，与原有功能目录隔离）。

### 2. 新增 provider adapter 接口 ✅ 已完成

- [x] 添加 `IAiProviderAdapter`
- [x] 添加 `SupportsApiFormat(AiProviderApiFormat apiFormat)`
- [x] 添加 `IAiProviderRegistry`
- [x] 添加 `AiProviderRegistry`
- [x] registry 按 `AiProviderKind` 查找 adapter
- [x] registry 对重复 provider kind 直接抛出 `InvalidOperationException`
- [x] adapter 对不支持的 api format 抛出 `NotSupportedException`

> 实现位置：接口在 `SelfClaw.Infrastructure/AiProviders/Abstractions/`（`IAiProviderAdapter`、`IAiProviderRegistry`，public）；实现 `AiProviderRegistry` 在 `SelfClaw.Infrastructure/AiProviders/`（`internal sealed`，构造时按 kind 建索引，重复 kind 抛 `InvalidOperationException`，未找到抛 `KeyNotFoundException`）。adapter 对不支持的 api format 抛 `NotSupportedException` 的契约已在接口文档注释中约定，具体实现见 Task 3/4。

### 3. 实现 OpenAI Chat Completions 支持 ✅ 已完成

- [x] 在 `OpenAiProviderAdapter` 中支持 `AiProviderApiFormat.OpenAIChatCompletions`
- [x] 使用 OpenAI SDK 创建 `OpenAI.Chat.ChatClient`
- [x] 使用 `AiProviderConnection.Endpoint`
- [x] 使用 `request.Secrets["api_key"]`
- [x] 转换为 `IChatClient`
- [x] 创建 `ChatOptions`
- [x] 映射 temperature/top_p/tool mode/tools
- [x] 支持 `thinking.type`、`reasoning_effort`、`parallel_tool_calls`、`store`
- [x] 对未知或类型不匹配 model options 记录日志但不中断

> 实现位置：`SelfClaw.Infrastructure/AiProviders/OpenAi/`（`internal sealed partial class OpenAiProviderAdapter`）。核心分部 `OpenAiProviderAdapter.cs` 负责 format 分派、共享 `ChatOptions`（采样/工具模式/工具）、API key 解析与 option 读取/日志助手；`OpenAiProviderAdapter.ChatCompletions.cs` 负责 Chat Completions 客户端创建与 raw 选项注入（通过 `Patch.Set`）。未知 option 记 debug 日志，类型不匹配记 warning 并忽略。
>
> 代码评审修正：① `thinking.type` 是非标准参数，严格 `OpenAI` 端点会 400，故只有 `OpenAICompatible` kind 才由 `EnableReasoning` 推导默认写入 `$.thinking.type`，严格 `OpenAI` 仅在显式配置时写入（显式对两 kind 都生效）；② adapter 现同时服务 `AiProviderKind.OpenAI` 与 `OpenAICompatible`（构造参数区分，每 kind 注册一个实例），消除 `OpenAICompatible` 无 adapter 的缺口；③ API key 校验改用 `IsNullOrWhiteSpace`。

### 4. 实现 OpenAI Responses API 支持 ✅ 已完成

- [x] 在 `OpenAiProviderAdapter` 中支持 `AiProviderApiFormat.OpenAIResponses`
- [x] 使用 OpenAI SDK 创建 `OpenAI.Responses.ResponsesClient`
- [x] 使用 `AiProviderConnection.Endpoint`
- [x] 使用 `request.Secrets["api_key"]`
- [x] 通过 `ResponsesClient.AsIChatClient(profile.Model)` 转换为 `IChatClient`
- [x] 复用统一 `ChatOptions`
- [x] 支持 `reasoning.effort`、`reasoning.summary`、`store`、`max_output_tokens`、`truncation`、`parallel_tool_calls`
- [x] Responses reasoning 只由 `ModelOptions` 显式控制，不默认使用 `EnableReasoning` 注入 reasoning 参数

> 实现位置：`SelfClaw.Infrastructure/AiProviders/OpenAi/OpenAiProviderAdapter.Responses.cs`（新分部）。`ResponsesClient(ApiKeyCredential, OpenAIClientOptions)` + `AsIChatClient(profile.Model)`；raw 选项用强类型 `CreateResponseOptions`（`ReasoningOptions`/`StoredOutputEnabled`/`MaxOutputTokenCount`/`TruncationMode`/`ParallelToolCallsEnabled`，符合设计偏好）。reasoning 仅在 `reasoning.effort`/`reasoning.summary` 显式存在时才注入，绝不读 `EnableReasoning`。核心分部已扩展 `SupportsApiFormat`（含 Responses）、分派 switch，并新增 `TryReadInt` 助手。Responses 为 SDK 评估期 API，整文件以 `#pragma warning disable OPENAI001` 抑制。

### 5. 新增全新 SQLite schema ✅ 已完成

- [x] 新增 `ai_provider_connections`
- [x] 新增 `ai_model_profiles`
- [x] 新增 `ai_model_profile_selections`
- [x] 新增索引：
  - `ix_ai_provider_connections_kind`
  - `ix_ai_model_profiles_connection`
  - `ix_ai_model_profiles_updated`
- [x] 不扩展旧 `profiles` 表
- [x] 不做旧 `profiles` 到新表的数据迁移

> 实现位置：`SelfClaw.Infrastructure/Data/Sqlite/SqliteDatabase.cs`。`CurrentSchemaVersion` 已提升到 `16`；初始化阶段新增三张独立 AI provider/profile 表与三个索引，继续使用 `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS`，不修改旧 `profiles` 表，也不执行旧 profile 数据迁移。新增测试 `Initialize_adds_ai_provider_schema` 覆盖表、索引和 schema version 记录。

### 6. 新增 repository ✅ 已完成

- [x] 添加 provider connection 的 list/get/upsert/delete
- [x] 添加 model profile 的 list/get/upsert/delete
- [x] 添加默认 selection 的 get/set
- [x] JSON 字段读写使用 `System.Text.Json`
- [x] 删除 provider connection 时依赖外键级联删除 model profiles
- [x] secret 只保存 ref，不保存明文

> 实现位置：新增 `AiModelProfileSelection` 与 `IAiProviderRepository` 于 `SelfClaw.Infrastructure/AiProviders/Abstractions/`；新增 SQLite 实现 `SelfClaw.Infrastructure/Data/Sqlite/Repositories/SqliteAiProviderRepository.cs`。repository 只读写新表，`credential_refs_json` 只保存 secret ref，`connection_options_json`/`model_options_json` 通过 `System.Text.Json` 序列化。新增测试覆盖 provider connection、model profile、selection 的保存读取，以及删除 provider connection 后 model profile/selection 的级联删除。

### 7. 添加 DI 注册 ✅ 已完成

- [x] 在 Infrastructure DI 中注册 `OpenAiProviderAdapter`
- [x] 注册 `AiProviderRegistry`
- [x] 注册新 repository
- [x] 不修改现有 `IAgentExecutionService` 的行为
- [x] 不改动现有 runtime 调用链

> 实现位置：`SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`。Infrastructure DI 现在注册 `IAiProviderRepository`、两个 `IAiProviderAdapter`（`OpenAI` 与 `OpenAICompatible` 各一个 `OpenAiProviderAdapter` 实例）和 `IAiProviderRegistry`。现有 `IAgentExecutionService` factory 与 runtime 调用链保持不变。新增 `ServiceCollectionExtensionsTests` 验证默认 DI 能解析新 repository、registry 和两个 OpenAI-family adapter。

### 8. 添加单元测试 ✅ 已完成

- [x] registry 查找成功
- [x] registry 未找到 provider 时抛出明确异常
- [x] registry 重复 provider kind 时抛出明确异常
- [x] OpenAI adapter 声明支持 Chat Completions 和 Responses
- [x] OpenAI Chat Completions 创建 `IChatClient`
- [x] OpenAI Responses 创建 `IChatClient`
- [x] OpenAI adapter 映射采样参数
- [x] OpenAI adapter 映射 tool mode
- [x] Chat Completions 应用 reasoning 默认值
- [x] Chat Completions 应用 model-specific options
- [x] Responses 应用 model-specific options
- [x] 不支持的 api format 抛出明确异常
- [x] repository 保存和读取 provider connection
- [x] repository 保存和读取 model profile
- [x] repository 保存和读取 selection
- [x] 删除 provider connection 后 model profiles 被级联删除

> 实现位置：新增 `SelfClaw.Tests/Infrastructure/AiProviders/AiProviderRegistryTests.cs` 覆盖 registry 成功、未找到和重复 kind；新增 `SelfClaw.Tests/Infrastructure/AiProviders/OpenAiProviderAdapterTests.cs` 覆盖 OpenAI adapter 的 format 声明、两种 `IChatClient` 创建、采样/工具映射、Chat Completions raw option、Responses raw option 和 unsupported format。repository 覆盖在 `SqliteRepositoriesTests`，DI 覆盖在 `ServiceCollectionExtensionsTests`。当前 `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore` 通过，67 passed。

### 9. 新增 Anthropic provider 支持 ✅ 已完成

- [x] 添加 `AiProviderKind.Anthropic`
- [x] 添加 `AiProviderApiFormat.AnthropicMessages`
- [x] 添加官方 `Microsoft.Agents.AI.Anthropic` 包引用
- [x] 新增 `AnthropicProviderAdapter`
- [x] 使用 `request.Secrets["api_key"]`
- [x] 使用 `AiProviderConnection.Endpoint` 设置 Anthropic SDK `BaseUrl`
- [x] 通过官方 `AsAIAgent` 的 `clientFactory` 捕获底层 `IChatClient`
- [x] 映射 temperature/top_p/tool mode/tools
- [x] 支持 `max_tokens` model option
- [x] 在 Infrastructure DI 中注册 Anthropic adapter
- [x] 不修改现有 `IAgentExecutionService` 行为
- [x] 添加 Anthropic adapter 单元测试

> 当前 Agent Framework 支持情况：官方包 `Microsoft.Agents.AI.Anthropic` 提供 Anthropic/Claude agent 支持；该包公开的是 `AsAIAgent` 扩展，并通过 `clientFactory` 暴露底层 `IChatClient`。因此 SelfClaw 现有 provider 抽象可以继续保持 `IChatClient` 入口，不需要绕过当前 Agent Framework。实现位置：`SelfClaw.Infrastructure/AiProviders/Anthropic/AnthropicProviderAdapter.cs`。测试位置：`SelfClaw.Tests/Infrastructure/AiProviders/AnthropicProviderAdapterTests.cs`。当前 `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore` 通过，73 passed。

## Acceptance Criteria

- `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj` 通过
- 现有桌面运行时行为不变
- `ChatClientAgentExecutionService` 当前逻辑不被替换
- 新抽象可以被后续执行层直接注入使用
- OpenAI Chat Completions 和 Responses API 都能以 `IChatClient` 形式创建
- 新表 schema 不依赖旧 `profiles` 表
- 新 repository 不读取旧 `ProviderProfile`

## Out of Scope

- 不接入 WPF 设置页
- 不迁移旧 `profiles` 数据
- 不让现有 runtime 读取新表
- 不实现 DeepSeek adapter
- 不修改 TranscriptVue
- 不改变当前 Agent 运行流程
