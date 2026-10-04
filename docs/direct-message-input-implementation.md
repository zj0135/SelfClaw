# Direct 消息队列与 Steer：分阶段实施与验收

状态：P0 补证通过；P1 已发现的审核问题全部修复，最终正式全量 1329 通过／0 失败／4 门控跳过。IR02 由原 reviewer 按用户要求直接修复，证据见 [P1 实施报告 §8](direct-message-input-p1-implementation-review.md)及[审核历史 §9](direct-message-input-p1-independent-review.md)。P2 实现与独立审核通过（IR-P2-01 测试夹具竞态已修复，6 次全量稳定）。P3 独立审核阻塞项与默认关闭开关下的失败/停止卡死已修复，自动回归通过（.NET 1500/0/4 连续两次、Vue 106）。P4 实现与自测完成，独立审核发现的 S06 末尾竞争与 S11 显式 Skill token 已修复并加入回归（.NET 全量 1542/0/4；新增 41 项 Steer 用例），见 [P4 实施报告](direct-message-input-p4-implementation-review.md)，**待独立复审**。P5 实现与自测完成（.NET 全量 1549/0/4、Vitest 126、Playwright 25、Vue build 通过），独立审核发现的 IR-P5-01（取消冲突吞掉后续消费提示）已修复并由原探针 22/22 复核，见 [P5 报告](direct-message-input-p5-implementation-review.md)；Queue 与 Steer 默认关闭，P6 未实施。更新：2026-10-04。

本文是可执行的任务与验收清单，不是已完成报告。代码路径相对仓库根目录；拟新增类名可以按实现调整，但验收行为及设计不变量不能隐式改变。设计的 I01–I16 是跨阶段完成条件。

数据前提（2026-10-02 确认）：功能完成后统一清理旧数据。各阶段使用目标 schema 的独立测试库，不承担旧数据迁移、字段回填、新旧格式混读或带旧数据升级的任务。验收仍覆盖当前 Direct、CLI、child、continuation 的新数据写入，以及新系统数据在重载和崩溃后的恢复。本次只修订文档，不执行清理。

架构前提：允许并要求重构被本功能改变的职责边界。每个阶段同时交付新增能力、接管的职责和被删除的路径；“功能可运行但两套实现并存”不满足退出条件。具体决策见设计 §0、§4、§5.5、§13；架构验收 A01–A08 与功能用例同为门槛。

## 1. 交付顺序和阶段门槛

```text
P0 基线与职责审查 → P1 显式 Direct 循环
                 → P2 回合模型、运行准入与提交基础
                 → P3 持久化 Queue → P4 Steer 执行语义
                 → P5 Vue 完整交互 → P6 恢复与架构发布验收
```

| 阶段 | 可交付成果 | 依赖 | 功能开放条件 | 当前状态 |
| --- | --- | --- | --- | --- |
| P0 | 基线证据、协议聚合和并发验证结果 | 无 | 不开放新功能 | 补证通过：报告 §10，23 通过 |
| P1 | 可控制安全边界的显式循环 | P0 | 普通 Direct 行为通过回归；Queue / Steer 关闭 | 实现与验证完成；IR01/IR02/IH01/IH02 已关闭；1329 通过、4 门控跳过 |
| P2 | 独立 turn 模型、唯一准入、统一 writer、目标 schema 和有序提交 | P1 | 真实 Queue / Steer 入口关闭；使用固定输入 fixture 验证边界 | **独立审核通过**（IR-P2-01 测试夹具竞态已修复，6 次全量稳定 1419/0/4） |
| P3 | Queue 受理、调度、最小桥接与列表交互 | P2 | 通过独立审核后方可小范围启用；Steer 关闭 | **实现与自动回归通过**：失败/停止后的空闲发送立即启动；continuation 不再为空转预留句柄；.NET 1500/0/4 连续两次，Vue 106；见文末 2026-10-04 记录 |
| P4 | 完整 Steer、安全工具跳过、审批及末尾竞争 | P3 | 可通过关联请求验证；尚未完成整体 UI 验收 | **复核修复完成，待独立复审**；见 [P4 报告](direct-message-input-p4-implementation-review.md) |
| P5 | 队列编辑、提升 Steer、完整反馈和恢复 | P4 | 候选版本，进入桌面验收 | **实现与自测完成，待独立审核**；见 [P5 报告](direct-message-input-p5-implementation-review.md) |
| P6 | 故障恢复、跨模式回归及发布证据 | P5 | 所有发布门槛满足后启用 | 未开始 |

P3 完成后可以独立交付 Queue。P2 的最终 turn/message 模型与统一运行准入必须先落地，不能先在首条 assistant 上挂回合状态或给 Engine 增加第二套队列执行逻辑。P4 是 Steer 正式开放的前置条件。P5 的组件开发可以提前，最终验收依赖真实后端。

每个阶段建议拆成数个可独立审查的提交。此文不规定工期；P0 核实 SDK 行为后再根据真实改动估算，不能用未验证的实现假设承诺日期。

## 2. 通用完成定义与测试方法

每个阶段必须具备：

1. 对应任务完成，新增入口接入 DI、启动／关闭和失败清理；无不可达或仅用于演示的业务分支。
2. 对应验收用例通过；保留命令、环境、测试名称、结果和证据位置。
3. 受影响项目构建成功，无新增编译警告；运行相关自动测试。跨运行时／持久化阶段完成后运行 .NET 全量回归。
4. 明确未覆盖项和跳过原因；未运行、跳过、失败、通过分开记载。
5. 更新设计偏差、当前阶段状态和已改变的运行架构说明，不把待实施行为写成现状。
6. 完成相应 A01–A08 架构检查，记录“原职责 → 新 owner → 已删除路径”；删除未使用的接口、SDK 适配器、状态字段和转发层。阶段内过渡代码必须在本阶段结束前清理，不靠功能开关长期保留双实现。

测试采用可控 fake provider、fake tools、SQLite 临时数据库、可注入时间／提交故障与 TaskCompletionSource 屏障。并发测试用显式同步点控制顺序，不用随机 Sleep 碰运气。真实 provider 和桌面 smoke 是补充，不能代替确定性协议与事务测试。

运行中使用可观察的 provider-call、tool-execution、boundary-commit 计数；验证“未调用”必须让错误实现有机会继续执行后再断言，不能只在异步任务尚未调度时检查一次零值。

## 3. P0：基线与技术验证

对应设计：§2、§7、§8、§9。目标：在改循环前固定当前 SDK／adapter 的实际行为，发现无法直接搬用的契约。

### 任务

- [x] P0.1 记录基线提交、.NET / M.E.AI / provider SDK 版本、schema 和现有 Direct／Vue 回归结果。
- [x] P0.2 追踪 `AiChatClientFactory` 到原生 adapter：验证不启用 UseFunctionInvocation 时，流式 tool arguments、callId、reasoning 元数据、finish reason 和 usage 的形状。
- [x] P0.3 验证 SDK 当前 128 次迭代的精确计数和默认工具并发行为，形成替换前后的对照表。
- [x] P0.4 审计 `TurnId == MessageId` 的消费者：recorder、finalizer、工具锚点、usage SQL、child／continuation、活动面板、通知与 transcript 投影。
- [x] P0.5 给“有序 boundary event + ACK”制作最小验证：producer 等待时 consumer 仍可提交；consumer 异常时 producer 可以退出。
- [x] P0.6 核对 Engine 与 SessionCoordinator 的重复启动锁、运行注册和取消所有权；输出目标职责表，确定 run coordinator 的唯一 owner。
- [x] P0.7 审核 turn/message、usage、finalizer 和 delivery 的事务边界，列出必须替换的查询与写入方；按设计 §13 建立删除清单。
- [x] P0.8 补齐 B01 成功的两轮真实工具往返和 B04 第二次请求 HTTP body 序列化验证（G01／G02）；既有脚本事件和入站解析结果不足以替代。
- [x] P0.9 补齐 B06 waiter 先注册／后注册、consumer 先取消／先关闭、前序内容归约及单 waiter 限制（G03）；事件记录线程安全，顺序由同步点控制。
- [x] P0.10 把 finish reason 后调用到达、Ollama stop + 调用固定为断言；串行工具验证移除超时探测，修正与断言相反的两个测试名称（G04）。

优先读取／扩展：`DirectAgentChatRuntimeTests`、`AiChatClientFactoryTests`、`OpenAiProviderAdapterTests`、`AnthropicProviderAdapterTests`、`OllamaProviderAdapterTests`、`ConversationTurnRecorderTests`。新增 fixture 存入对应测试目录，不把生产账号响应或凭据提交到仓库。

P0 实际新增：`ProtocolBaselineProbeTests`（四个适配器的离线协议 fixture）、`DirectLoopBaselineTests`（迭代／并发／用量归一化基线）、`InputBoundaryBarrierBaselineTests`（边界 ACK 原型，P2 落地生产实现后删除）。

