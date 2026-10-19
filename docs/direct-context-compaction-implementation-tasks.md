# Direct 上下文自动压缩 —— 分步实施任务文档

> 配套设计：《direct-context-compaction-design.md》v2（2026-10-17）。本文按任务粒度拆解，每个任务可独立提交、独立验证。
> 引用格式：D§n 指设计文档章节；文件路径均相对仓根。

## 使用约定

- **完成定义（每个任务通用 DoD）**：`dotnet build SelfClaw.slnx` 零新增警告、`dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj` 全绿；涉及前端时 `SelfClaw.TranscriptVue` 内 `npm test` 与 `npm run build` 通过；涉及 UI 的任务附手动验收步骤。
- **提交粒度**：一个任务一个（或一组）提交，提交信息沿用仓内惯例（`feat(compaction): …` / `refactor(direct): …` / `feat(settings): …`）。
- **规模**：S ≈ 半天内，M ≈ 1 天，L ≈ 2-3 天（含测试）。
- 标注 ⚠ 的是设计核对中确认的易错点，实施时优先核对。
- 阶段编号：P0 契约与持久化 → P1 计数与压缩引擎 → P2 设置链路 → P3 前端呈现 → P4 收尾。

## 决策现状（2026-10-17）

| # | 决策 | 状态 |
| --- | --- | --- |
| DP1 | schema v31 → v32：新增 `conversation_compactions` 表 + `turn_usage.last_request_input_tokens` 列 | **已确认**（旧库只读拒绝、不迁移不回填，接受本地库需重建） |
| DP2 | 计数改为离线 tiktoken 系 tokenizer（`Microsoft.ML.Tokenizers` + 数据包）+ provider 实测值，**不再使用字节估算** | **已确认方向**；包的最终选择见 C4（实现期内可换 `SharpToken` / `TiktokenSharp`，抽象层不变） |
| DP3 | 压缩模型选择存 `desktop-settings.json` 的 `direct_compression` 节点，不新增 model selection scope | 待确认（默认按此实施） |
| DP4 | 回合内（工具循环中）不压缩、不裁剪 | 待确认（默认按此实施） |
| DP5 | 关闭压缩时超窗**失败**而非裁剪 | 待确认（默认按此实施） |

DP3–DP5 与既有实现无冲突，按默认值推进即可；若要改变，改动集中在 C9 与 C7。

## 实施进度（2026-10-17，由 pi 实施、我审核）

| 任务 | 状态 | 审核结论 |
| --- | --- | --- |
| C1 Core 契约 | 已完成 | 通过（独立复跑 build 0 警告 0 错误；全量 1432 通过 / 0 失败 / 4 跳过） |
| C2 schema v32 + 仓储 | 已完成 | 通过（版本断言三处同步；v31 已加入“非目标版本被拒”用例集；新列为目标建表语句中的列，无补列分支） |
| C3 提交路径承载压缩项 | 已完成 | 通过（独立复跑 build 0 警告；全量 1441 通过 / 1 失败 / 4 跳过，失败项是既有的 `AutomationTickerQIntegrationTests` 计时抖动，隔离复跑通过，与本次改动无关） |
| C4 tokenizer 计数 | **已完成** | 代码与测试已就位（固定样本数值、缓存 FIFO、降级、并发均有用例；pi 侧全量 1461 通过 / 0 失败 / 4 跳过）。缺口已修：接口改为按**调用时**模型解析编码（`IsAvailable(model)` / `Count*(…, model)`，4 成员），tokenizer 与消息/工具指纹缓存均按编码分桶（key 含编码名），编码失败按编码隔离、各记一次 warning，无字节估算兜底。我侧独立复跑两次——修前（隔离 `bin/pi-c4-review/`）0 警告 0 错误、1460 通过 / 1 失败（失败项为既有 `AutomationTickerQIntegrationTests` 计时抖动，隔离单跑 3 s 通过，与本任务无关）；修后（隔离 `bin/pi-c4-fix-review/`）**0 警告 0 错误、1464 通过 / 0 失败 / 4 跳过**，`--filter TokenCounter` 22 通过，单实例 `gpt-4o`=3 / `gpt-4-turbo`=6 且两编码各只创建一次。已接受成本：某编码首次使用在回合线程上解压词表约 150–400 ms（预热属 §5.3 可选项，本次不做） |
| C5 `DirectContextCompressor` | **已完成** | 压缩器 + `CompressionInstruction` + 两个 DTO（`Context/Models/`，各自成文件）+ DI 单例 + 17 个用例。两处语义缺陷已修：`SourceMessageCount` 取 `PrefixUnits.Count`（一个单元 = 一条会话消息，另有专门用例固定该不变量）、`SummaryTokens` 由压缩器注入 `IDirectTokenCounter` 并用**实际产出摘要的 lease** 的 `Profile` 计算（回退路径用回合 profile，用例用同一 CJK 摘要的 6 vs 3 区分编码）。我侧独立复跑（隔离 `bin/pi-c5-fix-review/`）：**0 警告 0 错误、1480 通过 / 1 失败 / 4 跳过**，`--filter Compressor` 17 通过；唯一失败仍是既有 `AutomationTickerQIntegrationTests` 计时抖动（该用例此前隔离单跑 3 s 通过），与 C5 无关 |
| C6 composer 规划 + 边界重放 + 删字节估算 | **已完成** | `PlanCompression`（收**已计数的单元**：保留比例与 `SourceTokens` 都需要每单元计数）、`BuildUnits` / `BuildSystemText` 暴露给调用方计数、`BuildMessages` 按 `SourceSequence > ThroughSequence` 重放并把摘要注入 system 段第三位、窗口校验改 `countedTokens + 预留 > 窗口`（计数不可用 fail-open）、`BudgetFor` 与整套字节估算及两个旧开销常量删净（`rg` 无命中）、`DirectPromptHistoryUnit` 改 `(Messages, SourceSequence, int? Tokens = null)`、loop 改实例类并注入计数器（runtime 因此每回合全量计数，属 C7 要替换的临时实现）。我审出并修掉一处真实口径错误：触发判定漏算输出预留，小窗口下会在压完仍超窗时才失败 → 改为 `counted + reserve >= threshold`，新增用例已实测能区分新旧实现。我侧独立复跑（隔离 `bin/pi-c6-fix-review/`）：**0 警告 0 错误、1483 通过 / 0 失败 / 4 跳过**，`--filter Prompt` 51 通过 |
| C7 runtime 接入 | **已完成** | 落在 `RunStartedEvent` 之后、`Requesting` 之前：读策略 → 计数（跳过已被旧摘要覆盖的单元）→ `PlanCompression` → `Started` + `Compacting` → 压缩 → `Completed` / `Failed` → 用含摘要的 system 段重算 → `EnsurePromptFitsWindow`。另：`DirectCompressionPlan` 增 `ThresholdTokens`（单一真相源，不在 runtime 复制公式）、计数不可用的诊断沿用 `Initializing` 状态、`Compacting` 在 recorder 与 activity coordinator 两处映射已加、`DisabledDirectCompressionPolicySource` 新建并注册、压缩器改工厂注册（`internal` 构造 DI 不认）。我审出窗口校验在 runtime 与 composer **重复实现**（composer 那份生产路径不可达）→ 已删 composer 版及其 `countedTokens` 参数，随后把 composer `BuildMessages` 已无读者的 `budget` 参数也删掉（17 个调用点里只有 runtime 与 1 处测试需改）。我侧独立复跑两次（`bin/pi-c7-review/`、`bin/pi-c7-cleanup-review/`）：**0 警告 0 错误、1484 通过 / 0 失败 / 4 跳过**，`--filter Direct` 381 通过。遗留待 C16：`BuildMessages_sends_the_full_history_without_a_context_window` 与另一条全量重放用例语义重叠、压缩器里同名私有 `BuildMessages` 是否改名消歧 |
| C8 录制与投影 | **已完成** | recorder 加三个事件分支（Started 立即发布、Completed 写 `PendingCompactions` + `ReplaceCompaction` + 立即发布、Failed 只记日志与活动文案）；投影新增 `TranscriptCompactionNotice` 与 `_compactionCache`，按 `ThroughSequence` → `CreatedAtUtc` 排序后插到边界消息**之后**（我按代码确认顺序是先加消息项再加压缩项），无对应消息的边界落到历史末尾；补上投影层原本缺失的管道（`TranscriptProjectionRequest.Compactions` + `MainWindowViewModel` 转发快照 + `AutomationBridge` 注入压缩仓储并加载）。我侧独立复跑（隔离 `bin/pi-c8-review/`）：**0 警告 0 错误、1491 通过 / 0 失败 / 4 跳过**，`--filter Transcript` 46 通过、`--filter Recorder` 23 通过 |
| C9 用量摘要 | **已完成** | `TranscriptUsageSummary`（新）+ `TranscriptRenderState.Usage`；`BuildUsageSummary` 对已加载回合做一次线性求和（累计输入/输出相加；上下文、窗口、最近一次请求输入都取最新非空；`LastRequestInputTokens` 优先、缺失退回 `ContextTokens`；整会话无用量则为 null），子代理用量不并入、不新增 SQL；`CreateTranscriptPayload` 与 `CreateTranscriptPatch` 两处载荷都带 `Usage`。`ContextIsEstimated` 只在上下文数字来自本地计数时为 true（C17 起由 `UsageProgressEvent` 的估算快照提供）。（本轮 pi 未输出报告，我按代码 + 独立复跑审核）我侧独立复跑（隔离 `bin/pi-c9-review/`）：**0 警告 0 错误、1499 通过 / 0 失败 / 4 跳过**，`--filter Transcript` 54 通过 |
| C10 设置服务 + 策略源 | **已完成** | `SelfClaw.Desktop/Services/Compression/DirectCompressionSettingsService.cs`（新，实现 `IDirectCompressionPolicySource`，节点 `direct_compression`）：`GetAsync` 走 `_cached ??= LoadAsync`（回合路径零 I/O）、`SaveAsync` 先归一化再在锁内写盘更新缓存、`Changed` 在**锁外**触发（避免订阅者同步回读死锁）；归一化 clamp 比例 [0.10, 0.95]、绝对阈值 [4000, 1000000]、非有限值回落默认、不可用模型归零；坏文件或缺失回落默认且不抛（设计 C08）。DI：Desktop 注册服务并把 `IDirectCompressionPolicySource` 转发给它，覆盖 Infrastructure 的 `DisabledDirectCompressionPolicySource.Instance`，并加组合根顺序测试（共 2 条注册、单服务解析落在 Desktop 实例）。我侧独立复跑（隔离 `bin/pi-c10-review/`）：**0 警告 0 错误、1523 通过 / 0 失败 / 4 跳过**，`--filter DirectCompressionSettings` 23 通过 |
| C11 bridge + 路由 + 推送 | **已完成** | `Services/Compression/DirectCompressionSettingsBridge.cs`（新）：`TryHandleAsync` 同形于 `AppearanceSettingsBridge`；`get`/`save` 回 `{ type: direct-compression/state, requestId, policy }`，`save` 回显归一化结果、异常时附 `error` 并回退默认；订阅服务 `Changed` 后用 `PostPush` 广播（推送前重读服务、切回 UI dispatcher、fire-and-forget）；路由分支放在 appearance 旁，DI 注册与 `new WebViewMessageRouter(...)` 实参列表同步。我侧独立复跑：构建 **0 警告 0 错误**、`--filter DirectCompressionSettings` **34 通过 / 0 失败**；全量首跑出现一条与本任务无关的失败（`ConversationInputDispatcherTests.One_failing_conversation_does_not_block_another_conversation`），隔离复跑 3 次全绿（24/24）→ 判定为并发负载下的既有类抖动 |
| C12 设置页 | **已完成** | `SettingsView.vue` 加 `{ id: 'compression', label: '上下文压缩', en: 'COMPRESSION', icon: Shrink }`（AI 分组末尾）与 `defineAsyncComponent` 映射；新页 `components/settings/Compression.vue`（根 `sc-root sc-stage sc-page`）+ `components/settings/compression/` 六个子组件（含开关、比例、绝对阈值、模型选择）；新 `composables/useDirectCompression.js`（挂载即 `get` + 拉启用模型、订阅状态推送、400ms 防抖保存走 `requestLatest`、`SupersededError` 静默、失败只出错误条、卸载前冲刷待保存编辑）；新增 4 条前端用例。我侧独立复跑 `npm test`：**149 通过 / 0 失败**（29 文件），与 pi 自证一致；`npm run build` 成功 |
| C13 转录分隔项 | **已完成** | `components/Chat/transcript/CompressionNotice.vue`（新：发丝线分隔行 + 居中标签 + 副标题，可展开摘要，无摘要时退化为非交互行、只有可展开时才给 `aria-expanded`，摘要走 `renderMarkdown` 后再 `v-html`）；`renderers/usageMeter.js`（新：`formatTokenCount` + `formatCompressionNotice`，副标题四态）；`TranscriptPanel.vue` 按钉死位置插入 `v-else-if` 分支；`pluginTranscript.js` 把超限分支抽成 `truncateItem` / `truncateTurnOutcome` / `truncateCompaction`，并把 `compression` 纳入 metadata-only（摘要保留但限 2048 字符）。我侧独立复跑：`npm test` **30 文件 / 156 通过 / 0 失败**、`npm run build` 成功（1.28s） |
| C14 输入框用量显示 | **已完成** | `renderers/usageMeter.js` 增 `formatContextUsage`（上下文尺寸与窗口；未知→`不可用`；`contextIsEstimated` 时 `~` 只盖尺寸并给 `由本地计数近似` 提示；累计项未知或为 0 时整段省略）；`useAiProviderHost.js` 的 `formatTokens` 删除、统一到该 renderer（`ModelManagement.vue` 导入同步改指）；状态管道落地（`useChatTranscript.js` 的 `replaceState` 与 `transcriptBridge.js` 的 `patchState` 合并点，后者显式 `?? null` 以对抗宿主省略 null 的序列化）；`ChatView.vue` → `ComposerPanel.vue` → `ComposerStatusBar.vue` 的 prop 链；状态栏右侧用量块（`margin-left: auto`、`tabular-nums`、≤700px 只留上下文、未与 `.dirty-warning` 重叠）。我侧独立复跑：`npm test` **30 文件 / 163 通过 / 0 失败**、`npm run build` 成功（1.43s） |
| C15 前端测试 | **已完成** | 新增 3 个文件共 14 条用例：`compressionNotice.test.js`（6：可展开与 `aria-expanded` 往返、无摘要退化为非 button 行、摘要脚本被 sanitize、token 未知只留消息数、Model 进 `title`、无 compaction 不渲染）、`composerUsageMeter.test.js`（5：完整形态、上下文未知显示不可用且无数字、累计为 0 或未知不渲染、近似波浪号、usage 为 null 整块隐藏）、`pluginTranscriptCompression.test.js`（3：超限 compression 项仍是 compression 且 `segments` 为空、摘要限 2048 且为原前缀、turn-outcome 老行为未回归）；未改任何 `src/`。我侧独立复跑：`npm test` **33 文件 / 177 通过 / 0 失败**（基线 30/163） |
| C16 文档同步 + 清理 + 回归 | **已完成** | 文档同步：`AGENTS.md`（设置页接线补「上下文压缩」、深入阅读登记两份文档、核对不变量已是 v32）；`runtime-execution-flow.md`（§2 插入计数与压缩步骤、§5“历史构建”行、第 113 行改写且不动同段其它待测量项）；`pipeline-architecture.md`（新增 §6b 计数压缩节点、§4 事件表加三个事件、§5 载荷补 `usage` 与 `Compactions` / `CompressionNotice`）；`direct-message-input-design.md`（状态行 v32、§7.5 与第 140 行改压缩语义但保留批量准入、§14 时点记录加 v32 注）；`automation-system-design.md`（时点记录加 v32 注）；设计文档（状态行改“已完成（C1–C16）”、写回 C4 包与版本、C6 section 顺序、C7 校验归属、C13/C14 格式化口径、continuation 未退化）。清理：四个旧估算符号 `rg` 0 命中、`DirectPromptBudget` 注释已改、压缩器私有 `BuildMessages` 改名 `CreateSummarizerMessages`、修 `formatTokenCount` 进位（999999 → `1M`）并补边界用例。我侧独立复跑（**顺序固定：先前端后 C#**）：`npm test` **33 文件 / 178 通过**、`npm run build` 成功（1.40s）、`dotnet build` **0 警告 0 错误**、`dotnet test` **1536 通过 / 0 失败 / 4 跳过**、验收 `rg` 0 命中、`git diff --check` 仅 CRLF 提示。**已知遗留**：① 文档末尾 10 项手工验收需真实宿主与人工观察，留给用户；② `formatTokenCount` 只有 K/M 两档，≥1e9 会显示 `1000M`（未引入 B 档，属显示词汇问题，非契约缺陷）；③ 已提交（`e48935a`，129 文件含这两份新文档） |

