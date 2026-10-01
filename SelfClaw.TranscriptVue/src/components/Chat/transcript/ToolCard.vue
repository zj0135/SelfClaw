<script setup>
import { computed } from 'vue';
import { ChevronRight, Webhook } from 'lucide-vue-next';
import ToolStatusIcon from './ToolStatusIcon.vue';
import ToolHookDetails from './ToolHookDetails.vue';
import { splitSummaryLabel } from '../../../renderers/transcript.js';

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
const detailText = computed(() => props.segment.detailText || '暂无可展示的执行结果。');
const durationText = computed(() => props.segment.durationText || '');
const hook = computed(() => props.segment.hook || null);
const hasHookIntervention = computed(() => Boolean(
	props.segment.hook?.blockedBy
	|| props.segment.hook?.blockReason
	|| props.segment.hook?.argumentsModifiedBy?.length
	|| props.segment.hook?.approvalRequiredBy?.length
	|| props.segment.hook?.feedback?.length
	|| props.segment.hook?.ignoredFailures?.length,
));
</script>

<template>
	<section class="tool-block" :class="[status, { open, nested }]" :data-tool-segment-id="id">
		<button class="tool-summary" :class="{ nested }" type="button" :aria-expanded="open ? 'true' : 'false'"
			@click="emit('toggle')">
			<span class="tool-summary-main">
				<ToolStatusIcon :status="status" />
				<span class="inline-tool-label">{{ label.primary || '工具调用' }}</span>
				<span v-if="label.secondary" class="tool-summary-detail">{{ label.secondary }}</span>
			</span>
			<span class="tool-summary-side">
				<Webhook v-if="hasHookIntervention" class="tool-summary-hook" :size="13" :stroke-width="1.9"
					aria-label="插件 hook 干预" />
				<span v-if="durationText" class="tool-summary-duration">{{ durationText }}</span>
				<ChevronRight class="tool-summary-chevron" :size="14" :stroke-width="2" aria-hidden="true" />
			</span>
		</button>
		<div v-if="open" class="tool-details">
			<div class="tool-details-body">
				<pre class="tool-details-pre"><code>{{ detailText }}</code></pre>
			</div>
			<ToolHookDetails v-if="hook" :hook="hook" />
		</div>
	</section>
</template>

<style scoped>
@import './tool-blocks.css';
.tool-summary-hook {
	flex: none;
	color: var(--caution-icon);
}
</style>
