# SelfClaw 前端 Markdown 渲染重构方案

## 1. 结论

使用 Vue 前端的 `markdown-it` 渲染 Markdown，并用 `highlight.js` 处理代码块，方案合理，建议实施。

它能把“表现层 Markdown 解析、代码高亮和样式”放回真正消费内容的 WebView，解决后端 Markdig 输出与当前 Vue 样式不一致的问题，也能降低 WebView 消息中的 HTML 体积。这个改造不是简单替换一个包：需要先把 WebView 合同从“HTML 分段”改成“Markdown 分段”，并保留后端已有的思考块/工具锚点切分。

迁移完成后的最终状态不包含任何后端 Markdown→HTML 路径：不保留 `MarkdownHtmlRenderer`、`TranscriptRenderSegment.Html`、HTML 双写或前端 HTML fallback。

推荐的最终职责如下：

```text
数据库 / Agent 输出
    -> MessageRecord.MarkdownContent
    -> AssistantMessageSegmenter（后端：思考块、工具锚点、流式状态）
    -> TranscriptRenderSegment.Markdown（WebView 合同）
    -> Vue markdown-it + highlight.js + 安全清理
    -> BodySegment / ThinkingBlock 的 v-html
```

注意：前端方案仍然会在最后一步使用 `v-html`，但 HTML 只来自前端内部的受控渲染器，不再信任后端传来的 HTML，也不允许 Markdown 原文中的任意 HTML 直接进入 DOM。

## 2. 当前实现盘点

### 2.1 后端

- `SelfClaw.Infrastructure/Tools/Transcript/MarkdownHtmlRenderer.cs`
  - 使用 Markdig `UseAdvancedExtensions()`。
  - 使用 `DisableHtml()`，因此当前后端会阻止 Markdown 原文中的 HTML。
- `SelfClaw.Desktop/Services/Transcript/TranscriptProjection.cs`
  - 调用 `AssistantMessageSegmenter.Split()`。
  - 对思考块和正文调用 `MarkdownHtmlRenderer.ToHtml()`。
  - `TranscriptRenderSegment` 的 `Html` 字段进入 WebView。
  - 消息缓存保存 `(Kind, Markdown, Html)`，避免流式状态未变化时重复解析。
  - 失败/取消消息通过字符串拼接 HTML 错误段。
- `AssistantMessageSegmenter` 负责的是传输语义，不是视觉渲染：
  - 识别 `<think>...</think>` 和内部 thinking marker。
  - 识别 `<!--selfclaw:tool:{id}-->`。
  - 合并流式 Markdown、恢复工具锚点、标记 pending thinking。
  - 这些逻辑应继续留在后端，否则会把持久化格式和工具定位规则复制到前端。
- `TranscriptToolRunPresenter` 已经把工具调用转换成结构化字段（标题、状态、详情、耗时），工具详情不是 Markdown 渲染范围。

### 2.2 WebView 合同

- `WebViewHostChannel` 发送 `replaceState` 和 `patchState`；patch 只传递变化的 `TranscriptRenderItem`。
- `TranscriptRenderSegment` 当前字段为 `Kind、Html、IsPending` 及工具字段。
- 数据库存储的是原始 `MessageRecord.MarkdownContent`，没有数据库迁移需求。

### 2.3 Vue

- `BodySegment.vue`：当前将 `segment.html` 交给 `v-html`；迁移后改为对 `segment.markdown` 调用前端 renderer，再将 renderer 结果交给 `v-html`。
- `ThinkingBlock.vue`：当前将 `segment.html` 交给 `v-html`；迁移后改为对 `segment.markdown` 调用前端 renderer，并保留 `useDeferredHtml` 的延迟思考流式更新策略。
- `renderers/transcript.js`：把分段编排为正文、thinking、tool、tool-group；用户消息的 `[/skill]` token 在 HTML 生成后再做 DOM 文本节点替换。
- `App.vue` 已有正文、表格、引用、`pre/code`、图片和 thinking 的基础样式，但没有 highlight.js token 样式。
- `previewImage.js` 依赖 `.body.body-segment img` 和 `.thinking-markdown img` 的事件委托。

## 3. 目标合同和模块边界

### 3.1 替换 `Html` 为 `Markdown`

将 `TranscriptRenderSegment.Html` 替换为 `TranscriptRenderSegment.Markdown`：

```csharp
public sealed record TranscriptRenderSegment(
    string Kind,
    string Markdown,
    bool IsPending,
    string? Text = null,
    string? Status = null,
    string? SegmentId = null,
    string? DurationText = null,
    string? DetailTitle = null,
    string? DetailText = null,
    string? ToolName = null,
    string? SourceKind = null,
    string? SourceId = null,
    string? DisplayName = null);
```

