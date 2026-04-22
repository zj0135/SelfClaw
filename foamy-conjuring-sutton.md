# Context

当前对话区域只有文字流和右侧步骤卡片，缺少一个能直观看到“人 -> agent -> 工具/其他 agent”运行过程的视图。这个改动的目标是在输入框附近增加一个“可视化”切换按钮，开启后把中间 transcript 渲染区切换为 SVG 画布：固定显示 Human 节点，按编程模式/团队模式展示 agent 节点，并把现有后端运行时事件映射为节点状态、连线高亮和消息流动动画，整体风格贴近根目录 `design.png`。

## Recommended approach

### 1. 把“可视化”开关作为前端本地 UI 状态，而不是新的桌面端持久状态
- 在 `SelfClaw.TranscriptVue/src/App.vue` 增加本地 `visualizationEnabled` 状态，并按现有 `props down / emits up` 方式传给 `ComposerPanel` 和 `TranscriptPanel`。
- 在 `SelfClaw.TranscriptVue/src/components/ComposerPanel.vue` 复用现有 `计划模式` 开关的结构、样式类和交互位置，在输入框底部控制区新增“可视化”开关。
- 这个开关建议**不要**像计划模式那样在 `isBusy` 时禁用，因为用户开启可视化的主要场景就是观察运行中的过程。
- 这样做可以避免改 `replaceState` 顶层协议；`App.vue` 当前已经有很多纯前端 UI 状态（如左栏折叠、设置弹窗），可视化模式属于同一类短生命周期视图状态。

### 2. 只替换中间 transcript 区，不动现有右侧 StepsPanel
- 在 `SelfClaw.TranscriptVue/src/components/TranscriptPanel.vue` 中保留现有外层壳子和滚动容器位置，但在内部按 `visualizationEnabled` 分支：
  - `false` 时继续走现在的 `v-html="messagesHtml"`
  - `true` 时渲染新的 SVG 可视化组件（建议新建 `SelfClaw.TranscriptVue/src/components/TranscriptGraphView.vue`）
- 首版建议**保留右侧 `StepsPanel`**，不要同时隐藏整列：
  - 右侧已经承载 tool approval 按钮和活动详情，直接去掉会引入新的审批交互设计
  - 中间区切画布已经满足“将当前对话渲染区域切换成画布”的需求
  - 这样能最大化复用现有能力，降低回归风险

### 3. 不新建事件通道，继续复用现有 `replaceState` 快照；动画由前端对快照做 diff
- 继续使用桌面端现有的 `replaceState` 推送链路：
  - `SelfClaw.Desktop/ViewModels/MainWindowViewModel.cs`
  - `SelfClaw.Desktop/MainWindow.xaml.cs`
- 前端不要再请求新的实时事件流；改为在 `TranscriptGraphView.vue`（或配套 helper）里根据每次收到的新快照，对以下数据做派生和 diff：
  - `state.items`
  - `state.teamMembers`
  - `state.agentActivities`
  - `state.conversations`
  - `state.selectedConversationId`
  - `state.selectedConversationModeId`
- 推荐新增一个轻量 helper（如 `SelfClaw.TranscriptVue/src/visualization/buildGraphModel.js`），负责把快照转换为：
  - `nodes`
  - `edges`
  - `activeStates`
  - `packets`（短生命周期的移动消息/事件粒子）
- 关键原则：**不要复制一套后端运行时逻辑**，只在前端把现有投影结果再组织成图模型。

### 4. 为了稳定映射到“具体哪个 agent”，补 3 个最小后端字段
当前快照已经够驱动大部分可视化，但要稳定地把消息、工具事件、分支会话绑定到具体 agent，建议补以下字段，避免前端靠名字猜：

- `SelfClaw.Desktop/Services/Transcript/TranscriptRenderItem.cs`
  - 增加 `string? AgentId`
  - 用于把 assistant message 精确映射到对应 agent 节点
- `SelfClaw.Desktop/Services/Transcript/AgentActivityNode.cs`
  - 增加 `string? OwnerAgentId`
  - 用于把 tool activity / team event 精确挂到对应 agent 节点
