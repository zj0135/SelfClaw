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
/* 与工具行同构的纯文本行：图标 + 标签 + 箭头，行底一条 alpha 发丝线，展开内容沿导线缩进。 */
.thinking-block {
	min-width: 0;
	border-bottom: 1px solid var(--line-1);
}

.thinking-summary {
	display: flex;
	width: 100%;
	align-items: center;
	justify-content: flex-start;
	gap: 8px;
	padding: 5px 0;
	border: 0;
	background: transparent;
	color: var(--text-soft);
	text-align: left;
}

.thinking-summary.passive {
	cursor: default;
}

.thinking-summary:focus-visible {
	outline: 1px solid var(--accent-line);
	outline-offset: 2px;
	border-radius: 3px;
}

.thinking-spark {
	display: inline-grid;
	place-items: center;
	width: 18px;
	height: 18px;
	flex: none;
	color: var(--muted-soft);
	transition: color 120ms ease;
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
	color: var(--text-soft);
	letter-spacing: 0.01em;
	transition: color 120ms ease;
}

.thinking-summary:hover .thinking-label,
.thinking-block.open .thinking-label {
	color: var(--text-strong);
}

.thinking-chevron {
	margin-left: auto;
	color: var(--faint);
	transition: transform 140ms ease, color 120ms ease;
}

.thinking-summary:hover .thinking-chevron {
	color: var(--muted);
}

.thinking-block.open .thinking-chevron {
	transform: rotate(90deg);
	color: var(--muted);
}

.thinking-content {
	margin: 2px 0 4px 26px;
	padding: 2px 0 2px 12px;
	border-left: 1px solid var(--line-2);
}

.thinking-markdown {
	color: var(--muted);
	font-size: var(--fs-125);
	line-height: 1.72;
}

.thinking-placeholder {
	margin: 0;
	color: var(--muted-soft);
	font-size: var(--fs-12);
}
</style>
