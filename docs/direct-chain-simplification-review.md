# Direct 链路精简与可维护性审查

审查基线：`0a23176`（2026-09-28）+ 未提交工作树改动（子代理目录增强，见 §0.3）。
范围：`SelfClaw.Infrastructure/Agents/Direct/**` 及其直接协作者（`AiProviders`、`Agents/Subagents`、`Core/Runtime/Requests`）。
本文只提「简化 / 去冗余 / 提升可读性」的建议，不改变 Direct 的对外行为契约；每条建议都标注了**不能删的校验**与**风险**。

---

## 0. 结论摘要

### 0.1 一句话结论

Direct 链路的冗余**主要不是行数冗余，而是概念冗余**：同一个"这个回合允许用什么能力"的规则，被 4 个来源分别推导、被 3 个关卡分别校验；同一个"必须恰好一个终态"的不变量，被 2 层各自实现。因此删代码的收益有限（约 -3% LOC），删**分支与所有权重叠**的收益很大（-11 处 origin 分支、-3 处重复校验、-2 次重复 DB 读）。

### 0.2 体量基线（生产代码，不含测试）

| 区域 | 文件 | 行数 | 占比 |
|---|---|---|---|
| `Direct/Hooks` | 41 | 2,831 | 43% |
| `Direct/Capabilities` | 13 | 1,698 | 26% |
| `Direct/Tools` | 9 | 921 | 14% |
| `Direct/Context` | 5 | 516 | 8% |
| `DirectAgentChatRuntime.cs` | 1 | 608 | 9% |
| **合计** | **71** | **6,603** | 100% |

测试基线：`SelfClaw.Tests/Infrastructure/Agents/Direct` 26 个文件、6,588 行、225 个 `[Fact]/[Theory]`（其中 Hooks 13 文件 / 2,181 行）。**测试与生产代码接近 1:1，重构有安全网**，这是本审查建议"敢动手"的前提。

### 0.3 审查时的状态说明

工作树上有未提交的在途改动：`SubagentCapabilitySource` 新增 `ISubagentDefinitionCatalog` + 目录项（提供 subagent 名称/描述给模型）、`CapabilitySections.SubagentCatalog`、`SubagentCapabilities` 记录、`SubagentTaskPreflight` 相关测试。本文的结论建立在**含该改动的工作树**之上；其中 `_definitionCatalog` 已被 `ResolveCatalog` 使用，不是死字段。

### 0.4 建议清单（按优先级）

| # | 问题 | 类型 | 净收益 | 风险 |
|---|---|---|---|---|
| S1 | 子代理能力校验三层重复（含 2 次重复 DB 读） | 结构性冗余 | 删 1 层关卡 + 2 次 IO | 中 |
| S2 | `DirectTurnOrigin` 规则散落 11 处 / 7 文件 | 结构性冗余 | ✅ 已实施（见 §1） | 完成 |
| S3 | 能力 Lease / Scope / Resolver 三型交叉持有 | 所有权冗余 | ✅ 已实施（见 §1） | 完成 |
| S4 | `PluginCapabilitySource` 主路径与继承路径 80% 复制 | 复制粘贴 | ✅ 已实施（见 §1） | 完成 |
| D1 | `BindTools` 的 descriptor 对齐校验不可达 | 死校验 | 删 6 行 | 极低 |
| D2 | 终态纪律双层实现 + 1 处死分支 | 死代码 | 删 flag + 分支 | 低 |
| D3 | `setup.Invoker` 与 `state.Invoker` 双持有 | 可读性 | 单一真源 | 极低 |
| D4 | `RunStartedEvent` 的合成 session id 无消费者 | 噪音 | 明确语义 | 极低 |
| D5 | `AiProviderClientRequest.Tools` 必填但恒为空 | 契约陷阱 | 删字段 | 低 |
| C1 | 工具策略字面量分散 + 同一非法值两种行为 | 一致性 | ✅ 已实施（见 §3） | 完成 |
| C2 | `DirectAgentChatRuntime` 608 行承担 7 项职责 | 可读性 | 拆分 3 个文件 | 低 |
| C3 | `DirectToolInvoker` 工具故障契约（模型可见真实错误 + 自持熔断） | 行为契约 | ✅ 已实施（方案 B，见 §3） | 完成 |
| C4 | 4 类兜底文案指向同一个"工具结果不合法" | 可读性 | 统一诊断 | 极低 |

> 与用户直觉一致的校验类删减只有 D1 / D2 / D5 / C4 四项；**其余"看起来多余"的校验都必须保留**，理由见 §4。

---

## 1. P0 结构性简化

### S1. 子代理能力校验三层重复（已实施）

**状态：2026-09-29 已实施。**

**原问题**

同一次子代理任务，包/MCP 能力被校验三次，其中两次是完整重复：