工具段的 `Markdown` 传空字符串；工具的摘要和详情继续使用现有结构化字段。这样合同只有一个明确的内容来源，不会让调用方同时理解 HTML 和 Markdown 两套语义。

### 3.2 错误消息不要再拼接 HTML

在 `TranscriptRenderItem` 增加可选的 `ErrorMessage` 字段，后端只传纯文本；Vue 根据 `item.status` 选择 `message-error` 或 `message-cancelled` 样式并使用文本插值渲染。这样错误文本不会绕过 Markdown 安全策略，也不会再由 C# 手工拼接标签。

这比把错误塞进一个“伪 Markdown 段”更深：消息错误是消息状态，不是正文内容。

### 3.3 前端建立唯一 Markdown 渲染模块

新增 `SelfClaw.TranscriptVue/src/renderers/markdown.js`，对外只暴露一个小接口：

```js
renderMarkdown(source, { context: 'content' | 'thinking' | 'user' }) -> sanitizedHtml
```

模块内部负责：

1. 创建单例 `MarkdownIt`。
2. 配置 `html: false`，默认关闭 `linkify` 和 `typographer`，避免悄悄改变普通文本语义。
3. 按实际语料增加插件。基础语法、表格、删除线、围栏代码使用 markdown-it 核心；任务列表、脚注、定义列表只在兼容性语料证明需要时分别加入插件。
4. 通过 `highlight` 回调调用 highlight.js。
5. 对生成 HTML 做受控清理（推荐 `dompurify` 作为防御性第二层）。
6. 提供有上限的 LRU/Map 缓存，key 至少包含原文和 `context`，避免流式更新造成重复解析和内存无限增长。

这是前端的深模块：`BodySegment` 和 `ThinkingBlock` 只知道“传入 Markdown，得到安全 HTML”，不需要知道 parser、插件、语言注册或清理规则。

## 4. markdown-it 和 highlight.js 配置建议

### 4.1 MarkdownIt

建议初始配置：

```js
new MarkdownIt({
  html: false,
  breaks: false,
  linkify: false,
  typographer: false,
  highlight: highlightCode,
})
```

`html: false` 要与当前 Markdig `DisableHtml()` 的安全语义保持一致。不要为了“兼容更多 Markdown”打开原始 HTML；如果未来确有需要，应增加显式白名单，而不是直接设为 `true`。

### 4.2 代码高亮

使用 `highlight.js/lib/core`，只注册常见语言和别名，不要引入全量语言包。建议首批包含：JavaScript、TypeScript、JSON、Bash/Shell、PowerShell、C#、Python、Java、Go、Rust、SQL、CSS、XML/HTML、Markdown、YAML。

处理规则：

- 围栏带语言且语言在 allowlist 中：`hljs.highlight(code, { language, ignoreIllegals: true })`。
- 未知语言或无语言：返回经过 HTML escape 的纯文本；不要盲目 `highlightAuto`，避免误判和高 CPU。
- 输出保留 `class="hljs language-xxx"`，由全局 Markdown 样式表定义 token 颜色。
- 高亮失败必须回退为转义后的代码，不能让异常中断整个消息渲染。

### 4.3 安全清理和链接策略

即使 `html: false` 已经禁止原始 HTML，仍建议在 `v-html` 前使用 DOMPurify 做防御性清理，并限制标签、属性和协议：

- 允许 MarkdownIt 自己生成的 `p、h1-h6、ul、ol、li、blockquote、pre、code、table、thead、tbody、tr、th、td、a、img、em、strong、del、hr、br` 等。
- 允许 `class`、`href`、`src`、`alt`、`title`、`loading`、`rel`、`target` 等必要属性。
- 拒绝 `script/style/iframe/object/embed`、事件属性（`onerror` 等）和 `javascript:`、`vbscript:`、`file:` 协议。
- 图片继续支持 `https://attachments.selfclaw.local/...`；普通 Markdown 图片是否允许外部 HTTP(S) 由产品策略决定，至少必须拒绝脚本协议。
- 链接建议统一补充 `rel="noopener noreferrer"`；如后续要求在系统浏览器打开，可在事件委托中转给宿主，不让 WebView 页面被导航离开。

## 5. Vue 改造方案

