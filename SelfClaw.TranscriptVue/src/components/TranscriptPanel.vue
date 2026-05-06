<script setup>
import { ref } from 'vue';
import TranscriptGraphView from './TranscriptGraphView.vue';

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
	visualizationEnabled: {
		type: Boolean,
		default: false,
	},
	items: {
		type: Array,
		default: () => [],
	},
	conversations: {
		type: Array,
		default: () => [],
	},
	selectedConversationId: {
		type: String,
		default: null,
	},
	selectedConversationModeId: {
		type: String,
		default: 'programming',
	},
	selectedProfileModel: {
		type: String,
		default: '',
	},
	agentActivities: {
		type: Array,
		default: () => [],
	},
});

const emit = defineEmits(['scroll', 'preview-image']);
const scrollEl = ref(null);

function resolvePreviewImage(target) {
	if (!(target instanceof Element)) {
		return null;
	}

	const image = target.closest('.message-attachment-image, .body.body-segment img, .thinking-markdown img');
	if (!(image instanceof HTMLImageElement)) {
		return null;
	}

	const src = image.currentSrc || image.src || '';
	if (!src) {
		return null;
	}

	return {
		src,
		alt: image.getAttribute('alt') || '',
	};
}

function onTranscriptClick(event) {
	const previewImage = resolvePreviewImage(event.target);
	if (!previewImage) {
		return;
	}

	event.preventDefault();
	emit('preview-image', previewImage);
}

function onTranscriptKeydown(event) {
	if (event.key !== 'Enter' && event.key !== ' ') {
		return;
	}

	const previewImage = resolvePreviewImage(event.target);
	if (!previewImage) {
		return;
	}

	event.preventDefault();
	emit('preview-image', previewImage);
}

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
			'graph-mode': visualizationEnabled,
			'with-floating-plan': showPlanPanel,
			'with-floating-plan-collapsed': showPlanPanel && planPanelCollapsed,
		}"
		@scroll="emit('scroll', $event)"
		@click="onTranscriptClick"
		@keydown="onTranscriptKeydown"
	>
			<TranscriptGraphView
				v-if="visualizationEnabled"
				:items="items"
				:conversations="conversations"
				:selected-conversation-id="selectedConversationId"
				:selected-conversation-mode-id="selectedConversationModeId"
				:selected-profile-model="selectedProfileModel"
				:agent-activities="agentActivities"
			/>
			<div v-else v-html="messagesHtml"></div>
		</div>
	</section>
</template>
