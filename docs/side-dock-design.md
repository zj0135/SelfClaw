# 右侧功能面板（SideDock）设计

> 适用范围：`SelfClaw.TranscriptVue` 聊天视图右侧的功能面板，承载命令行（ConPTY 终端）、子代理看板与审查（工作区 Git diff，数据链路见 `docs/git-review-pane-design.md`）。宿主（WPF）窗口未改动，dock 外壳本身不依赖任何桥接协议；子代理读内容协议已删除（§5）。

## 1. 结论

1. **不加第二个 WebView2。** 窗口仍是一个全窗铺满的 `WebView2`，dock 是 Vue 组件，WPF 零改动。
   - 整个外壳就是一个网页：拖动、缩放热区、标题栏按钮、主题（`data-theme` + `ApplyCaptionTheme`）、字号缩放都在同一 DOM。第二个 WebView2 要重做桥接、主题同步、拖拽热区、焦点 / Escape 路由。
   - 迁移的两个功能本来就是 Vue 组件，数据 composable 与展示已分离，只动展示层；`terminal-*`、`toggle-terminal`、`activity-panel/*` 协议原样复用。
   - 第二个 WebView2 进程常驻多占约 100 MB，且两个 WebView 之间无法共享 xterm 实例、活动面板订阅与 localStorage。
2. **dock 只在聊天视图可见**：终端 cwd 跟随会话 workspace、子代理看板按会话 scope、diff 也是会话产物。切到设置等视图时 `ChatView` 卸载、dock 随之卸载，终端会话在宿主侧继续存活。
3. **交互模型（浏览器侧栏式）**：标题栏只有一个「面板」开关按钮；面板内是已打开工具的 tab 条，每个 tab 自带关闭键；没有 tab 时显示**启动页**（上下排列的「命令行」「子代理」及「打开」）；tab 条上的「+」随时回到启动页开其他工具；关 dock 只是隐藏，tab 保留；每次启动都从空启动页开始。
4. **对话区右上角的悬浮快捷栏**（`DockRail`）：每个工具一个图标，点击打开 / 切到该工具，它已是当前 tab 时再点一次收起 dock；当前会话有子代理任务时，子代理图标右上角亮一个安静的发光角点（运行中缓慢呼吸），不显示数字。

## 2. 组成

| 文件 | 职责 |
|---|---|
| `src/composables/useSideDock.js` | 模块级 store：开合 / 已开 tab 列表 / 当前 tab / 宽度 / 终端镜像位 / 徽标数；工具注册表；T1–T4 映射 |
| `src/components/dock/SideDock.vue` | 外壳：左缘拖宽把手、tab 条（tab 本体 + 关闭键为两个并列按钮、「+」、收起键）、启动页、`.dock-body`；持有 `useChatTerminal` 并把 `terminal-state` 镜像进 store；`workspaceRootId` prop 透传给审查 pane；`defineExpose({ blurTerminal })` |
| `src/components/dock/DockRail.vue` | 对话区右上角的悬浮快捷栏：按工具注册表渲染图标按钮 → `dock.toggleTool(id)`；子代理存在感角点读 `state.subagents` |
| `src/components/dock/TerminalPane.vue` | xterm 终端 pane（原 `chat/TerminalPanel.vue` 迁入）：cwd / 运行状态 / 重启；懒加载；主题与字体读取在 `renderers/terminalTheme.js` |
| `src/components/dock/SubagentDockPane.vue` | 子代理 pane：编排 `useActivityPanel` / `useSubagentActivity` / `useActivityDetail` / `useActivityPreferences`；工具条 = `activities/SubagentTaskSelect.vue`（任务下拉框 + 时长 + 取消 + 翻页）+ 刷新，主体 = `activities/SubagentTaskDetail.vue`；承载空态 / 错误 / 自动选中 |
| `src/components/dock/ReviewPane.vue` | 审查 pane：工具条（作用域下拉「未暂存 / 已暂存」+ 分支名 + 刷新）、空态分支、左 diff / 右文件列表两栏；数据层 `composables/useGitDiff.js`；子组件 `dock/review/ReviewFileList.vue`（文件导航列）与 `dock/review/ReviewDiffView.vue`（统一视图 diff）；纯函数 `renderers/unifiedDiff.js`（patch 解析）、`renderers/gitChangeLabels.js`（文案 / 路径拆分）与 `renderers/codeHighlight.js`（与 markdown 共用的 highlight.js 实例）。细节见 `docs/git-review-pane-design.md` |
| `src/styles/side-dock.css` | dock 外壳、tab 条、启动页共用样式，经 `SideDock.vue` 的 scoped 块 `@import` |
| `src/views/ChatView.vue` | `.chat-shell` flex 行 = `.workspace`（转录 + 输入区，右上角绝对定位的 `DockRail`）+ `SideDock`；把 `workspace.state.current?.id` 作为 `workspaceRootId` 传给 dock；点击工作区即 `blurTerminal()` |
| `src/App.vue` / `components/shell/WindowControls.vue` | 标题栏单个「面板」按钮（lucide `PanelRight`，aria-label `Panel`）→ `dock.toggle()`；拖动区右侧预留 190px |

