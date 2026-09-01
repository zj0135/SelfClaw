<script setup>
import { Network, Puzzle, Wrench } from 'lucide-vue-next';
defineProps({
	modelValue: { type: String, required: true },
	counts: { type: Object, required: true },
});
defineEmits(['update:modelValue']);
const categories = [
	{ id: 'plugin', label: '插件', icon: Puzzle },
	{ id: 'skill', label: '技能', icon: Wrench },
	{ id: 'mcpServer', label: 'MCP', icon: Network },
];
</script>

<template>
	<div class="tabs" role="tablist" aria-label="扩展类型">
		<button v-for="category in categories" :key="category.id" type="button" role="tab"
			:aria-selected="modelValue === category.id" :class="{ active: modelValue === category.id }"
			@click="$emit('update:modelValue', category.id)">
			<component :is="category.icon" :size="15" aria-hidden="true" />
			<span>{{ category.label }}</span>
			<small>{{ counts[category.id] || 0 }}</small>
		</button>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.tabs {
	display: flex;
	gap: 2px;
	border-bottom: 1px solid var(--sc-line);
}

button {
	position: relative;
	display: inline-flex;
	align-items: center;
	gap: 8px;
	min-width: 104px;
	height: 42px;
	padding: 0 14px;
	border: 0;
	background: transparent;
	color: var(--sc-mute);
	font-size: var(--fs-13);
}

button::after {
	position: absolute;
	right: 12px;
	bottom: -1px;
	left: 12px;
	height: 2px;
	background: transparent;
	content: '';
}

button.active {
	color: var(--sc-text);
}

button.active::after {
	background: var(--sc-acid);
}

button:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: -2px;
}

small {
	margin-left: auto;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
}
</style>