| C17 回合内用量实时显示 | **已完成** | 新增 `UsageProgressEvent(TurnUsage Usage, bool IsEstimated)`（Core）：**累计快照**语义，消费者整体替换、绝不累加到回合累加器，因此终端 `UsageReportedEvent` 仍是唯一落库观测。Direct 三处发布：回合开始的本地计数（`IsEstimated = true`，只填 context 字段，随 `ApplyCompactionAsync` 的计数结果发出；该 early return 已放宽为「计数可用就计数」，只对没有声明窗口且关闭压缩的模型多一次计数，换取状态栏不再整块缺失）、每个 provider 请求结束（`ReadResponseAsync` 的 `finally`，与 `ObserveUsage` 同点）、回合内压缩完成后重算（`EnsureInTurnPromptFitsWindow` 改为返回压缩后的 prompt 大小）。消费端：`ConversationRuntimeState.LiveUsage`（`TranscriptLiveUsage(TurnId, Usage, IsEstimated)`，瞬态、不落库、不动 schema v32）→ `ConversationSessionCoordinator.SelectedLiveUsage` → `TranscriptProjectionRequest.LiveUsage` → `BuildUsageSummary` 用实时快照**替换**运行回合已落库的用量（边界提交可能写过同一累加器的更早快照，相加会重复计数），缺失分项保留落库值。前端零改动（`~` 与近似说明早已就位）。测试：recorder（进度不写 `turn_usage`、估算不落库、终态清除）、Direct runtime（两个 provider 请求 = 两个快照 + 单个 `UsageReportedEvent`）、投影（替换而非相加、估算标记、快照回合不在 turns 时仍计入）、`DirectInTurnCompactionTests` 契约更新（`Disabled_compaction_still_publishes_the_counted_context_and_never_compacts`）。我侧独立复跑：`dotnet build` **0 警告 0 错误**、`dotnet test` **1631 通过 / 1 失败 / 4 跳过**（唯一失败是 `ViewDemoPluginTests`，因我用 `-p:OutDir` 把测试二进制挪出仓库导致 `TestRepositoryRoot` 找不到 `SelfClaw.slnx`；把输出放回仓库内单跑 **1 通过**）、`npm test` **38 文件 / 216 通过** |

| C18 计数口径修正 | **已完成** | 根因：`DirectTokenCounter.SerializerOptions` 用默认 `JavaScriptEncoder`，把中文转成 `\uXXXX`、`< > &` 转成 `\u003C` 一类，再对 `FunctionResultContent.Result` 这类**本来已是原文的字符串**做 `JsonSerializer.Serialize`；用真实会话数据实测放大约 **1.6 倍**（工具片段原文 166,672 tokens → 转义后 265,116）。表现为状态栏「回合开始估算 27% → 回合结束实测 20%」来回跳，同时放大压缩触发与窗口校验。修复：`Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`，且 `Serialize` 对 `string` 直通、只对其余类型序列化。修复后用同一份真实数据重算：估算 **199,078** vs provider 实测 **201,434**（差 1.2%，差值即系统提示词/工具定义/framing 未计入的部分）。补 3 条用例（CJK 结果不被转义、HTML 敏感字符与泛型尖括号不被转义、结构化 `JsonElement` 结果仍按 JSON 计数）。`--filter TokenCounter` **27 通过**；全量 **1634 通过 / 4 跳过**（唯一失败仍是 `-p:OutDir` 导致的 `ViewDemoPluginTests` 环境问题）。⚠ 不要为了让"本地估算 = 实测"而反过来改 provider 侧数字：`turn_usage` 里 `input_tokens` 已含缓存桶（实测 `201,434 = 1,242 + 200,192`），那是正确口径，不存在"漏加 cache_read"的问题 |

| C19 估算与实测的收敛 | **已完成** | 转义修复后偏差方向翻转：本地估算开始**低于**实测约 3 个百分点。用真实库（会话 `612a4867`，19 units / 496 条 ChatMessage / 239 组工具调用 / 2 张图）精确复现：`unitTokens = 222,707` vs provider `253,071`，残差 **30,364**（恰好 3%）。分解：wire JSON 结构（`{"role":"assistant","tool_calls":[{"id","type","function":{"name","arguments"}}]}` 等键名）**9,799**、7 个 workspace 工具定义与 system 约 **2,000**、其余约 **18,000（7%）是 tokenizer 固有差异**（`o200k_base` 近似 DeepSeek 自研词表，无法消除）。**不做**补 framing 常数：收益仅 1 个百分点，却要引入 OpenAI-wire 专属魔法数字，而 Anthropic 的 wire 形状完全不同。最终裁定（取代本轮中途的 `IfUnmeasured` 折中）：**回合开始不发布任何本地估算**，上下文数字只有 provider 实测一个来源；唯一的估算发布点是**压缩完成之后**（上下文确实变了、此刻无新实测）。同时恢复 `ContextWindowTokens is null && !policy.Enabled` 的 early return —— 不发布估算之后那次计数已无消费者。代价：首回合在第一个 provider 请求返回前显示"不可用"（回到 A5 之前的基线行为，且不再为它付 tokenizer 词表解压成本）。测试：`Disabled_compaction_leaves_the_turn_untouched_and_never_counts` 恢复原契约（`counter.Calls == 0` 且无 `UsageProgressEvent`）、新增 `StreamTurnAsync_never_publishes_a_local_estimate_at_turn_start`、在 in-turn 压缩用例里锁住"压缩后仍有估算"这一唯一例外。我侧独立复跑：`--filter Agents.Direct\|TokenCounter` **367 通过**、全量 **1635 通过 / 4 跳过**（唯一失败仍是 `-p:OutDir` 的 `ViewDemoPluginTests` 环境问题）、`npm test` **38 文件 / 216 通过**、`dotnet build` 0 警告 0 错误 |