| 关卡 | 位置 | 行为 |
|---|---|---|
| 受理前 | `SubagentTaskCoordinator.StartAsync` → `SubagentTaskPreflight.CheckAsync` | 3 次 IO：packages + MCP servers + model |
| 执行前 | `SubagentTaskExecutor.ExecuteAsync` → 同一 `CheckAsync` | 同一套 IO 与校验，完整重复 |
| 回合内 | `DirectTurnCapabilityResolver.ValidateCapturedPackageCeiling` → `CheckPackages` | 第三次遍历 packages |
| 回合内（后置） | `EnsureRequiredCapabilitiesResolved` | 第四次同义判断（仅看“是否加载成功”） |

**实施内容**

- `SubagentTaskPreflight` 收缩为**只判定三件廉价事实**：定义的工具策略是否仍不超出捕获 ceiling（纯比较）、捕获的工作区是否仍存在、解析出的模型是否仍可用。删除对 `IExtensionPackageRepository` / `IMcpServerRepository` 的依赖与两次全表读；受理前与执行前调用同一个实现，语义一致。
- 能力授权与时效**留在回合内**：`ValidateCapturedPackageCeiling`（子代理回合的唯一授权/时效判定点，已补注释说明）＋ `EnsureRequiredCapabilitiesResolved`（必需能力未加载即失败）＋ `DirectTurnPolicy.AllowsMcpServer`（MCP 越界即排除）。
- 契约文档（`ISubagentTaskPreflight`）与 `SubagentTaskPreflight` 的 remarks 写明分工。

**对原评审的两处修正**

1. “三层校验”实为**一层重复 + 一对前后置检查**。`ValidateCapturedPackageCeiling`（执行前：授权且未变更？）与 `EnsureRequiredCapabilitiesResolved`（执行后：真的加载成功？）性质不同，合并会把“未授权的能力先加载再失败”变成常态，因此**保留两者**，只删除重复的执行前一层。
2. 成本论据要弱化：每个子代理任务省下的 2 次 SQLite 全表读在绝对量级上很小（表只有几行到几十行）。真正的收益是**一个错误面而不是两个**：不再有“受理时通过、执行前又拒绝”的第二套失败语义。

**行为差异（唯一一处，需知晓）**：能力在“受理之后、执行之前”被删除/禁用/升级时，此前由执行前 preflight 返回 `CapabilityUnavailable`；现在由回合内抛出 `InvalidDataException`，`SubagentTaskExecutor` 的既有映射把它记为 **`SnapshotInvalid`**（错误文案不变，仍是 `Plugin/Skill 'x' is unavailable or changed after task acceptance.`）。`SnapshotInvalid` 对“捕获快照已不成立”而言语义同样准确。

**验证**：`DirectTurnCapabilityResolverTests` 的两个用例从“preflight 与运行时用同一规则”改写为“由子回合执行强制，且网关不再重复校验”（显式断言变更后网关仍返回通过），子回合抛错与 continuation 降级断言保留；`SubagentTaskExecutorTests` / `SubagentTaskCoordinatorTests` / `SubagentActivityTestContext` 的 preflight 构造收敛为单参。全量 1172 通过 / 4 跳过。

### S2. Origin 分支散落：一个策略对象取代 11 处判断（已实施）

**状态：2026-09-29 已实施。**

**原问题**

`DirectTurnOrigin`（Interactive / Subagent / Continuation）的规则判断散落在 7 个文件 11 处：

```
DirectAgentChatRuntime.cs         Continuation → 必须有 checkpoint
DirectAgentChatRuntime.cs         Continuation → 不取 latest user（无附件）
DirectTurnCapabilityResolver.cs   Subagent|Continuation → 继承 hook plugins
DirectTurnCapabilityResolver.cs   != Subagent → 跳过 ceiling 校验
DirectTurnCapabilityResolver.cs   != Continuation → 原样返回请求
DirectTurnCapabilityResolver.cs   != Subagent → 跳过必需能力校验
Capabilities/SkillCapabilitySource.cs  Interactive 或 ceiling 含该 skill → 目录可见
Capabilities/McpCapabilitySource.cs    Interactive → 全部可见 / Continuation → 记录降级
Capabilities/SubagentCapabilitySource.cs Subagent → 不提供 delegate 工具
Context/DirectPromptComposer.cs        Continuation → 使用 completion batch（本就是数据驱动，未改）
```

**实施内容**

新增 `Capabilities/DirectTurnPolicy.cs`：`record DirectTurnPolicy(DirectTurnOrigin Origin, DirectCapabilityCeiling? Ceiling)` + `For(request)`，把规则写成有名字的谓词与方法：