已删除（被完整替代）：`activities/ActivityStage.vue`、`activities/ActivityFloatingPanel.vue`、`activities/SubagentActivitySection.vue`、`activities/SubagentTaskRow.vue`、`activities/ActivityContentReader.vue`、`composables/useActivityStageConstraint.js`、`composables/useActivityContent.js`、`chat/TerminalPanel.vue`。

## 3. 状态与持久化

```
state = { open, tabs, activeTab, width, terminalOpen, terminalActivated, subagents }
```

- `open`、`tabs`、`activeTab` 都不持久化：每次启动 dock 收起、tab 为空、启动页待命。
- `subagents = { total, running }`：当前会话的子代理任务存在感，由 `SubagentDockPane` 的订阅写入（面板收起时订阅仍热），只驱动快捷栏的角点，不显示数字。
- `activeTab === null` 即显示启动页；`tabs` 是已打开工具的有序 id 列表。
- `width` → `localStorage['selfclaw:dock-width']`，仅在拖拽松手（`commitWidth`）时写入；clamp 300–760px（默认 440），CSS 另加 `max-width: max(300px, min(55%, calc(100% - 520px)))`：对话区至少留 520px，否则输入栏比对话区还宽时溢出部分会被 dock 盖住、看起来像面板遮住了对话。桌面窗口默认 1440×900（`MainWindow.xaml`）。
- `terminalActivated`：终端 pane 首次激活后才挂载，之后只切显隐；`v-if` / `v-show` 都挂在普通容器 `.terminal-slot` 上，而不是异步组件本身（指令穿过 AsyncWrapper 与内层组件两级根节点传播不可靠）。启动时不加载 xterm chunk、不起 ConPTY。
- 工具注册表 `tools = [{ id:'terminal', label:'命令行' }, { id:'subagents', label:'子代理' }, { id:'review', label:'审查' }]`（各带 description），启动页、tab 条与快捷栏都按它渲染。**新增工具**：注册表加一项、`SideDock.vue` 与 `DockRail.vue` 的 `TOOL_ICONS` 加图标并在 `.dock-body` 多渲染一个 pane；启动页与 tab 自动出现，标题栏不需要改。pane 的 `visible` 一律走 §6 的 settle 门。

store API：`open()` / `close()` / `toggle()` / `toggleTool(id)`（快捷栏：打开 / 切到该工具，已是当前 tab 则收起）/ `activate(id)`（没 tab 就新建并切到，顺带打开 dock）/ `showLauncher()` / `closeTab(id)`（当前 tab 被关时退到左邻，没有则右邻，再没有就回启动页）/ `setWidth` / `commitWidth` / `applyTerminalState` / `setSubagentCounts(counts)`。面板与标题栏不放数量角标，快捷栏只有一个存在感角点。

## 4. 终端 ↔ 宿主映射

宿主 `TerminalHostController.IsOpen` 是唯一真值；前端不做乐观更新，只有用户主动关闭动作会先行复位 `terminalOpen`（否则"关后立刻重开"会在宿主回包到达前被旧状态误判为"已开着"而不发 toggle）。