| C20 请求上限改为看门狗 | **已完成** | 原 `MaximumProviderRequestsPerTurn = 128` 会误杀长任务（读两三百个文件即超过），且与回合内压缩已经存在的事实不匹配。落地三层改动：①上限提到 **1024** 并重新定义为**电路断路器**（注释写清"不是任务长度策略"）；②新增 `NoProgressGuard`：连续 5 个工具批次指纹相同（工具名 + 参数 + 序列化结果，**刻意不含 callId**，否则同样两次调用会被判为不同）即判卡死，取消未执行调用 + 补 `NoProgress` 本地结果 + 新 `DirectLoopOutcome.Stalled` → Failed（`NoProgress` 文案建议用户换指令重试）；"结果在变"的长任务（读同一文件、轮询构建）不受影响。阈值取 5 而非 3：重试、改完再读、二次确认都是合法模式，多等两轮的代价由请求上限兜住；③`MaximumInTurnCompactionsPerTurn` 3 → **10**，窗口校验从"仅压缩后"改为**每个请求前**（新增 `EnsureInTurnPromptFitsWindow` 的 `requestIndex > 0` 调用点；窗口未声明时直接跳过，不为无窗口模型付计数成本），超窗文案同时覆盖"额度已耗尽"与"压缩未能腾出空间"两种成因。测试改动：`Probe()` 与各 fixture 的工具改为**每次返回新结果**（否则新看门狗会在预算测试里先终止回合——这本身就是它该做的事）；128/3 的硬编码改为引用常量；steer 集成测试原断言"跑到 1024 才算 BudgetExhausted"，实际会在 1024 次 SQLite 进度提交后触发 `database is locked`（约 60–80 s），改为有界验证：6 轮后取消，断言 `provider.Requests == executions`（steer 未换来额外请求）且 steer 已消费。新增用例：重复批次 → `NoProgress`、结果变化 → 仍跑到上限、超窗每轮拒绝。我侧独立复跑：`dotnet build` **0 警告 0 错误**；`--filter Agents.Direct` **370 通过 / 0 失败 / 8 s**；全量 **1637 通过 / 4 跳过**（3 个失败均为环境或负载抖动：`ViewDemoPluginTests` 因 `-p:OutDir` 输出到仓库外；`ActivityPanelPerformanceTests` 与 `CliProbeProcessTests` 隔离复跑均通过） |

首轮实施记录的既定解释（后续任务照此推进，不要反向修改）：

- `ConversationTurnCommit.Compactions`、`ConversationTranscriptSnapshot.Compactions` 为**可空集合**（`= null` 表示无；C# 集合表达式不是编译期常量，写不出 `= []`），与 `MessageRecord.Segments` 的既有写法一致。
- `ConversationCompactionSelector.SelectLatest` 是四个 Direct 请求构造点共用的“取最新压缩项”，按边界排序，时间与 Id 只作 tie-break。
- continuation **未走退化路径**：压缩项随 `RecordedTurnFinalizationRequest → ConversationTurnFinalizationBuilder → commit → SubagentContinuationTurnCommitter` 的既有原子事务落库，`IRecordedTurnCommitter` 契约零改动；不要把 continuation 改成单独写入。
- `ConversationRuntimeState`、`SubagentTaskExecutor`、`AutomationRunExecutor`、`ConversationSessionCoordinator` 新增**必填**依赖，导致 12 处测试构造点机械改动；保留 fail-fast，不要改成可选注入 + 空实现分支。
- automation 会话与其他 origin 一样参与压缩（都是 Direct 回合）；若将来要排除，在 C7 决策。
- 已知缺口（2026-10-17 复核后收窄）：CAS 失守后的 `ReloadCommittedTurnAsync` 不加载压缩项，但它**不改动** `ConversationRuntimeState` 的压缩项集合（`ReplaceTurnContent` 只替换 turn / messages / toolRuns），所以不会抹掉本回合已由事件写入的分隔项；残留情形仅是“另一个写者提交的压缩项在内存里缺失”，会话重载即自愈。

## 任务总览

| ID | 任务 | 阶段 | 依赖 | 规模 |
| --- | --- | --- | --- | --- |
| C1 | Core 契约：策略 / 记录 / 事件 / 状态枚举 / `TurnUsage` 新字段 | P0 | — | S |
| C2 | schema v32：`conversation_compactions` + `turn_usage` 新列 + 仓储 | P0 | C1 | M |
| C3 | 提交路径承载压缩项与实测输入 token | P0 | C2 | L |
| C4 | `IDirectTokenCounter` + tokenizer 落地与降级 | P1 | C1 | L |
| C5 | `DirectContextCompressor`（模型解析、摘要调用、失败降级） | P1 | C4 | L |
| C6 | `DirectPromptComposer`：`PlanCompression` + 边界重放 + 删除字节估算 | P1 | C4 | L |
| C7 | Runtime 接入：触发判定、事件发布、终态与错误路径 | P1 | C5, C6 | L |
| C8 | 录制与投影排序：压缩项成为转录项 | P1 | C3, C7 | M |
| C9 | 用量摘要进 `TranscriptRenderState` 与 delivery 载荷 | P1 | C3 | M |
| C10 | `DirectCompressionSettingsService` + Core 策略源 + DI 顺序 | P2 | C1 | M |
| C11 | `direct-compression/*` bridge + 路由分支 + 变更推送 | P2 | C10 | S |
| C12 | 设置页：导航项 + `Compression.vue` + `useDirectCompression` | P2 | C11 | L |
| C13 | `CompressionNotice.vue` + 转录分支 + plugin 转录容忍 | P3 | C8 | M |
| C14 | 输入框区用量显示（`ComposerStatusBar` + `usageMeter`） | P3 | C9 | M |
| C15 | 前端测试：压缩提示、用量格式、插件转录截断 | P3 | C13, C14 | S |
| C16 | 文档同步 + 死代码清理 + 全量回归 + 手工验收 | P4 | C1-C15 | M |

依赖主线：C1 → C2 → C3 → {C5, C6} ← C4 → C7 → C8 → C13（后端到提示可见）；C3 → C9 → C14；C1 → C10 → C11 → C12 可与 P1 并行（设置链路不阻塞压缩引擎）。

---

## P0 —— 契约与持久化地基

### C1 Core 契约：策略 / 记录 / 事件 / 状态枚举 / `TurnUsage` 新字段

**目标**：把 D§4.1 与 D§8.1 的类型一次定全，后续任务只消费不改签名。（D§4.1、D§8.1）

**改动文件**

- `SelfClaw.Core/Runtime/DirectCompressionPolicy.cs`（新）：`Enabled` / `ThresholdRatio` / `AbsoluteThresholdTokens` / `ModelProfileId`，默认值见 D§4.1
- `SelfClaw.Core/Models/Conversations/ConversationCompactionRecord.cs`（新）
- `SelfClaw.Core/Interfaces/Agents/IDirectCompressionPolicySource.cs`（新，1 个方法：`Task<DirectCompressionPolicy> GetAsync(CancellationToken)`）
- `SelfClaw.Core/Interfaces/Conversations/IConversationCompactionRepository.cs`（新）：`ListAsync` / `GetLatestAsync`
- `SelfClaw.Core/Runtime/Agent/ContextCompactionStartedEvent.cs`、`ContextCompactionCompletedEvent.cs`、`ContextCompactionFailedEvent.cs`（新，每文件一个 DTO）
- `SelfClaw.Core/Runtime/Agent/AgentRunStatus.cs`：追加 `Compacting = 4`
- `SelfClaw.Core/Runtime/Requests/DirectChatTurnRequest.cs`：追加 `ConversationCompactionRecord? Compression = null`（可选参数追加在末尾）
- `SelfClaw.Core/Models/Usage/TurnUsage.cs`：追加 `int? LastRequestInputTokens = null`（放在可选参数末尾，紧邻 `ContextTokens` 的语义注释一起说明区别）
- `SelfClaw.Core/Runtime/TurnUsageAccumulator.cs`：累计该字段（**取最新非空、绝不求和**，与 `InputTokens` 的求和语义显式区分）

**步骤**

1. 严格遵循 AGENTS.md 代码约定 1：DTO 为 `record` + 主构造器，无逻辑；每文件一个类型。
2. ⚠ `AgentRunStatus` 是 CLI parser 与 Direct 共用的枚举：只追加值，不改既有序号；CLI 永不产生 `Compacting`。
3. `DirectChatTurnRequest` 与 `TurnUsage` 的新参数都带默认值，保证既有构造点（`TurnUsageReporter`、`SqliteMappings`、测试）零改动编译通过。
4. `TurnUsage` 注释需写清：`LastRequestInputTokens` = 本回合最后一次 provider 请求的**输入** token 数（真实上下文规模）；`ContextTokens` = 该次调用 input + output，语义保持不变。

**测试**：`SelfClaw.Tests/Core/Runtime/ChatTurnRequestTests.cs` 增补“新增可选参数为 null 时行为不变”；`TurnUsageAccumulatorTests` 增补“未观测到新字段时保持 null，观测到后取最新值”。

**验收**：编译通过、既有测试全绿。

---

### C2 schema v32：`conversation_compactions` + `turn_usage` 新列 + 仓储

**目标**：压缩项与实测输入 token 持久化落地，旧库按既有策略拒绝。（D§4.2）

**改动文件**

- `SelfClaw.Infrastructure/Data/Sqlite/SqliteDatabase.cs`：`CurrentSchemaVersion` 31 → 32；`UnsupportedSchema` 文案改为 “requires schema v32”
- `SelfClaw.Infrastructure/Data/Sqlite/SqliteSchema.cs`：新增表 DDL 与索引；`turn_usage` 增加 `last_request_input_tokens INTEGER NULL`；`RequiredTables` 追加 `conversation_compactions`
- `SelfClaw.Infrastructure/Data/Sqlite/SqliteMappings.cs`：`CompactionSelectColumns` + `ReadCompaction`；`TurnSelectColumns` 与 `ReadTurn` 增加 `last_request_input_tokens`
- `SelfClaw.Infrastructure/Data/Sqlite/Repositories/SqliteConversationCompactionRepository.cs`（新，实现 `IConversationCompactionRepository`）
- `SelfClaw.Infrastructure/Data/Sqlite/Repositories/SqliteConversationTurnWriter.cs`：`UpsertUsageAsync` 写入新列
- `SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`：注册仓储（单例）
- `AGENTS.md`：不变量 10 的版本号与表清单同步（若尚未在 C16 统一处理，可在此先改一行）
- 版本断言三处同步 31 → 32：`SqliteRepositoriesTests.cs:127`（`maxSchemaVersion`）、`SqliteSubagentSchemaTests.cs:22,29`（用例名含 `schema_v31`）、`SqliteSchemaInitializationTests.cs:53`（`"requires schema v31"` 文案）与 `:67,92,97`（预置 v31 库的拒绝/重开用例）

**步骤**

1. 表 DDL 与既有表同批 `CreateSql`；`through_sequence` 建 `CHECK(through_sequence >= 0)`；索引 `(conversation_id, created_at_utc DESC)`。
2. `ListAsync` 按 `created_at_utc, id` 升序返回某会话全部压缩项；`GetLatestAsync` 取**边界最大**的一条（`through_sequence DESC, created_at_utc DESC, id DESC`；当前边界随回合单调递增，两种排序等价，但语义以边界为准，重放只用最新、全量保留供回溯）。⚠ 首轮实现用的是 `created_at_utc DESC, id DESC`，实施 C3 时改正并补一条“边界优先于时间”的用例。
3. ⚠ 两张外键都 `ON DELETE CASCADE`：会话或回合删除时压缩项必须随删；删除会话的既有级联路径不新增代码。
4. ⚠ 新列是**必建列**而不是“版本内兼容列”：v31 库整体拒绝，所以不需要 `EnsureColumnExistsAsync` 之类的渐进补列逻辑，也不要写“缺列则回退”的分支（那会制造第二套 schema 形态）。
5. ⚠ 同步检查 `SqliteDatabase` 中既有“表清单完整性”校验（`incomplete v31 (missing …)` 分支），确保新表加入 `RequiredTables` 后老库判定变为版本不匹配而不是缺表。
6. `SqliteConversationCompactionRepository` 只提供只读查询与 `InitializeAsync`；写入统一走回合 writer（C3），避免两条写路径。

**测试**（`SqliteRepositoriesTests`、`SqliteSchemaInitializationTests`、`SqliteSubagentSchemaTests` 调整 + 新增 `SqliteConversationCompactionRepositoryTests`）

- 版本断言 32；v31 库启动被拒绝且错误信息含 “requires schema v32”（`SqliteSchemaInitializationTests` 的“原库未被修改”断言保留）。
- round-trip：写入三条、`ListAsync` 顺序、`GetLatestAsync` 取最后一条。
- 删除会话 → 压缩项随删；删除回合 → 压缩项随删。
- nullable 列（`model` / `source_tokens` / `summary_tokens` / `last_request_input_tokens`）读写保持 null 语义。
- `turn_usage` 新列与旧列同事务写入、按 `turn_id` upsert 不丢值。

