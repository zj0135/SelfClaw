<script setup>
import { computed } from 'vue';
import { FileText, LoaderCircle } from 'lucide-vue-next';
import { highlightLines, languageForLines } from '../../../renderers/codeHighlight.js';
import { changeKindLabel } from '../../../renderers/gitChangeLabels.js';
import { parseUnifiedDiff } from '../../../renderers/unifiedDiff.js';

// 超过这个行数就不做语法高亮（纯文本仍然显示）：hljs 对几千行一次性着色会卡住主线程。
const MAX_HIGHLIGHTED_LINES = 4000;

const props = defineProps({
	file: { type: Object, default: null },
	loading: { type: Boolean, default: false },
	error: { type: String, default: '' },
	// 加载中还没有 file 时用它撑起文件头，避免切换文件时标题闪空。
	selectedPath: { type: String, default: null },
});

const parsed = computed(() => (props.file && !props.file.isBinary ? parseUnifiedDiff(props.file.patch) : { hunks: [] }));
const kind = computed(() => (props.file ? changeKindLabel(props.file.kind) : null));
const headerPath = computed(() => props.file?.path ?? props.selectedPath ?? '');
const renamedFrom = computed(() => (props.file?.oldPath && props.file.oldPath !== props.file.path ? props.file.oldPath : ''));
const notice = computed(() => {
	if (!props.file) return '';
	if (props.file.isBinary) return '二进制文件，不显示内容';
	if (!parsed.value.hunks.length) return renamedFrom.value ? '仅重命名，内容没有改动' : '没有可显示的内容改动';
	return '';
});

// 行号只有一列：删除行给旧行号，其余给新行号（与编辑器内联 diff 的读法一致）。
// hunk 前面有被省略的行（不是从第 1 行开始，或不是第一个 hunk）时插入一条分隔行。
const rendered = computed(() => {
	const hunks = parsed.value.hunks;
	const texts = hunks.flatMap((hunk) => hunk.lines.map((line) => line.text));
	const language = texts.length <= MAX_HIGHLIGHTED_LINES ? languageForLines(props.file?.path, texts) : '';
	return hunks.map((hunk, index) => {
		const html = highlightLines(hunk.lines.map((line) => line.text), language);
		return {
			header: hunk.header,
			context: hunk.context,
			separator: index > 0 || hunk.oldStart > 1 || hunk.newStart > 1,
			lines: hunk.lines.map((line, lineIndex) => ({
				kind: line.kind,
				number: line.kind === 'remove' ? line.oldNumber : line.newNumber,
				html: html[lineIndex],
				noNewline: Boolean(line.noNewline),
			})),
		};
	});
});
</script>

<template>
	<section class="diff-view" aria-label="文件改动">
		<header v-if="headerPath" class="diff-head">
			<span v-if="kind" class="diff-kind" :class="`tone-${kind.tone}`">{{ kind.label }}</span>
			<span class="diff-path" :title="renamedFrom ? `${renamedFrom} → ${headerPath}` : headerPath">
				<s v-if="renamedFrom">{{ renamedFrom }}</s><template v-if="renamedFrom"> → </template>{{ headerPath }}
			</span>
			<span v-if="file && !file.isBinary" class="diff-stat">
				<em class="add">+{{ file.addedLines }}</em>
				<em class="remove">−{{ file.removedLines }}</em>
			</span>
		</header>
		<p v-if="error" class="pane-error" role="alert">{{ error }}</p>
		<div v-if="loading" class="diff-status" role="status">
			<LoaderCircle :size="16" class="spin" aria-hidden="true" />
			<span>加载中...</span>
		</div>
		<div v-else-if="!file" class="diff-status">
			<FileText :size="28" :stroke-width="1.5" aria-hidden="true" />
			<span>选择一个文件查看改动</span>
		</div>
		<div v-else-if="notice" class="diff-status diff-notice">{{ notice }}</div>
		<div v-else :key="file.path" class="diff-scroll">
			<table class="diff-table">
				<tbody v-for="(hunk, hunkIndex) in rendered" :key="hunkIndex">
					<tr v-if="hunk.separator" class="diff-hunk" :title="hunk.header">
						<td class="gutter">···</td>
						<td class="code">{{ hunk.context }}</td>
					</tr>
					<tr v-for="(line, lineIndex) in hunk.lines" :key="lineIndex" class="diff-line" :class="line.kind">
						<td class="gutter" :title="line.noNewline ? '文件末尾没有换行' : undefined">{{ line.number ?? '' }}</td>
						<!-- 内容来自 highlight.js（已转义）或 escapeHtml，不含任何外部 HTML。 -->
						<td class="code" v-html="line.html"></td>
					</tr>
				</tbody>
			</table>
			<p v-if="file.truncated" class="diff-truncated">内容过长，已截断显示</p>
		</div>
	</section>
</template>

<style scoped>
.diff-view {
	display: flex;
	flex-direction: column;
	min-width: 0;
	min-height: 0;
}

.diff-head {
	display: flex;
	flex: 0 0 auto;
	align-items: center;
	gap: 8px;
	min-width: 0;
	padding: 7px 10px;
	border-bottom: 1px solid var(--border);
}

.diff-kind {
	flex: none;
	padding: 2px 7px;
	border-radius: 999px;
	font: 600 var(--fs-10)/1.4 var(--font-ui);
}

.tone-modified {
	background: color-mix(in srgb, var(--warning) 14%, transparent);
	color: var(--warning);
}

.tone-added {
	background: color-mix(in srgb, var(--success) 14%, transparent);
	color: var(--success);
}

.tone-deleted {
	background: color-mix(in srgb, var(--danger) 14%, transparent);
	color: var(--danger);
}

