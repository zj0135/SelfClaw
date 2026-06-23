<script setup>
import { computed } from 'vue';
import { ICONS } from './settingsIcons';

const props = defineProps({
	name: { type: String, required: true },
});

// ICONS values are trusted static SVG strings defined in this repo.
const markup = computed(() => ICONS[props.name] || '');
</script>

<template>
	<span class="settings-icon" v-html="markup"></span>
</template>

<style scoped>
.settings-icon {
	display: inline-flex;
	align-items: center;
	justify-content: center;
}

.settings-icon :deep(svg) {
	width: 100%;
	height: 100%;
}
</style>