| 规则 | 触发 | 动作 |
|---|---|---|
| T1 | 命令行 tab 被激活（启动页「打开」/ 点 tab / dock 重开时它正是当前 tab）且 `!terminalOpen` | `post toggle-terminal` |
| T2 | `close()`（收起 dock）或 `closeTab('terminal')` 且 `terminalOpen` | `terminalOpen=false` + `post terminal-close` |
| T3 | 收到 `terminal-state.isOpen=true` | `terminalActivated=true`；确保命令行 tab 存在；dock 收起则打开并切到它，开在启动页也切到它；开在其他 tab 时不抢 |
| T4 | 收到 `isOpen=false` | 只更新镜像位（关闭永远由用户发起） |

- **切 tab 不碰宿主**：终端开着时切到子代理，隐藏的 xterm 继续接收 `terminal-output`（`v-show` 复显时 ResizeObserver 触发 `fit`）。只有收起 dock 或关掉命令行 tab 才 `terminal-close`——宿主语义与原底部抽屉一致：会话保留、暂停 flush。
- 关掉命令行 tab 不卸载 xterm（pane 仍挂载、仅隐藏）：再打开时 T1 让宿主恢复输出，滚动缓冲还在。
- `useChatTerminal` 对 `terminal-state` 的订阅带 `replayLast: true`：宿主只在 WebView 导航完成时 `PublishState`，ChatView / SideDock 重挂载（从设置页返回、WebView 重载）靠 hostBridge 的粘性重放拿回最近开合状态。
- Escape 由 `MainWindow.HandlePreviewKeyDown` 统一裁决，优先级 **图片预览浮层 → 终端 → 中断回合**。宿主先于网页拿到按键（网页里的浮层永远等不到那一下），所以浮层开合由前端回写：`image-preview/open` / `image-preview/close` 落到 `WebViewMessageRouter.IsImagePreviewOpen`。浮层开着时宿主只推 `image-preview/dismiss` 让前端关掉它，既不写终端也不 `StopSelectedConversation`；`terminal-focus-change` 语义不变。WebView 导航即整页重载，`OnTranscriptNavigationStarting` 顺手清掉该状态，避免 Escape 之后永远只推 dismiss。

## 5. 子代理 pane

- `SubagentDockPane` 随 `ChatView` 常驻挂载（未打开 / 未激活时 `v-show` 隐藏），`activity-panel` 订阅保持热：`activity-panel/rendered` ACK 节奏与原浮动面板一致，再打开时任务与详情即刻可用。
- 布局：工具条一行 = 任务下拉框（`SubagentTaskSelect.vue`，原生 `<select>`，选项只有名称、常规字重、宽度收窄，`data-status` / `data-phase` 带状态与阶段）+ 框外只放时长（排队中显示「排队 Xs」，开始运行后只显示「运行 Xs」，每秒刷新，完成后定格；不显示阶段文字，它来回跳观感差）+ 翻页（仅多页时）；右侧固定两个键：停止（红色实心方块、无边框，仅选中任务可取消时出现，class 仍为 `task-cancel`）与刷新；下方整块是 `SubagentTaskDetail`。没有任务列表行，也没有并排 / 堆叠切换与容器查询。
- 自动选中：pane 可见且没有选中任务时自动选列表第一项，选择记入 `preferences.view.taskId`，再可见时恢复。翻页后选中任务不在本页时，下拉框补一个「（不在本页）」占位项（`data-missing`），详情照常按 id 加载。
- `useActivityDetail(panel, preferences, visible)` 的 `visible = open && activeTab === 'subagents'`（再经 §6 的 settle 门）：收起 / 切走 / 关 tab 时清详情、保留阅读位置。
- `useActivityPreferences` 的 `view` 只剩 `taskId`（开合与 tab 由 store 接管）；`collapse` / `readings` 不变。
- **读内容链路已删除**：详情里不再有「工具参数 / 工具结果 / 内容」chips 与参数查看窗口（工具块本身已展示参数与结果摘要）。随之删除 `activity-panel/read-content` 协议、wire detail 的 `content` 引用列表、`ActivityPanelPublisher.RequireSelection`、宿主 `SubagentActivityService.ReadContentAsync`、Core 的 `SubagentActivityContent.Read` 与 `SubagentContentQuery` / `SubagentContentPage`。旧客户端再发 `read-content` 只会收到 `unsupported-activity-operation`。`content-changed` 仍是详情版本不匹配的错误码，保留。
- 空态：无会话显示「开始一个会话后，这里会显示子代理活动」；有会话无任务显示「暂无子代理活动」。
- AGENTS 不变量 10 保持：dock 在 `.workspace` 之外，转录与输入区不接触子代理数据。