1. `BodySegment.vue`
   - 读取 `segment.markdown`。
   - 根据角色传入 `context: 'user' | 'content'`。
   - `computed` 调用 `renderMarkdown`，继续使用现有的延迟策略；可将 `useDeferredHtml` 重命名为更准确的 `useDeferredRenderedContent`，行为保持不变。
   - 根节点统一增加 `.markdown-content`，图片点击委托改为匹配 `.markdown-content img`。
2. `ThinkingBlock.vue`
   - 读取 `segment.markdown`，传入 `context: 'thinking'`。
   - 保留 pending、折叠、占位符和 160ms 延迟。
3. `MessageContent.vue`
   - 按 `item.errorMessage` 渲染纯文本错误提示。
   - 不再依赖后端已经拼好的错误 HTML。
4. `renderers/transcript.js`
   - 删除 `renderSkillTokensInUserHtml` 的 HTML 后处理。
   - 将 `[/skill-id]` 做成 markdown-it 的受控 inline rule，仅在 `context: 'user'` 生效。
   - inline rule 必须排除 inline code；围栏代码天然不会走 inline rule。skill 名称必须经过 renderer escape，不能拼接未转义 HTML。
   - 首版就实现 inline rule，不保留旧的 HTML 后处理路径；避免先解析 HTML 再改 DOM。
5. 样式
   - 把 `App.vue` 中正文 Markdown、thinking Markdown、`pre/code`、表格、引用和图片样式迁移到 `src/styles/markdown.css`，由 `main.js` 或 `App.vue` 全局引入。
   - 增加 `.markdown-content pre code.hljs` 和 token class 的浅色主题；不要使用 scoped style，因为 v-html 生成的节点不会携带 Vue scope 属性。

## 6. 后端改造范围

### 6.1 `TranscriptProjection`

- 删除 `_markdownHtmlRenderer` 依赖和 `RenderMarkdownSegment`。
- 消息缓存由 `(Kind, Markdown, Html)` 改为 `(Kind, Markdown)`，或直接依赖现有的消息 fingerprint；缓存的目的变为保持 `TranscriptRenderItem` 引用稳定，而不是缓存 HTML。
- assistant 的正文/thinking 段把 `segment.Markdown` 写入 `TranscriptRenderSegment.Markdown`。
- user 消息同样只写 Markdown。
- 失败/取消状态写入 `TranscriptRenderItem.ErrorMessage`，不再调用 `WebUtility.HtmlEncode` 拼接 `<p>`。
- 工具段的结构化字段、插入位置、工具锚点和附件 URL 逻辑保持不变。

### 6.2 删除后端渲染器

严格切换时，以下内容必须与 wire DTO 和 Vue 资产在同一个发布变更中删除；不允许存在“后端仍生成 HTML、前端暂时兼容读取”的中间状态：

- `SelfClaw.Infrastructure/Tools/Transcript/MarkdownHtmlRenderer.cs`。
- `AddSelfClawInfrastructure()` 中的 `MarkdownHtmlRenderer` 注册。
- `Markdig` 在 Infrastructure、Desktop、Tests 项目中的 PackageReference，以及 `Directory.Packages.props` 中的版本项（确认没有其他使用点后再删）。

不需要数据库迁移；`MessageRecord.MarkdownContent`、thinking marker 和工具锚点持久化格式保持不变。

## 7. 严格切换实施策略

本次迁移不采用 fallback、双写或旧宿主兼容读取。`Html` 字段和后端 HTML renderer 从合同中一次性移除；前端和宿主必须作为同一个可发布单元构建。允许在开发分支上分步提交，但每个可运行/可发布构建都必须满足新合同，不能产生“前端期待 Markdown、宿主仍发送 HTML”的中间资产。

建议执行顺序：

1. 在迁移分支先建立 Markdown fixtures、markdown-it/highlight.js renderer、XSS 测试和 CSS 快照；这些准备工作不改变运行时合同。
2. 同一变更中修改 `TranscriptRenderSegment` 为 `Markdown`、增加 `ErrorMessage`，更新 `TranscriptProjection`、`BodySegment.vue`、`ThinkingBlock.vue`、skill token inline rule 和 `previewImage.js`。
3. 在同一变更中删除 `MarkdownHtmlRenderer`、Markdig PackageReference、所有 `segment.html` 引用和旧错误 HTML 拼接。
4. 同时执行 `npm run build`，将新 TranscriptVue 资产与宿主一起验证；执行 `dotnet build SelfClaw.slnx` 和全量测试。
5. 只发布通过新合同 smoke test 的桌面包。若发现问题，回滚整个桌面包/提交，不通过保留旧 HTML 字段来热修复。

发布前必须检查：

