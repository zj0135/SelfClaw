<script setup>
import { computed } from 'vue';
import { buildRenderBlocks } from '../../../renderers/transcript.js';
import ThinkingBlock from './ThinkingBlock.vue';
import ToolGroup from './ToolGroup.vue';
import ToolCard from './ToolCard.vue';
import BodySegment from './BodySegment.vue';

const props = defineProps({ item: { type: Object, required: true }, collapse: { type: Object, required: true }, compact: Boolean });
const emit = defineEmits(['preview-image']);
const blocks = computed(() => buildRenderBlocks(props.item));
</script>

<template>
	<div class="message-blocks" :class="{ compact }">
		<template v-for="block in blocks" :key="block.key">
			<ThinkingBlock v-if="block.type === 'thinking'" :data-activity-block-id="compact ? block.key : null" :id="block.id" :item="item" :segment="block.segment"
				:is-last="block.isLast" :open="collapse.isThinkingOpen(block.id)" @toggle="collapse.toggleThinking(block.id)"
				@preview-image="emit('preview-image', $event)" />
			<ToolGroup v-else-if="block.type === 'tool-group'" :data-activity-block-id="compact ? block.key : null" :block="block" :collapse="collapse" />
			<ToolCard v-else-if="block.type === 'tool'" :data-activity-block-id="compact ? block.key : null" :id="block.id" :segment="block.segment" :summary-label="block.summaryLabel"
				:open="collapse.isToolOpen(block.id)" @toggle="collapse.toggleTool(block.id)" />
			<BodySegment v-else :data-activity-block-id="compact ? block.key : null" :item="item" :segment="block.segment" :is-first="block.isFirst" :is-last="block.isLast"
				@preview-image="emit('preview-image', $event)" />
		</template>
	</div>
</template>

<style scoped>
.message-blocks { display: grid; gap: 8px; min-width: 0; }
.compact { font-size: var(--fs-12); gap: 6px; }
.compact :deep(.thinking-block), .compact :deep(.tool-block), .compact :deep(.tool-group-block), .compact :deep(.tool-block.nested) {
	border: 0; border-radius: 0; border-bottom: 1px solid var(--border); background: transparent;
}
.compact :deep(.thinking-summary), .compact :deep(.tool-summary), .compact :deep(.tool-group-summary) { padding: 7px 0; }
.compact :deep(.thinking-label), .compact :deep(.inline-tool-label), .compact :deep(.tool-group-label) { font-size: var(--fs-12); letter-spacing: 0; }
.compact :deep(.thinking-content), .compact :deep(.tool-details), .compact :deep(.tool-group-details) { padding: 2px 0 8px; }
.compact :deep(.tool-details-body) { border: 0; border-radius: 0; background: var(--panel-soft); }
.compact :deep(.body.body-segment) { padding: 0; font-size: var(--fs-12); line-height: 1.65; overflow-wrap: anywhere; }
.compact :deep(pre) { max-width: 100%; white-space: pre-wrap; overflow-wrap: anywhere; }
.compact :deep(img) { max-width: 100%; height: auto; }
.compact :deep(table) { display: block; max-width: 100%; overflow: auto; }
</style>
