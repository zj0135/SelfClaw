<script setup>
import { computed } from 'vue';
import { ChevronRight } from 'lucide-vue-next';
import ToolStatusIcon from './ToolStatusIcon.vue';
import { splitSummaryLabel, toolStatusLabel } from '../../../renderers/transcript.js';

const props = defineProps({
	id: { type: String, required: true },
	segment: { type: Object, required: true },
	summaryLabel: { type: String, default: '' },
	nested: { type: Boolean, default: false },
	open: { type: Boolean, default: false },
});

const emit = defineEmits(['toggle']);

const status = computed(() => props.segment.status || 'completed');
const label = computed(() => splitSummaryLabel(props.summaryLabel || props.segment.text || '工具调用'));
const detailTitle = computed(() => props.segment.detailTitle || 'Tool');
const detailText = computed(() => props.segment.detailText || '暂无可展示的执行结果。');
const durationText = computed(() => props.segment.durationText || '');
const sourceText = computed(() => {
	if (!props.segment.sourceId) return '';
	const labels = { mcp: 'MCP', skill: 'Skill', plugin: 'Plugin' };
	return `${labels[props.segment.sourceKind] || 'Extension'} · ${props.segment.sourceId}`;
});
</script>

<template>
	<section
		class="tool-block"
		:class="[status, { open, nested }]"
		:data-tool-segment-id="id"
	>
		<button
			class="tool-summary"
			:class="{ nested }"
			type="button"
			:aria-expanded="open ? 'true' : 'false'"
			@click="emit('toggle')"
		>
			<span class="tool-summary-main">
				<ToolStatusIcon :status="status" />
				<span class="inline-tool-label">{{ label.primary || '工具调用' }}</span>
				<span v-if="label.secondary" class="tool-summary-detail">{{ label.secondary }}</span>
			</span>
			<span class="tool-summary-side">
				<span v-if="durationText" class="tool-summary-duration">{{ durationText }}</span>
				<ChevronRight class="tool-summary-chevron" :size="14" :stroke-width="2" aria-hidden="true" />
			</span>
		</button>
		<div v-if="open" class="tool-details">
			<div class="tool-details-header">
				<span>{{ detailTitle }}</span>
				<small v-if="sourceText" class="tool-source">{{ sourceText }}</small>
			</div>
			<div class="tool-details-body">
				<pre class="tool-details-pre"><code>{{ detailText }}</code></pre>
			</div>
			<div class="tool-details-footer">
				<span class="tool-details-status" :class="status">{{ toolStatusLabel(status) }}</span>
			</div>
		</div>
	</section>
</template>

<style scoped>
.tool-details-header { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.tool-source { color: var(--text-muted); font-size: 10px; font-weight: 500; letter-spacing: 0; }
</style>