- `SelfClaw.Desktop/Services/Transcript/TranscriptConversationItem.cs`
  - 增加 `string? BoundAgentId`
  - 用于团队模式下 direct agent conversation 判断“Human 当前发给谁”

对应填充位置：
- `SelfClaw.Desktop/ViewModels/MainWindowViewModel.cs`
  - `BuildMessageItem(...)`：把 `message.AgentId` 写入 `TranscriptRenderItem.AgentId`
  - `BuildConversationItem(...)`：把 `conversation.BoundAgentId` 写入 `TranscriptConversationItem.BoundAgentId`
- `SelfClaw.Desktop/ViewModels/MainWindowViewModel.Team.cs`
  - `BuildTeamMemberActivityNode(...)` / `BuildTeamAgentEventNode(...)`：把 `agent.Id` 写入 `OwnerAgentId`
- `SelfClaw.Desktop/Services/Transcript/TranscriptToolRunPresenter.cs`
  - `BuildActivityNode(...)`：把 `toolRun.AgentId` 写入 `OwnerAgentId`

这样做之后，不需要新增 runtime event 类型，也不需要改 WebView 消息协议的顶层结构；序列化现有对象时这些字段会自然带到前端。

### 5. 画布节点与布局策略
#### 编程模式
- 固定一个 `Human` 节点
- 固定一个单 assistant 节点（id 可在前端合成，如 `programming-agent`）
- assistant 标签优先级：
  1. 最近 assistant message 的 `title`
  2. `selectedProfileModel`
  3. `Assistant`
- 所有 assistant message、tool activity 都归到这个单节点

#### 团队模式
- 使用 `state.teamMembers` 作为 agent 主节点集合
- 布局建议贴近 `design.png`：
  - `Human` 固定在左上/上方
  - `Coordinator` 放在中心偏上
  - 其他 team members 按数组顺序扇形/弧线排在下半区
- 由于 `MainWindowViewModel.cs:1770-1776` 已按 `SortOrder` 排过一次，前端直接按 `state.teamMembers` 当前顺序布局即可

### 6. “Human 发消息指向哪个 agent”的判定规则
- 编程模式：永远指向单 assistant 节点
- 团队模式：
  - 如果当前选中会话不是 agent branch conversation，则指向 `Coordinator`
  - 如果当前选中会话是 direct agent conversation，则指向 `selectedConversation.BoundAgentId`
- 这与现有 direct session 语义保持一致，来源是：
  - `MainWindowViewModel.cs:1562-1597` 的 `TryMatchAgentMention(...)`
  - `MainWindowViewModel.cs:1600-1637` 的 `CreateAgentConversationAsync(...)`

### 7. 事件到视觉效果的映射
直接复用当前后端已投影出来的状态，不另起一套命名：

- **用户消息**
  - 来源：`state.items` 中 `role === 'user'`
  - 表现：从 Human 沿目标 edge 发出一段短促流光 / packet 动画

- **assistant 开始输出 / streaming**
  - 来源：`AssistantMessageStartedEvent`、`AssistantDeltaEvent` 最终投影到 `state.items` 中 assistant message 的 `status` / `isThinking`
  - 表现：目标 agent 节点呼吸光圈、外环脉冲、连线微亮

- **assistant 完成**
  - 来源：`AssistantMessageCompletedEvent`
  - 表现：节点短暂完成闪光，随后回到稳定态

- **tool started / completed / awaitingapproval / failed / cancelled**
  - 来源：`state.agentActivities`，状态值沿用现有 `running / awaitingapproval / completed / failed / cancelled`
  - 表现：
    - 在所属 agent 周围生成一个短生命周期的 tool 卫星点/徽标
    - `running`：青蓝色流动
    - `awaitingapproval`：琥珀色警示环
    - `completed`：绿色完成闪光
    - `failed`：红色抖动/辉光
    - `cancelled`：灰色淡出
- **team member status changes**
  - 来源：`TeamAgentStatusChangedEvent` 最终投影到 `state.teamMembers` / `state.agentActivities`
  - 表现：节点边框和外发光颜色切换，Coordinator 与该成员之间的连线短暂高亮

