<script setup>
import AppearanceRow from './AppearanceRow.vue';

const props = defineProps({
	mode: { type: String, default: 'system' },
	resolved: { type: String, default: 'light' },
});

const emit = defineEmits(['select']);

const modes = [
	{ id: 'light', label: '浅色' },
	{ id: 'dark', label: '暗色' },
	{ id: 'system', label: '跟随系统' },
];

function optionLabel(item) {
	if (item.id === 'system') {
		const resolvedText = props.resolved === 'dark' ? '暗色' : '浅色';
		return `跟随系统 (${resolvedText})`;
	}
	return item.label;
}
</script>

<template>
	<AppearanceRow label="主题模式" :index="0" class="theme-mode-row">
		<select class="theme-select" :value="props.mode" aria-label="主题模式"
			@change="emit('select', $event.target.value)">
			<option v-for="item in modes" :key="item.id" :value="item.id">
				{{ optionLabel(item) }}
			</option>
		</select>
	</AppearanceRow>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.theme-mode-row {
	border-bottom: 1px solid var(--sc-line) !important;
}

.theme-select {
	min-width: 188px;
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

.theme-select:hover {
	border-color: var(--sc-faint);
}

.theme-select:focus-visible {
	outline: none;
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}
</style>