.tone-renamed {
	background: var(--accent-soft);
	color: var(--accent);
}

.diff-path {
	flex: 1 1 auto;
	min-width: 0;
	overflow: hidden;
	color: var(--text);
	font: 600 var(--fs-12)/1.4 var(--font-mono);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.diff-path s {
	color: var(--muted);
	font-weight: 500;
}

.diff-stat {
	flex: none;
	display: inline-flex;
	gap: 5px;
	font: 500 var(--fs-105)/1 var(--font-mono);
}

.diff-stat em {
	font-style: normal;
}

.diff-stat .add {
	color: var(--success);
}

.diff-stat .remove {
	color: var(--danger);
}

.pane-error {
	margin: 0;
	padding: 8px 10px;
	border-bottom: 1px solid var(--border);
	color: var(--danger);
	font-size: var(--fs-11);
	overflow-wrap: anywhere;
}

.diff-status {
	display: flex;
	flex: 1 1 auto;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	gap: 8px;
	padding: 24px 12px;
	color: var(--muted);
	font-size: var(--fs-12);
	text-align: center;
}

/* ===== 代码面：--diff-* 跟随主题（浅色白底深字，深色沿用代码块的深底），滚动条走应用默认 ===== */

.diff-scroll {
	flex: 1 1 auto;
	min-height: 0;
	overflow: auto;
	background: var(--diff-bg);
	color: var(--diff-fg);
	animation: diff-fade 160ms var(--ease-out);
}

/* App.vue 给所有 <table> 画了边框、10px 圆角与单元格线（给 markdown 表格用的），这里全部归零。 */
.diff-table {
	width: auto;
	min-width: 100%;
	overflow: visible;
	border: 0;
	border-radius: 0;
	border-collapse: collapse;
	border-spacing: 0;
	background: transparent;
	font-family: var(--font-code);
	font-size: var(--code-fs);
	line-height: 1.6;
}

.diff-line td,
.diff-hunk td {
	padding: 0;
	border: 0;
	vertical-align: top;
}

/* 单列行号：粘在左侧，横向滚动时不跟着跑；左缘 3px 色条标出增删。 */
.gutter {
	position: sticky;
	left: 0;
	z-index: 1;
	width: 1%;
	min-width: 3.4em;
	padding: 0 10px 0 7px;
	border-left: 3px solid transparent;
	background: var(--diff-bg);
	color: color-mix(in srgb, var(--diff-fg) 45%, transparent);
	text-align: right;
	user-select: none;
}

.code {
	padding: 0 16px 0 12px;
	white-space: pre;
	tab-size: 4;
}

.diff-line.add td {
	background: color-mix(in srgb, var(--success) 17%, var(--diff-bg));
}

.diff-line.add .gutter {
	border-left-color: var(--success);
	background: color-mix(in srgb, var(--success) 26%, var(--diff-bg));
	color: color-mix(in srgb, var(--success) 75%, var(--diff-fg));
}

.diff-line.remove td {
	background: color-mix(in srgb, var(--danger) 19%, var(--diff-bg));
}

.diff-line.remove .gutter {
	border-left-color: var(--danger);
	background: color-mix(in srgb, var(--danger) 30%, var(--diff-bg));
	color: color-mix(in srgb, var(--danger) 75%, var(--diff-fg));
}

.diff-line.context:hover td {
	background: color-mix(in srgb, var(--diff-fg) 6%, var(--diff-bg));
}

/* 被省略行的分隔条：居中省略号 + git 给的函数 / 节标题，只用底色区分，不画线。 */
.diff-hunk td {
	background: color-mix(in srgb, var(--diff-fg) 5%, var(--diff-bg));
	color: color-mix(in srgb, var(--diff-fg) 50%, transparent);
	font-size: var(--fs-105);
	line-height: 2;
}

.diff-hunk .gutter {
	text-align: center;
	letter-spacing: 1px;
}

.diff-hunk .code {
	font-style: italic;
}

.diff-truncated {
	margin: 0;
	padding: 8px 14px;
	background: color-mix(in srgb, var(--warning) 8%, var(--diff-bg));
	color: var(--warning);
	font-size: var(--fs-105);
}

/* hljs 配色走 --diff-syntax-*：类名集合与 styles/markdown.css 一致，外加 xml / css 语法用到的几个。 */
:deep(.hljs-comment),
:deep(.hljs-quote),
:deep(.hljs-meta),
:deep(.hljs-doctag) {
	color: var(--diff-syntax-comment);
}

:deep(.hljs-keyword),
:deep(.hljs-selector-tag),
:deep(.hljs-literal),
:deep(.hljs-title),
:deep(.hljs-name) {
	color: var(--diff-syntax-keyword);
}

:deep(.hljs-string),
:deep(.hljs-attr),
:deep(.hljs-regexp),
:deep(.hljs-template-variable) {
	color: var(--diff-syntax-string);
}

:deep(.hljs-number),
:deep(.hljs-built_in),
:deep(.hljs-variable) {
	color: var(--diff-syntax-number);
}

:deep(.hljs-type),
:deep(.hljs-class .hljs-title),
:deep(.hljs-selector-class),
:deep(.hljs-selector-id),
:deep(.hljs-selector-pseudo),
:deep(.hljs-attribute) {
	color: var(--diff-syntax-type);
}

.spin {
	animation: diff-spin 1s linear infinite;
}

@keyframes diff-spin {
	to {
		transform: rotate(360deg);
	}
}

@keyframes diff-fade {
	from {
		opacity: 0;
		transform: translateY(4px);
	}

	to {
		opacity: 1;
		transform: none;
	}
}

@media (prefers-reduced-motion: reduce) {
	.diff-scroll {
		animation: none;
	}

	.spin {
		animation: none;
	}
}
</style>