### 验收

| ID | 场景 | 必须观测到的结果 |
| --- | --- | --- |
| B01 | 纯文本、thinking、两次工具往返 | 记录实际事件序列、provider 调用数、唯一终态和总用量 |
| B02 | 一响应多工具、arguments 分多片到达 | 明确何时参数完整、每个 callId 只对应一次真实调用 |
| B03 | 128 次迭代边界及持续故障工具 | 固定请求次数；连续故障第三次尝试按现有预算终止，不消耗 128 次 |
| B04 | Anthropic 签名内容及 OpenAI 支持的两种 API 格式 | 保存原生响应后可构造合法下一请求；明确需保留的 adapter 元数据 |
| B05 | 多个 usage 碎片和没有 usage 的响应 | 固定按请求归一化规则，不把碎片数当调用数，不伪造零用量 |
| B06 | producer 等 ACK，consumer 正常／异常／被取消 | 正常路径继续；其他路径所有任务在测试超时内退出，无 gate 泄漏 |

退出条件：B01–B06 有证据，A01／A02 有目标职责图和改造清单；如果签名／工具参数无法正确回传，P1 不得以仅文本测试通过宣称完成。把发现的必要调整写回设计，而不是保留未知项到发布时。

复审历史：最初部分满足（13 项重跑通过），当时两轮往返、出站序列化和确定性屏障待补证。当前 G01–G04 已通过，实际名称、23 项结果及新发现修复见 P0 报告 §10。A01／A02 的目标清单成立，不代表 P2 实现已通过。

## 4. P1：显式 Direct 循环，保持普通回合行为

对应设计：§4、§7.1–7.2、§9。目标：运行时拥有每次 provider 请求与工具批次之间的控制权。保持正常回合行为，同时修正已证实的 usage 碎片计数并明确采用 128 次请求硬上限；暂不开放输入队列。

### 任务

- [x] P1.1 `AiChatClientFactory` 提供单次调用管线，保留 HTTP hooks、日志脱敏、provider 与 capability lease 的释放顺序。
- [x] P1.2 新增 `DirectConversationLoop` 和运行上下文服务；完整枚举后按 SDK 等价语义聚合原生响应，不因提前收到 finish reason 停止；保留后续请求实际需要的签名、ID 及元数据。
- [x] P1.3 将 `DirectToolInvoker` 的业务逻辑提取到可直接调用的入口；全量工具仍经过同一 hooks／审批／checkpoint／故障预算。
- [x] P1.4 适配 translator 的工具事件、FinalText 和 usage；先按 adapter 契约得到单请求用量，再跨请求累加，修正碎片重复计数；只有一个逻辑终态。
- [x] P1.5 采用 MaximumProviderRequestsPerTurn=128，含首次和 steer；第 128 次仍有工具时不执行、以 BudgetExhausted 收尾。以显式循环结果区分正常完成和预算耗尽，不让 finish reason=Stop 掩盖残留调用。
- [x] P1.6 删除被替代的 UseFunctionInvocation 装配、无消费者的 pipeline FunctionInvoker 配置及 SDK 入口；若有真实消费者，在证据中点名，适配层仍只能转入唯一 invoker 业务实现。
- [x] P1.7 调整 P0 基线测试：纯 SDK 行为探针与新 runtime 测试分开；旧工厂的 UseFunctionInvocation 不能再包住显式循环造成双层执行。原错误 usage／129 请求断言只作为旧基线证据，新 runtime 断言目标契约。
- [x] P1.8 修复独立审核 IR01：逐片段取显式或可推导 total 后累加，total-only=10 与 input/output=3/4 合计 17；原样独立探针复跑通过，本项关闭。未知 total 的下游保存问题另见 P1.9。
- [x] P1.9 修复 IR02：统一 TurnUsage 的未知 total 语义，由生产方推导、Core 只合并；删除 translator 补偿，三种 CLI 同步在生产边界归一化。Interactive／child／continuation 的未知及完整用量 SQL 往返通过，不涉及 P2 schema。

复审历史：事件 total=null 曾在落库后变为 7／19；独立全量曾有两项 Activity 时序失败。当前 IR02 已修复，Activity 测试采用 requestId 和 ACK 消除时序假设，最终重新运行正式全量为 1329 通过／0 失败／4 门控跳过，旧失败证据保留在审核报告。

继承问题追踪：IH01、IH02 已在 P1 修复；ContentFilter/Refusal 先于工具执行返回 Blocked，零响应／仅 usage 等没有内容或完成信息时 Failed。明确 Stop 的无正文响应、有效 reasoning/原生内容仍可成功。这两项是继承缺口，IR01 才是新增归一化缺陷；修复没有开放 Queue。

改动集中在 `Infrastructure/Agents/Direct/` 和 `Infrastructure/AiProviders/`；Core 只添加确有跨项目消费者的契约。既有 continuation checkpoint 不能变成普通异步事件。

### 验收

| ID | 场景 | 必须观测到的结果 |
| --- | --- | --- |
| L01 | 无工具的流式成功回合 | 文本／thinking 无丢失、重复；唯一终态、通知和 runCompleted |
| L02 | 一响应多调用、调用晚于 finish reason、Ollama-style Stop + tool call | 完整枚举后串行执行；不提前成功；每个 call/result 匹配且下一请求合法 |
| L03 | Shell 非零返回、实际异常、审批拒绝 | 常规失败不计故障预算；异常按预算；拒绝为 Canceled；均对模型可见 |
| L04 | checkpoint 失败、未绑定工具 | 实际工具执行数为 0，明确失败，无 SDK 隐式重试 |
| L05 | Length 有／无正文；连续 ToolCalls 或 Stop + 调用；第 128 次纯回答 | Length 语义保持；最多 128 请求、不发第 129 次、不执行末次工具；末次纯回答可成功；旧 SDK 129 仅作对照 |
| L06 | 取消 provider 流、取消工具、消费者提前退出 | OperationCanceledException 保持取消语义，provider／tool 退出并释放 lease |
| L07 | 工具后继续推理的原生请求序列 | 延续 P0 G02 对第二次 HTTP 请求的断言；Anthropic 签名、OpenAI 两格式工具结果和必要元数据正确 |
| L08 | 增量 usage、累计快照、无 usage、跨请求缓存和成本 | 每个有观测请求最多计一次 ProviderCalls；正确总量不受碎片数影响，累计快照不重复加；无 usage 不伪造报告；只一份最终结果 |
| L09 | CLI、child、continuation 原有用例 | CLI 无新增行为；child 结果和 continuation 原子交付不回归 |

退出条件：L01–L09 通过，.NET 全量回归通过，A03／A05 对本阶段通过；证据明确记录旧 SDK 129 次到新硬上限 128 次的差异，以及 usage 重复计数修正。默认串行已核对。Queue／Steer 开关仍关闭。

## 5. P2：回合模型、运行准入与提交基础

对应设计：§4–6、§8–9、§11、§13。目标：先建立唯一运行准入和最终 turn/message 存储模型，测试输入可以触发有序消费边界；不开放真实 Queue / Steer 入口。

### 任务

- [x] P2.1 一次建立目标 schema：conversation_turns、messages.turn_id / sequence、turn_usage.turn_id、输入／队列状态表与消息序号表；不回填旧数据，不增加 messages.turn_status。
- [x] P2.2 TurnId 与全部 MessageId 独立；assistant 片段按实际输出创建，工具关联片段；将 MessageRecord 的回合 usage／错误／时长职责迁到 turn snapshot。
- [x] P2.3 新增纯数据 boundary event 及当前 run handle 的输入控制端；每回合最多一个 waiter，复用批次 claimId 确认，不建立第二套投递存储。
- [x] P2.4 重构 TurnFinalization 和 SqliteTurnFinalizationWriter 为统一 turn commit／SqliteConversationTurnWriter；开始、输入边界、终态及 delivery 使用相同事务内写入规则。
- [x] P2.5 recorder 按序形成边界快照并提交，更新内存后确认执行，再调度展示；finalizer 只对存在的当前片段进行 FinalText 对齐，无输出也能记回合结果与用量。
- [x] P2.6 回放统一使用 turn_id 和 sequence；适配 Blocked 输入排除、Cancelled／Failed 的完整片段和 Truncated 续写，删除旧格式回退及邻接推断。
- [x] P2.7 适配主 transcript、Activity 共享投影、统计 SQL 和通知；CLI／child／continuation 不产生 boundary。
- [x] P2.8 抽取 ConversationRunCoordinator：整合 Engine 的 gate／tombstone／admission 与 SessionCoordinator 的活动注册、取消和完成句柄；移除原启动锁及重复运行判断。
- [x] P2.9 Engine 只接受已准入 handle 并执行；SessionCoordinator 只负责选择、加载及展示；当前发送和 CLI 使用新的开始回合事务，删除 AddUserMessageAsync 独立写入；continuation 接入统一准入并保留自身原子交付，child 保留专用生命周期 owner。
- [x] P2.10 用 repository fixture 写入输入批次，验证真实 SQLite 消费屏障；这不是可发布的 Queue 入口。完成设计 §13 在本阶段对应的删除项。
- [x] P2.11 按 P0 消费者表改 child／continuation finalization 所有权校验、task 用量与 delivery envelope，统一使用真实 turn；删除 SubagentExecutionSession 的空 assistant 占位过滤。
- [x] P2.12 生产输入会话保存 Failed / Cancelled / Closed 状态，注册与终结原子裁决；移植 G03 的早／晚注册、第二 waiter 与关闭验证后，删除 P0 临时 harness，不保留两套屏障实现。