| 成员 | 语义 |
|---|---|
| `IsDelegated` | 委派回合受捕获 ceiling 约束，交互回合受 Agent 绑定约束 |
| `InheritsHookPlugins` / `InheritedHookPlugins` | 父回合捕获的 hook Plugin 仍是本回合的策略；缺失或变更即阻断 |
| `RequiresCapturedCapabilities` | Subagent 子回合必须仍满足 ceiling，故缺失/变更致命 |
| `ShrinksToCapturedCapabilities` | Continuation 只收缩：变更的能力带诊断降级丢弃 |
| `RequiresToolExecutionCheckpoint` | 仅 Continuation 必须在任何工具执行前提交 checkpoint |
| `HasFreshUserMessage` | 仅 Continuation 没有新的用户消息（附件/`runStarting` 输入因此为空） |
| `CanDelegateToSubagent` | Subagent 子回合不再提供 delegate 工具 |
| `AllowsSkill(id)` / `AllowsMcpServer(server)` | 能力可见性（含“ceiling 内且未变更”的 MCP 判定） |
| `DiagnosesRemovedCapabilities` | 仅 Continuation 把被移除的能力记为降级（Subagent 走致命路径） |

结果：`grep ExecutionContext.Origin` 在 Direct 内只剩一处**数据透传**（把 origin 交给 hook 上下文供插件匹配），不再有任何规则分支。

**与原建议的差异（有意为之）**：原建议把 policy 作为参数穿过四个来源的 `ResolveAsync`。实施时改为“每个消费点在需要时 `DirectTurnPolicy.For(request)` 就地构造”——策略是请求的纯函数，穿透参数会改动 4 个来源签名与约 10 个测试调用点，却不带来新的语义保障。规则集中在一个文件的收益已完全得到。

**验证**：新增 `DirectTurnPolicyTests` 用三个事实逐项钉住三种 origin 的规则矩阵（含 MCP revision 变更时 Subagent 拒绝 / Continuation 降级的差异）；全量 1172 通过 / 4 跳过。

### S3. 能力 Lease / Scope / Resolver 三型交叉持有（已实施）

**状态：2026-09-29 已实施。**

**原问题**

- `DirectTurnCapabilityLease` 构造函数 8 个参数 + 1 个 `Func<ValueTask>`：`systemInstructions`、`tools`、`messageAdjustments`、`diagnostics`、`disposeAsync`、`hooks`、`hookNotices`、`hookBlockReason`；`Tools` 与 `Bindings` 由同一入参派生。
- `DirectTurnLeaseScope` 82 行：两个 `List`、两个 `Add` 重载、两段反向释放、一把锁，只为区分“MCP 先释放、Plugin 后释放”。
- lease 同时是“结果 DTO”和“释放句柄”，且自己又实现了一份幂等（`Interlocked.Exchange(...) == 0 && _disposeAsync is not null`）。

**实施内容**

- `DirectTurnLeaseScope` 改为单一有序 `List<IAsyncDisposable>`：`IDisposable`（Plugin version lease）由一个私有 `SynchronousLease` 适配，两个 `Add` 重载内圈成一个，两段 drain 内圈成一段反向释放。释放顺序由“按获取顺序倒序”定义：Plugin 先于 MCP 加入，因此 MCP 连接先关 —— 与原来的“MCP 列表先排空”完全一致，但规则变得可推导。倒序释放改为“尽力释放全部、重抛首个异常”，修掉原来“一个 MCP 释放抛错会跳过其余 MCP”的缺口。
- `DirectTurnCapabilityLease` 不再接收 `Func<ValueTask>`，改为持有 `DirectTurnLeaseScope?`；幂等只由 scope 实现，lease 只负责持有。`DisposeAsync` 变成一行 `_scope?.DisposeAsync() ?? ValueTask.CompletedTask`。
- Resolver 的构造调用从 `leases.DisposeAsync` 改为 `leases`；测试的 4 参构造不变（scope 为可选参数），只有三个依赖“释放顺序”的用例改为向 scope 注入一个记录型 lease。

**未做（低价值）**：把 lease 再拆为 `CapabilityResolution` + `CapabilityLease` 两个类型。lease 现在只是一组只读属性加一个转发句柄，再引入一层 record 会增加转换点而无新保障；若后续出现第二种释放策略再拆。

**代价**：本轮净增约 25 行（抽出的辅助与文档抵消了删除的重复），收益是“一个生命周期概念”与两处不再重复的职责。

---

### S4. `PluginCapabilitySource` 双路径复制（已实施）

**状态：2026-09-29 已实施。**

**原问题**：`ResolveAsync` 主循环与 `ResolveInheritedHooksAsync` 都在做：查 package → `IsIntact` → 读 manifest → 校验 manifest.Id → 读取已确认权限 → 取 version lease → 追加 hooks。差别只有失败后果：主路径 `diagnostics.Degrade` 继续，继承路径 `return HookNotes.InheritedHookPluginBlocked(id)`（致命），且两者的退化文案与 hook notice 文案不同（现状说明不是“真正相同”的两段，所以不能简单地合成一句）。

