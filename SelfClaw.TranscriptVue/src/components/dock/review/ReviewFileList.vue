<script setup>
import { computed } from 'vue';
import { changeKindLabel, splitPath } from '../../../renderers/gitChangeLabels.js';

const props = defineProps({
	files: { type: Array, default: () => [] },
	selectedPath: { type: String, default: null },
	atFileLimit: { type: Boolean, default: false },
});
const emit = defineEmits(['select', 'step']);

const rows = computed(() => props.files.map((file) => {
	const { directory, name } = splitPath(file.path);
	const kind = changeKindLabel(file.kind);
	const hasCounts = file.addedLines != null || file.removedLines != null;
	return { file, directory, name, kind, hasCounts, title: file.oldPath ? `${file.oldPath} → ${file.path}` : file.path };
}));

// ↑/↓ 只报告方向：选中的推进逻辑在 useGitDiff.selectNeighbor，与工具条的上一个 / 下一个按钮共用。
function onKeydown(event) {
	if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
	event.preventDefault();
	emit('step', event.key === 'ArrowDown' ? 1 : -1);
}
</script>

<template>
	<div class="file-list" role="listbox" aria-label="改动文件" tabindex="0" @keydown="onKeydown">
		<button v-for="row in rows" :key="row.file.path" class="file-row" :class="[`tone-${row.kind.tone}`, { active: row.file.path === selectedPath }]"
			type="button" role="option" :aria-selected="row.file.path === selectedPath" :title="row.title" :data-path="row.file.path"
			:data-kind="row.file.kind" @click="emit('select', row.file)">
			<span class="file-badge" :title="row.kind.label">{{ row.kind.letter }}</span>
			<span class="file-text">
				<strong>{{ row.name }}</strong>
				<small v-if="row.directory">{{ row.directory }}</small>
			</span>
			<span v-if="row.file.isBinary" class="file-stat muted">二进制</span>
			<span v-else-if="row.hasCounts" class="file-stat">
				<em class="add">+{{ row.file.addedLines ?? 0 }}</em>
				<em class="remove">−{{ row.file.removedLines ?? 0 }}</em>
			</span>
		</button>
		<p v-if="atFileLimit" class="file-limit">仅显示前 500 个文件</p>
	</div>
</template>

<style scoped>
.file-list {
	display: flex;
	flex-direction: column;
	gap: 1px;
	min-height: 0;
	padding: 6px;
	overflow: auto;
	outline: none;
}

.file-list:focus-visible {
	box-shadow: inset 0 0 0 2px var(--accent-soft);
}

.file-row {
	position: relative;
	display: grid;
	grid-template-columns: 18px minmax(0, 1fr) auto;
	align-items: center;
	gap: 8px;
	width: 100%;
	padding: 6px 8px 6px 10px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--text-soft);
	text-align: left;
	cursor: pointer;
	transition: background 0.14s, color 0.14s;
}

.file-row:hover {
	background: var(--panel-hover);
	color: var(--text);
}

.file-row.active {
	background: var(--accent-soft);
	color: var(--text);
}

.file-row.active::before {
	position: absolute;
	top: 7px;
	bottom: 7px;
	left: 2px;
	width: 2px;
	border-radius: 2px;
	background: var(--accent);
	content: '';
}

.file-badge {
	display: grid;
	place-items: center;
	width: 18px;
	height: 18px;
	border-radius: 5px;
	font: 700 var(--fs-10)/1 var(--font-mono);
}

.tone-modified .file-badge {
	background: color-mix(in srgb, var(--warning) 14%, transparent);
	color: var(--warning);
}

.tone-added .file-badge {
	background: color-mix(in srgb, var(--success) 14%, transparent);
	color: var(--success);
}

.tone-deleted .file-badge {
	background: color-mix(in srgb, var(--danger) 14%, transparent);
	color: var(--danger);
}

.tone-renamed .file-badge {
	background: var(--accent-soft);
	color: var(--accent);
}

.file-text {
	display: grid;
	gap: 1px;
	min-width: 0;
}

.file-text strong,
.file-text small {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.file-text strong {
	font-size: var(--fs-12);
	font-weight: 600;
}

.file-text small {
	color: var(--muted);
	font-size: var(--fs-105);
}

.file-stat {
	display: inline-flex;
	gap: 4px;
	font: 500 var(--fs-105)/1 var(--font-mono);
	white-space: nowrap;
}

.file-stat em {
	font-style: normal;
}

.file-stat .add {
	color: var(--success);
}

.file-stat .remove {
	color: var(--danger);
}

.file-stat.muted {
	color: var(--muted);
	font-family: var(--font-ui);
}

.file-limit {
	margin: 8px 8px 4px;
	color: var(--faint);
	font-size: var(--fs-105);
}
</style>