**验收**：`dotnet test --filter Sqlite` 通过；手工删除本地库后应用正常启动并建出 v32 结构。

---

### C3 提交路径承载压缩项与实测输入 token

**目标**：压缩项与回合终态同事务提交；实测输入 token 从 runtime 一路落到 `turn_usage`；runtime state 与快照贯通。（D§4.1、D§4.3、D§4.4）

**改动文件**

- `SelfClaw.Core/Models/Conversations/ConversationTurnCommit.cs`：追加 `IReadOnlyList<ConversationCompactionRecord>? Compactions = null`（C# 集合表达式不是编译期常量，无法写 `= []`；null 视为空，与 `MessageRecord.Segments`、`DirectChatTurnRequest.ToolExecutions` 的既有写法一致）
- `SelfClaw.Infrastructure/Data/Sqlite/Repositories/SqliteConversationTurnWriter.cs`：
  - `TryWriteAsync` 在内容写入后同事务 upsert 压缩项（`INSERT … ON CONFLICT(id) DO UPDATE`，保证重试幂等）
  - `ValidateCommit`：校验每个压缩项的 `ConversationId` 与回合一致、`TurnId` 等于提交回合、`Summary` 非空、`ThroughSequence >= 0`、`SourceMessageCount >= 0`
- `SelfClaw.Infrastructure/Agents/Direct/TurnUsageReporter.cs`：记录每次 provider 调用的输入 token，`Build()` 时写入 `LastRequestInputTokens`（取最后一次观测值）；累计语义在 C1 的 `TurnUsageAccumulator`
- `SelfClaw.Desktop/Services/Runtime/RecordedTurnFinalizationRequest.cs`：追加 `Compactions`
- `SelfClaw.Desktop/Services/Runtime/ConversationTurnFinalizationBuilder.cs`：把 `Compactions` 透传进 `ConversationTurnCommit`
- `SelfClaw.Desktop/Services/Runtime/AgentTurnState.cs`：追加 `List<ConversationCompactionRecord> PendingCompactions`
- `SelfClaw.Desktop/Services/Runtime/ConversationTurnRecorder.cs`：`PendingFinalization` 组装时带上待提交压缩项
- `SelfClaw.Desktop/Services/Runtime/ConversationRuntimeState.cs`：compaction 列表 + `ReplaceCompaction` + 纳入 `CaptureSnapshot`
- `SelfClaw.Desktop/Services/Runtime/ConversationTranscriptSnapshot.cs`：追加 `IReadOnlyList<ConversationCompactionRecord> Compactions = null`（默认值）
- `SelfClaw.Desktop/Services/Runtime/ConversationSessionCoordinator.cs`：`LoadTranscriptAsync` 并行加载压缩项
- `SelfClaw.Desktop/Services/Subagents/SubagentTaskExecutor.cs`、`SubagentContinuationExecutor.cs`、`SelfClaw.Desktop/Services/Automation/AutomationRunExecutor.cs`、`SelfClaw.Desktop/Services/Runtime/ConversationTurnEngine.cs`：构造 `DirectChatTurnRequest` 时带上所属会话的最新压缩项

**步骤**

1. ⚠ `ValidateCommit` 现有校验要求所有 `Messages` 为 Assistant 角色；压缩项是独立集合，不要塞进 `Messages`，否则触发角色校验失败。
2. ⚠ continuation 是 detached 回合（`Origin == Continuation` 不允许 `CommitProgressAsync`），压缩项只能随 `SubagentContinuationTurnCommitter` 的最终原子事务提交；不要为它开第二条写入路径。
3. ⚠ `TurnUsage.LastRequestInputTokens` 是**取最新、不求和**：它描述的是“最后一次请求的输入规模”，与 `InputTokens`（回合内跨请求求和）语义不同，累加会得到无意义的数字；该路径已在 C1 完成。同一列在 `UpsertUsageAsync` 中必须用 `COALESCE(excluded.last_request_input_tokens, turn_usage.last_request_input_tokens)`：null 表示未观测，不得抹掉已落库的实测值；这是唯一不做快照覆盖的列，SQL 旁要写清原因。
4. ⚠ `ConversationTranscriptSnapshot` 的默认值写法要与既有 record 风格一致；构造函数调用点多（`ConversationSessionCoordinator` / `SubagentTaskExecutor.LoadRuntimeAsync` / 测试），带默认参数避免全量改点。
5. `ConversationRuntimeState.ReplaceCompaction` 走既有 `_gate` 串行化并置空 `_snapshot`，与 `ReplaceTurn` / `ReplaceMessage` 保持一致。
6. 若 C3 发现 committer 契约膨胀过大（例如 child 提交器需要额外分支），按 D§11 的退化路径处理：continuation 不落压缩项，仅本回合生效，并在 C16 写回设计文档。

**测试**

- `ConversationTurnRecorderTests`：终态提交携带压缩项；重试（`TryWriteAsync` 二次调用）不产生重复行。
- `SqliteConversationTurnRepositoryTests`：压缩项与回合终态同事务；提交校验拒绝跨会话/跨回合压缩项。
- `TurnUsageAccumulatorTests` / `DirectUsagePersistenceTests`：`last_request_input_tokens` 取最新且不累加；无观测时保持 null；与 `input_tokens` 同事务落库。
- `ConversationTurnTestContext`（TestDoubles）增补 `LatestCompaction` 读取辅助，供 C7/C8 复用。

**验收**：Direct 单个回合结束后，`conversation_compactions` 出现对应行且与 `conversation_turns.status` 同时可见；`turn_usage.last_request_input_tokens` 等于最后一次请求的输入 token 数。

---

## P1 —— 计数与压缩引擎

### C4 `IDirectTokenCounter` + tokenizer 落地与降级

**目标**：把字节估算换成离线 tokenizer 计数，并提供明确的降级行为。（D§5.1、D§5.2、D§5.3、D§5.5）

**改动文件**

- `Directory.Packages.props`：新增（版本已于 2026-10-17 在 nuget.org 核实为最新稳定版）
  - `Microsoft.ML.Tokenizers` 2.0.1
  - `Microsoft.ML.Tokenizers.Data.O200kBase` 2.0.1
  - `Microsoft.ML.Tokenizers.Data.Cl100kBase` 2.0.1
- `SelfClaw.Infrastructure/SelfClaw.Infrastructure.csproj`：对应 `PackageReference`
- `SelfClaw.Infrastructure/Agents/Direct/Abstractions/IDirectTokenCounter.cs`（新）
- `SelfClaw.Infrastructure/Agents/Direct/Context/DirectTokenCounter.cs`（新）
- `SelfClaw.Infrastructure/AiProviders/AiChatOptions.cs`：新增 model option key 常量 `TokenizerEncodingKey = "tokenizer.encoding"` 并在注释里说明取值
- `SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`：注册 `IDirectTokenCounter`（单例）

**接口形状**（⚠ 编码必须按**调用时的模型**解析：计数器是单例，把 profile 只在构造函数里注入只服务测试，生产路径一旦在构造期定死编码，`tokenizer.encoding` 覆盖与按模型名映射都会静默失效）

```csharp
// Abstractions/IDirectTokenCounter.cs（Infrastructure 内部抽象）
internal interface IDirectTokenCounter
{
    bool IsAvailable(AiModelProfile? model);
    int? CountText(string? text, AiModelProfile? model);
    int? CountMessages(IReadOnlyList<ChatMessage> messages, AiModelProfile? model);
    int? CountTools(IEnumerable<AITool>? tools, AiModelProfile? model);
}
```

`null` 表示“该模型的编码计数不可用”（§5.5），而不是“0 个 token”。实现内部按编码名缓存 tokenizer 与计数结果（消息缓存键含编码名），失败按编码各记一次 warning；必须有一条“同一个单例对 gpt-4o 与 gpt-4 分别用 o200k_base 与 cl100k_base”的用例，否则这个缺口不会被任何测试挡住。

**步骤**

1. **编码解析**（私有静态，不额外建类）：`ModelOptions[TokenizerEncodingKey]` 显式指定 → 按模型名映射（`gpt-4o` / `o1` / `o3` 系 → `o200k_base`；`gpt-4` / `gpt-3.5` 系 → `cl100k_base`）→ 默认 `o200k_base`。解析结果做白名单校验，未知值回落默认并记一次 warning。
2. **惰性初始化**：首次计数时创建 `TiktokenTokenizer`（数据包内含压缩词表，首次需解压约 1 MB）。⚠ 初始化在回合执行线程上完成，不在 UI 线程；失败时置 `IsAvailable = false` 并只记录一次 warning（含异常类型与消息，不含正文）。
3. **框架开销常量**：`PerMessageFramingTokens = 4`、`PerToolFramingTokens = 8`，与 tiktoken 正文计数相加。注释写明这是补偿 wire 角色标记与工具包装，不是“内容估算”。
4. **缓存**：单条消息按 `(MessageId, 内容指纹)` 缓存计数，容量上限 4096，先进先出淘汰；工具按 schema 文本指纹缓存（容量小，同上限）。⚠ 缓存键必须含内容指纹，否则消息被 `MessageAdjustments` 改写后会命中旧值。
5. **不保留字节估算**：`DirectPromptComposer` 的 `EstimateTokens` 系列在 C6 删除；本任务不要新增任何“估算兜底”分支。
6. ⚠ `ConfigureAwait(false)`（若引入异步初始化）、NRT、`sealed`、`internal`，均按 AGENTS.md 代码约定。
7. 可选：在 `App.xaml.cs` 启动序列末尾加一个后台预热任务（fire-and-forget 并捕获异常），让首个回合不承担解压延迟；若不加，则记录为已知首次延迟。

**测试**（新增 `DirectTokenCounterTests`）

- 编码解析：显式 override 优先、模型名映射、未知值回落 `o200k_base` 并告警。
- 中文/英文/emoji/混排文本的计数为正且对同一输入稳定；长文本计数明显区别于 `字节数 / 3`（用一条固定样本断言具体数值，锁定实现不被无意改回估算）。
- 消息计数包含框架开销：单条空文本消息计数等于 `PerMessageFramingTokens`。
- 工具计数包含 schema 文本与 `PerToolFramingTokens`。
- 缓存：同一消息两次计数结果一致且只编码一次（用可注入的编码工厂计数创建/编码次数）。
- 降级：编码工厂抛错 → `IsAvailable == false`、三个 `Count*` 返回 `null`、只记录一次 warning、不抛异常。
- 线程安全：并行调用不抛、结果一致（缓存加锁或用 `ConcurrentDictionary`）。

**验收**：`dotnet test --filter TokenCounter` 全绿；断网环境下首次计数成功（数据包离线，不触发任何网络请求）。

---

### C5 `DirectContextCompressor`

**目标**：把“给定前缀单元 → 一段摘要”做成独立、可测的服务，含模型解析与失败降级。（D§6）

**改动文件**

- `SelfClaw.Infrastructure/Agents/Direct/Context/DirectContextCompressor.cs`（新）：压缩服务，构造注入 `IAiChatClientFactory` / `IAiModelCatalog` / `IDirectTokenCounter`
- `SelfClaw.Infrastructure/Agents/Direct/Context/Models/DirectCompressionRequest.cs`、`.../Models/DirectCompressionResult.cs`（新）：两个 DTO 各自独立成文件（AGENTS 约定 1），形状见设计 §6
- `SelfClaw.Infrastructure/Agents/Direct/Context/CompressionInstruction.cs`（新）：固定摘要指令文案
- `SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`：注册 compressor（单例）

**步骤**

