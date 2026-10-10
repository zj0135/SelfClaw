# P3 独立审核问题修复记录

日期：2026-10-03。用户授权在原独立审核后修复生产代码。基线仍为 `17243f9` 上的未提交工作区；未提交、推送、清理用户数据或结束用户进程。

**IR-P3-01–11 已修复并完成自动回归。最终同一代码连续两次 .NET 全量为 1490 通过 / 0 失败 / 4 门控跳过；Vue 106 通过，Playwright 17 通过。** 原审核失败及其原始证据保留，不改写成当时已通过。这里是修复与验证记录，不冒充新的独立复审；Queue 开关保持默认关闭，Steer 未开放，P4–P6 未开始。

> **开关状态更新（2026-10-08）：** 本文所记 Queue／Steer 功能开关已在 `8ad27da` 删除、两者始终开启；下文保持当时原文。权威记录见 [分阶段实施与验收](direct-message-input-implementation.md) 文末「2026-10-08 功能开关移除」。

## 修复落点与验证

路径基于仓库根；`Inputs` = `SelfClaw.Desktop/Services/ConversationInputs/`，`Runtime` = `SelfClaw.Desktop/Services/Runtime/`，`Sql` = `SelfClaw.Infrastructure/Data/Sqlite/Repositories/`。

| 审核项 | 修复 | 生产位置 | 验证 |
| --- | --- | --- | --- |
| IR-P3-01 订阅/枚举 | Vue 使用 UUID；宿主专用 View DTO 输出小写 status/kind；正文详情仍独立读取 | useConversationInputs.js；Inputs/Views、ConversationQueueStateFormatter.cs | GUID bridge、实际 formatter 和原前端探针；新增 Queue Playwright |
| IR-P3-02 Held FIFO | 领取必须是最早未处理项；前序 Pending/Claimed/Held 均阻止越过 | Sql/SqliteConversationInputRepository.Scheduling.cs:35 | Held A/Pending B、不越过；普通 FIFO 与编辑/取消四种先后 |
| IR-P3-03 释放窗口 | Engine 记录真实 Turn+Persisted 结果；排队 handle 留给 dispatcher 在结果处理后 Complete。取消也读取 Engine 的持久化结果；落库失败优先 persist-failed | Runtime/ConversationTurnEngine.cs:95、ConversationRunHandle.cs；Inputs/ConversationInputDispatcher.cs:193 | Failed/Persisted=false 两个原完成通知 TCS 窗口；新增 Cancelled+终态落库故障 |
| IR-P3-04 暂停 CAS | 用户 pause/resume revision 冲突明确报错；stop/删除/失败用独立强制暂停事务，写 paused 与 revision 同提交。系统失败暂停写库失败时保留原 handle 并限速重试，关闭先停准入后取消重试 | Sql Scheduling:13；Service:81；ConversationDeletionService；Dispatcher:225 | stale revision 不能冒充暂停；stop 后持久 paused；终态失败窗口回归 |
| IR-P3-05 开关 | Resume/Retry/扫描不再调度 disabled backlog；只给新接受的唯一空闲输入传 immediateInputId，仍走同一 dispatcher/claim 链 | Service:95；Dispatcher:120 | disabled 恢复保留输入且不运行；宿主明确返回 queue-disabled |
| IR-P3-06 公平性 | 每轮遍历受容量限制的全部候选（最多 500 个输入），各会话普通准备异常独立处理；扫描每轮固定 250ms 间隔 | Dispatcher:98 | 32 个暂停父会话后的第 33 个可执行；不同会话隔离 |
| IR-P3-07 删除/关闭 | RunCoordinator 统一跟踪输入受理/工作区准备操作，拒绝 tombstone/stopping 后新操作；删除先取消并等待这些操作再暂停/级联；关闭等待同一 owner。不是第二张 busy 表 | Runtime/ConversationInputOperation.cs；ConversationRunCoordinator.cs:30,47,204；VM SubmitDirectAsync | 删除后迟到受理拒绝；受理默认模型读取被 TCS 阻塞时，删除/关闭取消并等待，零落库 |
| IR-P3-08 快照/retry | 存完整 Agent 定义、内容 SHA-256 指纹、格式版本、具体模型/Root/权限。指纹含原定义 Mode，执行使用受理的 Direct 模式。进程内 AgentRevision 仅保留诊断，不作跨进程定义真值；retry 重新捕获并重验，不静默解除暂停 | ConversationInputExecutionSnapshot；ConversationInputSnapshots；ExecutionValidator；Service:108 | 重启 revision 重置后同内容仍有效、内容变化被 Held；真实定义变化后 retry/resume；重复请求不重解析默认 |
| IR-P3-09 Root | 执行/retry 检查 Root ID 属于会话、数据库路径匹配及物理目录存在 | ExecutionValidator | 存在 DB 行但物理目录不存在时 Held，零 runtime |
| IR-P3-10 幂等入口 | Vue 超时重试保留 clientRequestId 与提交时 conversationId；router/VM 原样传递。payload 保存不可变 RequestPrompt/RequestFingerprint，编辑不破坏原请求去重；重复请求先读原 snapshot，模型默认/定义变化不使同一请求变新请求 | useChatComposer；WebViewMessageRouter；VM；Service:48；Sql input repository | router 两个 transport request 保持同 durable ID/target；真实 Service 默认模型/定义变化后同 inputId 且唯一 user；编辑后原请求仍去重 |
| IR-P3-11 状态/跟随 | 只有完整 state 才进 reducer，部分操作 result 不清列表；旧 generation 的 ACK/错误不操作新会话；忙碌入队不恢复 transcript follow | useConversationInputs/useChatComposer | 原 Node 探针 3/3；Vitest 超时/跨导航/阅读位置；Playwright partial result 后列表保留 |
| 最大快照 | 用实际完整 envelope 反复测 UTF-8 大小；独立 DTO 映射限制 preview 200、错误和元数据；超限按真实 envelope 裁剪 | ConversationQueueStateFormatter | 500 项极限中文载荷实际序列化 ≤256KiB，非用占位 envelope 估算 |
| 启动恢复 | 所有入口 await 同一个恢复 Task，完成提交前不受理/领取；旧 Interactive Direct Running turn 标记 Interrupted，保留 Consumed，旧 Pending Steer 一律 Held | Dispatcher:54；Sql Scheduling:117 | 恢复提交 TCS 屏障；Consumed 不重放；旧 Running target 的 Pending Steer 恢复 |
| 规范/重复写 | repository 按受理、Scheduling、Boundary、Queries 分为同一 sealed partial owner；JSON/View DTO 独立文件；FollowUp 复用 InsertUserAsync；业务 OCE 重抛，后台任务统一观测/等待 | Sql repository 四文件、Models；Views；TurnWriter | 构建 0 警告/0 错误；P2 消费/delivery/CLI 回归在全量通过 |