**实施内容**：抽出一个私有 `LoadPluginAsync(plugin, ct)`，返回命名元组 `(Manifest, Lease, SkipReason, UnconfirmedPermissions)`：

- 共享：`IsIntact`、manifest 读取、manifest.Id 比对、已确认权限比对、version lease 获取；“未确认权限”与“损坏”用 `UnconfirmedPermissions` 区分，两个调用方各自保持原有文案（升级消息与 hook notice 都逐字不变）。
- 各自保留：主路径的 degrade + hook notice + 后续 instructions/skills/roots 展开；继承路径的阻止语义。
- lease 所有权仍是“调用方拥有”：主路径在展开完成后交给 scope，继承路径立即交给 scope，失败时由 `catch`/`finally` 释放。

**验证**：`PluginHookCapabilityTests`、`DirectTurnCapabilityResolverTests`、`HookAllEventsPluginTests` 覆盖权限未确认 / 损坏 / 变更 / 继承阻断四类场景，均未修改断言；全量 1169 通过 / 4 跳过。

---

## 2. P1 死代码与不可达校验

### D1. `BindTools` 的 descriptor 对齐校验不可达

**证据**：`DirectTurnCapabilityResolver.cs:159` 的 `binding.Tool.Name != binding.Descriptor.ProviderName`。全部 5 个 `DirectToolDescriptor` 构造点都使用函数自身的名字：

| 构造点 | 名字来源 |
|---|---|
| `WorkspaceAgentToolset.cs:81` | `name`（同一方法参数同时给 `AIFunctionFactory` 与 descriptor） |
| `SkillRuntimeToolset.cs:56` | `name`（同上） |
| `McpToolAdapter.cs:43` | `providerName`（`tool.WithName(providerName)` 之后） |
| `SubagentCapabilitySource.cs:116 / :155` | `name`（同上） |

**建议**：删除该 half 条件；把"名字由 AIFunction 派生"的事实固化成一个构造助手（例如 `DirectToolBinding.Create(AIFunction, kind, source, displayName, requiresApproval)` 内部读 `Tool.Name`），使不一致在类型层面不可能出现。

**理由**：它在校验我们自己的构造代码，而不是外部输入；同时它把两种不同故障（重名 / 描述符错配）塞进同一句异常文案，反而降低诊断质量。**重名检查保留**（理由见 §4.7）。

---

### D2. 终态纪律双层实现 + 死分支

**证据 1（死分支）**：`DirectAgentChatRuntime.cs:130/141/148/166/177`。三条路径（blocked / 正常 / 异常）都会写终态并置 `runCompletedEmitted = true`；取消路径置 `cancellationObserved = true` 并抛出。因此 `:177` 的兜底 `if (!runCompletedEmitted && !cancellationObserved)` 恒为 false，`runCompletedEmitted` 这个 flag 只服务于这个死分支。

**证据 2（双层实现）**：`DispatchingAgentChatRuntime.EnforceProtocolAsync`（约 120 行）实现了"终态恰好一次、终态后事件丢弃、超时取消 adapter"；`DirectAgentChatRuntime.ProduceEventsAsync` 自己又实现了"异常→Failed 终态、结束必须有终态"。

**建议**

- 删除 Direct 内的 `runCompletedEmitted` 与 `:177` 兜底分支（-6 行）。
- 明确唯一所有权：**由 dispatcher 强制终态契约**（它已经有 5s 清理超时，且 CLI 适配器确实需要它——`CliAgentChatRuntime` 在 `GetSessionIdAsync` 抛错时不会产出终态）。Direct 只保留 `catch` 以完成"先报 usage、再写 Failed 终态"的顺序，不再重复"是否已写终态"的记账。

**理由**：同一个不变量两个实现者，且两处的兜底文案不同（`"…ended without a completion status."` 出现在两个类里）。保留 dispatcher 版本，因为它的位置对所有适配器生效，而 Direct 的 try/catch 无法覆盖 CLI 的前置失败。

---

### D3. `setup.Invoker` 与 `state.Invoker` 双持有

**证据**：`DirectAgentChatRuntime.cs:289-290`（`state.Invoker = invoker`）、`:380`（`setup.Invoker` 用于翻译）、`:208`（`state.Invoker?.CallCount` 用于 runCompleted hook）。

**建议**：只保留 `DirectTurnSetup.Invoker`，`DeliverRunCompleted` 改为接收 `setup?.Invoker?.CallCount`。

**理由**：`TurnState` 注释说它存在的理由是"让失败的回合仍能投递一次 runCompleted"——那只需要 `Hooks` 与 `Elapsed`；`Invoker` 是搭车的第三个字段，让读者以为 invoker 的生存期与 hooks 绑定。

