<script setup>
import { computed, ref } from 'vue';
import { useStreamingContent } from '../../../composables/useStreamingContent.js';
import { renderMarkdown } from '../../../renderers/markdown.js';
import { resolvePreviewImage } from '../../../renderers/previewImage.js';

const props = defineProps({
	item: { type: Object, required: true },
	segment: { type: Object, required: true },
	isFirst: { type: Boolean, default: false },
	isLast: { type: Boolean, default: false },
});

const emit = defineEmits(['preview-image']);
const containerRef = ref(null);

// Both transcript surfaces use the same sanitized Markdown pipeline.
const sourceHtml = computed(() => renderMarkdown(props.segment.markdown, {
	context: props.item.role === 'user' ? 'user' : 'content',
}));
const isStreaming = computed(() => props.item.role === 'assistant' && (Boolean(props.segment.isPending) || props.item.status === 'streaming'));

useStreamingContent(containerRef, sourceHtml, isStreaming);

// v-html 里的 <img> 不是组件元素，点击预览靠委托命中。
function onClick(event) {
	const preview = resolvePreviewImage(event.target);
	if (preview) {
		event.preventDefault();
		emit('preview-image', preview);
	}
}
</script>

<template>
	<div ref="containerRef" class="body body-segment markdown-content" :class="{ first: isFirst, last: isLast }"
		@click="onClick"></div>
</template>