必须审查的现有类型：`ConversationRuntimeState`、`ConversationTurnRecorder`、`DesktopTurnFinalizer`、`DirectEventTranslator`、`DirectPromptComposer`、`TerminalBlockAligner`、`SqliteTurnFinalizationWriter`、`SqliteUsageStatisticsReader`、`SubagentContinuationTurnCommitter`。实际路径通过仓库搜索确认。

### 验收

| ID | 场景 | 必须观测到的结果 |
| --- | --- | --- |
| M01 | 独立 Turn T 下创建 U0/A0/U1/A1，所有消息 ID 均不同于 T | 流式、DB 重载和 provider 历史顺序一致；回合状态只来自 T |
| M02 | A0 有 thinking、文本、两个工具，A1 有另一个工具 | 块和工具属于正确 messageId；终态不把 A0 正文拼回 A1 |
| M03 | 人为阻塞 recorder 的前序工具结果处理 | provider 下一调用数不增长；边界只能在前序事件处理完成后提交 |
| M04 | 人为阻塞 SQLite 提交 | runtime 继续等待 ACK；无下一 provider 请求，无提前 Consumed |
| M05 | 提交成功但 ACK 丢失，再次确认同 boundaryId | 通过重读确认同一结果；不重复 user、片段、工具或 usage |
| M06 | waiter 先注册或 consumer 先失败／取消／关闭，再测第二个 boundary 同时注册 | 所有注册顺序立即或有界结束，第二 waiter 被拒绝；gate 与 lease 释放，无永久等待 |
| M07 | 首响应前多次输入边界、无输出失败和后续多个片段 | 不创建／移位空 assistant；已分配序号不改写；无 assistant 仍能保存并投影回合失败 |
| M08 | 目标 schema 中创建相同时间戳、不同 sequence 的消息，并重载／重复初始化 | 只按 sequence 稳定排序；必填 turn_id 和序号约束生效；不按时间戳猜测，不回填缺失字段 |
| M09 | 边界两次保存累计 usage，最终再保存一次；另测无正文但有 usage | 每 TurnId 只有一行，不依赖任何 MessageId；child 按 turn_id 查询正确，总量不重复 |
| M10 | 已封存片段后失败／取消；Blocked／Truncated 历史 | 回放符合设计，不出现孤立 tool result、不绕过 Blocked 原始输入、不重复续写提示 |
| M11 | 目标 schema DDL / 边界事务 / 最终事务中断 | 整体回滚或提交后的完整状态，不出现半个消费批次 |
| M12 | CLI、child、continuation、活动面板回归 | 全部采用独立 turn/message；detached 的 turn、消息和 usage 均不提前落库，delivery 仍原子提交 |

退出条件：M01–M12 通过，真实 SQLite 往返和 .NET 全量回归通过；A01／A02／A04／A07／A08 的本阶段相关项通过。Engine／SessionCoordinator 的旧准入路径、message-based usage 与单 assistant finalization 假设已删除。不能只用内存测试替代字段约束和事务验证。

## 6. P3：持久化 Queue 与调度

对应设计：§3、§5、§6、§10、§11、§13。目标：在 P2 的目标模型和唯一运行准入上接入受理与调度，忙碌时可入队，成功后自动逐项执行；Queue 可独立交付。

### 任务

- [x] P3.1 在 P2 已有输入契约与存储上完成受理校验、条件更新、容量上限和恢复查询，不另建第二套 input model 或为阶段编号增加 schema 版本。
- [x] P3.2 `ConversationInputService` 受理并冻结执行选择，幂等响应，编辑／取消校验 revision；新会话复用既有 Workspace Root 准备。
- [x] P3.3 所有 Direct 发送纳入同一受理／dispatcher 路径；开始事务写 turn、user 和 Consumed，不创建空 assistant；删除 VM 空闲时绕过队列执行的分支。
- [x] P3.4 Engine 返回真实持久化终态结果，dispatcher 据此推进；在 P2 的开始事务中加入 claim 校验及 Consumed 更新，不再新增一条 user 写入路径。
- [x] P3.5 dispatcher 只发现候选、请求 run coordinator 准入、执行和唤醒；停止、删除、关闭通过既有 owner，不复制其 running／busy 状态机。
- [x] P3.6 最小桥接／Vue 入口：Direct 忙碌时加入队列、查看列表、取消、暂停／恢复；ACK 后清输入框，Steer 不开放。
- [x] P3.7 启动时恢复 Pending / Claimed 状态并暂停；模型失效、定义改变、Held 阻塞有可操作提示。

建议新增测试目录：`SelfClaw.Tests/Desktop/Services/ConversationInputs/`，存储测试继续位于 `Infrastructure/Data/Sqlite/Repositories/`。服务通过 DI 注册；不得让 VM 再增加一份内存队列。

### 验收

| ID | 场景 | 必须观测到的结果 |
| --- | --- | --- |
| Q01 | 当前 A 正在执行，依次入队 B、C | A 运行时 provider 看不到 B/C；A 成功提交后 B、C 各自启动独立 Turn |
| Q02 | 空闲会话已有队列，再普通发送 D | D 进入队尾，不绕过 B/C；同一会话无双重运行 |
| Q03 | ACK 丢失，同 requestId 重试；同 ID 改正文 | 前者同 inputId 且只一条 user；后者 RequestConflict |
| Q04 | 编辑／取消与领取同时发生，分别控制两种先后 | 仅条件更新胜者生效；Claimed 不可改；不出现执行旧正文却展示新正文 |
| Q05 | A 成功事件已到，但终态事务失败 | B 不启动，queue 暂停，显示持久化失败原因 |
| Q06 | A Failed / Blocked / Truncated / Cancelled | 均暂停；Pending 保留；不自动执行 B |
| Q07 | 点击停止与 A 结束同时发生 | 停止先赢则 B 不启动；若 B 已准入，按同一会话 stop 规则停止实际活动回合并暂停 |
| Q08 | 模型被禁用、Agent revision 变更、Workspace Root 丢失 | 输入 Held、后项不越过；不切默认模型、不使用另一会话目录 |
| Q09 | queue 与 continuation 同时申请准入 | 同一 gate 下唯一所有者；已受理用户输入优先，另一父会话不被阻塞 |
| Q10 | 在 A 会话入队后切到 B 会话 | 输入仍投递 A，执行快照不读取 B 的模型／目录／权限 |
| Q11 | 删除会话、删除失败、应用退出 | 无删除后重建或新领取；失败恢复为暂停；所有已启动任务有收尾 |
| Q12 | 入队后进程重启；旧 Claimed 残留 | Pending 可见且暂停；旧 Claimed 转 Held；无自动副作用重放 |
| Q13 | 条数 20/21、全局 500/501、正文 64 KiB 边界 | 上限内可接受，超限明确拒绝；并发提交不突破限制 |
| Q14 | FollowUp 消费事务中逐点注入写入失败 | 不出现孤立 Consumed／重复 user；重新加载能解释消费状态 |
| Q15 | 两个会话有队列，一个持续失败或暂停 | 另一个照常推进；调度非全局阻塞，无忙循环 |
| Q16 | 含附件的首版提交、功能开关关闭 | 附件明确 UnsupportedAttachments；关闭开关不丢已有输入、不继续调度 |

退出条件：Q01–Q16 及相关 Vue 用例通过；A01／A04／A06 通过；用临时库展示受理→统一准入→消费→终态的记录，确认没有空闲 Direct 的旁路。目标 schema 沿用 P2，队列开关方可在验收环境启用。

## 7. P4：Steer 执行语义与竞争处理

对应设计：§3、§6、§7、§8。目标：输入可在安全边界影响当前逻辑回合，且不破坏协议、审批或恢复规则。

### 任务