1. ⚠ 前缀单元直接复用 `DirectPromptHistoryUnit.Messages`（已保证工具调用与其结果成对），不要重新序列化成纯文本，否则丢失角色与工具语义。
2. 模型解析：`ModelProfileId` 有值 → `IAiChatClientFactory.PrepareAsync(id)` + `Create(preparation, new AiChatClientPipelineOptions([]))`，`try/finally` 释放 lease；无值 → 复用本回合 lease 的 `Client`，另传独立 `ChatOptions`（不带 Tools、不带 ToolMode）。
3. ⚠ 指定模型不可用（`IAiModelCatalog.IsModelAvailableAsync == false` 或 `PrepareAsync` 抛错）→ **回退到本回合模型**，返回 result 时标记实际使用的模型，并让调用方发一条诊断；不得因此失败回合。
4. 输出约束：`MaxOutputTokens = clamp(sourceTokens / 4, 512, 4096)`（`sourceTokens` 来自计数）；其余采样走 provider 默认（不覆盖模型配置里的 temperature 等）。
5. 空/全空白响应、provider 异常：返回 `null` 并记录 warning（含 conversationId/turnId，不含正文）；`OperationCanceledException` 必须重抛。
6. 有旧摘要时把旧摘要作为第一条 user 消息（`<selfclaw-context-summary version="1">` 包装）参与总结，保证多次压缩递进而非覆盖丢失。
7. ⚠ `ConfigureAwait(false)` 全量；不使用 `!`。

**测试**（新增 `DirectContextCompressorTests`，用 fake `IChatClient` / `FakeChatClientFactory`）

- 输入 message 序列与角色/工具调用保真（断言发往 client 的 `ChatMessage` 序列）。
- 指定模型可用时使用独立 lease 且被释放；不可用时回退本回合 lease 且返回的 model 为回退值。
- `MaxOutputTokens` 计算边界：极小前缀（512 下限）、极大前缀（4096 上限）。
- 空响应/异常 → 返回 null 且不抛出；取消 → 抛 `OperationCanceledException`。
- 旧摘要在输入首部且带包装标记。
- `SourceMessageCount` 取 `PrefixUnits.Count`（**会话消息条数**）：含工具调用的单元不得按 `unit.Messages.Count` 求和而虚高。
- `SummaryTokens` 由压缩器用**实际产出摘要的那个 lease 的 `Profile`** 经 `IDirectTokenCounter` 算出（断言等于摘要文本的计数）；计数不可用时为 null。

**验收**：自动化测试全绿；`SELFCLAW_PROVIDER_SMOKE=1` 下可选用真实模型跑一次压缩并人工核对摘要质量。

---

### C6 `DirectPromptComposer`：规划 + 边界重放 + 删除字节估算

**目标**：删除预算裁剪与字节估算；新增压缩规划与摘要注入；计数由调用方传入。（D§5.6、D§7）

**改动文件**

- `SelfClaw.Infrastructure/Agents/Direct/Context/DirectPromptComposer.cs`：
  - 新增 `PlanCompression` 与 `DirectCompressionPlan`
  - `BuildMessages` 增加 `ConversationCompactionRecord? compression` 参数并把摘要作为 system section 注入
  - 删除 `BuildHistoryWithinBudget` 与 `EstimateTokens` / `EstimateMessageTokens` / `EstimateToolTokens` / `EstimateUserPrompt` / `EstimateMessages` / `EstimateTools`
- `SelfClaw.Infrastructure/Agents/Direct/Context/Models/DirectPromptBudget.cs`：注释语义改为“窗口与输出预留”，不再是可裁剪额度
- `.../Context/Models/DirectPromptHistoryUnit.cs`：`(Messages, EstimatedTokens)` → `(Messages, SourceSequence, int? Tokens = null)`（`Tokens` 由调用方计数后回填）
- `.../Context/Models/DirectCompressionPlan.cs`（新）：`ShouldCompress` / `PrefixUnits` / `ThroughSequence` / `RetainedUnitCount` / `SourceTokens`
- `SelfClaw.Infrastructure/Agents/Direct/DirectConversationLoop.cs`：`FitsBudget` 改用 `IDirectTokenCounter`（构造注入）与上下文计数
- `SelfClaw.Tests/Infrastructure/Agents/Direct/Context/DirectPromptComposerTests.cs`：删除预算裁剪与字节估算相关用例，新增规划与边界用例
- `SelfClaw.Tests/Infrastructure/Agents/Direct/Context/DirectPromptTurnHistoryTests.cs`：同步调整

**步骤**

1. `PlanCompression` 返回：`ShouldCompress`（`countedTokens + 输出预留 >= threshold`，预留 = `budget.MaxOutputTokens ?? DefaultOutputTokenReserve`）、`PrefixUnits`、`ThroughSequence`、`RetainedUnitCount`、`SourceTokens`；纯函数、无 I/O、不做计数、不抛业务异常。⚠ 原 `DirectPromptHistoryUnit` 只有 `(Messages, EstimatedTokens)`、**不带 sequence**；规划循环是按 `orderedMessages` 逐条构造单元的（**一条消息 = 一个单元**，`BuildAssistantUnit` 也只对应一条消息），因此 C6 给它补了 `SourceSequence`，`ThroughSequence` 直接取前缀最后一单元的该字段。不要另造 id → sequence 映射表（composer 里只有 `turnsById`，没有消息表），也不要用时间戳、数组下标或“最新压缩项之前的一切”推断边界。
2. ⚠ 保留策略：从最新单元向前累加直到 ≤ `threshold * 0.5`，**至少保留最新 1 个单元**；`PrefixUnits` 为空即不压缩。边界必须落在单元之间，绝不可拆开一个单元（工具调用与结果）。
3. `BuildMessages`：重放 `Sequence > compression.ThroughSequence` 的单元；摘要作为独立 system section（`<selfclaw-context-summary version="1">`）注入，位置固定为 **system 段第三位**：`[agent instructions, CompletionInstruction?, 压缩摘要?, ...capability/extension instructions]` —— 紧随 agent instructions 与 completion 指令、排在扩展/能力 instructions 之前。理由：无压缩时 prompt 与现状逐字节一致，且摘要属会话内容、扩展 instructions 仍排在其后。该顺序必须写进测试，不要留模糊。⚠ 现状是把这些 section 用 `\n\n` 拼进**同一条** System 消息（`systemSections` 数组），所以摘要是该数组里的一个元素、不是新增第二条 system 消息；不要改成分多条 system 消息发送。
4. ⚠ 受保护内容不变：续写提示、completion batch、steer 批次仍排在 `mandatory` 里，不受压缩与边界影响；`MaximumCompletionBatchBytes` 校验保留。
5. ⚠ 保留“system + tools + 输出预留 > 窗口”的可读错误（配置错误，与用户历史无关）；删除“最新历史消息装不下即报错并丢弃其余”的路径。该校验今天在 `BudgetFor`（由 `BuildMessages` 调用），改造后必须改用**调用方传入**的 `countedTokens` / `outputReserveTokens` 判断，`BudgetFor` 那个“历史预算返回值”删除；`PlanCompression` 按第 1 条保持纯函数、不抛业务异常；实际落地为 `EnsurePromptFitsWindow(budget, countedTokens)`，判定 `countedTokens + 预留 > 窗口`，计数不可用时跳过（fail-open）。（全仓唯一外部调用点是 `DirectConversationLoop.cs` 的 `FitsBudget`，已核实无其他调用方。）
6. `DirectConversationLoop.FitsBudget`：用计数器对“当前上下文 + 待消费批次”计数；计数不可用（`null`）时按既有保守策略处理（**允许**消费批次，让 provider 决定），并在注释说明这是 §5.5 的降级语义。⚠ 计数与可用性查询必须显式传**当回合的 `AiModelProfile`**（C4 接口是 `IsAvailable(model)` / `Count*(…, model)`）；拿不到 profile 就传 `null`（= 默认编码），不得自造估算，也不得在某处硬编码编码名。现成事实（已核实）：`FitsBudget(DirectTurnSetup setup, DirectConversationContext context, ConversationInputBatch batch)` 是 **private static**，当回合模型与窗口就在 `setup.ProviderLease.Profile`（`ResolveContextWindowTokens(setup.ProviderLease.Profile)`），工具与输出预留来自 `setup.ProviderLease.Options` —— profile 直接可用、无需新增依赖管道；接入计数器后该方法改为**实例方法**（计数器构造注入），并同步改写它那句 “shares the composer's UTF-8 heuristic” 的 XML 注释（改造后不再成立）。
7. ⚠ 删除后检查是否有只被裁剪/估算路径使用的私有成员，一并删除（AGENTS.md 约定 10）。明确点名 `PerMessageOverheadTokens = 8` 与 `PerToolOverheadTokens = 16` 这两个**字节估算加数**：它们与 C4 的 framing 常量（`PerMessageFramingTokens = 4` / `PerToolFramingTokens = 8`）语义不同，必须删掉，不允许并存或叠加。
8. ⚠ `DirectPromptHistoryUnit.EstimatedTokens` 是字节估算时代的字段名与语义。计数退场后它只允许承载 `IDirectTokenCounter` 的结果（改名，如 `Tokens`，并更新全部构造点）；若删掉裁剪路径后不再有任何读取方，则直接删除该字段。不允许保留“字节估算”的旧语义，也不允许留下没人读的死字段。

**测试**（`DirectPromptComposerTests`）

- 无压缩项时：完整历史全部进入 prompt（不再裁剪），单元顺序与工具调用/结果配对不变。
- 有压缩项时：更旧消息不出现；摘要 section 出现在 system 段且带包装标记；边界之后的消息完整保留。
- `PlanCompression`：未达阈值 → 不压缩；达阈值 → 前缀非空、`ThroughSequence` 等于前缀最后一单元的消息 sequence、保留单元数符合 50% 目标与“至少 1 个”下限。
- 单元完整性：前缀边界绝不落在同一 assistant 消息的工具调用与其结果之间。
- 受保护内容：completion batch 与续写提示在压缩后仍存在且顺序正确。
- 配置错误：tools + 预留超窗仍抛既有可读错误。
- ⚠ 固定“**一个单元 = 一条会话消息**”这一不变量：`SourceMessageCount` 取 `PrefixUnits.Count` 依赖它成立。测试断言前缀单元数等于被压缩的 `MessageRecord` 条数；若将来让一个单元跨多条存储消息、或把工具结果拆成独立单元，必须同时改 `SourceMessageCount` 的口径并写回设计文档。
- 删除断言：源码中不再存在字节估算方法（可用编译期即可，无需专门测试；由 `rg` 在 C16 核对）。

**验收**：`dotnet test --filter Prompt` 全绿；`rg 'EstimateTokens|BuildHistoryWithinBudget' SelfClaw.Infrastructure` 无命中。

---

### C7 Runtime 接入：触发判定、事件发布、终态

**目标**：回合开始处把计数、规划、压缩调用、事件发布与错误路径串起来。（D§5.4、D§5.5、D§6、D§8.1）

**改动文件**

- `SelfClaw.Infrastructure/Agents/Direct/DirectAgentChatRuntime.cs`：
  - 构造追加可选参数 `IDirectCompressionPolicySource? compressionPolicy = null`（缺省为禁用策略），注入 `IDirectTokenCounter` 与 `DirectContextCompressor`
  - `SetupTurnAsync`：读策略 → 计数（含增量维护起点，按 C4 接口传**当回合 `AiModelProfile`**）→ `PlanCompression` → 需要时发 `ContextCompactionStartedEvent` + `RunStatusEvent(AgentRunStatus.Compacting)` → `TryCompressAsync` → 成功发 `ContextCompactionCompletedEvent`，失败发 `ContextCompactionFailedEvent` → 用新摘要重新 `BuildMessages`
  - 压缩后重算（**必须含新 system 段里的摘要 section**）：`promptTokens + outputReserve > window` → 抛可读 `InvalidDataException`（提示开启压缩 / 换更大窗口模型）
  - 策略禁用且超窗 → 同类可读错误（DP5）
  - 计数不可用 → 本回合不压缩、不发压缩事件、发一条诊断状态（§5.5）
