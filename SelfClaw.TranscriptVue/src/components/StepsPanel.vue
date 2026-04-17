<script setup>
import { ref } from 'vue';

defineProps({
	stepsHeaderHtml: {
		type: String,
		default: '',
	},
	stepsPanelHtml: {
		type: String,
		default: '',
	},
});

const scrollEl = ref(null);

defineExpose({
	getScrollEl: () => scrollEl.value,
});
</script>

<template>
	<aside id="steps-panel-shell" class="panel steps-panel">
		<div id="steps-header" class="steps-header" v-html="stepsHeaderHtml"></div>
		<div id="steps-scroll" ref="scrollEl" class="steps-scroll" v-html="stepsPanelHtml"></div>
	</aside>
</template>
