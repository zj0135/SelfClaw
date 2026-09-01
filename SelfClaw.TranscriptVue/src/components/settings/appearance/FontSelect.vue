<script setup>
const props = defineProps({
	value: { type: String, default: '' },
	choices: { type: Array, required: true },
	// 下拉项用自身字族渲染，让用户在选之前就看到长什么样。
	previewInOptions: { type: Boolean, default: true },
});

const emit = defineEmits(['change']);

function optionStyle(choice) {
	return props.previewInOptions && choice.id ? { fontFamily: `"${choice.id}", inherit` } : {};
}
</script>

<template>
	<select class="font-select" :value="props.value" @change="emit('change', $event.target.value)">
		<option v-for="choice in props.choices" :key="choice.id || 'default'" :value="choice.id"
			:style="optionStyle(choice)">
			{{ choice.label }}
		</option>
	</select>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.font-select {
	min-width: 188px;
	padding: 7px 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-125);
	cursor: pointer;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.font-select:hover {
	border-color: var(--sc-faint);
}

.font-select:focus-visible {
	outline: none;
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}
</style>