## 6. 布局与几何

- `ChatView`：`.chat-shell { display:flex }`，`.workspace { flex:1; min-width:0; grid-template-rows: minmax(0,1fr) auto; grid-template-columns: minmax(0,1fr) }`（终端行与 `terminal-open` 规则已删），`SideDock` 为右侧 flex 项。列必须显式 `minmax(0,1fr)`：隐式 auto 列会按输入栏状态条的 min-content（约 600px，不可换行）撑得比工作区宽，转录、输入栏和消息导航锚点会整体溢出到 dock 下面；`tests/e2e/dock.spec.js` 的拖宽用例把"拖到上限后两列仍在 dock 左侧"钉住。
- 收起：`width:0` + `visibility:hidden`（延迟到 240ms 宽度动画结束），内容不可聚焦；打开时可见性立即恢复。
- 开合的宽度过渡期间，三个 pane 的 `visible` prop 延后到 `transitionend`（300ms 兜底）才置真：详情滚动位置恢复、xterm fit 与审查 pane 的「变为可见即刷新」只在稳定几何上发生，否则会被随后的回流带偏；`v-show` 的 display 切换不等。
- 拖宽把手在 dock 左缘（`left:-3px; width:7px`），`setPointerCapture` 捕获，拖动中加 `.resizing` 关掉宽度 transition。
- `--chat-content-width` / `--chat-inline-inset` 契约不变：dock 打开时转录列与输入框都按 `min(100%, …)` 同步收窄，`chatLayout.test.js` 与 `e2e/layout.spec.js` 不受影响。

## 7. 测试

- 单测：`tests/windowControls.test.js`（单个面板按钮）、`tests/dockRail.test.js`（快捷栏：每工具一键、打开 / 切换 / 再点收起、存在感角点）、`tests/sideDock.test.js`（store：启动页 / tab 打开关闭 / 左邻回退 / T1–T4 / 宽度 clamp 与持久化；SideDock 接线：启动页、可关闭 tab、「+」、宿主推送、拖宽、审查 tab 打开 / 关闭与 `workspaceRootId` 透传，三个 pane 用渲染函数桩）、`tests/subagentDockPane.test.js`（下拉框选任务、框外阶段与时长、存在感写回 store、自动选中与隐藏时不选、跨页占位项、流式 / ACK / 取消 / 阅读位置恢复 / 乱序回包）、`tests/reviewPane.test.js` 与 `tests/unifiedDiff.test.js`（审查 pane 与 patch 解析，见 `docs/git-review-pane-design.md` §7）、`tests/activityLabels.test.js`（时长文案规则）。
- e2e：`tests/e2e/activity.spec.js`（流式 / 隔离 / 阅读位置 / dock 几何 / 命令行 tab / 视口，任务切换走 `selectOption`）、`tests/e2e/dock.spec.js`（面板按钮 → 启动页 → tab；「+」回启动页；关 tab 回退与回启动页；宿主推送拉起命令行 tab；子代理工具条文案与无角标；快捷栏开关与角点；拖宽并跨重载保持）、`tests/e2e/review.spec.js`（审查工具：列表 / 自动选中 / 统一视图 / 作用域切换 / 刷新 / 非仓库空态）。夹具 `tests/fixtures/activityHost.js` 的 `terminal(isOpen)` 推送 `terminal-state`；`toggle-terminal` / `terminal-close` 只记录在 `requests` 里；`git-diff/*` 与可选的工作区选中项也由它应答。
- 宿主侧 `SelfClaw.Tests/.../DesktopActivityWebViewTests.cs`（`SELFCLAW_DESKTOP_SMOKE=1`）通过 `OpenSubagentDockAsync` 先点面板按钮、再在启动页点「子代理」，按 `.task-select option[data-status]` / `[data-phase]` 断言状态与阶段，取消前先用下拉框切到目标任务。

## 8. 已知限制与后续

- 视图往返会销毁 xterm 实例（`ChatView` 卸载），滚动缓冲丢失；宿主不回放历史输出，与原底部抽屉相同。
- 宽屏列宽 token 用 `100vw` 推导，dock 打开时对话列靠 `min(100%)` 封顶；若视觉过挤，可把断点改为容器查询。
