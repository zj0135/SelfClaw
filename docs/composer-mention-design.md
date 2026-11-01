# 输入框快捷命令与行内 chip 设计（`@` 文件 / `/` 指令 / `$` 技能）

状态：已实施（2026-11-01）· 纯前端改动，宿主零改动，schema 仍为 v32。
关系：技能 token 的宿主契约见 [扩展系统设计](direct-extensions-system-design.md) §8.2 / §8.4；输入受理与队列见 [消息队列设计](direct-message-input-design.md)；图片粘贴/拖拽见 [附件设计](input-attachment-design.md)。

## 0. 结论摘要

| 环节 | 实现 |
| --- | --- |
| 输入区 | `<textarea>` 换成 `ComposerEditor.vue`：`contenteditable="plaintext-only"` 的 div，里面是文本节点 + 不可编辑的 chip span |
| 触发 | `@` / `/` / `$` 位于文本节点开头或空白之后；触发符到光标之间的无空白串是 query |
| 数据源 | 文件树复用侧栏的 `workspace-tree/list`（`useWorkspaceTree`）；技能用 `extensions/list-effective-skills`（`useEffectiveSkills`，与「+」菜单共享缓存）；指令目前没有来源，列表只有空态 |
| 发送文本 | 技能 `[/id]`（不变）；文件 `@相对路径`，目录带尾 `/`，含空白或特殊字符时 `@"..."`；指令 `/name` |
| 转录 | `[/id]` 与 `@路径` 都渲染为 `.inline-chip`（纯文字，无图标）；样式在 `styles/inline-chip.css`，输入框 chip 共用 |
| 整块删除 | Chromium 对行内 `contenteditable=false` 元素的退格 / Delete 是原子删除，无需手写逻辑 |

## 1. 文本契约

| 类型 | 触发 | 可用条件 | 输入框 chip | 序列化 | 转录 |
| --- | --- | --- | --- | --- | --- |
| 技能 | `$` | `agentMode === 'direct'` | `BookOpenCheck` + id | `[/id]` | `skill_token` 规则 → `.inline-chip--skill` |
| 文件 / 目录 | `@` | 已选工作目录（`current.id` 且非 `isFallback`），否则弹层只提示 | `File` / `Folder` + 相对路径 | `@src/Foo.cs`、`@src/`、`@"my docs/a.md"` | `file_mention` 规则 → `.inline-chip--file` |
| 指令 | `/` | 始终 | `SquareSlash` + `/name` | `/name` | 暂无规则 |

- **裸路径字符类**在 `renderers/composerText.js`（`barePathPattern`）与 `renderers/markdown.js`（`fileMentionPattern`）两处必须保持一致：排除空白与 `"'<>()[]{}|,;:!?@`。路径含其中任何字符即用 `@"..."` 包裹，转录按同一规则识别回来。
- 转录识别是**保守**的：`@` 必须在文本开头或空白之后；裸路径剔掉尾部 `.`，且必须含 `/` 或 `.`（`@张三`、`@everyone`、`a@b.com` 都不是文件）。误判只影响视觉，不影响发送文本。
- 宿主不解析 `@路径` 与 `/name`；`[/…]` 这一形态由 `SkillTokenParser` 独占。不要为文件或指令引入第二套宿主 token 解析。

## 2. 触发规则（`detectMentionTrigger`）

1. 只看光标所在**文本节点**里光标之前的文字——chip 是独立元素，天然把文本节点切开。
2. 取最后一个空白之后的那一段；首字符是触发符即触发，其余为 query。`foo@bar`、`a@b.com`、`[/x`、`价格100$` 都不触发。
3. query 出现空白、光标离开区间、删掉触发符、编辑器失焦 → 关闭。
4. IME 组字（`compositionstart` ~ `compositionend`）期间不检测，结束后补一次。
5. Esc 关闭后记住 `(anchor 文本节点, start)`：同一位置继续打字不重开；触发消失（trigger 为 null）后清除记忆，所以删掉再重打 `@` 会重开。

## 3. 编辑器（`ComposerEditor.vue`）

