# Direct 消息输入：P2 实施与自测报告

日期：2026-10-03。基线：`ca42646b88b7088fc94f4e299b419c66f857b678`。
状态：**P2.1–P2.12 实现与自测完成，交付独立审核；本报告不是独立审核结论。**

P1 基线为 1329 通过、4 项原生／真实 provider 门控跳过。开始时已有的 `AGENTS.md`、运行/用量文档及未提交的设计、P0/P1 报告均作为有效上下文读取；在其基础上更新，没有覆盖 P0/P1 历史审核证据。没有提交或推送。

## 1. 范围和结论

完成独立 turn/message 模型、唯一 Interactive 运行准入、共用事务 writer、生产输入会话与真实 SQLite 消费屏障；同步切换 Direct、CLI、child、continuation、历史、通知和 transcript/activity 投影。

- 最终 .NET 全量：**1419 通过 / 0 失败 / 4 门控跳过**，共 1423 项。
- Vue：**94 通过 / 0 失败**；生产构建成功。
- Playwright：**15 通过 / 0 失败**。
- P0 `InputBoundaryBarrierBaselineTests` 和内嵌 harness 已删除，其场景迁至生产 session/repository 测试。
- **不开放 Queue/Steer 用户入口。** P2 只消费真实 repository fixture 已领取的批次，没有受理服务、输入 dispatcher、工具 supersede 或 UI 队列。
- 没有清理真实数据库、配置、附件或 Workspace Root；没有启动用户实际 Desktop 应用或结束其进程。旧库不会被本次启动逻辑升级或自动清空。

## 2. 实际执行链

```text
UI 捕获选择
  → ConversationRunCoordinator.TryReserve（先预留）
  → Workspace Root 准备 / 历史加载（准入锁外）
  → ConversationTurnEngine.ExecuteAsync(handle, request)
    → StartTurnAsync：conversation + Running turn + user 同事务
    → Direct / CLI runtime
    → ConversationTurnRecorder
      ├─ 实际输出：独立 assistant id + 持久预留 sequence
      ├─ 工具进度：当前实际片段 + tools + blocks 同事务
      ├─ 输入边界：前序归约 → 封存片段/users/usage/Consumed 同事务
      │             → 应用内存 → ACK → UI 发布
      └─ 终态：当前片段（若有）+ turn + usage 同事务
    → 释放 runtime/输入会话资源 → coordinator 完成句柄
```

child 保留 `SubagentTaskCoordinator` / `SubagentTaskExecutor` / `SubagentExecutionSession` 的专用生命周期。正常 child 在现有 claim 事务中建立 turn 和初始 user；排队取消、preflight 失败可在任务终结事务中建立无 assistant 的回合。

continuation 使用同一个 Interactive run coordinator，但 turn/messages/tools/usage 在执行期间全部 detached，仅在 delivery repository 的终態 + Delivered/DeadLetter 事务中持久化。它允许预留消息序号，不允许提前插入 Running turn。

## 3. 职责迁移与删除清单