## 所有权与边界

- SQLite 仍是输入真值；RunCoordinator 是唯一运行、取消、完成、删除与关闭 owner。`ConversationInputOperation` 只表示已受理的准备/存储工作，用于取消与等待；不代表模型 running，不参与另一个 busy 判定。
- 排队运行由 dispatcher 在失败暂停提交后释放句柄；CLI 保留 Engine 的原完成路径。dispatcher 的 Task 集合只观测/等待后台工作，停止后不新增领取。
- schema 仍为 v30，SqliteSchema.cs 相对基线没有改动，Git blob 都是 `bdd0f8569919a04234eeb758af213a3a153698e5`。新增 RequestPrompt/指纹/定义字段都在已有 JSON 载荷中，没有 DDL 或版本迁移。
- 输入受理 API 仅接受 Direct FollowUp；Steer/promote 显式拒绝。P2 已领取边界读取/消费保留，没有 Steer 领取、工具 supersede/跳过或第二运行状态机。
- 旧 P3 无定义指纹的待发送项会 Held，需明确 retry 重新捕获；Consumed 不重跑。不做旧 schema 兼容或数据清理。

## 测试与探针迁移说明

保留 `.scratch/direct-message-input/p3-ind-review/` 的原始失败源码/TRX。24 个审核 .NET 场景迁到正式 `ConversationInputReviewRegressionTests`，并补充生命周期与 router 用例，较原 1454 增加 36 个正式用例。