`plaintext-only` 在 Chromium / WebView2 的行为已用 msedge 实测（`.scratch/composer-mention/spike-editor.cjs`）：打字只产生文本节点、Enter 与 Shift+Enter 产生 `\n`、退格 / Delete 整块删除 chip、插入后光标能落在尾随空格里。富文本粘贴在 `about:blank` 测不了（无 `ClipboardItem`），按规范 plaintext-only 会降级为纯文本；即使浏览器塞进元素，序列化也只取文字。

- **chip DOM**：`<span class="inline-chip inline-chip--{kind}" contenteditable="false" data-chip-kind data-chip-value data-chip-label title>`，内部用 `render(h(ComposerChip), host)` 挂图标 + 标签（lucide 组件，不手写 SVG）。根上的 `MutationObserver` 在 chip 被移除时 `render(null)` 卸载，撤销放回来的空壳重新挂载。
- **序列化**（`serializeEditorNodes`）：文本节点原样（nbsp → 空格）、chip → token、`<br>` → `\n`、块元素之间补 `\n`（只在回退到 `contenteditable=true` 时会出现）。每次 `input` 序列化并 `update:modelValue`；为空时清掉残留节点让 `:empty::before` 占位符生效。
- **插入**：选择器选中 → 用 chip 替换 `@query` 这段文本；「+」菜单 → 在光标处插入。两者都在 chip 后补一个空格并把光标放进去；chip 紧贴前文（`hello|`、`[chip]|`）时再在前面补一个空格，避免 `hello@a.cs` 这类粘连；最后 `normalize()` 合并 Range 操作留下的空文本节点。
- **选区与焦点**：`focus()` 在部分实现里会把光标重置到元素开头，所以插入前先克隆当前选区、聚焦后再恢复，再以 live range 操作。
- **外观由编辑器自己持有**：`ComposerInput` 是多根节点组件（编辑器 + 弹层），父组件 `ComposerPanel` 的 scoped 样式到不了编辑器根节点。textarea 时代的 `min-height: 64px`、`flex: 1 1 auto`、`border: 0`、`outline: none`、字体行高全部写在 `ComposerEditor.vue` 自己的 scoped 块里；少了它们输入区会塌成一行并露出浏览器默认的 contenteditable 焦点框。`tests/e2e/queue.spec.js` 有对应的计算样式断言。
- 对外：`insertChip(chip)`、`replaceTriggerWithChip(chip, trigger)`、`clear()`、`focus()`；事件 `update:modelValue`、`submit`（Enter 且无修饰键、非组字、弹层未开）、`trigger`、`navigate`（弹层打开时按键先交出去，被 `preventDefault` 的不再处理）、`paste`（图片逻辑仍在 ComposerPanel）。
- 不做文本 → chip 的反向解析：父组件只会清空（发送成功后）。

## 4. 弹层（`ComposerInput.vue` = 编辑器 + `MentionPopover.vue` + `useComposerMention`）

- `useComposerMention` 只管状态机（kind / query / anchor / Esc 记忆 / 技能闸门）；`MentionPopover` 管定位、键盘与活动项；三类列表各自拿数据并通过 `update:items` 把可选项交给弹层。
- 定位：绝对定位固定挂在 `.composer-shell` 正上方（`bottom: calc(100% + 8px)`），`left: 0; right: 0` 与输入框同宽同轴，不跟随光标；`max-height: min(340px, 48vh)`。
- 键盘：↑↓ 移动；Enter / Tab 选择（弹层打开时 Enter **永远不发送**，没有可选项也只是吞掉）；文件树里 Enter 在目录上是展开/折叠、Tab 是引用目录、→ 展开、← 折叠；Esc 关闭并记忆位置。左右键在非目录项上不消费，光标移走后弹层自然关闭。
- 鼠标：弹层根 `@mousedown.prevent`，点击不夺编辑器焦点；目录行点击展开，文件行点击选择，目录行 hover / 活动时尾部出现「引用」按钮。
- 列表状态：文件——无工作目录提示 / 读取中 / 根错误 / 空目录 / 「每层最多显示 250 项」/ 行级错误 / 扁平过滤无结果；技能——读取中 / 错误 / 无可用技能 / 无匹配；指令——「暂无可用指令」/ 无匹配。
- `@` 的过滤只在**已加载过**的条目里做扁平匹配（`useWorkspaceTree.allEntries`），不做宿主搜索；提示文案说明「展开更多目录后再试」。

