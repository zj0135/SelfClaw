# 侧栏「归档」分组设计

> 适用范围：`SelfClaw.TranscriptVue` 左侧会话区的分组结构。宿主（WPF）只增加一个字段，不迁移 schema（仍 v32）。

## 1. 结论

1. **三个分组互斥**，判定只看「最后一次对话时间」：
   - `项目`：有工作区 且 未归档；`对话`：无工作区 且 未归档；`归档`：已归档（有工作区的按工作区分文件夹，无工作区的进伪文件夹 `未绑定工作区`）。
2. **口径是滚动 168 小时**：`now - updatedAtUtc > 7d` 才归档，恰好 7 天整点不归档。时间缺失 / 不可解析 / 未来时间一律**不归档**（留在原位）：按错时间把会话藏起来，用户既找不到也删不掉。
3. **时间戳来自宿主原始值，不由宿主判定归档**。`TranscriptConversationItem.UpdatedAtUtc`（ISO `O` 格式）随导航项下发；`Timestamp` 是给人看的本地格式，不参与计算。宿主给布尔值会在跨零点时不自动迁移，还得再加推送定时器。
4. **没有手动归档 / 取消归档**：归档纯由时间推导，会话再发一条消息（`updated_at_utc` 前进）就自己回到 `项目` / `对话`。手动归档需要给 `conversations` 加列 = schema 版本变更，没做。
5. **归档不是回收站**：不自动删除任何会话，删除仍走会话右键。

## 2. 链路

| 位置 | 职责 |
|---|---|
| `SelfClaw.Desktop/Services/Transcript/Views/TranscriptConversationItem.cs` | 末尾追加 `string? UpdatedAtUtc`（位置参数末尾，既有调用点不变） |
| `SelfClaw.Desktop/Services/Transcript/TranscriptProjection.cs` | `BuildConversationItem` 填 `conversation.UpdatedAtUtc.ToString("O")` |
| `SelfClaw.Desktop/ViewModels/MainWindowViewModel.cs` | `ApplyAdmittedConversation` 把记录刷成 `DateTimeOffset.UtcNow`（见 §3） |
| `src/renderers/sidebarGroups.js` | `isArchived` / `buildSidebarGroups`：纯函数，把会话列表切成 `{ projects, conversations, archived }` |
| `src/composables/useConversationNavigation.js` | 三块分组 → `navItems`；`now` 由 15 分钟 tick + `focus` / `visibilitychange` 推进 |
| `src/components/sidebar/AppSidebar.vue` | 按 `layout: 'folders' \| 'list'` 分支渲染；`canCreate` 控制加号；`emptyText` 控制空态；归档文件夹右键菜单收敛 |
| `src/composables/useSidebarSearch.js` | 无需改动：`filterChildren` 已递归所有 `type: 'folder'` |

## 3. 陷阱

1. **归档文件夹 id 必须单独前缀**（`archive-workspace-*`，项目是 `workspace-*`）。`expandedFolders`、`useSidebarPagination` 的 `limits`、`v-for :key` 都以文件夹 id 为键：同名会让两份折叠状态和分页互相串台。
2. **`ApplyAdmittedConversation` 必须刷新时间戳**。`_allConversations` 只在初始化 / 删除会话 / 重命名 / 删除工作区后从库重载；向已存在会话再发消息时带的是入库时的旧 `updated_at`。不刷新的话，用户在归档里点开会话发一条，它会继续留在归档里直到下次重载 —— 排序略滞后可以忍，归档状态错不行。
3. **宿主不 ack 时只有第一条 payload 会出去**（`TranscriptDelivery.Publish` 在 `_inFlight` 非空时只更新 `_latest`）。测试里读导航数据要给每条消息回 ack，并且倒着找最近一条带 `conversations` 的上报：补丁只在导航真的变化时才带这个字段（`WhenWritingNull` 会整个省略）。
4. **归档分组默认折叠**（`expandedGroups` 初值只含 `projects` / `conversations`）；检索时 `isGroupExpanded` 会强制展开，命中归档会话能直接看到。
5. **文件夹节点没有「重命名」**：分组名来自工作区 / Git 仓库，不是用户起的名字；这个入口此前一直是死代码（点了什么都不做），已删除。归档里的伪文件夹没有任何可用项，因此不弹右键菜单。
6. **分组标题右端只有一个槽位**（`.group-tail`，计数与加号 grid 叠放，hover 时数字淡出、加号浮现）。计数与加号若并排，没有加号的「归档」会把数字顶到最右，三组数字就不在同一条竖线上——增删分组时不要退回并排写法。
7. **加号的热区是整格 `.group-tail`，不是图标本身**（`onGroupTail` 拦下事件后发 `add-<groupId>`；槽位内的数字与图标一律 `pointer-events: none`）。理由实测：图标 13px 圆角，且计数淡出时透明度变化会改变绘制顺序，鼠标落在数字或图标缝隙上会穿透到 `group-head`，把「新建对话」变成折叠分组。`canCreate: false` 的分组不拦事件，点击照旧折叠。
8. **槽位内元素不吃鼠标事件，所以 `:hover` 也必须挂在槽位上**（`.group-head.has-create .group-tail:hover .group-add`，含 `--sb-accent-soft` 底色与 `--sb-accent` 图标色）。写回 `.group-add:hover` 不会报错，只会静默失效——加号悬停变成没有任何反馈。

## 4. 验收

- 把某会话 `updated_at_utc` 改到 8 天前 → 重启 → 它出现在归档（默认折叠）、原分组里消失。
- 归档里打开会话并发一条消息 → 侧栏立刻把它移回 `项目` / `对话`。
- 无工作区的旧会话 → 归档的 `未绑定工作区` 文件夹里；归档里的文件夹顺序与组内会话顺序都按时间降序。
- 归档文件夹右键只有「工作目录 / 在资源管理器打开」；有工作区根的才有这两项。
- 搜索命中归档会话 → 归档自动展开；清空搜索回到折叠态。