- `rg "Html|segment\.html|MarkdownHtmlRenderer|Markdig"` 在运行时代码中无结果（文档中的历史盘点文字除外）。
- WebView 只存在 `replaceState/patchState` 的 Markdown 合同，且前端没有 fallback 分支。
- 宿主和内置 TranscriptVue 资产来自同一次构建，避免缓存导致合同版本错配。

## 8. 测试和验收

### 8.1 语法兼容性语料

在迁移前建立一组 Markdown fixtures，并保存当前 Markdig 的基线截图或 HTML 结果，至少覆盖：

- 标题、段落、强调、删除线、链接、图片、换行。
- 有序/无序列表、嵌套列表、表格、引用、任务列表。
- fenced code（带语言、不带语言、未知语言、代码中包含 `<script>`）。
- `<think>`、内部 thinking marker、工具锚点前后相邻内容、pending thinking。
- 中文、emoji、长行、空消息、错误消息。
- 用户 `[/skill]` token、inline code 中的同名文本和代码围栏中的同名文本。

不要求 Markdig 与 markdown-it 生成完全相同的 HTML；要求用户可见结构、链接安全性、代码可读性和工具/思考块位置不回归。对 Markdig `UseAdvancedExtensions()` 中未被实际语料使用的扩展，不应为了形式兼容盲目增加插件。

### 8.2 后端 xUnit

- `TranscriptProjection` 断言正文和 thinking 段输出 `Markdown`，不含后端生成的 `<p>`、`<strong>` 等 HTML。
- 断言错误消息通过 `ErrorMessage` 传输，工具段仍然只使用结构化字段。
- 断言 patch 只更新变化消息，稳定消息保持同一引用/合同语义。
- 删除 `MarkdownHtmlRenderer` 后，项目和测试不再引用 Markdig。

### 8.3 前端 Vitest

新增 renderer 测试（必要时使用 jsdom）：

- Markdown 基础语法和表格/任务列表插件。
- 每个 allowlist 语言的高亮，以及未知语言的安全纯文本回退。
- `<script>`、`onerror`、`javascript:` 等输入不会产生可执行节点。
- 图片具有预览所需 class/属性，链接具有安全协议和 rel 属性。
- skill token 只在 user context 生效，且不会改写 inline code/代码围栏。
- 相同输入命中缓存；流式输入只重新渲染变化段。

### 8.4 手工/E2E 验收

- Direct 和 CLI 各发送一轮包含表格、引用、代码块、thinking 和工具调用的消息。
- 历史会话重放、取消、失败、空响应、WebView 导航离开再回来均正常。
- 长代码块横向滚动、图片预览、外链行为和浅色主题样式正常。
- 连续流式更新时 UI 不明显卡顿；120ms 后端发布节流和现有 thinking 延迟仍有效。
- 构建命令通过：`npm run build`、`dotnet build SelfClaw.slnx`、全量测试。

## 9. 风险和取舍

| 风险 | 影响 | 缓解措施 |
| --- | --- | --- |
| Markdig 与 markdown-it 语法差异 | 历史消息视觉变化 | 迁移前建立语料基线；按实际使用情况选择插件 |
| 渲染从后端移到 WebView 增加浏览器 CPU | 长消息/流式输出卡顿 | 保留 patch、120ms 发布节流、thinking 延迟和有上限缓存；highlight.js 只注册常用语言 |
| `v-html` 注入风险 | 可能执行不可信内容 | `html:false`、协议校验、DOMPurify、XSS 测试；不允许原始 HTML |
| WebView 合同不一致 | 页面空白或字段缺失 | 宿主与 TranscriptVue 资产原子发布；构建阶段运行 wire contract smoke test；出现问题回滚整包 |
| skill token 被代码解析 | 代码显示错误 | markdown-it inline rule 排除 code token，并增加专门测试 |
| 全局样式迁移回归 | 表格、代码、thinking 样式变化 | 生成 HTML 的样式放全局 CSS，完成截图/手工回归 |

## 10. 推荐执行顺序

1. 先提交 Markdown 语料和前端 renderer 单元测试。
2. 在同一个迁移变更中引入前端依赖、全局 Markdown 样式、Markdown wire DTO 和 Vue 渲染调用。
3. 在同一个迁移变更中删除后端 HTML renderer、Markdig 依赖、错误 HTML 拼接和所有 `segment.html` 读取。
4. 完成 WebView/Direct/CLI/流式/附件/工具回归，并执行 wire contract smoke test。
5. 最后做一次包体积、长消息性能和安全审计，再发布宿主与内置 TranscriptVue 资产组成的完整桌面包。
