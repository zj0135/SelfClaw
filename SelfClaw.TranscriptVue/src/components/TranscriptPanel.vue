<script setup>
import { ref } from 'vue';

defineProps({
	messagesHtml: {
		type: String,
		default: '',
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
		<div
			id="transcript-scroll"
			ref="scrollEl"
			class="transcript-scroll"
			@scroll="emit('scroll', $event)"
			@click="onTranscriptClick"
			@keydown="onTranscriptKeydown"
		>
			<div v-html="messagesHtml"></div>
		</div>
	</section>
</template>
