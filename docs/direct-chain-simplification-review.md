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
| S2 | `DirectTurnOrigin` 规则散落 11 处 / 7 文件 | 结构性冗余 | 单一策略入口 | 中 |
| S3 | 能力 Lease / Scope / Resolver 三型交叉持有 | 所有权冗余 | 一处生命周期 | 中低 |
| S4 | `PluginCapabilitySource` 主路径与继承路径 80% 复制 | 复制粘贴 | ~90 → ~40 行 | 低 |
| D1 | `BindTools` 的 descriptor 对齐校验不可达 | 死校验 | 删 6 行 | 极低 |
| D2 | 终态纪律双层实现 + 1 处死分支 | 死代码 | 删 flag + 分支 | 低 |
| D3 | `setup.Invoker` 与 `state.Invoker` 双持有 | 可读性 | 单一真源 | 极低 |
| D4 | `RunStartedEvent` 的合成 session id 无消费者 | 噪音 | 明确语义 | 极低 |
| D5 | `AiProviderClientRequest.Tools` 必填但恒为空 | 契约陷阱 | 删字段 | 低 |
| C1 | 工具策略字面量分散 + 同一非法值两种行为 | 一致性 | 单一判定 | 低 |
| C2 | `DirectAgentChatRuntime` 608 行承担 7 项职责 | 可读性 | 拆分 3 个文件 | 低 |
| C3 | `DirectToolInvoker` 工具故障契约（模型可见真实错误 + 自持熔断） | 行为契约 | ✅ 已实施（方案 B，见 §3） | 完成 |
| C4 | 4 类兜底文案指向同一个"工具结果不合法" | 可读性 | 统一诊断 | 极低 |

> 与用户直觉一致的校验类删减只有 D1 / D2 / D5 / C4 四项；**其余"看起来多余"的校验都必须保留**，理由见 §4。

---

## 1. P0 结构性简化

### S1. 子代理能力校验三层重复

**现状证据**

同一次子代理任务，包能力被校验三次，其中两次是完整重复：

| 关卡 | 位置 | 行为 |
|---|---|---|
| 受理前 | `Desktop/Services/Subagents/SubagentTaskCoordinator.cs:72` → `SubagentTaskPreflight.CheckAsync` | `ListPackagesAsync` + `ListMcpServersAsync` + `IsModelAvailableAsync` |
| 执行前 | `Desktop/Services/Subagents/SubagentTaskExecutor.cs:199` → `SubagentTaskPreflight.CheckAsync` | 同上，完整重复 |
| 回合内 | `Infrastructure/Agents/Direct/Capabilities/DirectTurnCapabilityResolver.cs:76` → `ValidateCapturedPackageCeiling` → `DirectCapabilityRules.CheckPackages` | 第三次遍历 packages |

加上 `EnsureRequiredCapabilitiesResolved`（`DirectTurnCapabilityResolver.cs:114/291`，仅 Subagent origin）再检查一次"必需能力是否真的加载成功"，以及 `McpCapabilitySource.IsAllowedByCapturedCeiling`（`McpCapabilitySource.cs:83-99`）对 MCP 的第四次同义判断。

**问题**

1. 3 个关卡、2 套错误类型（`SubagentPreflightFailure` vs `InvalidDataException`）、2 套用户可见文案，却只有一个语义："捕获 ceiling 必须被满足"。
2. 每个子代理任务因此多 2 次 `extension_packages` / `mcp_server_configs` 全表读。
3. 三处任一改动（例如后续给 package 加 `sourcePath` 校验）都必须同步三处。

**建议**

