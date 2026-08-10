<script setup>
import { computed } from 'vue';
import { useDeferredHtml } from '../../../composables/useDeferredHtml.js';
import { renderSkillTokensInUserHtml } from '../../../renderers/transcript.js';
import { resolvePreviewImage } from './previewImage.js';

const props = defineProps({
	item: { type: Object, required: true },
	segment: { type: Object, required: true },
	isFirst: { type: Boolean, default: false },
	isLast: { type: Boolean, default: false },
});

const emit = defineEmits(['preview-image']);

// 正文富文本是后端渲染好的 HTML，只能 v-html 注入；用户消息再叠一层 skill-token → chip 替换。
const sourceHtml = computed(() =>
	props.item.role === 'user' ? renderSkillTokensInUserHtml(props.segment.html) : props.segment.html);
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
	<div
		class="body body-segment"
		:class="{ first: isFirst, last: isLast }"
		@click="onClick"
		v-html="html"
	></div>
</template>
