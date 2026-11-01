# 右侧面板「审查」工具（工作区 Git diff）设计

> 适用范围：聊天视图右侧 dock 的 `review` 工具——展示当前 workspace 的 Git 改动（未暂存 / 已暂存）与逐文件统一视图 diff。只读：不做 stage / unstage / discard。dock 外壳、tab 与启动页的约定见 `docs/side-dock-design.md`。

## 1. 结论

1. **布局**：工具条 = 作用域下拉（「未暂存 (n)」/「已暂存 (n)」）+ 分支名 + 右侧一组图标键（上一个 / 下一个文件、刷新、折叠 / 展开文件列表）；主体左 = diff（统一视图：单列行号、+/− 底色、hunk 分隔条），右 = 改动文件导航列（宽 `clamp(168px, 38%, 240px)`，可折叠，偏好存 `localStorage['selfclaw:review-files-collapsed']`，由 `composables/useReviewLayout.js` 持有）。
2. **作用域**：以当前选中的 workspace root 为 git 工作目录，`status … -- .` + `diff --relative … -- .`，只列 workspace 子树内的改动，路径相对 workspace；root 即仓库根时等价全仓库。
3. **未暂存** = 工作树 vs 索引，含未跟踪文件（`?`）；**已暂存** = 索引 vs HEAD；冲突文件（`u`）两边都列、标「冲突」。
4. **请求模型**：纯 request / response，无宿主推送。一次刷新取回两个作用域（status v2 一趟给出 X / Y 两侧），切换下拉不再请求；单文件 patch 按需请求。前端 `requestLatest` latest-wins，旧回包静默丢弃。
5. **刷新时机**：tab 变为可见、手动刷新、可见时 workspace 根变化；隐藏时不发请求（再可见时整体刷新）。不随回合结束自动刷新。
6. **没选工作目录**（`workspaceRootId` 为空）不向宿主发请求，直接显示空态。

## 2. 组成

| 层 | 文件 | 职责 |
|---|---|---|
| Core | `Models/Git/GitChangeScope.cs`、`GitChangeKind.cs`、`GitChangedFile.cs`、`GitChangeSet.cs`、`GitWorkspaceChanges.cs`、`GitFileDiff.cs`；`Interfaces/Git/IGitDiffQuery.cs` | 契约与 DTO |
| Infrastructure | `Git/GitDiffService.cs` | `IGitDiffQuery`：探测仓库、列表、单文件 patch、未跟踪文件合成、截断、路径校验 |
| | `Git/GitStatusPorcelainParser.cs` | `status --porcelain=v2 -z` 与 `diff --numstat -z` 的纯解析（可不搭仓库直接单测） |
| | `Git/GitDiffLimits.cs` | `MaximumFiles = 500`、`MaximumPatchCharacters = 512 KiB` |
| Desktop | `Services/Git/GitDiffBridge.cs` | `git-diff/changes` / `git-diff/file` 回包；根取自 `IWorkspaceSelectionController.SelectedWorkspaceRoot` |
| | `Services/WebView/WebViewMessageRouter.cs` | `git-diff/` 前缀分支，**必须在 switch 之前**：`git-` 的 case 会把它吞给 `GitWorkspaceBridge` 并报 Unsupported |
| TranscriptVue | `composables/useGitDiff.js` | 数据层：两个作用域的列表、选中文件、patch、代际计数 |
| | `components/dock/ReviewPane.vue` | 编排：工具条、空态分支、左右两栏 |
| | `components/dock/review/ReviewFileList.vue` | 文件导航列：字母徽标、文件名 / 目录、`+n −m`、↑/↓ 键 |
| | `components/dock/review/ReviewDiffView.vue` | 文件头 + 统一视图表格 + 截断 / 二进制 / 纯重命名提示 |
| | `renderers/unifiedDiff.js`、`renderers/gitChangeLabels.js`、`renderers/codeHighlight.js` | patch 解析；种类文案 / 路径拆分；与 markdown 共用的 highlight.js 实例（按路径猜语言、整段高亮按行切） |

## 3. 协议

| 请求 | 入参 | 回包 |
|---|---|---|
| `git-diff/changes` | 无 | `{ type, requestId, ok:true, workspaceRootId, workspacePath, repositoryRootPath, branchName, isGitAvailable, isRepository, unstaged:{ files, atFileLimit }, staged:{ … } }` |
| `git-diff/file` | `scope`（`unstaged` / `staged`）、`path`、`oldPath?`、`kind` | `{ type, requestId, ok:true, workspaceRootId, scope, path, oldPath, kind, isBinary, patch, truncated, addedLines, removedLines }` |