- **只保留一个权威关卡**：受理时的 preflight 负责"拒绝受理"，用 `SubagentPreflightFailure` 返回给父 Agent（这是唯一能让父代理看到结构化错误码的路径）。
- 执行前的 `SubagentTaskPreflight.CheckAsync` 收缩为**只检查两个真正会随时间变化、且与本进程无关的事实**：工作区是否还存在、模型是否仍可用（对应 `WorkspaceUnavailable` / `ModelUnavailable`）。包与 MCP 的当前性交由回合内的解析结果承担。
- **删除 `ValidateCapturedPackageCeiling`**：它检查的三件事已被下游覆盖——强制授权由回合内的 ceiling 过滤/解析承担，必需项缺失由 `EnsureRequiredCapabilitiesResolved` 抛错承担。把它的"必须授权"语义折叠进解析循环（解析时 `captured is null` 即该 origin 的致命错误）。
- 顺带把 `EnsureRequiredCapabilitiesResolved` 重命名为 `EnsureSubagentCapabilitiesResolved`，并在方法内保留唯一的"致命/降级"决策点。

**理由**

这正是用户感知的"没必要的校验"：同一个不可变输入（`AgentRuntimeDefinition` + `DirectCapabilityCeiling`）在同一秒内被三套代码各判一次。TOCTOU 只需要**一个执行时刻的判定**，受理时刻的判定价值是"尽早拒绝"，不需要与执行时刻的判定等价。

**风险与缓解**：行为差异出现在"受理通过、执行前能力被删除/升级"的场景。缓解：`SubagentTaskPreflightTests` 已有 `CapabilityUnavailable` 用例，改造时把断言从"preflight 返回失败"迁移到"回合内抛 `InvalidDataException` 且任务进入 Failed 且错误码仍为 `CapabilityUnavailable`"。

---

### S2. Origin 分支散落：一个策略对象取代 11 处判断

**现状证据**

`DirectTurnOrigin`（Interactive / Subagent / Continuation）的判断散落在 7 个文件 11 处：

```
DirectAgentChatRuntime.cs         :224  Continuation → 必须有 checkpoint
DirectAgentChatRuntime.cs         :337  Continuation → 不取 latest user（无附件）
DirectTurnCapabilityResolver.cs   :148  Subagent|Continuation → 继承 hook plugins
DirectTurnCapabilityResolver.cs   :175  != Subagent → 跳过 ceiling 校验
DirectTurnCapabilityResolver.cs   :195  != Continuation → 原样返回请求
DirectTurnCapabilityResolver.cs   :297  != Subagent → 跳过必需能力校验
Capabilities/SkillCapabilitySource.cs  :65  Interactive 或 ceiling 含该 skill → 目录可见
Capabilities/McpCapabilitySource.cs    :83  Interactive → 全部可见
Capabilities/McpCapabilitySource.cs    :93  Continuation → 记录降级
Capabilities/SubagentCapabilitySource.cs:55 Subagent → 不提供 delegate 工具
Context/DirectPromptComposer.cs        :77 Continuation → 使用 completion batch
```

**问题**

"这个 origin 能做什么"是**一个**三值规则，却由每个来源（Plugin / Skill / MCP / Subagent / 运行时 / Prompt）各自 `if` 出来。新增一种 origin（例如未来的"重试"或"定时任务"）必须改 7 个文件，且没有任何地方能一眼看全规则。

**建议**

在回合开始时构造一个不可变策略对象，把规则集中，来源只消费结论：

```csharp
// Infrastructure/Agents/Direct/Capabilities/DirectTurnPolicy.cs（示意）
internal sealed record DirectTurnPolicy(
    DirectTurnOrigin Origin,
    DirectCapabilityCeiling? Ceiling,
    string ToolPolicy)
{
    internal static DirectTurnPolicy For(DirectChatTurnRequest request) => ...;

    // 能力可见性
    internal bool CanUseInteractiveExtensions => Origin == DirectTurnOrigin.Interactive;
    internal bool InheritsHookPlugins => Origin is Subagent or Continuation;
    internal bool MustShrinkToCeiling => Origin == DirectTurnOrigin.Continuation;
    internal bool RequiresCapabilitiesToResolve => Origin == DirectTurnOrigin.Subagent;
    internal bool CanDelegateToSubagent => Origin != DirectTurnOrigin.Subagent;

    // 运行期与 prompt
    internal bool RequiresToolExecutionCheckpoint => Origin == DirectTurnOrigin.Continuation;
    internal bool CarriesCompletionBatch => Origin == DirectTurnOrigin.Continuation;

    // 降级 vs 致命（唯一决策点）
    internal bool MissingCapabilityIsFatal => Origin == DirectTurnOrigin.Subagent;
    internal bool ChangedCapabilityIsAllowed => Origin == DirectTurnOrigin.Interactive;
}
```

