# 窗口顶栏 / 任务栏标题设计

> 适用范围：把「当前选中的会话」显示在窗口顶栏，并同步到 Windows 窗口标题（任务栏、Alt+Tab）。纯展示，无新 Wire 协议、无 schema 变更。

## 1. 结论

1. **两处标题，两套空态**，刻意不一致：
   - **网页顶栏**（`components/shell/WindowTitle.vue`）：只在正在看某条会话时出现；新对话、设置 / 生图 / 翻译 / 自动化等独立视图**整块不渲染**，不留空壳。
   - **Windows 窗口标题**（`MainWindowViewModel.WindowTitle`）：没有会话时回落 `SelfClaw`。任务栏必须有恒定名字，空标题会被系统显示成 exe 文件名。
2. **取数只有一条路**：标题来自宿主推下来的导航列表（`payload.conversations`），与侧栏同一个数组、同一个 `title` 字段。自动总结（`TitleChanged` → `ReloadConversationsAsync`）与手动重命名都只改这个字段，顶栏因此自动跟随，不需要第二条推送通道。
3. **会话 id 是唯一判据**：`selectedConversationId` 为 `null`（新对话）或当前是独立视图时一律留空，绝不回退到「列表第一条」——那会在切换途中闪出别人的标题。
4. **选中会话不在列表里时留空**（切换途中的过渡帧）：不能用 id 兜底当标题。
5. **左对齐、单行省略**：标题贴着侧栏边界起排，右边界让开标题栏按钮区（1 个面板按钮 + 3 个窗口按钮，共 200px），长标题用 `text-overflow: ellipsis` 收尾，完整值走原生 `title` 提示。

## 2. 链路

| 位置 | 职责 |
|---|---|
| `SelfClaw.Desktop/Services/Transcript/Views/TranscriptRenderState.cs` | `SelectedConversationId` + `Conversations`（既有字段，本特性没有新增） |
| `SelfClaw.Desktop/ViewModels/MainWindowViewModel.cs` | `WindowTitle` 派生属性：`"{会话标题} - SelfClaw"`，无会话回落 `SelfClaw` |
| `SelfClaw.Desktop/MainWindow.xaml.cs` | `OnViewModelPropertyChanged` 在空属性名（`PublishShell` 的整体失效广播）与 `SelectedConversation` 上同步 `Window.Title` |
| `src/composables/useConversationNavigation.js` | `activeConversationTitle`：独立视图 / 无选中 / 列表未命中 → `''` |
| `src/components/shell/WindowTitle.vue` | 纯展示层，`pointer-events: none` |
| `src/App.vue` | `.main-header` 内与 `.window-drag-region` / `WindowControls` 同层 |

## 3. 陷阱

1. **`pointer-events: none` 不能省**：标题横在窗口拖动区上面，一旦吃鼠标事件，标题栏中间那一段就拖不动窗口、双击也不最大化。拖动仍然只有 `.window-drag-region` 一份实现，不要为标题再绑一套 `pointerdown`。
2. **最大化要跟着下移 4px**：`SingleBorderWindow` 最大化时 DWM 把窗口向上顶出屏幕约 8px，可见高度变成 38px。`WindowControls` 已经用 `top: 10px` 补偿，标题必须同样 `translateY(4px)`，否则两者不在一条中线上。
3. **`WindowTitle` 只认 `SelectedConversation`，不认 `_allConversations`**：`ApplyAdmittedConversation` 里列表记录会被刷成 `DateTimeOffset.UtcNow` 并插到最前，用列表首条当标题会在发送消息的瞬间跳到错误的会话上。
4. **空属性名监听不能只留 `SelectedConversation`**：自动总结、重命名走的是 `ReloadConversationsAsync` → `ApplyConversationFilter` → `SetProperty(SelectedConversation)`，但新建会话（`SelectConversationCoreAsync(null)`）与 `PublishShell` 只发空属性名。只监听具名属性会让任务栏停在旧标题上。
5. **不要在 `MainWindow.xaml` 里用 `Title` 绑定**：`Title` 是普通 CLR 属性，`DataContext` 变了也不会自动求值；宿主必须显式赋值（`OnViewModelPropertyChanged`），`MainWindow.xaml` 里的 `Title="SelfClaw"` 只是设计器 / 首帧兜底。

## 4. 验收

- 新建对话 → 顶栏无标题，任务栏显示 `SelfClaw`。
- 侧栏点开一条会话 → 顶栏左对齐显示该会话标题；任务栏显示 `{标题} - SelfClaw`。
- 首条消息触发自动总结 → 顶栏与任务栏同时变成总结标题，不需要重新点选会话。
- 手动重命名 → 顶栏与任务栏立即跟随。
- 切到设置 / 生图 / 翻译 / 自动化 → 顶栏标题消失，任务栏回到 `SelfClaw`。
- 长标题（>60 字）→ 顶栏单行省略，鼠标悬停出完整标题。
- 标题栏任意位置（含标题文字上）都能拖动窗口、双击最大化。
- 窗口最大化 → 标题与右侧窗口按钮仍在同一条水平中线上。