迁移时没有放宽“不启动/不越过/不重建”的断言；有以下因生产边界改变所需的 fixture 调整：
- 调度 fixture 使用完整真实冻结定义，正常 fixture 先完成启动恢复；专门恢复用例显式在恢复前播种旧数据。
- GUID 问题选择修复 Vue 生成格式，因此 host 用例输入实际 UUID，并由原样 Node/Vitest 检查 Vue；没有把旧 `queue-...` 格式视为合法。
- 枚举断言移到真正发布的 formatter/View DTO，而非要求领域 DTO 自带 WebView 序列化策略；前端也可识别数字状态，原样 Node 探针通过。
- stale pause 用例从“读回 false”升级为“明确冲突 + system stop 后 durable paused=true”。
- fixture Dispose 通过 owner/dispatcher 真实等待退出，移除 Thread.Sleep 竞争；普通完成测试等待句柄终结，不能以队列列表空推断回合结束。

新增 `ConversationInputLifecycleTests` 使用 TCS 覆盖恢复提交、删除/关闭的受理屏障，另覆盖取消终态持久化失败、跨进程定义指纹、retry 和真实 envelope 上限。Router 测试证明目标/幂等键穿透实际入口。新增两个 Playwright Queue 用例验证入队、ACK 清框、取消、暂停/恢复和重载（测试 host，非 WPF）。

## 命令与实际结果