`IDirectTurnCapabilityResolver.ResolveAsync` 开头构造一次，传给四个来源（`ResolveAsync(..., policy, ...)`），运行时的 checkpoint 守卫与 composer 的 completion batch 也读同一对象。

**理由**

- 阅读收益最大：读 `DirectTurnPolicy` 一处即可知道三种回合的全部差异。
- 与 S1 天然合并：`MissingCapabilityIsFatal` / `ChangedCapabilityIsAllowed` 就是 S1 里"致命还是降级"的判定来源。
- 与设计文档 §8.3 的"origin 决定策略"表格形成一一对应，后续审计可直接对照。

**风险**：纯重构，无行为变化；但触及 4 个来源的构造函数，必须逐类跑现有 225 个测试。建议单独一个 PR，禁止与行为改动混合。

---

### S3. 能力 Lease / Scope / Resolver 三型交叉持有

**现状证据**

- `DirectTurnCapabilityLease` 构造函数 8 个参数 + 1 个 `Func<ValueTask>`：`systemInstructions`、`tools`、`messageAdjustments`、`diagnostics`、`disposeAsync`、`hooks`、`hookNotices`、`hookBlockReason`。其中 `Tools`（`AITool` 列表）与 `Bindings`（名字→binding 字典）由同一个入参派生。
- `DirectTurnLeaseScope` 82 行：两个 `List`、两个 `Add` 重载、两段反向释放、一把锁，只为区分 "MCP 先释放、Plugin 后释放"。
- Resolver（396 行）承担：策略过滤 + 四个来源编排 + 冲突/策略过滤 + ceiling 投影 + 诊断。

**问题**

一个回合的能力生命周期被三个类型分摊，且 lease 同时是"结果 DTO"和"释放句柄"。`DisposeAsync` 的 `Interlocked.Exchange(...) == 0 && _disposeAsync is not null` 这种写法，读者必须推理三次才能确认恰好释放一次。

**建议**

```csharp
// 结果与生命周期分离
internal sealed record CapabilityResolution(
    IReadOnlyList<string> SystemInstructions,
    IReadOnlyList<DirectToolBinding> Tools,
    IReadOnlyDictionary<Guid, string> MessageAdjustments,
    IReadOnlyList<string> Diagnostics,
    DirectTurnHooksPlan Hooks);            // HookPlan = hooks + notices + blockReason

internal sealed class CapabilityLease(CapabilityResolution resolution, DirectTurnLeaseScope scope)
    : IAsyncDisposable
{
    public CapabilityResolution Resolution { get; } = resolution;
    public ValueTask DisposeAsync() => scope.DisposeAsync();   // 幂等由 scope 保证
}
```

`DirectTurnLeaseScope` 改为单一有序 `List<IAsyncDisposable>`（`IDisposable` 用一层 `Func<ValueTask>` 包装），反向释放顺序由加入顺序决定，删除两个 `Add` 重载与两段 drain。

**理由**：`DisposeAsync` 幂等只应有一个实现（scope），lease 只做"持有"；来源侧不再需要 `if (!leases.Add(lease))` 的双路径（当前 Plugin 与 MCP 各写了一遍）。

---

### S4. `PluginCapabilitySource` 双路径复制

**现状证据**：`ResolveAsync` 主循环（约 90 行）与 `ResolveInheritedHooksAsync`（约 60 行）都在做：查 package → `IsIntact` → 读 manifest → 校验 manifest.Id → 读取已确认权限 → 取 version lease → 追加 hooks。差别只有失败后果：主路径 `diagnostics.Degrade` 继续，继承路径 `return HookNotes.InheritedHookPluginBlocked(id)`（致命）。