| 原职责 | 新 owner / 实际文件 | 删除或替换 |
| --- | --- | --- |
| Engine 准入、全局等待 gate、删除与关闭状态 | `Desktop/Services/Runtime/ConversationRunCoordinator.cs` | 删除 `_turnAdmissionGate`、`_pendingInteractiveAdmissions`、Engine tombstones/shutdown/准入方法 |
| Session 启动锁、活动字典、取消与完成 | 同一 coordinator + `ConversationRunHandle.cs` | 删除 `_startTurnGate`、`_runtimeStates`、Start/Stop/Complete/Abandon 生命周期入口 |
| RuntimeState running/CTS/completion | run handle | RuntimeState 仅保留内容/快照；删除 `IsRunning` 写入、CTS、完成 TCS 和生命周期 Dispose |
| 无约束 admitted DTO | coordinator 发行、一次执行的 handle | 删除 `AdmittedConversationTurn.cs`；执行、完成、删除按具体句柄/预留身份判定 |
| 开始时独立写 user | `SqliteConversationTurnRepository.StartTurnAsync` | 删除 Engine `AddUserMessageAsync` 和公共 `UpsertMessageAsync` |
| 裸工具进度先于所属消息落库 | turn repository 的进度事务 | 删除公共 `UpsertToolExecutionAsync`；实际片段、工具、块一起提交 |
| 单 assistant finalization writer | `SqliteConversationTurnWriter.cs` | 删除 `SqliteTurnFinalizationWriter.cs`；共用调用方 connection/transaction |
| 单 assistant commit DTO/interface | `ConversationTurnCommit` / `IConversationTurnRepository` | 删除 Core `TurnFinalization`、`ITurnFinalizationRepository`；集合可为空 |
| 首条 assistant 同时代表 turn | `ConversationTurnRecord` + recorder 的可空当前片段 | 删除 `MessageCreated`、`MessageId = TurnId`、空 assistant 占位 |
| 消息携带 usage/时长/错误/逻辑终态 | turn 及其查询/投影 | 删除 MessageRecord 的 Usage/DurationMs/ErrorMessage；消息枚举只剩 Sealed/Streaming/Interrupted |
| recorder 终态构造混入大量归约逻辑 | `ConversationTurnFinalizationBuilder.cs` | 提取已有对齐/关闭工具/终态映射；不另设状态机或提交路径 |
| 消息 JOIN usage，按时间戳回放 | turn usage 查询 + message sequence | 删除 message-based usage JOIN、Blocked 邻接推断及旧片段格式 fallback |
| P0 进程内 ACK 原型 | `Desktop/Services/ConversationInputs/DirectTurnInputSession.cs` | 删除测试 harness；一个 pending slot，状态/注册同锁，无 ACK 表 |
| child 的空 assistant 排除和等值查找 | 实际 turn + 可空 assistant | 删除 `ProviderMessages` 占位过滤，使用 InitialMessages/InitialTurns 和真实片段锚点 |
| child/continuation 把 Blocked/Truncated 改写为 Failed 事件 | committer 映射 task/delivery 结果 | turn 保留真实状态，task 可以 Failed 而 turn 为 Blocked/Truncated |
| message footer/error 作为结果真值 | `TranscriptTurnOutcome`、`TurnOutcome.vue` | footer 只是布局；无输出使用独立 `turn-outcome` 项，不制造消息 |
| 启动时 schema 修补/重建/版本回填 | `SqliteDatabase` + `SqliteSchema` | 旧迁移 helpers 删除；先只读检查，非目标库明确拒绝 |

`SubagentTaskCompletion.TurnFinalization` / delivery 同名属性仍表达一次终态提交，但其类型已经是 `ConversationTurnCommit`；不是旧单 assistant DTO 的兼容层。

保留必要的 Session 选择锁与加载合并、WebView transcript ACK、origin guard、child 生命周期、delivery lease/checkpoint 和 CLI 成本高水位。没有用新 coordinator 替代这些不同领域的机制。

## 4. 最终 schema

`SqliteDatabase.CurrentSchemaVersion = 30`。完整 DDL：`SelfClaw.Infrastructure/Data/Sqlite/SqliteSchema.cs`。

共 23 表：`schema_versions`、`ai_provider_connections`、`ai_model_profiles`、`ai_model_configurations`、`ai_model_profile_selections`、`workspace_roots`、`conversations`、`git_repositories`、`git_checkouts`、`messages`、`turn_usage`、`message_attachments`、`tool_runs`、`extension_packages`、`mcp_server_configs`、`cli_agent_sessions`、`subagent_tasks`、`subagent_deliveries`、`message_segments`、`conversation_turns`、`conversation_message_sequences`、`conversation_inputs`、`conversation_input_state`。

### 4.1 Turn、消息与用量

- `conversation_turns`：id、conversation_id、execution_mode、origin、status、started_at_utc、completed_at_utc、error_message。Running 必须未完成；非 Running 必须有完成时间；会话级联删除。
- 状态：Running / Succeeded / Failed / Cancelled / Truncated / Blocked / Interrupted。时间/错误归 turn，时长由起止时间投影，不再复制到消息。
- `messages`：必填 turn_id / sequence；`UNIQUE(conversation_id, sequence)`；`CHECK(id <> turn_id)`；复合 FK 保证 turn/会话一致。非 assistant 必须 Sealed。
- `conversation_message_sequences`：conversation_id PK、正数 next_sequence。所有 origin 共用持久预留；已预留序号不改写，允许空洞。
- `turn_usage`：turn_id PK，与 conversation_id 组成归属 FK。字段保留 input/uncached/cache/cache-write/output/reasoning/total、ProviderCalls、context/window、成本/来源和附加计数；total 可为 SQL NULL。created_at_utc 取回合开始时间。
- `tool_runs.message_id` 必填并引用实际片段；触发器拒绝 user/system 锚点；`message_segments` ToolCall 通过 `(tool_run_id, message_id)` 关联同一片段中的工具。