- **不需要改** `DirectEventTranslator`：现有 `RunStatusEvent` 就是 runtime 用 `writer.TryWrite` 直接写的（`SetupTurnAsync` 里两处），压缩的三个事件照此直接写，不要为它们新建口子或包装层。
- `SelfClaw.Tests/Infrastructure/Agents/Direct/DirectAgentChatRuntimeTests.cs`：`CreateRuntime` 支持注入 fake 策略源、fake 计数器与 fake compressor

**步骤**

1. 回合内压缩**只发生一次**（回合开始处）。设计 §6 提到的 “`MaximumCompactionAttemptsPerTurn = 2`” 已确认**不需要**落成常量或循环：那两次尝试的语义已由 C5 的“专用模型不可用即在本回合 lease 上重试”在一次 `TryCompressAsync` 调用内实现，runtime 再加计数只会产生没有调用方的死代码。
2. ⚠ 顺序：先发 `Started` 再调用模型；`Started` 之前必须已经确定要压缩，避免“发了开始却没有结束事件”。`Started` 的两个数字按设计 §8.1 的口径：`EstimatedTokens` = `promptTokens`（**不含**输出预留，与界面“上下文大小”同口径），`ThresholdTokens` = threshold（窗口 × 比例或绝对阈值）。
3. ⚠ `Compacting` 状态要能被后续 `Requesting` 覆盖：压缩结束后发 `RunStatusEvent(AgentRunStatus.Requesting)`，确认整条状态序列仍单调、无重复终态。⚠ 具体落点（现状已核实）：`SetupTurnAsync` 在 `Create(lease)` 之后依次写 `RunStartedEvent`、`RunStatusEvent(Requesting)`，然后才调 `BuildMessages`。改造后的顺序必须是 `Create(lease) → RunStartedEvent →（需压缩时）ContextCompactionStartedEvent + RunStatusEvent(Compacting) → 压缩 → Completed/Failed 事件 → RunStatusEvent(Requesting) → BuildMessages(带 compression)`。当回合模型直接取 `preparation.Profile`（`PrepareAsync` 的产物，在租约创建前就已可用，不要为拿 profile 另调 `PrepareAsync`）；压缩调用留在既有 try 内，失败绝不能跳出资源释放路径。
4. ⚠ 取消路径：压缩调用继承回合 `CancellationToken`；取消必须重抛并被 `ProduceEventsAsync` 的既有 `catch (OperationCanceledException)` 处理，不得转成 `ContextCompactionFailedEvent`。
5. ⚠ `WriteTerminalOutcome` 的映射不改；压缩失败不改变终态语义（C04）。
6. 压缩结果里的 `ThroughSequence` 由 `PlanCompression` 决定，runtime 只负责组装 `ConversationCompactionRecord`（`Id = Guid.NewGuid()`、`CreatedAtUtc` 与现有 Direct 代码口径一致；`SourceMessageCount` / `SourceTokens` / `SummaryTokens` / `Model` 直接取自压缩结果，`UsedFallbackModel = true` 时额外发一条诊断 `RunStatusEvent`）。⚠ 会话与回合 id 直接取 `DirectChatTurnRequest` 的 `ConversationId` / `TurnId`（两者是基类 `ChatTurnRequest` 的第一、第二个位置参数，已核实存在），不要新造 Guid，也不要留 `Guid.Empty`——`ConversationCompactionRecord.TurnId` 与 `ConversationId` 都有外键约束。
7. 上下文计数**增量维护**：回合开始时把计数结果与当前 `context.Messages` 数量一并记下；回合内追加消息/工具结果后只加增量，供 `FitsBudget` 与后续判断用（D§5.3）。首个使用某编码的回合会在回合线程上承担一次性词表解压（实测约 150–400 ms），这是**已接受的成本**，不算缺陷；启动预热是可选优化（§5.3），本次不做，C16 不要把它当遗漏项。

**测试**（`DirectAgentChatRuntimeTests` / `DirectConversationLoopContractTests`）

- 未达阈值：无压缩事件、provider 收到完整历史。
- 达阈值：事件序列为 `Started → (Completed | Failed)`；provider 第二次请求收到摘要 section + 边界后消息。
- 计数不可用：不压缩、不裁剪、发诊断、回合正常（provider 收到完整历史）。
- 禁用策略 + 超窗：终态 Failed，错误文案可读且不含正文。
- 压缩失败 + 未超窗：回合正常继续，`Failed` 事件只做诊断。
- 取消：抛 `OperationCanceledException`，不产生 `ContextCompactionFailedEvent`。
- 既有回归：单回合 ≤128 次请求、工具循环、steer 准入（`ContextBudgetExceeded` 仍需在压缩后无法容纳时出现）。

**验收**：`dotnet test --filter Direct` 全绿；`SELFCLAW_PROVIDER_SMOKE=1` 下把阈值调低跑通一次真实压缩。

---

### C8 录制与投影排序

**目标**：压缩项变成转录里可见的 `上下文压缩` 分隔项，并随终态落库。（D§8.2、D§8.3）

**改动文件**

- `SelfClaw.Desktop/Services/Runtime/ConversationTurnRecorder.cs`：三个事件分支（`Started` 更新活动文案；`Completed` 写 `PendingCompactions` + `session.ReplaceCompaction` + `RaiseTranscriptChanged(true)`；`Failed` 只记录）；状态文案映射 `AgentRunStatus.Compacting` → “正在压缩上下文…”（**已归 C7 落地**，本任务只需确认它真的存在并加断言，不要重复添加）
- `SelfClaw.Desktop/Services/AgentActivity/AgentActivityCoordinator.cs`：`MapRunStatus` 增加 `Compacting` → `(Thinking, "正在压缩上下文")`（同上，已归 C7）
- 子代理活动面板**不新增阶段值**：`SubagentExecutionSession.UpdatePhase` 的 `RunStatusEvent or RunStartedEvent => "requesting"` catch-all 会把 `Compacting` 落到 `"requesting"`。这是有意保留的（压缩本身确实在发请求，且面板的阶段词表属于子代理面板自身，见 AGENTS 不变量 11），不要为一个状态新增 `"compacting"` 阶段。
- `SelfClaw.Desktop/Services/Transcript/Views/TranscriptRenderItem.cs`：追加 `TranscriptCompactionNotice? Compaction = null`
- `SelfClaw.Desktop/Services/Transcript/Views/TranscriptCompactionNotice.cs`（新）
- `SelfClaw.Desktop/Services/Transcript/TranscriptProjection.cs`：在 `BuildItems` 中按 `ThroughSequence` 插入 `kind = "compression"` 项
- `SelfClaw.Desktop/Services/Transcript/Views/TranscriptProjectionRequest.cs`：追加 `IReadOnlyList<ConversationCompactionRecord>? Compactions = null`。**当前压缩项根本没到投影层**（该 record 只有 Messages / Turns / ToolRuns / Conversations / WorkspaceRoots / … / IsContinuation），必须补这条管道
- 两个构造点都要传：`MainWindowViewModel`（交互转录，转发快照里的 `Compactions`）与 `AutomationBridge`（自动化历史视图，现在只加载 messages / turns / toolRuns，要一并加载压缩项；别只做交互侧）

**步骤**

1. ⚠ 排序：压缩项插到 `sequence == ThroughSequence` 的消息项之后；多个压缩项按 `ThroughSequence` 升序，同值按 `CreatedAtUtc`。
2. ⚠ `_outcomeCache` / `_messageCache` 的失效逻辑要覆盖压缩项集合变化，否则界面不会刷新。现状：`TranscriptProjection.BuildItems` 里对这两个缓存各有一行内联剪枝（`foreach … Remove`，按 live 消息/回合键清理），**没有**名为 `Prune` 的方法（只有 `_messageProjector.PruneToolSegmentCache`）；压缩项集合变化时按同一处写法让受影响条目重新构建。
3. ⚠ 压缩项属于会话而非回合终态，`TranscriptProjection.BuildItems` 遍历 turns 时不要把它当成 turn-outcome 项。
4. `AgentActivityCoordinator.ApplyEvent` 的 `default: return false` 已能忽略新事件，确认不误触发状态变更。
5. 边界兜底（C8 已定）：`ThroughSequence` 在已加载消息里找不到对应项时（自动化历史整段加载时一般不会出现，将来做会话内分页才可能有），分隔项追加到**所有消息之后**；边界正好是某回合最后一条消息时，分隔项落在该消息与其 `turn-outcome` 之间（严格按“插到边界消息之后”），不挪到 footer 之后。
6. 发布级别与文案（C8 已定）：`Started` / `Completed` 用 `RaiseTranscriptChanged(true)` 立即发布（分隔项与活动文案都要马上可见），`Failed` 用 `false` 节流（与既有 `RunStatusEvent` 的处理一致）；活动区的失败文案用中文（“上下文压缩失败，本回合继续使用未压缩的上下文”），runtime 传来的英文 `Reason` 只进日志、不上屏。

**测试**

- `ConversationTurnRecorderTests`：三个事件的归约结果（活动文案、compaction 列表、待提交集合）。
- `TranscriptProjectionTests`：压缩项位置（在边界消息之后、后续消息之前）；无压缩项时投影输出与现状一致（防回归）；压缩项变化能触发新快照。

**验收**：Direct 回合触发压缩后，Vue 端在正确位置出现分隔项（需 C13 完成后可见）。

---

### C9 用量摘要进渲染状态

**目标**：为输入框区提供“当前上下文 + 累计输入/输出”数据，并区分实测与近似。（D§8.3）

**改动文件**

- `SelfClaw.Desktop/Services/Transcript/Views/TranscriptUsageSummary.cs`（新）：`InputTokens` / `OutputTokens` / `ContextTokens` / `ContextWindowTokens` / `ContextIsEstimated`
- `SelfClaw.Desktop/Services/Transcript/Views/TranscriptRenderState.cs`：追加 `TranscriptUsageSummary? Usage = null`
- `SelfClaw.Desktop/Services/Transcript/TranscriptProjection.cs`：从 `request.Turns` 计算（累计 = 所有回合 usage 求和；上下文取最新非空 `LastRequestInputTokens`，缺失时退回 `ContextTokens`）
- `SelfClaw.Desktop/Services/Transcript/TranscriptDelivery.cs`：`replaceState` 与 `patchState` 载荷都带 `usage`
- 前端两处（已核实）：`SelfClaw.TranscriptVue/src/composables/useChatTranscript.js` 的 `replaceState`（state 形状加 `usage`），以及 `composables/transcriptBridge.js`（**`patchState` 的实际合并点在这里**，带 `baseRevision` 守卫）。只改前者会让增量更新后字段消失

**步骤**

1. ⚠ `patchState` 的字段是显式列出的（不是整体序列化 state），漏字段会导致增量更新后前端拿不到用量；必须同步加入 `CreateTranscriptPayload` 与 `CreateTranscriptPatch` 两处。
2. ⚠ 复用 `TurnUsage` 的 null 语义：`TotalTokens` 未知保持未知，不把分项之和当总数（与 `formatTurnOutcome` 的既有约定一致）。
3. `ContextIsEstimated` 只在上下文数字来自本地计数来源（`IDirectTokenCounter`）时为 `true`；provider 实测值恒为 `false`。⚠ 不要在没有真实计数来源时把估算值上屏（违反 C12）。回合内的实时刷新见 C17。
4. 计算成本：只在 `Build` 时对已加载的 turns 做一次线性求和，不新增 SQL 查询、不触发 per-token 写入。

**测试**

- `TranscriptProjectionTests`：累计求和、上下文优先取 `LastRequestInputTokens`、缺失时退回 `ContextTokens`、未知窗口保持 null、空会话返回 null。
- `TranscriptDelivery` 相关测试：增量载荷包含 usage 字段。

**验收**：切换会话后快照携带正确用量；`dotnet test --filter Transcript` 全绿。

---