### 8. SVG 动画实现方式
- 在 `TranscriptGraphView.vue` 中使用 SVG 分层：
  1. 背景网格 / 渐变
  2. 静态连线
  3. 动态发光连线
  4. 节点圆环 / 图标 / 文案
  5. 流动 packet / tool satellite / 状态光晕
- 动画优先使用 **CSS + SVG class 切换**，而不是重 JS 时间轴：
  - WebView2 对 CSS/SVG 动画支持足够
  - 当前状态是快照流，CSS class 驱动更稳，和 `replaceState` 频繁刷新更兼容
- JS 只负责维护“短时动画对象”的生命周期（例如新 packet 出现 600~1200ms 后清掉）

### 9. 需要复用的现有逻辑
- `SelfClaw.TranscriptVue/src/components/ComposerPanel.vue`
  - 复用计划模式开关的结构和视觉语言
- `SelfClaw.TranscriptVue/src/App.vue`
  - 复用 `post(...)`、`handleWebViewMessage(...)`、`applyStatePayload(...)` 的状态流
- `SelfClaw.TranscriptVue/src/renderers/messages.js`
  - 复用 tool 状态语义和 tool action 分类思路
- `SelfClaw.TranscriptVue/src/renderers/steps.js`
  - 复用 `awaitingapproval / running / failed / cancelled / completed` 这些状态语义
- `SelfClaw.Desktop/ViewModels/MainWindowViewModel.cs:1111-1159`
  - 现有 runtime event -> UI state 的处理入口，不新增第二套 runtime 映射
- `SelfClaw.Desktop/Services/Transcript/TranscriptToolRunPresenter.cs`
  - 继续作为 tool activity 的统一投影位置
- `SelfClaw.Desktop/ViewModels/MainWindowViewModel.Team.cs:377-417`
  - 继续作为 team member / team event 节点的统一投影位置

## Critical files
- `SelfClaw.TranscriptVue/src/App.vue`
- `SelfClaw.TranscriptVue/src/components/ComposerPanel.vue`
- `SelfClaw.TranscriptVue/src/components/TranscriptPanel.vue`
- `SelfClaw.TranscriptVue/src/components/TranscriptGraphView.vue`（新建）
- `SelfClaw.TranscriptVue/src/visualization/buildGraphModel.js`（新建，或同等 helper）
- `SelfClaw.TranscriptVue/src/transcript.css`
- `SelfClaw.Desktop/Services/Transcript/TranscriptRenderItem.cs`
- `SelfClaw.Desktop/Services/Transcript/AgentActivityNode.cs`
- `SelfClaw.Desktop/Services/Transcript/TranscriptConversationItem.cs`
- `SelfClaw.Desktop/Services/Transcript/TranscriptToolRunPresenter.cs`
- `SelfClaw.Desktop/ViewModels/MainWindowViewModel.cs`
- `SelfClaw.Desktop/ViewModels/MainWindowViewModel.Team.cs`

## Verification
1. 前端构建：
   - `cd SelfClaw.TranscriptVue && npm run build`
2. .NET 构建（因为会改桌面端投影模型）：
   - `dotnet build SelfClaw.slnx`
3. 手动验证桌面端：
   - `dotnet run --project SelfClaw.Desktop/SelfClaw.Desktop.csproj`
   - 编程模式发送一条消息，确认 Human -> Assistant 动画出现，tool 事件在 assistant 周围更新
   - 团队模式发送一条消息，确认 Coordinator 与 team members 布局正确，节点状态会随讨论推进变化
   - 团队模式下用 `@AgentName` 开头发消息，确认 Human 指向该 agent，而不是 Coordinator
   - 制造需要审批的工具调用，确认画布出现 `awaitingapproval` 状态，同时右侧 `StepsPanel` 审批按钮仍可用
   - 运行中切换“文字 / 可视化”两种视图，确认不会打断 streaming 和滚动逻辑
4. 若后端投影逻辑有分支判断，补跑：
   - `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj`
