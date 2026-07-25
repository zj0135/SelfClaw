<script setup>
import { ChevronRight } from 'lucide-vue-next';
import ToolStatusIcon from './ToolStatusIcon.vue';
import ToolCard from './ToolCard.vue';

const props = defineProps({
	// 编排产出的 tool-group 块：{ id, members:[{id,segment}], status, summaryLabel }
	block: { type: Object, required: true },
	// 折叠状态的单一载体（useTranscriptCollapse 的返回值）。
	collapse: { type: Object, required: true },
});
</script>

<template>
	<section
		class="tool-group-block"
		:class="[block.status, { open: collapse.isToolGroupOpen(block.id) }]"
		:data-tool-group-id="block.id"
	>
		<button
			class="tool-group-summary"
			type="button"
			:aria-expanded="collapse.isToolGroupOpen(block.id) ? 'true' : 'false'"
			@click="collapse.toggleToolGroup(block.id)"
		>
			<span class="tool-group-summary-main">
				<ToolStatusIcon :status="block.status" />
				<span class="tool-group-label">{{ block.summaryLabel }}</span>
			</span>
			<span class="tool-group-summary-side">
				<ChevronRight class="tool-group-chevron" :size="14" :stroke-width="2" aria-hidden="true" />
			</span>
		</button>
		<div v-if="collapse.isToolGroupOpen(block.id)" class="tool-group-details">
			<ToolCard
				v-for="member in block.members"
				:key="member.id"
				:id="member.id"
				:segment="member.segment"
				nested
				:open="collapse.isToolOpen(member.id)"
				@toggle="collapse.toggleTool(member.id)"
			/>
		</div>
	</section>
</template>