- `files[]` 项：`{ path, oldPath, kind, addedLines, removedLines, isBinary }`；`kind` ∈ `modified | added | deleted | renamed | copied | untracked | conflicted`；`path` 相对 workspace、`/` 分隔；未跟踪文件没有行数（`null`）。
- **非仓库 / git 不可用不是错误**：回包仍 `ok:true` 且不带 `error`（hostBridge 见 `error` 即 reject），前端靠 `isRepository` / `isGitAvailable` 渲染空态。真正失败（未选工作目录、git 进程失败、非法路径）才 `ok:false` + `error`，前端显示在 `.pane-error`。
- `kind` 由前端从列表原样回传：宿主据此决定未跟踪文件走合成而不是 `git diff`；缺省按 `modified`，未知值不报错。

## 4. 宿主实现

- 探测：`rev-parse --show-toplevel --show-prefix`（两行；HEAD 未出生的全新仓库也成功——若把 `--abbrev-ref HEAD` 并进同一条命令，未出生 HEAD 会让整条命令 exit 128）。失败 → `isRepository=false`；`git.exe` 启动失败（`GitCommandRunner` 抛 `InvalidOperationException`）→ `isGitAvailable=false`。分支名另走 `symbolic-ref --quiet --short HEAD`，失败只意味着没有分支名。
- 列表：`-c core.quotepath=false status --porcelain=v2 -z --untracked-files=all -- .` + 两次 `diff --numstat -z --relative [--cached] -- .`，与分支探测四路并发（`Task.WhenAll`）。一次刷新 = 5 个 git 进程。
  - porcelain v2 的路径永远相对仓库根：去掉 `show-prefix` 前缀，前缀之外的记录丢弃。`1` 记录按 X / Y 分到已暂存 / 未暂存；`2`（重命名 / 复制）的旧路径是紧随其后的独立 NUL 字段，只挂在索引侧；`?` → 未暂存 / 未跟踪；`u` → 两侧「冲突」。
  - numstat `-z`：普通行 `add\tdel\tpath\0`；重命名行 `add\tdel\t\0old\0new\0`（以空路径结尾，后两段是旧、新路径）；`-\t-` 为二进制。按新路径合并到列表项。
  - 列表按路径 ordinal 排序，超过 `MaximumFiles` 截断并置 `atFileLimit`。
- 单文件（tracked）：`-c core.quotepath=false diff --no-color --no-ext-diff --find-renames --unified=3 --relative [--cached] -- <path> [<oldPath>]`；exit 0 / 1 都算成功。patch 含 `Binary files … differ` / `GIT binary patch` → `isBinary`。行数先在完整 patch 上统计，再按 `MaximumPatchCharacters` 在行边界截断（`truncated=true`，计数仍是完整值）。
- 单文件（untracked）：不走 `git diff --no-index`（exit code 与 `/dev/null` 的平台差异），在 C# 合成：读取 ≤ `WorkspaceFileAccess.MaxTextFileBytes`（更大则只取前段并 `truncated`），含 NUL 字节 → `isBinary` 且 `patch=null`；否则生成 `--- /dev/null` / `+++ b/<path>` / 单个 `@@ -0,0 +1,N @@` hunk（CRLF 归一为 LF）。
- 路径安全：`path` / `oldPath` 必须相对、无 `..` / `.` / 空段、不以 `/` 开头、不含 `:`；不满足抛 `ArgumentException`（桥回 `ok:false`）。合成路径再经 `WorkspaceFileAccess.ResolvePath` 做一次 root 内校验。

## 5. 前端状态与交互

