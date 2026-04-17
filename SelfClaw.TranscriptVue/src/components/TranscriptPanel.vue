<script setup>
import { ref } from 'vue';

defineProps({
	messagesHtml: {
		type: String,
		default: '',
	},
	showPlanPanel: {
		type: Boolean,
		default: false,
	},
	planPanelCollapsed: {
		type: Boolean,
		default: false,
	},
});

const emit = defineEmits(['scroll']);
const scrollEl = ref(null);

defineExpose({
	getScrollEl: () => scrollEl.value,
});
</script>

<template>
	<section class="panel transcript-panel">
		<div
			id="transcript-scroll"
			ref="scrollEl"
			class="transcript-scroll"
			:class="{
				'with-floating-plan': showPlanPanel,
				'with-floating-plan-collapsed': showPlanPanel && planPanelCollapsed,
			}"
			v-html="messagesHtml"
			@scroll="emit('scroll', $event)"
		></div>
	</section>
</template>