**建议**：抽一个 `TryLoadHookPluginAsync(ExtensionPackageRecord) → HookPluginLoadResult`，返回 `Loaded(manifest, lease, hooks)` 或 `Unavailable(reason)`；调用方决定 degrade 还是 block。`PluginCapabilitySource` 预计 318 → 约 230 行。

**理由**：权限/lease 是本仓库最容易出并发 bug 的两处（`PluginVersionLeaseManager` 有独立测试），复制两份意味着修一遍要记得另一遍。

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

### C1. 工具策略：字面量分散 + 同一非法值两种行为

**证据**

| 位置 | 内容 |
|---|---|
| `DirectCapabilityRules.cs:22-23` | `"none"`、`"read-only"` 字面量 |
| `DirectCapabilityRules.cs:25` | `Allows` 对未知策略 **throw `InvalidDataException`** |
| `DirectCapabilityRules.cs:110` | `ToolPolicyRank` 对未知策略 **返回 -1**，于是 `IsToolPolicyAuthorized` 返回 `false` |
| `SubagentDefinitionCatalog.cs:13/314/497` | 自定义 `DefaultToolPolicy = "read-only"`，并再写一遍 `"none" or "system"` |
| `SubagentExecutionSession.cs:271` | 合成 ceiling 里再写一遍 `"read-only"` |

**问题**：同一个非法输入，一条路径抛异常、另一条路径静默判否。调用方无法预知该不该 catch。

**建议**：在 Core 增加 `ToolPolicies`（`None` / `ReadOnly` / `System`）+ `TryParse(string, out ToolPolicyRank)`；`Allows`、`IsToolPolicyAuthorized`、`RestrictToolPolicy`、两个定义解析器全部改用它。让"未知策略"只有一种行为（建议：抛，因为它是定义文件解析的兜底，静默判否会把配置错误变成"工具忽然消失"）。

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
| PR2 | C1（策略字面量集中 + 单一未知策略行为） | 无 | 新增 `ToolPolicies` 单元测试 + 两个定义解析器测试 |
| PR3 | S4 + S3（`PluginCapabilitySource` 提取、lease/scope 收敛） | 无 | `PluginCapabilitySource`/`PluginVersionLeaseManager`/`AsyncHookExecutor` 既有用例 |
| PR4 | S2（`DirectTurnPolicy`） | 建议在 PR3 后 | 逐来源跑 `DirectTurnCapabilityResolverTests`、`SkillCapabilitySource`/`McpCapabilitySource` 相关用例 |
| PR5 | S1（子代理校验收敛为一处 + 删 `ValidateCapturedPackageCeiling`） | **依赖 PR4**（需要 `MissingCapabilityIsFatal` 作为唯一判定点） | `SubagentTaskPreflight` / `SubagentTaskCoordinator` / `SubagentTaskExecutor` / `DirectTurnCapabilityResolver` 用例 + 手动验证"受理后删除包"场景 |
| PR6 | C2（运行时拆分） | 无 | 翻译器新增直测；`DirectAgentChatRuntimeTests` 保持断言不变 |
| PR7 | C3（invoker 故障契约） | ✅ 已完成 | 新增故障/预算/非异常失败四类用例 + 全量回归 1162 通过 |

每批独立可回滚；PR4 与 PR5 之间存在顺序依赖，其余互不影响。

---

## 6. 量化目标与"不做"清单

**目标**

- 生产代码 6,603 → 约 6,250 行（-5%），其中 `DirectAgentChatRuntime` 608 → 约 350 行。
- `DirectTurnOrigin` 分支 11 处 → 1 处（策略对象）+ 少量消费点。
- 子代理任务的重复 package/MCP 读：3 次 → 1 次。
- 新增能力的触点：从"改 4 个来源 + 2 处校验 + dispatcher 约定"收敛为"改 policy + 对应来源"。

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
