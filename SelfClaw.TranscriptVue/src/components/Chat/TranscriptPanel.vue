<script setup>
import { ref } from 'vue';

defineProps({
	messages: {
		type: Array,
		default: () => [],
	},
	// 回合执行状态：{ label, elapsedText } 或 null。
	// CLI 非流式返回时消息区长时间无变化，用这行状态告诉用户回合仍在执行。
	turnStatus: {
		type: Object,
		default: null,
	},
});

const emit = defineEmits(['scroll', 'preview-image', 'transcript-click', 'transcript-keydown']);
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
	if (previewImage) {
		event.preventDefault();
		emit('preview-image', previewImage);
	}

	emit('transcript-click', event);
}

function onTranscriptKeydown(event) {
	if (event.key !== 'Enter' && event.key !== ' ') {
		return;
	}

	const previewImage = resolvePreviewImage(event.target);
	if (!previewImage) {
		emit('transcript-keydown', event);
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
		<div id="transcript-scroll" ref="scrollEl" class="transcript-scroll" @scroll="emit('scroll', $event)"
			@click="onTranscriptClick" @keydown="onTranscriptKeydown">
			<!-- 每条消息是独立的 keyed 节点：流式更新时只有内容变化的那条会被替换 innerHTML，
			     其余消息的 DOM（文本选区、图片、动画）保持不动。 -->
			<div v-for="message in messages" :key="message.id" class="message-row"
				:class="[message.role, message.status]" :data-message-id="message.id" v-html="message.html"></div>
			<div v-if="turnStatus" class="turn-status-row" role="status" aria-live="polite">
				<span class="turn-status-dot" aria-hidden="true"></span>
				<span class="turn-status-label">{{ turnStatus.label }}</span>
				<span v-if="turnStatus.elapsedText" class="turn-status-time">{{ turnStatus.elapsedText }}</span>
			</div>
		</div>
	</section>
</template>