---

### D4. `RunStartedEvent` 的合成 session id 无消费者

**证据**：`DirectAgentChatRuntime.cs:304` 写 `$"direct-{Guid.NewGuid():N}"`；`ConversationTurnRecorder.cs:84` 对 `RunStartedEvent` 只做 `EnsureAssistantMessage`，不读取 session id；只有 CLI（`CliAgentChatRuntime.cs:204`）把 session id 写回 `ICliAgentSessionStore`。

**建议**：把 `RunStartedEvent` 的 `SessionId` 明确为可选（`null` for Direct），或拆出 `CliSessionStartedEvent`。至少把 Direct 侧改传 `null`，让"Direct 每回合都是全新会话"这一事实在事件层可见。

**理由**：每回合生成一个 GUID 却无人消费，会让读者误以为 Direct 有会话恢复能力。

---

### D5. `AiProviderClientRequest.Tools` 必填但恒为空

**证据**：
- `AiChatClientFactory.cs:81`：`PrepareAsync` 返回 `Tools: []`。
- `AiChatClientFactory.cs:88`：`Create` 立刻 `request with { Tools = pipeline.Tools }`。
- 唯一消费者是 `AiChatOptions.cs:100-101`（`ToolMode` / `Tools`）。

**建议**：删除 `AiProviderClientRequest.Tools`，把工具改为 `Create(preparation, pipeline)` 的显式入参（`CreateChatOptions(request, tools)`）。

**理由**：一个在边界上恒为空的必填字段，是后续误用（以为 `PrepareAsync` 能带工具）的诱因；而且它让"准备阶段"与"管线组装阶段"的职责看起来可交换。这是"链路不清晰"的一个具体来源。

---

## 3. P2 可读性与一致性

### C1. 工具策略：字面量分散 + 同一非法值两种行为（已实施）

**状态：2026-09-29 已实施。**

**原问题**

| 位置 | 内容 |
|---|---|
| `DirectCapabilityRules.cs:22-23` | `"none"`、`"read-only"` 字面量 |
| `DirectCapabilityRules.cs:25` | `Allows` 对未知策略 **throw `InvalidDataException`** |
| `DirectCapabilityRules.cs:110` | `ToolPolicyRank` 对未知策略 **返回 -1**，于是 `IsToolPolicyAuthorized` 返回 `false` |
| `SubagentDefinitionCatalog.cs:13/314/497` | 自定义 `DefaultToolPolicy = "read-only"`，并再写一遍 `"none" or "system"` |
| `SubagentExecutionSession.cs:271` | 合成 ceiling 里再写一遍 `"read-only"` |
| `MainWindowViewModel.Agents.cs:25` | 合成不可用定义时再写一遍 `"none"` |

同一个非法输入，一条路径抛异常、另一条路径静默判否；调用方无法预知该不该 catch。

**实施内容**

- Core 的 `AgentRuntimeDefinition` 现在是三个策略名的唯一来源：新增 `NoneToolPolicy` / `ReadOnlyToolPolicy`（与已有 `SystemToolPolicy` 并列，紧贴它所约束的 `ToolPolicy` 属性）。
- `DirectCapabilityRules` 只剩一个 `ToolPolicyRank`；`IsToolPolicyAuthorized` 改为单次 rank 比较，未知策略与 `Allows` 一样抛 `InvalidDataException`（`UnknownToolPolicy` 统一文案，列出三个合法值）。选择“抛”而非“静默判否”：未知名只能来自定义解析回归，静默判否会把配置错误伪装成“工具忽然消失”，也会让 ceiling 被当成不合法值继续传递。
- `SubagentDefinitionCatalog` 的 `DefaultToolPolicy` 与两处校验、`SubagentExecutionSession` 的合成 ceiling、`MainWindowViewModel.Agents` 的不可用定义全部改用常量；两个定义解析器的合法集合仍各自保留（交互代理只接受 `system`，子代理接受三种）——这是产品规则而非重复。

**验证**：`DirectCapabilityRulesHookTests` 新增 rank 顺序的 6 个 `[InlineData]` 用例与 1 个“未知策略必须抛且文案含原值”用例；全量 1169 通过 / 4 跳过。

---

### C2. `DirectAgentChatRuntime` 职责过载

**现状**：608 行，内含 3 个嵌套类型（`TurnState`、`TurnOutputStream`、隐含的 `DirectTurnSetup` 联合态），同时负责：SDK 流消费、事件翻译、终态策略、usage 聚合、工具结果降级、hook 投递、资源释放、Channel 读写。

**建议**