### 4.2 输入目标结构（未发布 Queue）

`conversation_inputs` 包含设计要求的 request 去重、input sequence、kind/target、payload/execution snapshot、status/revision、claim/run-owner、consumed turn/message、时间及原因字段。

- requestId、input sequence 在会话内唯一；非空 consumed message 全局唯一。
- FollowUp 必须无 target 且具有 execution snapshot；Steer 必须有 target。
- claim/owner 成对；Claimed/Consumed 必须持有 claim。
- Consumed 必须同时有 consumed_turn_id / message_id / consumed_at_utc；其余状态不允许伪造消费映射。
- 复合 FK 保证 input、target turn、consumed turn、user message 属于同一会话；触发器拒绝 child 输入所有者及非 user 消费映射。
- `conversation_input_state` 保存 next_input_sequence、queue_revision、paused 和 pause_reason。边界消费与 revision 更新同事务。

P2 fixture 载荷为 `{"version":1,"prompt":"..."}`。固定批次共享 claimId，MessageId 由 claimId+inputId 的稳定摘要生成，不依赖任何 TurnId；消费前 message_id 仍为 NULL。没有新增 ACK 表。

### 4.3 初始化和旧数据

新库 DDL 与唯一 v30 marker 同事务。已有库先用不入池的只读连接检查版本/必需表；旧、未知、较新或缺失目标表的库报错，不迁移/补列/回填/清空。初始化写连接也不入池，普通运行连接保留池化。重复初始化、DDL 中断及拒绝旧库字节不变都有测试。

没有旧数据清理记录，因为本次没有执行该操作；正式使用旧数据目录前需另行明确授权和执行清理。

## 5. 运行准入、输入屏障与 P1 保持项

### 唯一准入

一个短 metadata lock 只保护注册/删除/关闭裁决；不会在其中加载历史、执行 provider/工具、等审批/ACK 或等待任务结束。handle 从准备前就被注册，所以取消、删除和 shutdown 能看到尚未完成准备的工作。旧完成只能释放自己的 handle，成功删除的 tombstone 不会被迟到失败清理重新打开。

Engine 仅执行已准入 handle；started 任务确认启动事务已经提交。最终结果区分实际 turn 与 Persisted；不能凭收到成功事件推进后续工作。observer 普通异常隔离，observer OCE 传播且保证准入/完成不泄漏。

### 有序消费

运行时只在安全边界发纯数据事件。事件消费者前序工具进度被阻塞时，即便生产者已经执行完本批工具，也不能发起下一次 provider 请求；事务提交被阻塞时，外部读连接仍看不到 Consumed 和 usage。

活动边界的首次 waiter 等待 recorder 应用已提交内存后的 ACK；重复/迟到确认按 claim/消费映射重读（前一个 slot 已清空时也不会永久等待）。数据库已提交但返回链异常时也复用映射，不重插 user，不重复工具/片段/usage。已关闭的会话不能通过重读恢复执行。边界提交后的 UI 发布失败被记录，但不撤销消费或阻塞 ACK。

失败、取消、关闭与 waiter 注册在同一锁下裁决；每回合仅一个未确认 boundary。**首个终结决定保留**，消费者提交失败不会被后续枚举器 teardown cancellation 覆盖。

### P1 契约

- 显式循环仍完整消费/原生聚合响应；追加输入只加入原生 User，签名和工具 call/result 不经显示 DTO 往返。
- 128 次请求硬上限（含首次）、唯一 invoker、连续工具故障预算、hooks/checkpoint 和资源释放顺序保持。P2 不实现抢占工具。
- translator 仅在 ACK 后切换当前片段 FinalText；不重置逻辑用量、callId 或预算。终态不会把 A0 正文拼回 A1。
- 边界用量是绝对快照，不加入 recorder 贡献累加器。Direct 仍只报告一份最终贡献，CLI 保持 parser 归一化/贡献累加。
- `TotalTokens=null` 始终未知。Interactive/child/continuation 真实 SQL 重读保持 NULL；完整场景 total=17 仍为17。

## 6. M01–M12 验收映射

下列均为实际正式测试名（省略共同项目/命名空间前缀），包含于最终 TRX。

