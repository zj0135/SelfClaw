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

<style scoped>
.thinking-block {
	margin: 0;
	overflow: hidden;
	border: 1px solid var(--card-line);
	border-radius: 12px;
	background: var(--card-surface);
	transition: border-color 0.15s;
}

.thinking-block:not(.pending):hover {
	border-color: var(--card-line-hover);
}

.thinking-block.last {
	margin-bottom: 6px;
}

.thinking-summary {
	width: 100%;
	display: flex;
	align-items: center;
	justify-content: flex-start;
	gap: 9px;
	padding: 9px 12px;
	border: 0;
	background: transparent;
	color: var(--text-soft);
	text-align: left;
}

.thinking-summary.passive {
	cursor: default;
}

.thinking-spark {
	display: inline-grid;
	place-items: center;
	width: 18px;
	height: 18px;
	color: var(--muted);
	flex: none;
}

.thinking-spark svg {
	width: 13px;
	height: 13px;
}

.thinking-spark.live {
	color: var(--accent);
	animation: spark-pulse 1.5s ease-in-out infinite;
}

@keyframes spark-pulse {
	50% {
		transform: scale(0.78);
		opacity: 0.6;
	}
}

.thinking-label {
	font-size: var(--fs-125);
	font-weight: 600;
	color: var(--text-strong);
	letter-spacing: 0.01em;
}

.thinking-chevron {
	margin-left: auto;
	color: var(--faint);
	font-size: var(--fs-14);
	transition: transform 140ms ease;
}

.thinking-block.open .thinking-chevron {
	transform: rotate(90deg);
	color: var(--text);
}

.thinking-content {
	display: none;
	padding: 0 12px 11px;
}

.thinking-block.open .thinking-content {
	display: block;
}

.thinking-markdown {
	padding: 6px 0 2px 12px;
	border-left: 2px solid var(--quote-line);
	color: var(--muted);
	font-size: var(--fs-12);
	line-height: 1.7;
}

.thinking-placeholder {
	margin: 0;
	color: var(--muted-soft);
	font-size: var(--fs-12);
}
</style>
