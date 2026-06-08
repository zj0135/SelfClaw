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

### 5. 新增全新 SQLite schema

- 新增 `ai_provider_connections`
- 新增 `ai_model_profiles`
- 新增 `ai_model_profile_selections`
- 新增索引：
  - `ix_ai_provider_connections_kind`
  - `ix_ai_model_profiles_connection`
  - `ix_ai_model_profiles_updated`
- 不扩展旧 `profiles` 表
- 不做旧 `profiles` 到新表的数据迁移

### 6. 新增 repository

- 添加 provider connection 的 list/get/upsert/delete
- 添加 model profile 的 list/get/upsert/delete
- 添加默认 selection 的 get/set
- JSON 字段读写使用 `System.Text.Json`
- 删除 provider connection 时依赖外键级联删除 model profiles
- secret 只保存 ref，不保存明文

### 7. 添加 DI 注册

- 在 Infrastructure DI 中注册 `OpenAiProviderAdapter`
- 注册 `AiProviderRegistry`
- 注册新 repository
- 不修改现有 `IAgentExecutionService` 的行为
- 不改动现有 runtime 调用链

### 8. 添加单元测试

- registry 查找成功
- registry 未找到 provider 时抛出明确异常
- registry 重复 provider kind 时抛出明确异常
- OpenAI adapter 声明支持 Chat Completions 和 Responses
- OpenAI Chat Completions 创建 `IChatClient`
- OpenAI Responses 创建 `IChatClient`
- OpenAI adapter 映射采样参数
- OpenAI adapter 映射 tool mode
- Chat Completions 应用 reasoning 默认值
- Chat Completions 应用 model-specific options
- Responses 应用 model-specific options
- 不支持的 api format 抛出明确异常
- repository 保存和读取 provider connection
- repository 保存和读取 model profile
- repository 保存和读取 selection
- 删除 provider connection 后 model profiles 被级联删除

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