- [x] P4.{n} 输入会话实现 Pending Steer 的高水位批次、targetTurn 校验、领取与末尾原子关闭；撤销审批／跳过工具前先提交 claim，新输入不扩展已领取批次。
- [x] P4.{n} provider 请求前／工具结果闭合后调用边界协议；同批输入保持顺序并保护上下文预算。
- [x] P4.{n} 工具阶段使用明确 Preparing / AwaitingApproval / ReadyToExecute / Executing / Terminal 状态；steer 与最后执行准入使用同一裁决点。
- [x] P4.{n} 审批返回显式 Approved / Denied / Superseded；定向撤销 pending，Vue／toast 过期操作失效；正常取消继续抛 OCE。
- [x] P4.{n} 生成未执行工具的完整 Canceled / SupersededBySteer 结果，正确维护 hook 配对、调用预算、故障预算和 checkpoint。
- [x] P4.{n} runStarting hooks、显式 Skill token、continuation、已关闭目标和不支持附件等情形按设计反馈；不静默改变权限或扩大能力。
- [x] P4.{n} 处理 stop、失败、Length、预算不足和崩溃时的未消费 steer；Held 可见、队列暂停，不自动重跑。
- [x] P4.{n} Steer 使用 P2 的同一个输入会话、writer 和回合模型；invoker 独占工具调度状态，不在 loop／approval handler 中复制第二套执行准入规则。

### 验收

| ID | 受控提交点／场景 | 必须观测到的结果 |
| --- | --- | --- |
| S01 | provider 仍在流式输出时提交 | 不取消该流；回复／工具单元闭合后加入本轮下一请求；TurnId 不变 |
| S02 | A 工具执行中，B/C 尚未执行时提交 | A 允许完成，B/C 实际执行数为 0；模型收到三者结果后才收到 steer |
| S03 | 工具等待审批时提交，并与输入取消／claim 失败竞争 | claim 成功后才定向 Superseded，已领取输入不可撤回；claim 失败不关闭审批；工具和拒绝审计符合真实胜者 |
| S04 | toast 允许与 steer、最后执行准入两两竞争 | 按裁决点唯一决定；已进入 Executing 的不承诺撤销；过期点击无效 |
| S05 | 前置 hook 正在运行时提交 | 已发生 hook 不回滚，后续审批／工具可被跳过；后置审计次数和原因真实 |
| S06 | 最后一段正常响应结束前后分别提交 | 关闭前接收则本轮消费；关闭后转 FollowUp，不丢失也不注入下一轮 |
| S07 | 连续多个 steer，领取时再提交新 steer | 第一批顺序固定；后者留下一边界；每个输入只消费一次 |
| S08 | 当前无新工具，但已有待消费 steer | 不提前完成；追加 user 后同一逻辑回合继续；仍只一终态／通知 |
| S09 | 输入本身或累计受保护输入超预算 | 提交前失败的输入 Held、不标 Consumed；已消费内容不静默裁掉，明确终止 |
| S10 | 模型反复工具调用，同时持续提交 steer | 不重置 128 预算；超限 Failed，剩余输入 Held；故障预算同样不重置 |
| S11 | runStarting hooks／显式 Skill token 的 steer | 转 FollowUp，给出原因；新回合完整执行原审查／Skill 校验 |
| S12 | continuation、child、CLI 或旧 targetTurn | continuation／已关闭目标按设计入队；child／CLI 明确不支持；绝不注入另一 Turn |
| S13 | 用户停止、provider 失败、Length 时有 pending steer | 入口关闭、未消费项 Held、队列暂停；没有额外 provider 请求掩盖失败 |
| S14 | 原生 reasoning + 工具批次 + steer 后续请求 | provider 协议 fixture 通过，签名与 callId 正确，用户文本仍为 User 角色 |
| S15 | skip、deny、tool fault、cancel 四类调用交错 | 模型结果和 UI 原因区分；故障预算、hook 审计、OCE 语义均正确 |

退出条件：S01–S15 通过，A01／A04／A07 复核通过；S03–S06 必须强制覆盖所有指定竞争顺序，不能用单次手工点按作为竞争证明。

## 8. P5：完整 Vue 交互和传输恢复

对应设计：§3、§10。目标：用户能理解输入在哪里、是否生效，导航和重载不会改变投递目标。

### 任务

- [x] P5.1 新增 `useConversationInputs` 和独立队列组件，ChatView 只负责布局与接线；复用 hostBridge 关联请求。
- [x] P5.2 完成编辑、删除、提升 Steer、Held 重试、暂停／恢复和受限 detail；使用 item revision／queue revision。
- [x] P5.3 host 发布 canSteer 和原因，UI 根据实际 origin／能力展示操作；发送按钮和停止按钮保持独立。
- [x] P5.4 订阅使用 subscriptionId、conversationId、queueRevision；重载获取完整状态，旧响应不能覆盖新选择。
- [x] P5.5 ACK 后清空草稿；超时重试复用 requestId；队列变化不强制主 transcript 跟随或跳动。
- [x] P5.6 输入预览、满额、编辑冲突、模式退回、等待生效和已加入上下文均有明确反馈；遵循 Lucide／scoped styles 规范。

### 验收

| ID | 操作 | 必须观测到的结果 |
| --- | --- | --- |
| U01 | Direct 忙碌时输入和提交 | 可以编辑并加入队列；主回合持续输出；ACK 前不清草稿 |
| U02 | 当前任务可 steer／不允许 steer | 两种操作含义明确；原因由 host 提供，UI 不自判权限 |
| U03 | 编辑、取消、提升操作收到版本冲突 | 保留用户编辑内容并刷新状态；不覆盖已领取输入、不复制原项 |
| U04 | 从 A 切到 B 后收到 A 的 submit ACK / state | 投递仍属 A；B 队列、草稿和 transcript 不受旧响应覆盖 |
| U05 | WebView 重载，state 通知丢失或乱序 | 重新订阅恢复 Pending、Held、paused；旧 revision 不回滚新状态 |
| U06 | 入队超时后重试、输入满额、正文超限 | 重试不重复；超限可理解且草稿不丢；后端上限不可绕过 |
| U07 | 阅读旧 transcript 时发生队列变化 | 不自动滚到底；消费正式消息时仍遵守既有跟随偏好 |
| U08 | 重载时存在 pending 工具审批，随后 steer | 审批条和队列状态一致；Superseded 后旧允许按钮失效 |
| U09 | 暂停队列但无忙碌回合，继续入队／恢复 | 输入保留且不自动启动；恢复后按 FIFO，Held 阻塞项明确显示 |
| U10 | CLI、child 活动面板、插件视图同时存在 | 无误展示可用 steer，无父 transcript 混入 child 输入，无跨 origin 受理 |

退出条件：U01–U10 的 Vitest／Playwright 用例通过，Vue build 通过；桌面真实 WebView2 核验另计入 P6。

## 9. P6：恢复、桌面与发布验收

对应设计：§6、§8–12。目标：在可控故障和实际桌面环境下证明不会漏消息、重跑不确定操作或阻塞关闭。

### 任务

- [ ] P6.1 使用独立临时 AppData／测试数据库执行进程恢复测试，禁止用开发者真实历史做破坏性 fixture。
- [ ] P6.2 故障注入覆盖 claim、消费事务、ACK、provider、工具、终态提交和关闭各边界。
- [ ] P6.3 .NET、Vue、Playwright 全量回归；WPF／WebView2、toast 和进程恢复 smoke。
- [ ] P6.4 使用至少一个已启用、支持 reasoning 的真实 provider 验证工具→steer→后续请求；记录协议格式与模型，不保存凭据。
- [ ] P6.5 核验容量、候选公平性、快照字节数、日志字段和 waiter／lease 清理；记录宿主开销，不把 provider 等待算成 queue 处理耗时。
- [ ] P6.6 演练关闭功能开关后的保留／暂停行为；更新运行链路、用量设计及必要的项目上下文，记录最终 schema 版本。
- [ ] P6.7 在功能实现完成后的数据清理工作中记录实际清理范围、完成状态和目标 schema 初始化结果；正式使用前满足该数据前提，不增加旧数据兼容路径。
- [ ] P6.8 对设计 §13 删除清单逐项给出代码位置和搜索证据；检查 DI、引用方向、重复状态和所有实际入口，完成 A01–A08，不以旧路径“暂时不用”代替删除。

### 故障与恢复矩阵

| ID | 故障点 | 重启／恢复后的预期 |
| --- | --- | --- |
| R01 | 输入受理事务提交前退出 | 没有 Accepted 事实；同 requestId 可重新提交 |
| R02 | 受理事务提交后、ACK 前退出 | 原 input 可查询，重试返回同 ID；Pending 可见且暂停 |
| R03 | claim 后、消费事务前退出 | 旧 claim 进入 Held，不自动运行 provider／工具 |
| R04 | user／片段／Consumed 同事务写入中失败 | 整批回滚；没有半条历史或半批 Consumed |
| R05 | 消费已提交、运行 ACK 前退出 | 消费事实保留；回合 Interrupted；不重复 user，不自动 provider 请求 |
| R06 | 工具已经执行、终态尚未提交时退出 | 不根据缺失终态或 tool_runs 推断可安全重跑；原输入不回到 Pending |
| R07 | 边界后另一轮 provider 请求失败 | 已封存消息和已知 usage 可恢复；逻辑回合失败，剩余输入 Held |
| R08 | 终态写入失败、coordinator 即将释放 | 后续 Queue 不启动；恢复后的逻辑状态和暂停原因可解释 |
| R09 | stop／shutdown 与等待 ACK 交错 | admission、producer、consumer、waiter 全部有界结束，无后台孤儿任务 |
| R10 | 运行中删除会话，或 child 等待取消超时 | 删除成功不再重建队列；删除失败保留会话且暂停，无跨会话误删除 |
| R11 | 输入索引唤醒丢失、某会话不可执行 | 重扫可恢复工作，其他会话不饥饿，后台无持续空转 |
| R12 | 关闭 Queue／Steer 开关再重启 | 输入仍可查且暂停；不会让旧 Pending Steer 自动命中新回合 |