1. 抽出 `DirectEventTranslator.cs`（现 `TurnOutputStream`，约 150 行）：`ChatResponseUpdate → AgentStreamEvent` 的纯翻译，可用单元测试直接驱动 MCP/工具结果矩阵。
2. 抽出 `DirectTurnState.cs`（现 `TurnState`）并与 setup 合并。
3. `DirectTurnSetup` 的"`ProviderLease is null` 表示 blocked"改为显式联合态：

```csharp
internal abstract record DirectTurnSetup
{
    internal sealed record Ready(AiChatClientLease Provider, CapabilityLease Capabilities,
        IReadOnlyList<ChatMessage> Messages) : DirectTurnSetup;
    internal sealed record Blocked(CapabilityLease Capabilities, string Reason) : DirectTurnSetup;
}
```

**理由**："`null` 表示另一种状态"是当前运行时最隐晦的约定（注释里用一句"SetupTurnAsync already wrote the Blocked terminal event"解释）。联合态让 `switch` 编译期穷尽，也让"谁负责写 Blocked 终态"变成类型签名的一部分。

目标形态：`DirectAgentChatRuntime` ≈ 350 行，只保留"准备 → 消费 → 终态 → 释放"。

---

### C3. `DirectToolInvoker`：工具故障契约（已实施，方案 B）

**状态：2026-09-29 已实施。** 原评审提出的是"模型看不到真实错误 + 熔断依赖 M.E.AI 未文档化行为"两项问题，最终采用方案 B：invoker 自持连续故障预算，把故障转成模型可见的失败结果，超预算时以专用异常终止回合。

#### C3.1 原问题（实测基线）

用真实 `ChatClientBuilder.UseFunctionInvocation` + `FunctionInvoker` 管线跑一个抛异常的工具，基线行为是：

| 观察点 | 基线结果 |
|---|---|
| 模型看到的内容 | `"Error: Function failed."`（`System.String`）——**不是**真实异常 |
| transcript 工具卡 | `ToolCallStatus.Failed` + `ResultSummary = "tool exploded"` + `ResultContent = 完整堆栈` |
| 回合终态 | `Failed :: tool exploded`，provider 调用 **4** 次后停止 |

两条根因：

1. `IncludeDetailedErrors` 全仓库未设置 → 默认 `false` → 模型只拿到通用错误串（依据 `Microsoft.Extensions.AI` XML 文档：`FunctionResultContent.Result` 为 "a generic error message if the function call failed"；`Exception` "is not serialized"）。于是出现**不安全的不对称**：transcript 看得到真实错误与堆栈，而模型恰恰是唯一需要该信息来自我纠正的角色。
2. 熔断由 `FunctionInvokingChatClient.MaximumConsecutiveErrorsPerRequest`（默认 3）提供，而 SelfClaw 只显式设了 `MaximumIterationsPerRequest = 128`——一个决定回合成本的规则活在第三方默认值里。

#### C3.2 实施后的契约

| 层 | 改动 |
|---|---|
| `DirectToolInvoker` | 工具抛非取消异常时不再 rethrow，而是构造 `DirectToolResult(Failed, exception.Message, {type,message}, exception.ToString())` 并走与正常路径相同的收尾（hook `toolExecuted` → `RecordOutcome` → `AttachFeedback`）；`Detail` 仍是 `[JsonIgnore]` 的展示only堆栈 |
| `DirectToolInvoker` | 新增 `MaximumConsecutiveToolFaults = 2`：“连续故障预算”；任何无故障调用（含被拒绝/被阻断）重置计数；超预算时抛 `DirectToolFaultLimitException`（携带工具自身异常作为 inner） |
| `AiChatClientFactory` | 显式 `MaximumConsecutiveErrorsPerRequest = 0`（M.E.AI 文档语义：“zero → all function calling exceptions immediately terminate the function invocation loop”）——使 invoker 唯一可能未捕获的那个 rethrow 就是终止信号 |
| `DirectToolFaultLimitException`（新） | 让终态文案说明“因连续故障而停”而不是把工具异常原样冒充成回合错误，同时保留工具堆栈 |

实测契约（`DirectToolPipelineTests` 固定）：

| 场景 | 模型看到 | 工具执行 | provider 往返 | 回合终态 |
|---|---|---|---|---|
| 工具持续抛异常 | 前 2 次：`DirectToolResult(Failed, "tool exploded")` | 3 | 3 | `Failed :: Tool 'probe_tool' faulted 3 times consecutively… Last error: tool exploded` |
| 工具返回 `Failed` 但不抛 | 每次都看到失败结果 | 5（客户端请求多少就执行多少） | 6 | `Succeeded` |

与基线相比：模型从“0 次看到真实错误”变成“2 次”，而坏工具的成本从 4 次尝试降到 **3 次**（`MaximumConsecutiveToolFaults + 1`）。

#### C3.3 对原评审两处结论的修正