## P2 —— 设置链路

### C10 `DirectCompressionSettingsService` + 策略源

**目标**：设置在 Desktop 落地，并让 Infrastructure 安全地读到。（D§9.1、D§9.2）

**改动文件**

- `SelfClaw.Desktop/Services/Compression/DirectCompressionSettingsService.cs`（新，实现 `IDirectCompressionPolicySource`，节点名 `direct_compression`）。⚠ 路径按仓库既有约定：设置服务放**按功能分目录**的文件（`Services/Appearance/AppearanceSettingsService.cs`、`Services/ProgrammingAssistant/ProgrammingAssistantSettingsService.cs`），`Services/Settings/` 里只有共享的 `DesktopSettingsJsonStore.cs`
- `SelfClaw.Infrastructure/Agents/Direct/Context/DisabledDirectCompressionPolicySource.cs`（新，返回 `Enabled = false` 的默认策略）
- `SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`：注册默认策略源
- `SelfClaw.Desktop/Composition/DesktopServiceRegistration.cs`：注册 `DirectCompressionSettingsService`（覆盖默认实现）

**步骤**

1. 参照 `AppearanceSettingsService`：`_gate` + `_cached` + `Normalize` + `SaveAsync` 后触发 `Changed`。存储 API 已核实：`DesktopSettingsJsonStore.ReadNodeAsync<T>(nodeName, serializerOptions?, ct)` / `WriteNodeAsync<T>(nodeName, value, serializerOptions?, ct)`，两者内部已带信号量与原子写，不要再自己包一层文件读写。
2. ⚠ `Normalize`：比例 clamp [0.10, 0.95]、绝对阈值 clamp [4 000, 1 000 000]、`NaN`/无穷回落默认；`ModelProfileId` 用 `IAiModelCatalog.IsModelAvailableAsync` 校验，不可用则归零（跟随会话模型）。
3. ⚠ DI 顺序：`App.xaml.cs` 先 `AddSelfClawInfrastructure` 后 `AddSelfClawDesktop`；单服务解析取最后一次注册，因此 Desktop 的实现生效。必须写一条测试固定该顺序（否则压缩会静默失效）。
4. ⚠ `GetAsync` 在压缩路径上会被调用：不要每次都做模型可用性 I/O；模型校验放在 `SaveAsync` 与首次加载，运行期只读缓存。
5. 两处边界已定（C10 复核）：设置文件损坏或不可读时**只回落默认策略，不修复也不记日志**——压缩策略在回合路径上，抛异常会让整回合失败，坏文件留待设置页保存覆盖（C08）。`NormalizeAsync` 里 `IAiModelCatalog` 查询抛异常时**不吞**、任其冒出：catalog 故障属更底层问题，回合内其它路径同样会失败，静默回落只会掩盖故障。

**测试**（新增 `DirectCompressionSettingsServiceTests`）

- 归一化边界（比例/阈值 clamp、NaN、未知模型归零）。
- 保存后读回一致、缓存即时更新、`Changed` 触发一次。
- 文件缺失/损坏 → 返回默认策略不抛。
- 组合根顺序测试：注册 Desktop 实现后解析到 Desktop 服务（参照 `ServiceCollectionExtensionsTests` 的构成方式）。

**验收**：`desktop-settings.json` 出现 `direct_compression` 节点；删除节点后回落默认。

---

### C11 `direct-compression/*` bridge + 路由

**目标**：设置页与宿主之间的协议。（D§9.3）

**改动文件**

- `SelfClaw.Desktop/Services/Compression/DirectCompressionSettingsBridge.cs`（新，与 C10 的服务同目录；仓库惯例是按功能分目录，`AppearanceSettingsBridge` 就在 `Services/Appearance/`）
- `SelfClaw.Desktop/Services/WebView/WebViewMessageRouter.cs`：`type.StartsWith("direct-compression/")` 分支（放在 appearance 分支附近）
- `SelfClaw.Desktop/Composition/DesktopServiceRegistration.cs`：注册 bridge，并加入 `WebViewMessageRouter` 构造参数列表

**步骤**

1. 逐条对照 `AppearanceSettingsBridge`：`TryHandleAsync` 返回 `(object Response, …)`、`requestId` 回显、`OperationCanceledException` 重抛、异常 → `{ type, requestId, error }`。
2. 支持：`direct-compression/get`（返回 `direct-compression/state`）、`direct-compression/save`（归一化后落盘并返回）。
3. ⚠ `WebViewMessageRouter` 的构造参数与 `DesktopServiceRegistration` 中显式 `new WebViewMessageRouter(...)` 的实参列表必须同步更新（先例：`DesktopServiceRegistration.cs:156` 注册 bridge，约 `:205` 传进 router 构造）。
4. ⚠ 广播 `direct-compression/state` 用 `WebViewHostChannel.PostPush(object payload)`；`PostResponse` 只用于应答。先例是 `ExtensionSettingsBridge` 的 `extensions/state-changed` 与 `PetSettingsBridge` 的设置变更推送——它们在 shell 未 ready 时是 no-op（`PostPush` 返回 bool），照先例处理返回值。
5. 载荷与推送语义（C11 复核已定）：响应统一为 `{ type: "direct-compression/state", requestId, policy: { enabled, thresholdRatio, absoluteThresholdTokens, modelProfileId } }`；`save` 回显**归一化后**的策略（非法值不报错），异常时同形状附 `error` 字段并回退默认策略（不做假成功）。推送同形状、`requestId` 为 null，推送前**重读服务**而不是复用保存返回值，且必须切回 UI dispatcher（`Dispatcher.CheckAccess` → `InvokeAsync`）。推送是 fire-and-forget（`_ = PublishAsync()`），其中 `OperationCanceledException` 被捕获后不重抛（它没有自己的取消令牌，只有 dispatcher 关闭这种情形，重抛会变成未观察异常）——这是对代码约定 6 的**有意例外**，必须有注释说明。

**测试**（新增 `DirectCompressionSettingsBridgeTests`，对齐 `AiProviderSettingsBridgeTests`）

- 每条消息 requestId 回显、响应 `type` 正确。
- 非法比例/阈值 → 归一化后落盘（不是报错），响应里是归一化结果。
- 未识别消息 → 返回 null 由路由抛 `ArgumentException`。
- 保存后广播一条 `direct-compression/state`（对齐 `ExtensionSettingsBridge` 的推送用例）。

**验收**：WebView2 devtools 手发 `{type:'direct-compression/get', requestId:'x'}` 收到状态回包。

---

### C12 设置页

**目标**：上下文压缩设置真实可用。（D§9.4）

**改动文件**

- `SelfClaw.TranscriptVue/src/views/SettingsView.vue`：AI 分组新增 `compression` 导航项 + `defineAsyncComponent` 映射。⚠ 导航项形状已核实为 `{ id, label, en, icon }`，**`icon` 必填**（同组其它项都有，lucide 图标），漏了会让渲染异常
- `SelfClaw.TranscriptVue/src/components/settings/Compression.vue`（新）
- `SelfClaw.TranscriptVue/src/components/settings/compression/*.vue`（按体量拆分，如 `CompressionToggle.vue` / `ThresholdField.vue` / `CompressionModelSelect.vue`）。⚠ 共享样式的相对深度随目录变：页面本体 `components/settings/Compression.vue` 用 `@import '../../styles/settings-console.css'`，拆到子目录后是 `@import '../../../styles/settings-console.css'`（`components/settings/appearance/*.vue` 五个子组件都是后者）；页面根元素照既有写法 `class="… sc-root sc-stage"`（见 `components/settings/agents/AgentSettingsPanel.vue`）
- `SelfClaw.TranscriptVue/src/composables/useDirectCompression.js`（新）

**步骤**

1. `useDirectCompression`：`onMounted` → `direct-compression/get`；订阅 `direct-compression/state`；`save` 用 `requestLatest` 防抖，`SupersededError` 静默；失败回显错误条、不做假成功。
2. 压缩模型下拉复用 `ai-providers/list-enabled-models`（参照 `useComposerModelSelection` 的请求方式），展示为「跟随当前会话模型」+ 已启用模型列表。
3. 阈值输入给百分比滑杆 + 数字输入；无窗口模型绝对阈值给数字输入 + 说明文案（何时生效）。
4. ⚠ 前端校验只做即时反馈，以后端归一化结果为准回显；组件样式 `<style scoped>` + `@import` 共享样式（深度见上：页面 `../../styles/`、子目录 `../../../styles/`）；图标用 lucide，界面禁 emoji。
5. ⚠ `SettingsView.vue` 的 `navSeq` 编号会自动重排，确认其他页面视觉无回归。
6. 页面需说明“计数不可用时本回合不压缩”的行为，避免用户误以为开关失效。
7. 已定（C12 复核）：导航项放 AI 分组**末尾**（只让 plugins / pet / about 的 `navSeq` 顺移，编号仍是两位、无宽度回归）；阈值输入不显示 k/M 缩写（`formatTokens` 还在 `useAiProviderHost.js`，C14 抽进 `renderers/usageMeter.js` 后可在阈值旁加 `≈64k` 预览，属可选项）；保存粒度是开关与模型选择**即时发**、滑杆与数字框走 400ms 防抖（数字框在 change / 失焦提交，避免中间态被归一化打断输入），切页在防抖窗口内先冲刷待保存改动；宿主推送在存在“未确认的本地编辑”时被忽略，保存失败保留用户草稿并出错误条、不静默回滚；`tests/directCompression.test.js` 的 4 条用例保留，C15 在其之上补专用测试。

**验收**：设置页可读写；改阈值后重启保持；保存非法值被归一化；`npm run build` 通过。

---

## P3 —— 前端呈现

### C13 `CompressionNotice.vue` + 转录分支

**目标**：`---上下文压缩---` 分隔项在转录中可见可展开。（D§8.4）

**改动文件**

- `SelfClaw.TranscriptVue/src/components/Chat/transcript/CompressionNotice.vue`（新）
- `SelfClaw.TranscriptVue/src/components/Chat/TranscriptPanel.vue`：项循环实测为 `<div v-for="item in items" :key="item.id" class="message-row" :class="[item.role, item.status]" :data-message-id="item.id">`，其中已有 `<TurnOutcome v-if="item.kind === 'turn-outcome'" :outcome="item.turnOutcome" />` 与 `<MessageContent v-else … />`。压缩项插在两者之间：`<CompressionNotice v-else-if="item.kind === 'compression'" :notice="item.compaction" />`（放在 `MessageContent` 的 `v-else` 之前）。不要另起 `v-for`、也不要改 row 结构
- `SelfClaw.TranscriptVue/src/renderers/pluginTranscript.js`：新 kind 的容忍路径

**步骤**

1. 视觉：细分隔线 + 居中标签 `上下文压缩`，副标题 `已压缩 N 条消息 · 48.2k → 1.2k tokens`（token 未知时只显示消息数）；点击/回车展开摘要正文。
2. ⚠ 摘要正文走 `renderers/markdown.js` 的既有 sanitize 流程，不得直接 `v-html` 未处理字符串。
3. ⚠ `pluginTranscript` 的超限分支按 `item.kind` 分流：`turn-outcome` 与 `compression` 都退化为带元数据的精简项，不能退化成 `content` 段（否则插件侧会把摘要当正文）。
4. 无压缩项时组件不渲染，保持既有 DOM 结构不变（避免影响滚动锚点与折叠测试）。
5. 形状对齐同级组件 `transcript/TurnOutcome.vue`：`<script setup>` + `defineProps({ notice: { type: Object, default: null } })`，副标题文本由 `renderers/` 里的纯函数生成（不要写在模板里）；样式沿用既有 token（`var(--text-muted, #777)`、12px、数字加 `font-variant-numeric: tabular-nums`）。
6. 已定（C13 复核）：token 缩写用**大写** `K/M` 并沿用既有 `formatTokens` 语义（非有限或 ≤0 → `—`、`32K` 这类预设串透传），副标题形如 `已压缩 12 条消息 · 48.2K 到 1.2K tokens`（两侧未知时只留消息数）——与既有口径一致优先于设计 §8.4 的示例写法；插件侧超限时 `compaction.summary` 保留但截断到 2048 字符（与 `errorMessage` 同口径），C15 据此写“摘要被限长”用例；`Model` 额外放进分隔项的 `title`（`压缩模型：xxx`）作为补充，保留。