| ID | 代表测试 | 实际证据 |
| --- | --- | --- |
| M01 | `DirectInputBoundaryIntegrationTests.One_turn_interleaves_real_provider_output_tools_and_committed_user_messages`；`DirectPromptTurnHistoryTests.Interleaved_fragments_replay_by_sequence_even_with_equal_timestamps_and_shuffled_input` | 单 T 下 U0/A0/U1/A1 全独立；流式、SQLite、provider 历史顺序一致 |
| M02 | 同上集成测试；`SqliteConversationInputRepositoryTests.Boundaries_order_independent_fragments_users_and_tools_by_sequence_not_timestamp` | A0 thinking/text/两个工具，A1 第三个工具；A1 最终仅 `A1 done` |
| M03 | `DirectInputBoundaryIntegrationTests.Previous_tool_result_reduction_blocks_the_next_real_provider_request` | 阻塞 recorder 前序工具结果进度；producer 已进入未完成 waiter，请求仍为1，释放后为3 |
| M04 | `DirectInputBoundaryIntegrationTests.Actual_sqlite_commit_is_a_barrier_before_consumption_and_next_provider_request` | 内部 before-commit TCS 在实际写事务末尾阻塞；其他连接 Consumed=0、usage=0、无 U1，provider 不前进 |
| M05 | `DirectTurnInputSessionTests.Lost_ack_is_reconfirmed_from_consumption_without_repeating_any_write`（早/晚）；`Duplicate_boundary_after_new_output_cannot_seal_or_overwrite_the_current_fragment`；SQL `Duplicate_confirmation_does_not_reseal_fragment_or_replace_newer_usage` | 同 claim 重读，不重复写入或用量；旧边界不封存/覆盖新的 A1 |
| M06 | `Terminal_session_state_releases_early_and_late_waiters`（failure/cancel/close×早晚）；`A_second_distinct_boundary_is_rejected_and_cancellation_closes_the_first`；集成 `Cancel_or_consumer_failure_during_commit_releases_provider_and_preserves_unconsumed_input` | 迟到 waiter 有界退出；拒绝第二 boundary；provider 释放、input 保持 Claimed，取消语义不变 |
| M07 | `Multiple_claims_before_first_response_are_committed_before_the_first_provider_request`；`Multiple_boundaries_before_output_never_create_or_move_an_assistant`；`ConversationTurnRecorderTests.No_output_failure_persists_usage_on_the_turn_and_keeps_unknown_total` | 首 provider 请求已有 U0/两条预先输入，无 assistant；无输出失败仍有 turn/usage，无空消息 |
| M08 | SQL `Reserved_sequences_are_independent_monotonic_and_allow_gaps`、`Sqlite_unique_input_order_request_ids_and_message_sequences_are_enforced`；schema `Target_v30_reopens_read_only_and_has_no_legacy_message_columns` | 相同时间不影响序号排序；重复/NULL/跨归属被真实 SQLite 拒绝；重复初始化无旧字段回填 |
| M09 | `Boundary_snapshots_and_final_usage_overwrite_one_turn_row_without_accumulating`（完整/未知）；`DirectUsagePersistenceTests` 九项 | 两边界再终态仍只有一行；input=21/output=9/calls=3 不重复，NULL total 和完整17跨三条链保持 |
| M10 | `DirectPromptTurnHistoryTests.Blocked_turn_excludes_every_input_with_or_without_an_assistant`、`Unsuccessful_turn_retains_sealed_fragments_and_excludes_unfinished_fragments`、`Only_terminal_tools_anchored_to_the_actual_message_and_conversation_are_replayed`、`Truncated_multi_fragment_turn_appends_exactly_one_continuation_after_the_last_fragment` | Blocked 按 turn 排除全部输入；失败/取消保留封存单元；无孤立结果；续写提示一次 |
| M11 | schema `Fresh_DDL_and_version_marker_roll_back_together_when_before_commit_fails`；SQL `Start_failure_rolls_back_conversation_turn_user_and_allocator`、`Progress_fault_leaves_no_orphan_fragment_tool_or_usage`、`Boundary_before_commit_fault_rolls_back_entire_claim_content_usage_and_allocations`、`Finalization_fault_restores_running_turn_and_streaming_fragment` | DDL/start/progress/boundary/final 中断整体回滚；delivery/task rollback 同套件验证 |
| M12 | `DirectUsagePersistenceTests`；`ContinuationRecoveryTests.Interrupted_real_function_pipeline_recovers_from_durable_checkpoint`；CLI runtime、child/session/activity、continuation/dispatcher suites | 各来源独立 turn/message；detached 终态前无 turn/messages/tools/usage；原子交付和 checkpoint 保留 |

