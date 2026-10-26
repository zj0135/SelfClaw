<script setup>
import { computed, ref, watch } from 'vue';

const props = defineProps({
	shellKind: { type: String, default: 'WindowsPowerShell' },
	shells: { type: Array, default: () => [] },
	saving: { type: Boolean, default: false },
});

const emit = defineEmits(['select']);

const selectRef = ref(null);

// 可用性完全由宿主回显决定：shells 里缺失或 available !== true 一律禁用，前端不做本地猜测。
const options = computed(() => {
	const definitions = [
		{ kind: 'WindowsPowerShell', label: 'Windows PowerShell', en: 'POWERSHELL 5.1' },
		{ kind: 'PowerShellSeven', label: 'PowerShell 7', en: 'PWSH 7+' },
		{ kind: 'CommandPrompt', label: '命令提示符', en: 'CMD.EXE' },
		{ kind: 'GitBash', label: 'Git Bash', en: 'BASH' },
	];
	return definitions.map((definition) => {
		const shell = props.shells.find((item) => item.kind === definition.kind) || {};
		return {
			...definition,
			available: shell.available === true,
			resolvedPath: typeof shell.resolvedPath === 'string' ? shell.resolvedPath : '',
			active: props.shellKind === definition.kind,
		};
	});
});

watch(
	() => [props.shellKind, props.saving],
	() => {
		if (selectRef.value && !props.saving) {
			selectRef.value.value = props.shellKind;
		}
	},
	{ flush: 'post' },
);

function handleChange(event) {
	const nextKind = event.target.value;
	if (nextKind === props.shellKind) return;
	const match = options.value.find((item) => item.kind === nextKind);
	if (match && match.available) {
		emit('select', nextKind);
	} else {
		event.target.value = props.shellKind;
	}
}
</script>

<template>
	<div class="shell-row">
		<div class="row-meta">
			<span class="row-label">默认 Shell</span>
		</div>
		<div class="row-control">
			<select ref="selectRef" class="shell-select" :value="props.shellKind" aria-label="选择默认 Shell"
				@change="handleChange">
				<option v-for="shell in options" :key="shell.kind" :value="shell.kind"
					:disabled="!shell.available" :data-available="shell.available ? 'true' : 'false'">
					{{ shell.label }}{{ !shell.available ? ' (未检测到)' : '' }}
				</option>
			</select>
		</div>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.shell-row {
	display: grid;
	grid-template-columns: minmax(0, 1fr) minmax(0, auto);
	align-items: center;
	gap: 18px;
	padding: 14px 0;
	border-bottom: 1px solid var(--sc-line);
}

.row-meta {
	display: flex;
	flex-direction: column;
	min-width: 0;
}

.row-label {
	color: var(--sc-text);
	font-size: var(--fs-13);
	font-weight: 600;
}

.row-control {
	display: flex;
	align-items: center;
	justify-content: flex-end;
	gap: 8px;
	min-width: 0;
}

.shell-select {
	min-width: 200px;
	padding: 7px 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-125);
	cursor: pointer;
	transition:
		border-color 0.16s,
		box-shadow 0.16s;
}

.shell-select:hover {
	border-color: var(--sc-faint);
}

.shell-select:focus-visible {
	outline: none;
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

@media (max-width: 720px) {
	.shell-row {
		grid-template-columns: minmax(0, 1fr);
		gap: 10px;
	}

	.row-control {
		justify-content: flex-start;
	}
}
</style>