## 5. 转录与样式

- `renderers/markdown.js`：`skill_token` 输出改为 `.inline-chip.inline-chip--skill`；新增 `file_mention`（仅 `env.context === 'user'`），输出 `.inline-chip.inline-chip--file`，`title` 为全路径。markdown-it 的 `text` 规则把 `@`、`[`、`$` 当终止符，自定义规则能在这些位置被命中。
- `styles/inline-chip.css` 全局引入（`main.js`，紧随 `markdown.css`）：转录 HTML 串与编辑器程序插入的 DOM 共用。技能主题色，文件中性色；字号相对父级（`0.92em`），输入框与紧凑面板都能缩放。
- **垂直对齐**：`vertical-align: middle` + `top: -1px`，不设 `min-height`（高度由 `line-height: 1.45` 撑出，不把正文行撑高）。Edge 实测（`.scratch/composer-mention/align-spike.cjs`）chip 中心与同行文本中心差 0.2px，带图标 / 无图标 / 拉丁文一致；原先的 `vertical-align: -4px` 低 2.5～4.7px 且 26px 高度会撑高行。
- **用户消息换行**：`renderMarkdown` 在 `context === 'user'` 时 `breaks: true`，Shift+Enter 产生的单个 `\n` 渲染为 `<br>`；助手正文仍是 CommonMark 软换行。
- 转录 chip 没有图标：它由 markdown HTML 串生成，无法挂 lucide 组件，且禁止手写 SVG；图标只在输入框 chip 上。
- 删除了 `App.vue` 的 `.message-skill-chip` / `.composer-inline-skill*` 与 `markdown.css` 的 `.message-skill-chip`；删除了无人引用的 `SkillPicker.vue`、`ChatView.insertPrompt`、`ComposerPanel.insertText/getShellEl`。

## 6. 不变量与已知限制

- 发送文本就是模型看到的文本：技能 token 由宿主剥离并注入指令（既有契约），文件 / 指令不经宿主处理。
- `useEffectiveSkills` 的缓存是模块级、按 `${agentId}:${capabilityRevision}` 命中，并发只发一次请求；`capabilityRevision` 由宿主在扩展状态变化时递增。
- 程序性 DOM 插入会打断浏览器原生撤销栈：Ctrl+Z 跨 chip 不保证。
- `useWorkspaceTree.open()` 会清空缓存，每次打开 `@` 重新拉根层一次。
- 队列卡片的编辑框仍是普通 textarea，里面显示的是 token 原文（`[/id]`、`@path`）。
- 指令没有来源：`MentionCommandList` 的 `commands` prop 为空时只有空态；有来源后由上层传入，不要为此造注册表。

## 7. 测试

- 单测（`npm test`）：`tests/renderers/composerText.test.js`（触发检测、token 格式、序列化）、`tests/renderers/markdown.test.js`（转录 chip 规则与误判边界）、`tests/composerMention.test.js`（`$` / `@` 触发 → 弹层 → 键盘选择 → chip 插入、Esc 记忆、程序插入补空格、clear）。`tests/helpers/composerEditor.js` 提供 `typeIntoEditor`，替代对 `textarea` 的 `setValue`。
- e2e（`npm run test:e2e`）：`tests/e2e/queue.spec.js` 改为 `.composer-editor` + `toHaveText`，在 msedge 里覆盖 fill / Enter 发送 / ACK 后清空。
- 真机手工项（`dotnet run`）：Direct 下 `$` 选技能后发送能显式激活；CLI 下 `$` 原样；`@` 树形展开 / 选择 / Tab 引用目录 / 输入过滤，发送后转录 chip 且模型能按路径读文件；无工作目录提示；`/` 空态；退格整块删除、Shift+Enter 换行、弹层打开时 Enter 只选择；中文 IME 不误触；粘贴图片进附件、富文本只留纯文本；忙碌入队与 steer 路径下带 chip 的消息正常；明暗主题下 chip 一致。