### 桌面手动验收脚本

1. 在测试 Workspace Root 新建 Direct 会话，使用可控多步工具任务；任务执行时加入两条 FollowUp，确认按序单独执行。
2. 再启动三工具调用任务，在第一工具执行时 steer；确认已执行工具的结果保留，后续工具显示真实跳过原因，回答遵循新输入。
3. 使用需要审批的写文件工具，审批等待时 steer；确认审批消失，旧 toast 的允许操作失效，文件没有被该调用修改。
4. 入队后切换会话并重载 WebView，检查队列归属、草稿和 reading position；回到原会话确认状态一致。
5. 入队后停止生成，等待超过一次调度扫描，确认队列保持暂停；手动恢复后再执行。
6. 在测试宿主的指定故障点终止进程并重启，核对 R02–R08，检查正式消息顺序与 input 消费映射。
7. 用真实 provider 完成 reasoning、工具、steer 和一次新回合回放；核查请求合法、usage 不重复，明确未覆盖的 provider 格式。
8. 运行一次 CLI、一次 child + continuation，核查现有审批边界、activity panel 和 delivery 结果；不对 CLI 声称支持 steer。

发布退出条件：所有对应自动用例通过，R01–R12 和 A01–A08 有证据，桌面脚本完成；功能实现完成后，正式使用前完成旧数据清理并初始化目标 schema。恢复用例均使用新系统生成的数据。未运行的原生或真实 provider 检查必须列为未完成门槛，跳过不折算为通过。Queue 独立版本需完成 P0–P3、对应架构及恢复／桌面用例，不能据此标记 Steer 已验收。

## 10. 测试运行入口

以下为实施时使用的 PowerShell 命令，本次文档编写没有执行这些构建、测试或 smoke。常规开发先跑受影响套件；阶段退出／发布时再跑全量。依赖恢复成功后不要无理由重复 restore。

