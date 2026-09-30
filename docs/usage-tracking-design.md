# Token、上下文与成本归一化设计

本文件记录 Direct 与 CLI 两种模式如何把各自 provider / CLI 的用量口径归一化成同一份
`TurnUsage`，以及它如何落库、聚合。实现入口：

- `SelfClaw.Core/Models/Usage/TurnUsage.cs` — 归一化后的单回合用量 DTO。
- `SelfClaw.Core/Runtime/TurnUsageAccumulator.cs` — 把一次回合内的多次观测合并成一条 `TurnUsage`。
- `SelfClaw.Core/Runtime/Agent/UsageReportedEvent.cs` — 运行时事件；Direct 只发一条聚合观测，
  CLI 解析器每解析到一条 usage 发一条，由记录器累加。
- `SelfClaw.Infrastructure/Data/Sqlite/Repositories/SqliteUsageStatisticsReader.cs` — 只读聚合。
- `SelfClaw.Infrastructure/AiProviders/TurnUsageCostCalculator.cs` — 按模型单价估算成本。

## 1. 字段契约

| 字段 | 含义 |
|------|------|
| `InputTokens` | **完整**输入侧，包含 cached 与 cache-write；这是唯一的输入口径 |
| `UncachedInputTokens` | 按普通输入价计费的部分；由累加器用 `InputTokens - Cached - CacheWrite` 推导 |
| `CachedInputTokens` / `CacheWriteInputTokens` | 缓存读取 / 缓存写入，独立保留用于命中率与单价 |
| `OutputTokens` | 含 reasoning/thinking；`ReasoningTokens` 只做展示，不重复计入总量 |
| `TotalTokens` | provider 上报总量；缺失时退化为 `InputTokens + OutputTokens` |
| `ProviderCalls` | 产生这些 token 的 provider 调用次数（CLI 解析器可用的最佳近似） |
| `ContextTokens` | **最后一次 provider 调用**的 `输入 + 输出`，即下一回合的起始上下文 |
| `ContextWindowTokens` | 该次调用的模型上下文窗口 |
| `CostUsdMicros` / `CostSource` | 成本（微美元）与来源：`None` / `Estimated` / `ProviderReported` |
| `AdditionalCountsJson` | provider 专有计数（如 cache creation、web search 请求）的**最新一次**观测 |

累加规则：token 各字段跨 provider 调用求和；`ContextTokens`/`ContextWindowTokens` 取最后一次非空；
`Model` 取最后一次非空；成本按来源分别求和，`ProviderReported` 一旦出现就覆盖全部估算值。

## 2. 各 provider / CLI 的口径

### Direct（M.E.AI）

`DirectEventTranslator` 每次收到 `UsageContent` 就观测一次（`ProviderCalls: 1`），
`FunctionInvokingChatClient` 会把工具循环里每次内部调用的 `UsageContent` 逐个透传，
所以累加结果就是整回合的真实用量；最后一次观测的 input+output 就是下一步上下文。
翻译器在回合结束前用模型配置补齐 `Model`、`ContextWindowTokens` 并按单价估算 `CostUsdMicros`。

官方 Anthropic 12.49 适配器已知映射（用于核对，不要凭直觉改）：

- `InputTokenCount = input_tokens + cache_read_input_tokens + cache_creation_input_tokens`（含缓存）
- `CachedInputTokenCount = cache_read_input_tokens`
- cache-write 位于 `AdditionalCounts["CacheCreationInputTokens"]`

### Claude Code CLI

- `result.usage` 是**本轮主 agent loop 内所有 API 调用的合计**，不是最后一次调用：
  token 总量用它，`ProviderCalls` 用 result 的 `num_turns`，
  `ContextTokens` 必须取最后一条 `assistant` 消息的 `message.usage`。
- `total_cost_usd` 是**会话累计值**，`--resume` 后会从 transcript 保存的累计继续
  （且包含 Task subagent / sidechain / compaction，和 `usage` 口径不同）。
  因此它不能直接记为单回合成本：`CliAgentChatRuntime` 通过
  `ICliAgentSessionStore.TrackCumulativeCostAsync` 记录高水位
  （`cli_agent_sessions.cost_baseline_usd_micros`），写库的是本回合 delta；
  小于基线视为会话 reset，直接采用上报值；零值结果（崩溃/启动失败路径）不更新基线。

### Codex CLI

`turn.completed.usage` 是 OpenAI 口径：`input_tokens` 已包含 cached 子集。
token 总量直接用，`cached_input_tokens` 独立保留，累加器推导 uncached。

### OpenCode CLI

`step_finish` 每个 step（一次 provider 调用）上报一次，cache 读写位于 `cache.read`/`cache.write`，
`input` 不含缓存；`cost` 记为 `ProviderReported`。

## 3. 持久化

每个 assistant 消息最多一条 `turn_usage` 记录（`message_id` 主键，`ON DELETE CASCADE`）。
写入与消息本身在同一个事务内完成（`SqliteTurnFinalizationWriter.UpsertMessageAsync`），
`MessageRecord.Usage` 与 `MessageSelectColumns` 的 `LEFT JOIN` 负责读写往返。
`created_at_utc` 取消息的 `updated_at_utc`，用于按会话聚合。Schema v29 起
新建数据库不再创建 `messages.input_tokens/output_tokens`；v29 之前的旧列保留但不再读取，
历史用量不做回填（随历史记录一起清空）。

## 4. 聚合与展示边界

- `IUsageStatisticsReader` 明确分成两个口径：`GetConversationUsageAsync` 只看该会话自身的回合，
  `GetSubagentUsageAsync` 通过 `subagent_tasks.child_turn_id` 只统计该父会话委派的子代理回合，
  父会话自己的消耗不会混入子代理报表。目前只有后端聚合，没有前端页面消费。
- 命中率定义为 `cached / (cached + uncached)`，cache-write 记作未命中。
- 成本估算要求模型同时配置输入价与输出价；只配一侧时返回 `null` 而不是把另一侧按 0 计。
  缓存价缺省时回退到输入价。