所有输出位于 `.scratch/direct-message-input/p3-fixes/`；.NET 使用 `-p:BaseOutputPath=bin/p3-fixes/`，Vue 使用隔离 outDir。Playwright 因前轮已确认的沙箱收尾限制，沿用沙箱外运行；本轮两次都正常退出。

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.dotnet"
$env:DOTNET_NOLOGO = '1'
dotnet build SelfClaw.slnx --no-restore -p:BaseOutputPath=bin/p3-fixes/ --nologo
dotnet test SelfClaw.slnx --no-build --no-restore -p:BaseOutputPath=bin/p3-fixes/ --logger 'trx;LogFileName=full-final-2.trx' --results-directory .scratch/direct-message-input/p3-fixes
dotnet test SelfClaw.slnx --no-build --no-restore -p:BaseOutputPath=bin/p3-fixes/ --logger 'trx;LogFileName=full-final-3.trx' --results-directory .scratch/direct-message-input/p3-fixes
npm --prefix SelfClaw.TranscriptVue test
npm --prefix SelfClaw.TranscriptVue run build -- --outDir ../.scratch/direct-message-input/p3-fixes/vue-dist
npm --prefix SelfClaw.TranscriptVue run test:e2e
node .scratch/direct-message-input/p3-ind-review/frontend-probes.mjs
git diff --check 17243f9
```

| 执行 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| build-final | 0 警告/0 错误 | 0 | — |
| 定向首轮 targeted-1 | 145 | 0 | 0 |
| 定向补证 targeted-2 | 127 | 0 | 0 |
| 全量初轮 full-1（补证前） | 1486 | 0 | 4 |
| 全量 full-final-1（最后两处校验收紧前） | 1490 | 0 | 4 |
| 最终全量 full-final-2 / full-final-3（同一代码连续两次） | 各 1490 | 0 | 各 4 |
| Vue/Vitest 最终 | 106（24 文件） | 0 | 0 |
| Vue build | 成功 | 0 | — |
| Playwright 首轮/最终 | 各 17 | 0 | 0 |
| 原样 Node 前端探针 | 3 | 0 | 0 |
| diff --check | exit 0 | 0 | — |

本次已执行测试没有出现失败后放宽断言或跳过的情况。中间 diff --check 发现一个文件末尾多余空行，已修正。四项既有门控 smoke 仍跳过，未将其折算成通过。真实 provider/Ollama、真实 WPF/WebView2、toast、原生进程恢复和桌面手动脚本未运行；仍是后续验收限制。

## 文件校验和

| 证据文件 | SHA-256 |
| --- | --- |
| `build-final.log` | `a8928420483ed204bc2d54a3279cead874bfb4c24609824496f3004b696649fc` |
| `targeted-1.trx` | `651c98d9f66fc03376fe5003102b81ade30f931c6bc2d3aae1caad372e08596a` |
| `targeted-2.trx` | `0b495e3a1d81ae186d5a47fe6bf23c1a67658bfcdd600eba77106e31448ff292` |
| `full-1.trx` | `72f49ae09218808d4a8de28605c09e138ba3a344b3db1da26a48ffb246160ad4` |
| `full-final-1.trx` | `ff53f336edcc5204b0df8b88821999b41484785039da934da83baa9f3761078b` |
| `full-final-2.trx` | `d32599f90c0edacf8c60aeac3beb7483b906f10632d65330674ac0db4eb8165a` |
| `full-final-3.trx` | `26e1be120ee15e49e598a8db47ae1846d9aca122ed434ac2723ee74277907f49` |
| `vue-test-final.log` | `823375205e0ad120b6dbdf0136e6d7096d768816606a84d0c8b35cf8a68c0cdc` |
| `vue-build.log` | `ae82d07bb576c9fd5aaf32b148287ef229449dd3fd80c9391d9a3fdf1373e2d4` |
| `playwright-1.log` | `00fd40fbd422797a03e238caf182464a72c283776d59a9e75319b626234c9df0` |
| `playwright-final.log` | `531218caec923079de7ad59677fc6d323007482f3e74876e857167c349049668` |
| `playwright-results.json` | `48488da76466dbbbefdb1d4b5009dfaf9605d87ae8581f07b784cbf10f4edef6` |
| `original-frontend-probes.log` | `c74d95ece6442e617559e526a7bdfe3f6f89b7b39bfe63ab8fcbd1404c9bc1f2` |

## 新增正式回归的完整名称与计数

- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputLifecycleTests.Cancellation_with_a_failed_terminal_commit_keeps_the_persist_failure_reason`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputLifecycleTests.Consumed_input_is_not_replayed_on_recovery_and_its_unfinished_turn_is_interrupted`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputLifecycleTests.Definition_fingerprint_survives_restart_and_detects_changed_file_even_when_revision_resets`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputLifecycleTests.Deletion_and_shutdown_cancel_and_drain_an_acceptance_before_it_can_commit`：2 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputLifecycleTests.Editing_a_pending_item_does_not_destroy_the_original_request_deduplication`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputLifecycleTests.Retry_recaptures_a_real_changed_definition_and_resume_runs_the_new_frozen_definition`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputLifecycleTests.Same_client_identity_survives_default_model_and_definition_changes_after_acceptance`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputLifecycleTests.Startup_holds_a_pending_steer_bound_to_an_old_running_turn`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputLifecycleTests.Submission_awaits_the_shared_recovery_commit_before_acceptance_or_execution`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputLifecycleTests.The_actual_queue_envelope_never_exceeds_256KiB_and_items_never_include_full_body`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Concurrent_acceptance_enforces_20_and_utf8_64KiB`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Consumption_write_fault_rolls_back_at_each_table`：4 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Deleted_conversation_must_not_be_recreated_by_late_acceptance`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Disabled_feature_must_not_resume_persisted_backlog`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Explicit_edit_cancel_claim_order`：4 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Failed_terminal_must_pause_before_releasing_admission`：2 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Global_limit_must_reject_501st_in_a_new_conversation`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Held_head_must_block_following_pending_after_resume`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Idle_new_D_starts_existing_B_and_preserves_C_D_order`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Missing_physical_root_must_hold_before_runtime`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Pause_revision_conflict_must_not_be_treated_as_paused`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Paused_Held_parent_blocks_only_its_own_continuation`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Published_item_status_must_match_Vue_string_contract`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Retry_must_recapture_changed_agent_snapshot`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Running_A_excludes_B_C_and_each_followup_has_its_own_turn`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Scan_must_reach_candidate_after_32_paused_parents`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.ConversationInputs.ConversationInputReviewRegressionTests.Vue_subscription_identity_must_be_accepted_by_real_bridge`：1 通过 / 0 失败。
- `SelfClaw.Tests.Desktop.Services.WebView.WebViewMessageRouterTests.Prompt_retries_forward_the_same_client_identity_and_explicit_conversation_target`：1 通过 / 0 失败。