## 7. 架构验收

| ID | 本阶段证据/结论 |
| --- | --- |
| A01 | `ConversationRunCoordinatorTests` 并发唯一所有者、跨会话阻塞隔离、旧 handle、准备期取消/关闭、observer 取消；`ConversationDeletionServiceTests` 删除/child timeout/并发删除；旧 Engine/Session 状态已删除。child 仍独立。通过本阶段自测/核查。 |
| A02 | M01/M07/M09/M12；独立 turn/messages/usage，零 assistant 状态投影；无首消息真值。通过。 |
| A04 | 同一 transaction-only writer 被 start/progress/boundary/terminal/task/delivery 调用；M11 真实失败事务、M12 detached 断言。通过。 |
| A07 | 单 pending slot，纯事件 DTO；claim 消费映射重确认，G03 已迁移，M03–M06 通过；没有 ACK DB/输入 retry dispatcher。通过。 |
| A08 | 删除清单已落实；生产搜索无旧准入字段、MessageCreated、旧 message status、message-based usage JOIN、SqliteTurnFinalizationWriter、公共裸 message/tool upsert 或 UseFunctionInvocation。CLI/child/continuation 已切换。通过本阶段核查。 |

保留的 `RunHandle` 完成/执行保护是同一注册所有者的生命周期，不是第二张 busy 表；Session 的加载锁不是运行准入。没有借 P2 建立通用工作流框架。

## 8. 命令、结果与失败历史

Windows；.NET SDK 10.0.400、Node v22.19.0、npm 10.2.5，既有依赖未升级。命令在仓库根目录运行。使用 `bin/p2-validation/` 隔离输出，避免运行中的用户 Desktop 锁住旧 DLL；没有杀用户进程。没有无理由再次 restore。

```bash
DOTNET_CLI_HOME="$PWD/.dotnet" DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
DOTNET_NOLOGO=1 DOTNET_CLI_UI_LANGUAGE=en \
dotnet test SelfClaw.slnx --no-restore -p:BaseOutputPath=bin/p2-validation/ \
  --logger 'trx;LogFileName=p2-delivery-final.trx' \
  --results-directory .scratch/direct-message-input/p2-results

npm --prefix SelfClaw.TranscriptVue test
npm --prefix SelfClaw.TranscriptVue run build
npm --prefix SelfClaw.TranscriptVue run test:e2e
git diff --check
```

同样的 DOTNET 环境下另外执行了 `dotnet build SelfClaw.slnx --no-restore -p:BaseOutputPath=bin/p2-validation/ --nologo`：成功，0 警告、0 错误，日志 `p2-build-final.log`。文件编码核查确认所有改动的文本源码/文档均可按 UTF-8 读取。

定向验证使用同样的 `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore -p:BaseOutputPath=bin/p2-validation/`：

- `p2-targeted-01.trx` filter：`FullyQualifiedName~ConversationTurn|FullyQualifiedName~DirectTurnInputSession|FullyQualifiedName~DirectInputBoundary|FullyQualifiedName~SqliteSchemaInitialization|FullyQualifiedName~SqliteConversationInput|FullyQualifiedName~ConversationRunCoordinator|FullyQualifiedName~DirectUsagePersistence`。
- `p2-barriers-final.trx` filter：`FullyQualifiedName~DirectTurnInputSession|FullyQualifiedName~DirectInputBoundary|FullyQualifiedName~DirectConversationLoopContract|FullyQualifiedName~ConversationRunCoordinator`。

