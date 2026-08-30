<script setup>
import { computed } from 'vue';
import { Sparkles, ChevronRight } from 'lucide-vue-next';
import { useDeferredHtml } from '../../../composables/useDeferredHtml.js';
import { renderMarkdown } from '../../../renderers/markdown.js';
import { resolvePreviewImage } from './previewImage.js';

const props = defineProps({
	id: { type: String, required: true },
	segment: { type: Object, required: true },
	item: { type: Object, required: true },
	isLast: { type: Boolean, default: false },
	open: { type: Boolean, default: false },
});

const emit = defineEmits(['toggle', 'preview-image']);

// 复刻旧 renderThinkingSegment 的判定：有内容才可展开；无内容但仍在思考时显示被动占位。
const isPending = () => Boolean(props.segment.isPending);
const isLive = () => isPending() && props.item.isThinking;
const hasContent = () => Boolean(props.segment.markdown);
const shouldRender = () => hasContent() || isLive();
const label = () => (isLive() ? '思考中...' : '思考完毕');
const sourceHtml = computed(() => props.segment.markdown
	? renderMarkdown(props.segment.markdown, { context: 'thinking' })
	: '<p class="thinking-placeholder">Thinking content is streaming.</p>');
const shouldDeferHtml = computed(() => isLive());
const contentHtml = useDeferredHtml(sourceHtml, shouldDeferHtml);

function onContentClick(event) {
	const preview = resolvePreviewImage(event.target);
	if (preview) {
		event.preventDefault();
		emit('preview-image', preview);
	}
}
</script>

<template>
	<section v-if="shouldRender()" class="thinking-block"
		:class="[{ open, pending: isPending(), last: isLast, 'no-content': !hasContent() }]"
		:data-thinking-id="hasContent() ? id : null">
		<button v-if="hasContent()" class="thinking-summary" type="button" :aria-expanded="open ? 'true' : 'false'"
			@click="emit('toggle')">
			<span class="thinking-spark" :class="{ live: isLive() }" aria-hidden="true">
				<Sparkles :size="13" :stroke-width="2" />
			</span>
			<span class="thinking-label" :class="{ 'shimmer-text': isLive() }">{{ label() }}</span>
			<ChevronRight class="thinking-chevron" :size="14" :stroke-width="2" aria-hidden="true" />
		</button>
		<div v-else class="thinking-summary passive">
			<span class="thinking-spark" :class="{ live: isLive() }" aria-hidden="true">
				<Sparkles :size="13" :stroke-width="2" />
			</span>
			<span class="thinking-label" :class="{ 'shimmer-text': isLive() }">{{ label() }}</span>
		</div>
		<div v-if="hasContent() && open" class="thinking-content" @click="onContentClick">
			<div class="thinking-markdown markdown-content" v-html="contentHtml"></div>
		</div>
	</section>
</template>