- `useGitDiff(workspaceRootId, visible, bridge)`：`state = { scope, loading, error, loadedOnce, workspacePath, repositoryRootPath, branchName, isGitAvailable, isRepository, unstaged, staged, selectedPath, file, fileLoading, fileError }`，`files` = 当前作用域列表，`hasPrevious` / `hasNext`；`refresh()` / `setScope()` / `selectFile(file)` / `selectNeighbor(±1)`（工具条按钮与文件列表的 ↑/↓ 共用）。
- 自动选中：刷新或切换作用域后，选中路径仍在当前列表就保留（内容可能变了，重新拉 patch），否则选第一项；没有文件就清空详情。
- 代际计数：列表与文件各一套；回包只在代际匹配时应用；`SupersededError` 静默。
- `visible` 由 `SideDock` 经 settle 门给出（宽度动画结束后才为 true），`false → true` 即刷新。`workspaceRootId` 变化：清选中；可见则刷新。
- 文件导航列：`role="listbox"`，↑/↓ 切换；行 = 字母徽标（M / A / D / R / C / U / !，配色 `--warning` / `--success` / `--danger` / `--accent`）+ 文件名 + 目录 + `+n −m`（二进制显示「二进制」）；`atFileLimit` 时提示「仅显示前 500 个文件」。
- diff 视图：文件头（种类标签、路径、重命名 `old → new`、`+n −m`）；代码面**跟随主题**：`tokens.css` 的 `--diff-bg` / `--diff-fg` / `--diff-syntax-*`，浅色是白底深字加一组白底上对比度足够的语法色，深色指回 markdown 代码块的 `--code-surface` / `--syntax-*`（不要换回 `--code-bg`，那组 token 刻意两个主题都深底）；字号与字族仍走 `--code-fs` / `--font-code`。表格行 = 单列行号（删除行给旧行号，其余给新行号；粘在左侧）| 文本（`white-space: pre`、横向滚动）；新增 / 删除行整行着色并在行号左缘加 3px 色条，没有 +/− 标记列。`App.vue` 的全局 `table` / `td` 规则（边框、10px 圆角、单元格线）在 `.diff-table` 里全部归零，表内不画任何线。hunk 前有被省略的行（不是从第 1 行开始或不是第一个 hunk）时插一条「···」分隔行（只用底色区分），右侧放 git 附带的函数 / 节标题。
- 语法高亮：`renderers/codeHighlight.js` 是与 markdown 共用的 highlight.js 实例。按扩展名挑语言，`.vue` 按 hunk 内容在 javascript / xml / css 之间投票；一个 hunk 的行拼成一段高亮一次再按行切回（切点闭合 / 重开 `<span>`，块注释、模板字符串跨行不丢），超过 4000 行只显示纯文本。冲突文件的 combined diff 按前两列任一 + / − 降级；`\ No newline at end of file` 以行号单元格的 title 表达；截断提示「内容过长，已截断显示」。

## 6. 空态文案

| 条件 | 标题 / 说明 |
|---|---|
| `workspaceRootId` 为空 | 尚未选择工作目录 / 在输入框下方选择一个目录后，这里会展示它的 Git 改动。 |
| `isGitAvailable=false` | 未找到 git.exe / 请安装 Git 并确保 git.exe 在 PATH 中，然后刷新。 |
| `isRepository=false` | 当前 workspace 不在 Git 仓库中 / 打开一个 Git 仓库目录后，这里会展示当前 workspace 作用域内的改动。 |
| 当前作用域无文件 | 没有未暂存的改动 · 没有已暂存的改动 / 工作区很干净。 |
| 未选文件 | 选择一个文件查看改动 |
| 二进制 / 纯重命名 | 二进制文件，不显示内容 / 仅重命名，内容没有改动 |

## 7. 测试

- 宿主：`SelfClaw.Tests/Infrastructure/Git/GitStatusPorcelainParserTests.cs`（记录分派、旧路径字段消费、前缀剥离、numstat 重命名 / 二进制）、`GitDiffServiceTests.cs`（真实临时仓库：列表与行数、已暂存重命名、子目录作用域、未跟踪合成与二进制、截断、未出生 HEAD、非仓库、非法路径）、`SelfClaw.Tests/Desktop/Services/Git/GitDiffBridgeTests.cs`（回包形状、非仓库不带 `error`、参数校验、失败包）、`WebViewMessageRouterTests`（`git-diff/` 先于 `git-` 兜底）。
- 前端：`tests/unifiedDiff.test.js`、`tests/reviewPane.test.js`（可见即刷新、作用域切换不请求列表、自动选中、各空态、错误、截断、刷新保留选中、workspace 变化、latest-wins）、`tests/sideDock.test.js` / `tests/dockRail.test.js`（第三个工具）、`tests/e2e/review.spec.js`。夹具 `tests/fixtures/activityHost.js` 应答 `git-diff/*`，开关 `gitRepository(false)` / `gitEmpty(true)` / `gitTruncated(true)` / `gitError(msg)`；`useWorkspace()` 让 `get-workspace-selection` 回一个选中根（默认关闭，其他用例依赖「没有工作目录」的回包）。

## 8. 已知限制

- `--untracked-files=all` 在未跟踪文件极多的仓库上偏慢；列表有 500 上限与加载态，必要时可降为 `normal`。
- 重命名一端在 workspace 子树之外时，git 会退化为删除 + 新增。
- 冲突文件的 combined diff 只做降级显示，不保证逐字对齐；仅重命名 / 模式变更没有 hunk，显示提示而非空白。
- 没有分栏对照（dock 宽度 300–640px 下统一视图更可读）；语法高亮是按 hunk 整段跑 hljs，hunk 起点落在块注释中间时该 hunk 的前几行会按普通代码着色；不随回合结束自动刷新。
