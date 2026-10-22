<script setup>
defineProps({
	modelValue: { type: String, required: true },
});
defineEmits(['update:modelValue']);

const ranges = [
	{ id: '7d', label: '近 7 天' },
	{ id: '30d', label: '近 30 天' },
];
</script>

<template>
	<div class="range-tabs" role="tablist" aria-label="用量范围">
		<button v-for="item in ranges" :key="item.id" type="button" role="tab"
			:aria-selected="modelValue === item.id" :class="{ 'is-active': modelValue === item.id }"
			@click="$emit('update:modelValue', item.id)">
			{{ item.label }}
		</button>
	</div>
</template>

<style scoped>
@import './usage-dashboard.css';

.range-tabs {
	display: inline-flex;
	gap: 2px;
	padding: 2px;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-raise);
}

button {
	height: 24px;
	padding: 0 10px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--sc-mute);
	font-family: inherit;
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0.02em;
	cursor: pointer;
	transition:
		background 0.16s var(--sc-ease-out),
		color 0.16s var(--sc-ease-out);
}

button:hover {
	color: var(--sc-text);
}

button.is-active {
	background: var(--sc-acid-soft);
	color: var(--sc-text);
}

button:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 1px;
}
</style>