- **侧信道不能删。** 原评审说 `_outcomes` / `TryTakeOutcome` 存在的原因“只有一个”（异常路径）——不完全对。`RecordOutcome` 在正常路径、拒绝路径、阻断路径也会写入，它传递的是 `EffectiveArgumentsJson` / `ArgumentsModifiedBy` / `ApprovalRequiredBy` / `BlockedBy` / `IgnoredFailures` 这些**无法塞进 `DirectToolResult.HookFeedback` 的 hook 元数据**（schema v28 的 `tool_runs` hook 列就靠它落库）。本次改动删掉的是异常路径重复的那一整段（单独的 `ToolExecutedAsync` + `RecordOutcome` + rethrow），收尾合并为一条路径。
- **`_outcomes` 的注释已过时。** 原文写着“The result object does not exist on this path, so the side-band outcome is the only place…”，现已随代码删除。

#### C3.4 本次同时修掉的小项

- `needsApproval` 的 `&&`/`||` 优先级已显式加括号（原 C3 “证据 1”）。
- 原评审指出的“异常路径端到端零覆盖”已补齐：`DirectToolPipelineTests` 新增故障与“失败但不抛”两个真实管线用例；`DirectToolInvokerHookTests` 新增预算耗尽与计数重置用例，并把原来断言“必顶抛异常”的用例改为断言新的故障契约。

---

### C4. 四类兜底文案指向同一个"工具结果不合法"

| 位置 | 兜底 |
|---|---|
| `DirectAgentChatRuntime.DescribeToolResult` | `"The tool returned an invalid Direct result."` |
| `WorkspaceAgentToolset.Bind.MarshalResult` | `"Tool '{name}' returned an invalid result."` |
| `DirectToolInvoker.InvokeAsync` | `"Direct tool '{toolName}' is not bound to this turn."` |
| `SkillRuntimeToolset` / `SubagentCapabilitySource` | 直接把 `value` 当结果（无校验） |

前两条语义相同（marshal 结果与声明类型不符），第三条对应"模型调了未绑定函数"，第四条则完全跳过检查。建议：统一为 `DirectToolBinding` 上的一个 marshal 助手，只保留一句话，并在文档注释里写明"仅防御未绑定调用，正常路径不可达"。

---

## 4. 明确「看起来多余但必须保留」的校验

用户方向是"简化没有必要的校验"。以下八项在阅读时会显得冗余，但**删除会造成真实功能或安全回退**，建议保留并在代码注释里写明理由：

1. **`AiChatClientFactory.PrepareAsync` 的 6 项校验**（默认模型选择、profile 存在、profile 启用、connection 存在、connection 启用、adapter 支持 API Format）：每项都有唯一且面向用户的错误文案，是"禁用模型不会悄悄切换默认模型"这一产品承诺的实现点（见 `docs/runtime-execution-flow.md` §2）。
2. **`DirectAgentChatRuntime` 的续写 checkpoint 守卫**（`Origin == Continuation` 必须有 `IToolExecutionCheckpoint`）：这是 schema v27 的"执行前持久化"承诺；缺失时必须在任何工具执行前失败。价格 3 行，收益是防止"不确定重放"。
3. **续写时"缺失即致命"的 `EnsureRequiredCapabilitiesResolved`**：父代理冻结的 ceiling 是授权边界，静默降级等于扩大或缩小授权。保留，但按 S1/S2 合并到唯一决策点。
4. **`HookArgumentsValidator`（149 行）**：它不是"重复校验参数"，而是实现**"拒绝本次改写、保留原参数继续执行"**这一策略。删除它会让被 hook 改坏的参数直接进入工具调用并失败（有副作用风险）。可精简的部分只有"JSON Schema 子集"的实现方式（例如把 `required/type/additionalProperties` 三项收敛成对生成 schema 的单次遍历），不建议整体删除。
5. **`HookProtocol.Parse` 的 decision 白名单**：`invalidDecision` 与"未知决策"必须区分，否则插件的拼写错误会被当成"无决策"静默放行。
6. **MCP 结果的 65,536 字节上限（`McpToolResultFormatter`）**：provider 侧没有二次保护，删除会直接把上下文打爆。
7. **`BindTools` 的重名检查**：跨来源重名在结构上几乎不可能，但**MCP 内部可能**——`McpToolAdapter.Slug` 把非字母数字映射为 `_`（`-` 保留），因此同一服务器上 `a.b` 与 `a_b` 两个工具会解析成同一个 provider 名。保留检查，但请把异常文案拆成"重名（附两个来源）"与"描述符错配"两种（配合 D1）。
8. **审批的 fail-closed 默认**（`ToolApprovalHandler is null → return false`）：这是安全默认值，不能改成"无 handler 即放行"。

---

## 5. 建议实施顺序