**验收**：触发压缩后转录出现分隔项；刷新/重开会话后仍在（来自持久化数据）；插件面板摘要视图不崩。

---

### C14 输入框区用量显示

**目标**：常驻显示当前上下文大小与累计输入/输出 token。（D§8.4）

**改动文件**

- `SelfClaw.TranscriptVue/src/renderers/usageMeter.js`：**C13 已建好该文件并放入 `formatTokenCount`（k/M 缩写）**，本任务在其之上补 `formatContextUsage` 等输入框用量所需的纯格式化，不要另起文件、也不要另写一套缩写规则。⚠ 同时把已存在于 `composables/useAiProviderHost.js` 的 `formatTokens(value)`（k/M 缩写；非有限或 ≤0 返回 `—`）**合并进这个 renderer**（纯格式化本应放 `renderers/`，AGENTS 前端约定 1），并把 `useAiProviderHost.js` 与 `components/settings/ModelManagement.vue` 的导入改指到新位置；若两份缩写语义有差，以 `formatTokens` 的既有行为为准并让 `formatTokenCount` 与之统一（C15 会为它写用例）。`renderers/turnOutcome.js` 里用 `toLocaleString()` 输出每回合完整数字，是另一种口径，保持不动
- `SelfClaw.TranscriptVue/src/composables/useChatTranscript.js`：state 增加 `usage`
- `SelfClaw.TranscriptVue/src/composables/useChatTranscript.js`：`replaceState` 里把 `usage` 读进 state。⚠ C9 只做了宿主侧，前端这条状态管道尚未实现，属本任务
- `SelfClaw.TranscriptVue/src/composables/transcriptBridge.js`：`patchState` 的合并点同样要带 `usage`（增量路径的实际落点，带 `baseRevision` 守卫；漏了就只在全量替换后可见）
- `SelfClaw.TranscriptVue/src/views/ChatView.vue`：把 `state.usage` 传给 `ComposerPanel`
- `SelfClaw.TranscriptVue/src/components/Chat/ComposerPanel.vue`：新增 prop 并透传
- `SelfClaw.TranscriptVue/src/components/Chat/ComposerStatusBar.vue`：右侧渲染用量。现状：`.composer-statusbar` 是 `position: relative` 的 flex 行（`gap: 12px`、高 36px），子项依次是工作区选择器、模式控件、git 控件，**没有右侧槽位**，所以用量要自己 `margin-left: auto` 推到最右；另注意 `.dirty-warning` 绝对定位在 `right: 12px; bottom: -25px`，别与它重叠

**步骤**

1. 展示形态：`上下文 12.4k / 32k`（窗口未知 → `上下文 12.4k`）+ 分隔点 + `累计 输入 1.24M · 输出 342k`；无 usage 时整块不渲染。
2. ⚠ 约定 `ContextIsEstimated === true` 时数字加 `~` 前缀并在 title 里说明“由本地计数近似”（本地计数快照上屏时即为 `true`，provider 实测值为 `false`）。
3. ⚠ `ComposerStatusBar` 是 flex 行且已有 git 控件，窄屏（≤700px）需隐藏累计部分，只留上下文。
4. 已定（C14 复核）：累计项未知或为 0 时**整段省略**（不显示一排破折号）；`~` 近似前缀只加在**上下文尺寸**上（窗口是模型声明值、不是计数得来）；窄屏隐藏累计与分隔点用 CSS 媒体查询 `display: none`（AGENTS 不变量 6 的 `visibility: hidden` 约束只针对插件浮层，不适用此处）；`useAiProviderHost.js` 里的 `formatTokens` 已删除、统一由 `renderers/usageMeter.js` 的 `formatTokenCount` 提供（`formatPrice` 与该文件自己的 `trimNumber` 留在原处）。
4. 组件保持 `<style scoped>`；不在 `App.vue` 全局样式里加东西；不因用量刷新触发 composer 重挂载（只加 prop，不改 `key` 与结构层次）。

**验收**：Direct 会话跑几轮后数字增长且与转录末端的 tokens 一致；CLI 会话无 usage 时不显示。

---

### C15 前端测试

**目标**：把新增渲染与格式化纳入 `npm test`。（D§12）

**改动文件**

- `SelfClaw.TranscriptVue/tests/transcriptRenderers.test.js` 或新增 `tests/compressionNotice.test.js`
- `SelfClaw.TranscriptVue/tests/usageMeter.test.js`（**C13 已建，含 7 条 token 缩写与副标题用例；本任务在其上补用例，不要另建文件**）。⚠ 测试栈已核实：`npm test` = `vitest run --config vitest.config.js`；devDependencies 里已有 vitest 5 / @vue/test-utils 2.5 / jsdom 26 / @playwright/test（e2e），既有用例都直接从 `src/renderers/*.js` 导入并用 vitest 的 `describe/expect/it`。**不要新增测试依赖、不要换 runner**；需要挂组件时用 @vue/test-utils。另注：`tests/directCompression.test.js` 由 C12 建好，同样在其上补而不是重建

**步骤**

1. `CompressionNotice`：有摘要/无摘要、token 未知、展开交互、markdown sanitize。
2. `usageMeter`：小数与 k/M 边界、null 处理、窗口未知形态、`~` 前缀。
3. 插件转录：`compression` 项超限截断后仍是压缩项且摘要被限长。

**验收**：`npm test` 全绿（新增用例数在提交信息中说明）。

---

## P4 —— 收尾

### C16 文档同步 + 死代码清理 + 全量回归

**目标**：文档与实现一致，无残留旧路径。（D§10.2、D§12）

**步骤**

1. `AGENTS.md`：不变量 10 / 12 的版本号已由 C2 改好（核对即可，不要重复改）；「深入阅读」清单加入本设计文档；设置页接线状态补「上下文压缩」；若新增了 NuGet 依赖，按仓内惯例在必要处记录。⚠ 「深入阅读」在第 65–71 行、现有 4 条（端到端主干 / Direct 输入 / 桌面前端 / 子系统），新设计文档尚未登记，把它加到「子系统」那条即可（`direct-context-compaction-design.md` 与配套 `-implementation-tasks.md`）；`docs/` 下**没有总索引文件**，不需要另行登记。同时清理过时的版本断言引用：`docs/direct-message-input-design.md` 第 3 行「当前 SQLite schema v31」与其 §14 首条的 v31 表述改为 v32（或去掉具体版本号）；`docs/automation-system-design.md` 里「本次升级到 v31」属时间点记录，保留原文，只在该段落标注“后续已升至 v32”。
2. `docs/runtime-execution-flow.md` §5：把“token 预算仍是 UTF-8 启发式裁剪”改为“tokenizer 计数驱动压缩，不再裁剪”，并在 §2 的 Direct 顺序里加入回合开始处的计数与压缩步骤。⚠ 已定位：该句在文件**第 113 行**（`token 预算仍是 UTF-8 大小的启发式估算，不是 provider tokenizer。…`），同段落还并排写着“Direct 事件 Channel 仍无界”等其它待测量项 —— 只改估算那一句，别顺手改别的结论。
3. `docs/pipeline-architecture.md`：§4 事件表（第 169 行起）加入三个压缩事件；§5 输出链路（第 203 行起）的 `replaceState` / `patchState` 载荷字段列表（第 214–215 行）补 `usage`，并补 `compression` 渲染项。⚠ 另外**第 118 行**的 Direct 流程框仍写着 “History 反向预算裁剪(工具调用/结果不可拆)”，必须改成“按 tokenizer 计数触发压缩 + 按边界重放”（工具调用/结果不可拆这条不变）。
4. `docs/direct-message-input-design.md` §7.5 上下文预算（**第 314 行**）：标注裁剪规则已由压缩取代，保留其“批量准入”语义描述。具体要改的三句：第 316 行“历史按完整单元裁剪”、第 320 行“预算仍是 UTF-8 启发式估算，不能宣称真实 tokenizer 精确计数”、第 140 行表格里的“有真实追加／裁剪行为的运行上下文服务…与历史 composer 共用预算规则”。注：第 207–208 行的 64 KiB / 256 KiB 上限是另一回事（正文与快照体积），保留不动。
5. `docs/direct-context-compaction-design.md`：把实施偏离（如 C3 的 continuation 退化、C6 的 section 顺序最终值、C4 实际选定的 tokenizer 包与版本）写回设计文档，不留“历史保留”。
6. 清理核对：
   - `rg 'EstimateTokens|BuildHistoryWithinBudget|EstimateMessages|EstimateTools' SelfClaw.Infrastructure SelfClaw.Desktop` 无命中（测试中的对应断言也应已删除或改为计数断言）。
   - `DirectPromptBudget` 注释已更新，不再表述为可裁剪额度。
   - 无“估算兜底”死分支残留。
   - ⚠ 顺手修一处 renderer 进位缺陷（C15 发现、按边界未擅自改）：`formatTokenCount` 四舍五入后会输出 `1000K`（999999 → `1000K`、999950 → `999.95K`），应按“格式化后再回判量级”修正为 `1M`，并在 `tests/usageMeter.test.js` 补一条边界用例（C13/C14 的既有用例不含该点）。
7. 全量回归，**顺序固定**：先在 `SelfClaw.TranscriptVue` 跑 `npm test` 与 `npm run build`，**再**跑 `dotnet build` + `dotnet test` —— 前端构建会重写 `SelfClaw.Desktop/Assets/TranscriptVue/`（Vite 先清目录再写新哈希名），与 C# 构建交叉会报 `MSB3030 找不到 vue-vendor-*.js`；同样**不要并发跑两个 dotnet 构建**（互锁 `obj/`/`bin/`，会让 testhost 崩溃并污染结果）。测试用 `-p:BaseOutputPath=bin/<name>-review/` 重定向，避免被运行中的 Desktop 应用锁住 DLL。最后跑 `git diff --check`。

**验收**：文档与代码一致；无新增警告；测试全绿。

---

## 手工验收清单

1. **压缩可见**：把触发比例调到最低（如 10%），跑一个多轮 Direct 会话 → 转录出现 `上下文压缩` 分隔项，副标题数字合理，摘要可展开。
2. **计数准确性**：对 OpenAI 系模型，比较 tokenizer 计数与 provider 回报的 `last_request_input_tokens`，偏差应只有框架开销级别；对 Claude/Ollama 记录偏差供参考。
3. **离线可用**：断网首次启动，压缩与用量显示均正常，不出现词表下载等待或失败。
4. **计数降级**：用测试钩子或临时移除数据包模拟计数不可用 → 本回合不压缩、历史完整、界面不显示估算数字、活动区出现诊断。
5. **不再裁剪**：把压缩关闭，用长会话跑到超窗 → 回合以可读错误失败（提示开启压缩），转录中历史**没有**消失。
6. **摘要复用**：压缩后继续对话 → 下一回合输入 token 明显回落且模型仍记得早期结论；重启应用后行为一致。
7. **模型可配**：设置为另一个已启用模型 → 压缩确实走该模型（`conversation_compactions.model`）；把该模型禁用 → 自动回退当前会话模型且回合不失败。
8. **用量显示**：输入框区实时显示上下文与累计输入/输出；切换会话、切换 CLI/Direct 表现正确。
9. **持久化**：重启后压缩项、用量、设置全部保持；删除会话后 `conversation_compactions` 无残留行；`turn_usage.last_request_input_tokens` 有值。
10. **旧库行为**：用 v31 库启动 → 只读拒绝并给出明确提示（DP1 的既定后果）。