在仓库根目录：

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location).Path '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
dotnet restore SelfClaw.slnx
dotnet build SelfClaw.slnx --no-restore
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-build --no-restore --logger 'trx;LogFileName=direct-input.trx' --results-directory .scratch/direct-message-input/test-results
```

目标测试示例（新增测试命名确定后补入筛选表达式）：

```powershell
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --filter 'FullyQualifiedName~DirectAgentChatRuntimeTests|FullyQualifiedName~DirectToolInvokerTests|FullyQualifiedName~ConversationTurnRecorderTests|FullyQualifiedName~ConversationTurnEngineTests|FullyQualifiedName~SubagentDeliveryDispatcherTests'
```

在 `SelfClaw.TranscriptVue` 目录，已安装依赖后：

```powershell
npm test
npm run build
npm run test:e2e
```

在仓库根目录启用原生／provider smoke；先记录必要的测试宿主、CLI 安装、已启用模型及桌面交互条件：

```powershell
$env:SELFCLAW_DESKTOP_SMOKE = '1'
$env:SELFCLAW_PROVIDER_SMOKE = '1'
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-build --no-restore --logger 'trx;LogFileName=direct-input-smoke.trx' --results-directory .scratch/direct-message-input/smoke-results
Remove-Item Env:SELFCLAW_DESKTOP_SMOKE
Remove-Item Env:SELFCLAW_PROVIDER_SMOKE
```

环境开关只启用实际已有／本次新增的 smoke；不是自动覆盖本文全部原生用例的保证。实施时把对应测试全名填入证据表，缺失的测试需要新增或保留明确的手动证据。

## 11. 架构验收与不变量追踪

以下检查以代码审查、调用点搜索和相应行为测试联合证明，不要求为类名或文件长度编写形式化测试。P0 先确认目标，相关阶段完成时检查真实实现，P6 汇总最终证据。

| ID | 检查内容 | 通过条件／证据 |
| --- | --- | --- |
| A01 | 状态和运行所有权 | 列出输入、运行注册、取消／完成句柄、内容流、工具调度的唯一 owner；Engine、SessionCoordinator、dispatcher 没有各自可写 busy 表；取消和同时准入用例覆盖真实入口。P0：目标职责表与现状双锁证据已形成（P0 报告 §4）；实现检查在 P2。 |
| A02 | Turn 与 Message 的领域分离 | 构造所有 MessageId 均不同于 TurnId 的 Direct／CLI／child 用例；无输出回合可保存状态和 usage；SQL／DTO／投影没有首消息承载回合的特殊规则。P0：全部 `TurnId == MessageId` 耦合点已逐条定位（P0 报告 §3）；行为用例在 P2。 |
| A03 | SDK 循环替换完整 | Direct 只存在自有循环；删除无人使用的 UseFunctionInvocation 装配与 FunctionInvoker 配置；必要适配层有明确真实消费者且无重复业务规则 |
| A04 | 提交职责和事务一致 | 开始、边界、终态及 delivery 使用同一事务内 writer；repository 不通过另一个 repository 私自提交；故障注入证明无半批输入或提前持久化的 detached turn |
| A05 | 分层和抽象必要性 | Core 无 Desktop／Infrastructure／SDK 消息依赖；DTO 纯数据；每个接口都有实际边界或替换需求，无只转发一次且没有独立职责的服务、无通用工作流框架 |
| A06 | 用户提交只有一条执行入口 | 空闲发送、忙碌入队和恢复均经同一 Direct 受理／调度链；无 VM 直接执行旁路、无两次 user 写入；Q02／Q03／Q14 通过 |
| A07 | 输入提交屏障有界且只负责同步 | 每回合最多一个进程内 boundary waiter；无新增 ACK 表／重试 dispatcher／回放队列；复用 claimId 和消费映射；M03–M06、R09 通过 |
| A08 | 全量切换与删除完成 | 设计 §13 每项标记实际删除／重构位置，引用者和 DI 全部切换；CLI、child、continuation 使用目标模型；无旧 schema fallback 或长期双写 |

每项证据记录代码位置、验证方式和未解决问题；搜索到一个同名词不能单独断言失败，必须确认它是否仍承担被替换的职责。保留当前仍有效的子代理生命周期、transcript ACK 和应用 origin guard，不能把不同领域的必要机制误删为“重复”。

| 设计不变量 | 主要验收用例 | 阶段 |
| --- | --- | --- |
| I01 单所有者、统一准入 | A01、Q02、Q07、Q09、Q15、R11 | P2 / P3 / P6 |
| I02 幂等与唯一 user | Q03、Q04、Q14、M05、R02–R05 | P2 / P3 / P6 |
| I03 未消费隔离及目标绑定 | Q01、Q10、S06、S12、U04 | P3 / P4 / P5 |
| I04 工具调用／结果闭合 | L02、L07、S02、S14 | P1 / P4 |
| I05 持久化提交屏障 | Q14、M03–M06、R04–R05 | P2 / P3 / P6 |
| I06 消息顺序与回放 | M01–M02、M07–M08、M10、U05 | P2 / P5 |
| I07 唯一终态、hooks 和 usage | L01、L08、M09、S08、S15 | P1 / P2 / P4 |
| I08 审批、checkpoint 和策略 | L04、S03–S05、S11–S12、U08 | P1 / P4 / P5 |
| I09 停止与不确定执行不重放 | Q05–Q08、Q12、S13、R03–R08 | P3 / P4 / P6 |
| I10 waiter 与资源退出 | B06、L06、M06、R09 | P0 / P1 / P2 / P6 |
| I11 原有运行模式保持 | L09、M12、U10、桌面脚本 8 | P1 / P2 / P5 / P6 |
| I12 容量与预算有界 | B03、Q13、S09–S10、U06 | P0 / P3 / P4 / P5 |
| I13 回合与消息独立 | A02、M01、M07、M09、M12 | P2 / P6 |
| I14 唯一状态来源 | A01、A05、A06、Q09、S04 | P2 / P3 / P4 / P6 |
| I15 唯一事务内写入规则 | A04、M11、M12、Q14、R04 | P2 / P3 / P6 |
| I16 替换与删除完整 | A03、A06、A08、设计 §13 清单 | P1 / P2 / P3 / P6 |

## 12. 实施记录模板

详细临时日志可以放 `.scratch/direct-message-input/`；该目录被 Git 忽略。阶段结论、设计偏差和可复核的证据摘要必须提交到 `docs/`，不能只留下一个指向本机忽略文件的链接。

每完成一阶段，在本节追加一条记录并更新第 1 节状态：

```text
阶段：P_
日期／实施提交：
执行环境：Windows、.NET、Node、浏览器／WebView2、SDK 版本
schema：本阶段目标版本、初始化与约束验证结果
旧数据清理：正式使用前的完成状态与执行记录（测试阶段注明独立测试库）
完成任务：
职责迁移与删除：原 owner → 新 owner → 已删除字段／路径／DI
验收用例：ID → 实际测试全名／手动步骤 → 结果
架构验收：A01–A08 的相关项 → 代码位置／搜索／行为证据 → 结果
构建／自动测试命令与计数：通过、失败、跳过分别列出
原生／真实 provider 检查：已执行项、模型与协议格式、未覆盖项
关键证据：请求／事件计数、SQLite 查询结果、恢复前后状态
设计偏差及理由：引用设计章节和本次修订
风险／阻塞项：
结论：通过／未通过；Queue／Steer 是否允许启用
```

初始记录：2026-10-02 仅形成设计与实施验收文档；没有实现本功能，没有运行本文的构建或功能验收用例，不声明任何阶段已通过。

以下 P0 初次记录与复审记录保留历史；当前结论以文末补证/P1 交付记录及报告为准。

阶段：P0
日期／实施提交：2026-10-02，基线 `0b8c509c37ac1831b51643e19fd8429af84e522a`（P0 仅新增测试与文档）
执行环境：Windows；.NET SDK 10.0.400 / 运行时 10.0.11；M.E.AI 10.10.0、M.E.AI.OpenAI 10.10.0、OpenAI SDK 2.13.0、Anthropic 12.49.0、OllamaSharp 5.4.30、MCP.Core 2.2.0；Node v22.19.0
schema：基线 v29（P0 未变更 schema）
旧数据清理：未执行；P0 未产生持久化行为变更，验收使用离线 fixture
完成任务：P0.1–P0.7 的基线探查与审计已执行；复审要求 P0.8–P0.10 补齐退出证据
职责迁移与删除：P0 不改生产代码；目标归属为 `ConversationRunCoordinator`（取代 Engine gate + SessionCoordinator 启动锁），删除清单已逐项定位（P0 报告 §4／§5）
验收用例：既有 114 项及新增 13 项测试执行通过；B01 只证明 scripted 事件翻译、B04 只证明入站解析与合并、B06 部分顺序依赖调度，三者完整验收仍待 G01–G03。B02／B03 的顺序断言和命名改进见 G04。实际测试名称保留在 P0 报告中。
架构验收：A01（目标职责表）、A02（Turn/Message 改造清单）完成；A03–A08 待后续阶段
构建／自动测试命令与计数：`dotnet build SelfClaw.slnx` 0 警告 0 错误；`dotnet test` 全量 1246 通过 / 0 失败 / 4 跳过（smoke 门控）；`npm test` 89 通过；`npm run build` 成功
原生／真实 provider 检查：未执行（需 smoke 环境变量与已启用模型）；未覆盖项见 P0 报告 §7
关键证据：四适配器协议原始输出（附录 A）；迭代对照表（§2 B03）；屏障四路径（§2 B06）；事务边界与 `BeginTransaction` 搜索（§5）
设计偏差及理由：报告 §6 的 8 项调整已吸收；新预算明确 128 次逻辑请求，旧 129 次是对照；usage 先按请求归一化；child 占位过滤随占位删除；目标架构不变
风险／阻塞项：P0 G01–G04 尚未完成；第二次请求序列化应先离线补证，再由 P1 L07 回归，不能拖到真实 provider smoke 才发现问题
结论：P0 补证中；Queue / Steer 不允许启用（开关尚未实现）

修订记录：2026-10-02 根据用户确认，移除旧数据回填、兼容读取和带旧数据升级验收；改为实现完成后统一清理旧数据，直接验证目标 schema。保留当前执行链路的功能回归及新系统数据的恢复验收；未执行数据清理。

架构复审记录：2026-10-02 根据用户提出的整洁与可维护原则，将逻辑回合从首条 assistant 中独立出来，usage 改按 turn_id 关联；强制统一运行准入、共用事务 writer、删除旁路。P2 改为基础重构，P3 再交付 Queue。新增 A01–A08 架构验收及 I13–I16 不变量。本记录只表示文档已修订，不表示这些重构或验收已经完成。

P0 证据复审记录：2026-10-02 复跑新增 13 项全部通过，核对归档 TRX 及 resolved packages；将原“P0 全部通过”修正为“基线及架构审计已执行，退出证据补齐中”。更新 P1 预算、响应完整枚举和用量契约，以及 P2 waiter 终结状态、所有权校验和删除任务。此次未修改测试或生产代码，未重复执行全量／Vue／原生 smoke。

### 2026-10-02 P0 补证及 P1 交付记录（当前）

- 完成任务：P0.8–P0.10（G01–G04）和 P1.1–P1.7；P2–P6 未实施，Queue/Steer 用户入口未开放。
- P0：替换前补证 23 通过/0 失败/0 跳过，修复屏障单 waiter、Chat Completions reasoning 回传和 Responses 原生 function item 丢失；见 P0 报告 §10。
- P1：`DirectConversationLoop` 接管 SDK 自动循环；`DirectConversationContext` 持有原生历史；唯一 `DirectToolInvoker` 直接调用；`RequestUsageAccumulator` 按请求归一化 token 和 total，再跨请求计账。
- 删除：生产 UseFunctionInvocation、pipeline FunctionInvoker 委托、FunctionInvocationContext 入口、旧 StreamResponseAsync、fake factory pipelineBuilder 和 runtime 测试中的 SDK 循环包装。纯 SDK 基线单独保留。
- 预算：最大 128 次请求，含首次；第 128 次工具不执行并以 BudgetExhausted 收尾；纯最终回答可成功。旧 129 请求/128 工具仅作为对照。
- 验收：L01–L09 与 A03/A05 本阶段自测/代码核查通过；方法全名和行为证据见 [P1 报告](direct-message-input-p1-implementation-review.md) §4。
- 构建：`dotnet build SelfClaw.slnx --no-restore` 成功，0 警告/0 错误。交付全量 `dotnet test SelfClaw.slnx --no-restore --logger 'trx;LogFileName=p1-delivery.trx' --results-directory .scratch/direct-message-input/p1-results`：1283 通过/0 失败/4 门控跳过。
- 失败记录：首轮全量 MCP PID 文件读写竞争 1 失败，改为原子发布测试 marker 后通过；新增 mixed total/stale total 测试 3 失败，单请求归一化修复后通过。全部保留在报告和 TRX，不用跳过代替修复。
- 原生检查：真实 provider、真实 Ollama、WPF/WebView2 手工 smoke 未运行；门控 smoke 4 项跳过，详情见报告 §6。没有 Vue 产品代码变更，本轮未跑 Vue。
- 数据：schema v29 不变；没有修改 Core/Desktop/数据库/Vue 产品代码，没有清理实际数据；CLI、child、continuation 产品约束保留。
- 交付状态：实现及自测完成，代码和报告待另一位 reviewer 独立审核；本记录不代表独立审核通过。

### 2026-10-02 独立审核问题首轮修复（历史）

- IR01、IH01、IH02 均已修复；规则、实际测试名与完整命令见实施报告 §7。独立审核文件保留原文，没有由实施者改为“审核通过”。
- 新增正式测试 25 项。修复前边界测试 15 失败/8 通过；修复后的受影响回归 592 通过；原样独立探针重跑 5 通过（含旧 SDK 对照，由修复者执行）。
- 恢复普通构建后 0 警告/0 错误；正式全量 `review-fixes-full.trx` 为 1308 通过/0 失败/4 门控跳过，确认不含临时探针。
- 本次未运行真实 provider、桌面手动 smoke 或 Vue；不改 Core/CLI/数据库结构，也没有清理实际数据。状态：修复及自测完成，待独立复审。

### 2026-10-02 IR02 直接修复与验证（当前）

- 用户授权原 reviewer 直接修改生产实现；已统一 Core 用量契约，CLI parser 负责归一化，删除 Direct translator 局部补偿。
- 正式定向测试 94 通过，全量 1329 通过／0 失败／4 门控跳过；包含此前两项 Activity 失败用例。IR02 及已发现 P1 问题全部关闭。
- 本次修改 Core／CLI 的必要边界，没有修改 recorder／数据库结构、没有提前实施 P2。保留未运行的原生／真实 provider smoke 限制，证据见实施报告 §8。

### 2026-10-03 P2 交付记录（当前）

- 基线 ca42646；完成 P2.1–P2.12。schema v30、独立 turn/message/usage、唯一准入、共用事务 writer 与真实 SQLite 消费屏障已接入生产链。
- M01–M12、A01/A02/A04/A07/A08 本阶段自测/源码核查见 [P2 实施报告](direct-message-input-p2-implementation-review.md)，不代表独立审核通过。
- 最终 .NET 全量 1419 通过／0 失败／4 门控跳过；Vue 94 通过、构建成功、Playwright 15 通过。失败与修复过程保留在报告。
- 删除 message-based finalization/usage、旧准入路径及 P0 harness；普通/child 工具进度改为片段/工具/块原子写入，continuation 不提前持久化。
- 不实施 P3–P6，不开放 Queue/Steer；没有启动真实用户应用、清理旧数据、提交或推送。原生／真实 provider smoke 仍有明确门控限制。

### 2026-10-03 P2 独立审核（当前）

- 独立复审者在基线 ca42646 工作区重新构建并运行：build 0 警告/0 错误；M01–M12 定向 156 通过/0 失败；Vue 94、vite build 成功、Playwright 15。
- 生产实现七项重点全部核实通过：TurnId/MessageId 分离、唯一准入与删除屏障、有序消费屏障与 WebView ACK 解耦、同一事务 writer 与 detached continuation、顺序/回放一致、P1 用量契约、删除清单与无 Queue/Steer 越界。
- 全量首轮 1418/1/4，失败 `SqliteSubagentTaskRepositoryTests.Delivery_terminal_fault_rolls_back_delivery_turn_fragment_and_usage`（`ObjectDisposedException: SQLitePCL.sqlite3`）；两次复跑 1419/0/4。定位为 P2 新增测试夹具的进程级 `SqliteConnection.ClearAllPools()` 与并行连接打开的竞态（IR-P2-01）。
- 结论：**P2 生产实现通过独立审核；全量“稳定全绿”声明需修复 IR-P2-01 后重新独立复核。Queue/Steer 仍不得开放。** 原生／真实 provider／WPF-WebView2 手工 smoke 未验证，不计通过。
- 本轮仅审核与文档，未修改生产代码、未清理数据、未提交或推送。完整范围、命令、哈希与未验证项见 [P2 独立审核](direct-message-input-p2-independent-review.md)。

### 2026-10-03 P2 独立审核问题修复与最终判定（当前）

- 用户授权修复审核发现的问题。仅改测试基础设施与一个死成员，未改 P2 生产行为：
  - IR-P2-01：新增测试专用 `SqliteTestPools.ClearFor(path)`，以路径作用域 `ClearPool` 取代全部 `SqliteConnection.ClearAllPools()`；`AiModelConfigurationTests` 中一条指向临时库的默认池化原始连接改为 `Pooling=False`。
  - IR-P2-02：删除无消费者的 `ConversationSessionCoordinator.SelectedToolRuns`；`_deletions` tombstone 为 I09 不变量实现，保留待 P6。
- 验证：build 0 警告/0 错误；修复后**连续 6 次 .NET 全量均 1419 通过／0 失败／4 门控跳过**（TRX SHA-256 见独立审核 §2.1）；M01–M12 定向 156 通过；Vue 94、build、Playwright 15 与首轮一致。
- 判定：**P2 独立审核通过，全量回归稳定绿色；P2 可以开启 P3。** P3 仍不得开放 Steer、不得让 child/CLI 获得输入队列、不得绕过唯一准入。原生／真实 provider／进程恢复 smoke 仍为未完成门槛。

### 2026-10-03 P3 实施记录（历史，实施者自述）

- 基线 `17243f9`；完成 P3.1–P3.7。schema 仍为 **v30**，未新增 DDL、未 bump 版本。
- 交付：`ConversationInputService` 受理/快照/幂等/revision；`ConversationInputDispatcher` 候选扫描、唯一准入、FIFO 逐项执行与失败暂停；`SqliteConversationInputRepository` 实现 `IConversationInputStore`/`IConversationInputSchedulerStore`；开始事务新增 actor claim 消费；`ConversationInputBridge` + `ConversationInputPublisher` + Vue `useConversationInputs`/`ConversationQueue.vue`；启动恢复。
- 删除/迁移：VM 空闲 Direct 直接执行 → 统一受理 + dispatcher；CLI 保留既有准入；未新增第二套 input model/busy 表；未新增 user 写入路径。
- 开关：`SELFCLAW_QUEUE_ENABLED` 默认关闭；关闭时拒绝忙碌入队、不扫描 backlog，空闲单条仍经同一路径执行。Steer 未开放（`promote`/`steer` 返回 `steer-not-enabled`）。
- 验收：Q01–Q16 由 `SqliteConversationInputQueueTests`（11）、`ConversationInputDispatcherTests`（15）、`ConversationInputHostTests`（4）、`ConversationDeletionServiceTests`（删除暂停 2 项）、`ConversationRunCoordinatorTests`（准入隔离 1 项）、`DesktopServiceRegistrationTests`（1 项）及 VM/Vue 用例全覆盖；A01/A04/A06 见 [P3 报告 §9](direct-message-input-p3-implementation-review.md)。
- 构建／测试：build 0 警告/0 错误；.NET 全量 1454 通过／0 失败／4 门控跳过（连续两次）；Vue 100、build 成功、Playwright 15。
- 未运行：真实 provider、真实 WPF/WebView2、toast、进程恢复 smoke（4 项门控跳过）与桌面手动脚本。
- 状态：实现与自测完成，**待独立审核**；不代表独立审核通过，未提交或推送。


### 2026-10-03 P3 独立审核（历史，不通过）

- 基线 `17243f9`，独立读取全部约束、生产调用链和测试断言，使用隔离输出重新构建/运行；未修改生产代码或既有测试、未清理用户数据、未提交或推送。
- 正式结果：build 0 警告/0 错误；.NET 全量连续两次各 1454 通过/0 失败/4 门控跳过；独立搜索的受影响套件 201/0/0；Vue/Vitest 100/0/0；隔离 Vite build 成功。Playwright 首次 15 项完成但收尾挂起，沙箱外复跑正常结束为 15/0/0；这 15 项没有 Queue 端到端场景。
- 独立探针：真实 SQLite + run coordinator/dispatcher/bridge 的 24 项为 **14 通过/10 失败/0 跳过**；九个失败方法逐个隔离仍失败，终态 Failed/Persisted=false 两种释放窗口另行隔离均失败；生产 composable 的 Node 探针 0 通过/3 失败。完整名称、原始错误、命令、SHA-256 及 Q01–Q16/A01/A04/A06 逐项判定见 [独立审核报告](direct-message-input-p3-independent-review.md)。
- 阻塞：Vue subscriptionId 非 GUID、状态枚举不匹配；Held 被跳过；Engine 释放准入先于失败暂停；暂停 revision 冲突被忽略；关闭开关仍 Resume backlog；前 32 个暂停会话饿死后项；删除后的迟到受理重建会话；Agent 定义未冻结/retry 未重新捕获；物理 Root 消失仍启动；真实用户 requestId 没有传入幂等受理；部分操作响应清空列表/入队强制跟随。
- 已核实部分：schema v30/DDL 与 P2 完全一致；无 Steer 执行路径，promote/steer 明确拒绝；child/CLI 真实入口边界保留；VM 无空闲 Direct 直接执行旁路；未发现第二套会话 busy 真值；开始/边界/终态/delivery 复用同一事务 writer，逐表消费故障注入通过。
- 未验证且不计通过：真实 provider/Ollama、真实 WPF/WebView2、toast、原生进程恢复 smoke 和桌面手动脚本。没有用正式测试绿色替代这些门槛，也没有用重跑掩盖生产探针失败。
- **结论：P3 不通过独立审核，不能独立交付；Queue 不作为通过验收的功能启用；Steer 继续关闭。** P2 已通过的基线结论不变，P4–P6 不开始。实施者自述报告保留原样。


### 2026-10-03 P3 审核问题修复（当前，待独立复审）

- 用户授权修复 IR-P3-01–11。修复真实队列协议、Held/FIFO、终态释放窗口、暂停 CAS、功能开关、公平扫描、删除/关闭受理生命周期、完整定义指纹及 retry/Root 校验、真实请求幂等、旧 ACK/部分状态与 transcript 跟随。
- RunCoordinator 仍为唯一 owner；受理操作纳入同一 owner 的取消/等待。排队 Engine 结果明确包含 Turn+Persisted，dispatcher 在失败暂停持久化之后释放句柄。schema v30 不变，无新 DDL，无 Steer 执行路径，无空闲 Direct 旁路。
- 启动恢复采用所有入口共享的完成 Task；旧 Running Interactive Direct → Interrupted、旧 Pending Steer → Held，Consumed 不重放。快照使用完整实际 envelope 验证 256KiB；DTO 独立文件，repository 分为同一 owner 的四个职责文件。
- 原 24 个审核 .NET 场景迁入正式测试，增加生命周期及 router 用例，共新增 36 项。最终同一代码连续两次全量各 **1490 通过/0 失败/4 门控跳过**；Vue 106/0/0；Playwright 17/0/0（新增 2 个 Queue 浏览器用例）；原样前端审核探针 3/3；build 0 警告/0 错误，diff --check 成功。
- 原始独立审核失败及其哈希保留；测试 fixture/协议边界调整说明、完整名称、命令和新 SHA-256 见 [修复记录](direct-message-input-p3-review-fixes.md)。
- 不冒充新的独立复审；Queue 开关默认关闭，Steer 继续关闭，P4–P6 不开始。真实 provider/Ollama、WPF/WebView2、toast、原生进程恢复 smoke 和桌面手动脚本仍未运行，不计通过。未清理用户数据、提交或推送。

### 2026-10-03 P3 现场缺陷修复（取消回合后队列闪烁 / 后续输入不生效）

- 现场：A 运行中停止后，队列进入暂停；dispatcher 每 250ms 仍把暂停会话当作候选，反复 `TryReserve`→claim 失败→`Complete`，每次都对所选会话重发 transcript，导致闪烁；新输入排队但不调度。
- 修复：`ListConversationIdsWithUnprocessedInputsAsync` 重命名为 `ListDispatchableConversationIdsAsync`，只返回“未暂停且最早未处理项为 Pending FollowUp”的会话；dispatcher 在领取前先判定可调度，暂停/非 Pending 头直接 `Blocked`，不占用运行句柄。
- 验证：新增 5 个 dispatcher 用例（含完整“A 运行→停止→扫描不闪烁→恢复按序执行 B/C”）；反向还原后前三项失败，确认捕获该缺陷。.NET 全量 1495/0/4（连续两次）、Vue 106、build、Playwright 17。详见 [现场修复记录](direct-message-input-p3-field-fix-queue-flicker.md)。
- 未提交、未推送、未清理数据；schema 仍 v30，无 DDL。

### 2026-10-04 P3 验收修复（默认关闭开关卡死与 continuation 空转）

- 失败／停止后的 `paused` 只挡住**已经在队里的** FollowUp，不再挡住空闲新发送。FIFO 头若就是这次 `immediateInputId`，领取同事务解除暂停并立即启动（开关开、关都如此）。已有前序 Pending/Held 时新项仍只入队，扫描不会越过暂停头。
- 开关关闭时，未处理积压改为 `pending-inputs` 拒绝，不再误报“当前回合执行中”。Resume/Retry/扫描仍不调度 disabled backlog（IR-P3-05）。
- continuation 先读 `HasUnprocessedInputs`，有未处理项时不预留运行句柄、不发布 `Changed`。预留后仍复核一次，避免 TOCTOU。
- 回合崩溃恢复从输入仓储挪到 `SqliteStartupRecovery`（Infrastructure 初始化、dispatcher 启动前），覆盖 Interactive origin，不再用错误文案反查消息。
- 队列接口拆成 Store / UpdateStore / SchedulerStore（各 ≤5 方法）；列表操作进 `ConversationInputQueueService`；原因码文案只留 `ConversationInputReasonText`。
- 回归：`A_failed_turn_then_idle_send`、`A_user_stop_then_idle_send`（开关开/关）、`A_blocked_continuation_does_not_churn_run_changes`。定向 182/0/0；全量连续两次 1500/0/4；Vue 106。未跑 Playwright、真实 provider、WPF smoke。schema 仍 v30。

### 2026-10-04 P4 交付记录（当前，实施者自述）

- 基线 `17b73a4`；完成 P4.1–P4.8。schema 仍为 **v30**，未新增 DDL/迁移；Queue 仍 `SELFCLAW_QUEUE_ENABLED` 默认关闭，Steer 新增 `SELFCLAW_STEER_ENABLED` 默认关闭。
- 交付：`IConversationInputSteeringStore` 高水位领取/释放/Held；`SettleSteerIntakeAsync` 随终态事务原子收口；`DirectTurnInputSession` 领取、策略发布、pulse 与 Hold；`DirectToolInvoker` 独占工具阶段与执行许可，`ToolApprovalDecision` 显式三值；`DirectConversationLoop` 预算守卫与边界消费；`send-prompt` 可选 `kind=steer`/`targetTurnId` 关联入口。
- 删除：`IConversationInputRepository.ReadClaimedBatchAsync` 及 P2 fixture 读取路径；未新增 ACK 表、投递队列或第二套状态机。
- 验收：S01–S15 由 `DirectSteerLoopIntegrationTests`（7）、`DirectToolInvokerSteerTests`（8）、`DirectSteerPolicyTests`（4）、`ConversationInputSteerServiceTests`（9）、`SqliteConversationSteerRepositoryTests`（12）及既有审批/协调/注册用例覆盖；A01/A04/A07 证据见 [P4 报告 §5](direct-message-input-p4-implementation-review.md)。
- 独立审核修复：IR-P4-01（S06 末尾竞争）改为最终边界在 `_claimGate` 内原子“领取或关闭”，成功收口不再把残留 steer 重开为 FollowUp；IR-P4-02（S11 显式 Skill token）由 `ISkillTokenDetector` 在受理与领取两处转 FollowUp。修复后 .NET 全量 **1542 通过／0 失败／4 门控跳过**。
- 构建／测试：build 0 警告/0 错误。
- 未运行：真实 provider、WPF/WebView2、toast、进程恢复 smoke 与桌面手动脚本；Vue 无产品代码改动，本轮未跑。均不计通过。
- 状态：实现与自测完成，**待独立审核**；不代表独立审核通过，未提交或推送。

### 2026-10-04 P5 交付记录（当前，实施者自述）

- 基线 `8d72122`；完成 P5.1–P5.6。schema 仍为 **v30**，未新增 DDL/迁移；Queue `SELFCLAW_QUEUE_ENABLED` 与 Steer `SELFCLAW_STEER_ENABLED` 均默认关闭。P4 后端未改动消费语义，仍待独立复审。
- 交付：`ConversationInputSteerPolicy` 成为受理回退、promote 校验与状态发布的唯一判定；`ConversationInputPublisher` 每次推送按活动句柄发布 `canSteer/steerReason/steerTargetTurnId` 并订阅 run change；`conversation-input/promote` 经 `ConversationInputQueueService.PromoteAsync` + 存储层 `PromoteAsync` 在会话 gate 内原子改写原行；Vue 完成编辑／取消／提升／重试／暂停／恢复／detail、ACK 清稿、超时幂等重试、模式退回与消费反馈；ChatView 只接线。
- 存储：仅接受 `Pending FollowUp`；Held 需先 retry 重新校验；stale revision/已领取/已关闭目标返回 Conflict／Invalid；显式 Skill token 与受理/领取同规则转 FollowUp；未新增输入行（保持 inputId/sequence）。
- 验收：U01–U10 由 Vitest（`conversationInputs`／`chatComposer`／`conversationQueueUi`）与 Playwright（`queue.spec.js` 10 项）覆盖，宿主侧由新增 7 项 .NET 用例固定；逐项证据见 [P5 报告 §4](direct-message-input-p5-implementation-review.md)。
- 独立审核修复：IR-P5-01（取消冲突后抑制名单未撤销，吞掉真实消费的「已加入上下文」）改为仅成功取消保留抑制，非 `ok` 结果立即撤销；新增 2 项 Vitest 回归，原审核探针 22/22（含原失败项）。独立审核文件保持原文。
- 构建／测试：build 0 警告/0 错误；.NET 全量 **1549 通过／0 失败／4 门控跳过**；Vitest **126 通过**（25 文件）；Vite build 成功；Playwright **25 通过**（queue 10 + activity 15）。
- 未运行：真实 provider、WPF/WebView2、toast、进程恢复 smoke、桌面手动脚本；4 项门控跳过。均不计通过；桌面真实 WebView2 留 P6。
- 状态：实现与自测完成，独立审核 IR-P5-01 已修复并复核；不代表已验证桌面行为，未提交或推送。

### 2026-10-04 P5 独立审核（当前）

- 结论：U01–U10 主路径成立，P5 不能记为无缺陷。IR-P5-01：取消冲突会把该 steer 记成已本地结束，之后真正消费时不再提示「已加入上下文」。详见 [P5 独立审核](direct-message-input-p5-independent-review.md)。
- 本轮运行：Vitest 124/0；Vue build 成功（输出在审核用目录，未覆盖 Desktop 资源）；官方 Playwright 25/0，23.6 秒正常退出；生产 composable 探针 21 通过 / 1 失败；独立浏览器点击 7/0。
- 未运行：.NET 全量、真实 WebView2、真实 provider、toast、进程恢复。Queue 与 Steer 仍默认关闭。未改生产代码，未提交或推送。