| 批次 | 内容 | 前置 | 验证 |
|---|---|---|---|
| PR1 | D1 + D2 + D3 + D4 + D5（死代码与噪音，零行为变化） | 无 | `dotnet test` 全绿；Direct 相关 225 用例不变 |
| PR2 | C1（策略字面量集中 + 单一未知策略行为） | ✅ 已完成 | 六个 rank 用例 + 未知策略抛错用例 + 全量回归 1169 通过 |
| PR3 | S4 + S3（`PluginCapabilitySource` 提取、lease/scope 收敛） | ✅ 已完成 | `PluginHookCapabilityTests` / `DirectTurnCapabilityResolverTests` 断言未改；全量 1169 通过 |
| PR4 | S2（`DirectTurnPolicy`） | ✅ 已完成 | 逐来源跑 `DirectTurnCapabilityResolverTests`、skill/MCP 相关用例；新增策略矩阵测试；全量 1172 通过 |
| PR5 | S1（子代理校验收敛为一处，保留回合内前后置两项检查） | ✅ 已完成 | `DirectTurnCapabilityResolverTests` 改写入 preflight 不再重复校验；全量 1172 通过 |
| PR6 | C2（运行时拆分） | 无 | 翻译器新增直测；`DirectAgentChatRuntimeTests` 保持断言不变 |
| PR7 | C3（invoker 故障契约） | ✅ 已完成 | 新增故障/预算/非异常失败四类用例 + 全量回归 1162 通过 |

每批独立可回滚；PR4 与 PR5 之间存在顺序依赖（已完成），其余互不影响。

---

## 6. 进度、量化目标与"不做"清单

**已落地（2026-09-29）**

| 提交 | 内容 | 生产代码变化 | 测试 |
|---|---|---|---|
| `a6a421d` | C3 工具故障契约（方案 B） | +1 新类型、invoker 重排 | 1162 通过 |
| `956f04a` | PR1：D1–D5 死代码与不可达校验 | −12 行（含 1 个删类、1 个删字段） | 1162 通过 |
| `214ec0d` | PR2：C1 工具策略常量与单一未知值行为 | ±0，3 处字面量收敛 | 1169 通过 |
| 本轮（PR3） | S4 + S3 复制与生命周期收敛 | 净 +24 行（辅助与文档抵消删除） | 1169 通过 |
| 本轮（PR4） | S2 单一策略对象 | +1 类型（~70 行），-11 处散落分支 | 1172 通过 |

实测：Direct 目前 73 文件 / 6,865 行（含未提交的 PR3 与 C3 新增）；`DirectAgentChatRuntime.cs` 608 → 598 行。**行数目标（6,603 → 6,250）已下调预期**：本轮的收益是“同一规则只有一个实现”，不是行数；强行减行会引入新的抽象层。

**剩余目标**

- 子代理任务的能力校验：执行前 preflight 只留“工具策略 / 工作区 / 模型”三件廉价事实，能力授权与时效集中在回合内一处判定（错误码由 `CapabilityUnavailable` 变为 `SnapshotInvalid`，已记入 §1）。
- `DirectAgentChatRuntime` 拆分（C2/PR6，P2 级）：598 行，拆出事件翻译器与显式联合态。
- S2 已完成：origin 规则现在只存在于 `DirectTurnPolicy`（Direct 内只剩一处 origin 数据透传给 hook 上下文）。

**不做**

- 不动 Hook 事件集（6 事件）与 `AsyncHookExecutor` 的每插件 FIFO/驱逐/lease 纪律：它们有明确不变量与独立测试，属产品范围而非冗余。
- 不为"每回合重建工具集"做缓存：`AIFunctionFactory.Create` 的开销相对一次 provider 往返可忽略，且 Subagent/Skill/MCP 工具本质上是每回合的；若要做，必须先有测量数据。
- 不改 `DispatchingAgentChatRuntime` 的 5 秒 adapter 清理超时与终态裁剪语义（它是 CLI 前失败路径的唯一保障）。
- 不为 `DirectTurnHooks` 增加"零 hook 时跳过构造"的微优化：省下的是 6 次空列表过滤，收益低于新增 null 分支的可读性成本。

---

## 7. 阅读路径建议

改造完成后，Direct 链路建议按以下顺序可读：

```
DirectAgentChatRuntime.StreamTurnAsync        (准备 → 消费 → 终态)
  → AiChatClientFactory.PrepareAsync / Create (模型与管线)
  → DirectTurnPolicy                          (本回合允许什么)
  → DirectTurnCapabilityResolver              (四来源 → 统一 DirectToolBinding)
  → DirectPromptComposer                      (预算内历史 + 必需输入)
  → DirectToolInvoker                         (hook → 审批 → checkpoint → 执行)
  → DirectEventTranslator                     (SDK 更新 → AgentStreamEvent)
  → DispatchingAgentChatRuntime               (终态契约)
```
