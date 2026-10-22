<script setup>
import { computed, ref } from 'vue';
import { ChevronRight } from 'lucide-vue-next';
import { renderMarkdown } from '../../../renderers/markdown.js';
import { formatCompressionNotice } from '../../../renderers/usageMeter.js';

const props = defineProps({ notice: { type: Object, default: null } });
const presentation = computed(() => formatCompressionNotice(props.notice));
const summaryHtml = computed(() => (presentation.value?.hasSummary
	? renderMarkdown(props.notice.summary, { context: 'content' })
	: ''));
const open = ref(false);

function toggle() {
	if (!presentation.value?.hasSummary) {
		return;
	}

	open.value = !open.value;
}
</script>

<template>
	<div v-if="presentation" class="compression-notice">
		<!-- 有摘要才是可展开控件：没有摘要时退化为普通分隔行，避免出现空按钮。 -->
		<component :is="presentation.hasSummary ? 'button' : 'div'" class="compression-summary"
			:type="presentation.hasSummary ? 'button' : null"
			:aria-expanded="presentation.hasSummary ? (open ? 'true' : 'false') : null"
			:title="presentation.title || null" @click="toggle">
			<span class="compression-rule" aria-hidden="true"></span>
			<span class="compression-head">
				<span class="compression-label">{{ presentation.label }}</span>
				<span class="compression-subtitle">{{ presentation.subtitle }}</span>
				<ChevronRight v-if="presentation.hasSummary" class="compression-chevron" :class="{ open }"
					:size="13" :stroke-width="2" aria-hidden="true" />
			</span>
			<span class="compression-rule" aria-hidden="true"></span>
		</component>
		<div v-if="open && presentation.hasSummary" class="compression-content">
			<div class="compression-markdown markdown-content" v-html="summaryHtml"></div>
		</div>
	</div>
</template>

<style scoped>
/* 分隔行：两侧发丝线夹一段居中标签，展开的摘要沿导线缩进，与工具卡同一套语言。 */
.compression-notice {
	flex: 1 1 auto;
	min-width: 0;
}

.compression-summary {
	display: flex;
	width: 100%;
	align-items: center;
	gap: 12px;
	padding: 4px 0;
	border: 0;
	background: transparent;
	color: var(--text-muted, #777);
	font-size: 12px;
	line-height: 1.6;
	cursor: pointer;
}

div.compression-summary {
	cursor: default;
}

.compression-summary:focus-visible {
	outline: 1px solid var(--accent-line, rgba(127, 127, 127, 0.4));
	outline-offset: 2px;
	border-radius: 3px;
}

.compression-rule {
	flex: 1 1 auto;
	height: 1px;
	background: var(--line-1, rgba(127, 127, 127, 0.18));
}

.compression-head {
	display: inline-flex;
	flex: none;
	align-items: baseline;
	gap: 10px;
}

.compression-label {
	font-weight: 600;
	letter-spacing: 0.04em;
}

.compression-subtitle {
	font-variant-numeric: tabular-nums;
}

.compression-chevron {
	align-self: center;
	color: var(--faint, #999);
	transition: transform 140ms ease, color 120ms ease;
}

.compression-summary:hover .compression-chevron,
.compression-summary:hover .compression-label {
	color: var(--text-strong, #333);
}

.compression-chevron.open {
	transform: rotate(90deg);
}

.compression-content {
	margin: 8px 0 2px 12px;
	padding: 2px 0 2px 12px;
	border-left: 1px solid var(--line-2, rgba(127, 127, 127, 0.24));
}

.compression-markdown {
	color: var(--text-muted, #777);
	font-size: 12px;
	line-height: 1.72;
}
</style>