| 执行 | 通过 | 失败 | 跳过 | 说明 |
| --- | ---: | ---: | ---: | --- |
| 初次定向 | 103 | 1 | 0 | fixture 遗漏已有 uncached 推导期望，补为11，未改变 total 规则 |
| 首轮全量 `p2-full-01.trx` | 1408 | 16 | 4 | 暴露真实取消竞争，以及旧 schema 升级 fixture、固定输出目录、连接池清理和新字段 fixture 问题 |
| 二轮全量 `p2-full-02.trx` | 1422 | 2 | 4 | 临时 teardown 抑制尝试破坏 P1 消费者退出 OCE；已撤销。另一个为捕获多请求后 fixture 错用 Single |
| 三轮全量 `p2-full-03.trx` | 1426 | 0 | 4 | 此时尚含10项 P0 harness 测试 |
| 删除 harness 后屏障定向 | 75 | 0 | 0 | 生产 session、真实 SQLite、取消与 P1 循环契约 |
| 最终 `p2-delivery-final.trx` | **1419** | **0** | **4** | 删除10项原型，另加重复/迟到确认和首请求前多批次生产回归 |
| Vue `p2-vue-test-initial.log` | 94 | 0 | 0 | 23 文件；此后未改 Vue 源码 |
| Vue build `p2-vue-build.log` | 成功 | 0 | — | Vite 8.2.2；性能诊断提示非构建错误 |
| Playwright `p2-vue-e2e.log` | 15 | 0 | 0 | 浏览器 fixture，不是 WPF/WebView2 smoke |

编译过程的旧构造器/接口调用点错误已修复，没有以生产兼容构造器掩盖。最终测试构建无编译警告/错误。`git diff --check` 成功；LF→CRLF 提示不是空白错误。

最终 TRX SHA-256：`6fbbe0e2305d7398893dfbf9e2177cd72a6e769f7510b68bcf18573f9907058a`。
原始日志/TRX 保留在 `.scratch/direct-message-input/`；本报告保存可独立理解和重跑的摘要，不仅引用本机日志。

### 真实问题修复说明

消费事务抛 IOException 时，recorder 先终结输入 session；随后枚举器 teardown 会取消 producer。此前两个异常竞争，可能用 TaskCanceledException 覆盖 IOException。最终修复在 session 的同一终结裁决中保留首个失败/取消/关闭状态，未改 P1 runtime 的 provider-before-capability 释放和 OCE 传播规则。丢失 ACK 的首次/再次 waiter 也分别验证：首次不会在 recorder 尚未应用内存时自发确认，重复查询才用于修复确认链。

旧 schema 测试改为拒绝且不改数据；测试临时连接正确释放池或不入池。插件示例路径测试从固定 `../../../../` 改为查找解决方案根，支持隔离输出。没有放宽工具效果、原生签名、checkpoint、内容或总量断言。

## 9. 偏差与剩余范围

1. 采用短全局 metadata lock + 每会话唯一 handle，而非额外 keyed semaphore。锁内没有 I/O/等待，跨会话阻塞测试证明长任务独立；减少 keyed-lock 回收竞争。
2. 增加共用 writer 的原子工具进度用例，保留原产品进度耐久性；这是替换孤立工具写入，不是第二套终态路径。
3. Core 复用现有 DirectTurnOrigin 名称，CLI 取 Interactive；不复制值完全相同的 origin 类型。
4. child 保持零或一条实际 assistant 详情，显式添加 Turn，而非为不支持的 child 输入重做 Activity 多消息分页。所有提交模型仍为片段集合。
5. input 字段/消费结构已建立，但受理幂等服务、容量/FIFO/暂停恢复策略、Steer 领取/关闭竞争和工具许可、运行中输入预算保护仍属 P3/P4；P2 fixture 不证明这些未来阶段行为。
6. 新库拒绝旧版本意味着不能直接用旧用户数据目录启动新版本；这不是兼容升级产品，正式使用前仍需独立的数据清理安排。本次未执行。
7. 原生事件通道仍沿用 P1 的无界 Channel；本阶段没有重写流量控制或 provider 协议。

## 10. 跳过与未运行

最终四项门控跳过：

- `ActivityProviderSmokeTests.Configured_provider_reasoning_reaches_the_live_activity_channel`：未启用真实 provider smoke。
- `ActivityProcessRecoveryTests.Child_process_waits_with_uncommitted_text_and_recorded_tool`。
- `ActivityProcessRecoveryTests.Forced_process_exit_recovers_only_persisted_history_as_interrupted`。
- `DesktopActivityWebViewTests.Real_WebView_renders_three_live_children_and_restores_after_navigation`。

未运行真实 provider/Ollama 服务、真实 WPF/WebView2 手工验证及原生进程恢复 smoke。协议使用离线 HTTP fixture；浏览器 e2e 使用测试 host。跳过不算通过。P6 的真实服务/原生发布门槛尚未完成。

交付为工作区代码与本报告，等待独立审核；没有标记独立审核通过。
