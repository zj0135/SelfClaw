<script setup>
import { computed } from 'vue';
import { useDeferredHtml } from '../../../composables/useDeferredHtml.js';
import { renderMarkdown } from '../../../renderers/markdown.js';
import { resolvePreviewImage } from './previewImage.js';

const props = defineProps({
	item: { type: Object, required: true },
	segment: { type: Object, required: true },
	isFirst: { type: Boolean, default: false },
	isLast: { type: Boolean, default: false },
});

const emit = defineEmits(['preview-image']);

// Both transcript surfaces use the same sanitized Markdown pipeline.
const sourceHtml = computed(() => renderMarkdown(props.segment.markdown, {
	context: props.item.role === 'user' ? 'user' : 'content',
}));
const shouldDeferHtml = computed(() => props.item.role === 'assistant' && props.item.isThinking);
const html = useDeferredHtml(sourceHtml, shouldDeferHtml);

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
	<div class="body body-segment markdown-content" :class="{ first: isFirst, last: isLast }" @click="onClick"
		v-html="html"></div>
</template>
