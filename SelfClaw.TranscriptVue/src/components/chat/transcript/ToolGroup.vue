<script setup>
import { computed } from 'vue';
import { ChevronRight } from 'lucide-vue-next';
import ToolStatusIcon from './ToolStatusIcon.vue';
import ToolCard from './ToolCard.vue';

const props = defineProps({
	// 编排产出的 tool-group 块：{ id, members:[{id,segment}], status, isActive, summaryLabel }
	block: { type: Object, required: true },
	// 折叠状态的单一载体（useTranscriptCollapse 的返回值）。
	collapse: { type: Object, required: true },
});

// 组本身跟随忙碌态自动开合；组内成员不自动展开，只保留手动展开能力。
const groupOpen = computed(() => props.collapse.isToolGroupOpen(props.block.id, props.block.isActive));
function memberOpen(id) { return props.collapse.isToolOpen(id, false); }
function toggleMember(id) { props.collapse.toggleTool(id, false); }
</script>

<template>
	<section class="tool-group-block" :class="[block.status, { open: groupOpen }]"
		:data-tool-group-id="block.id">
		<button class="tool-group-summary" type="button"
			:aria-expanded="groupOpen ? 'true' : 'false'"
			@click="collapse.toggleToolGroup(block.id, block.isActive)">
			<span class="tool-group-summary-main">
				<ToolStatusIcon :status="block.status" />
				<span class="tool-group-label">{{ block.summaryLabel }}</span>
			</span>
			<span class="tool-group-summary-side">
				<ChevronRight class="tool-group-chevron" :size="14" :stroke-width="2" aria-hidden="true" />
			</span>
		</button>
		<div v-if="groupOpen" class="tool-group-details">
			<ToolCard v-for="member in block.members" :key="member.id" :id="member.id" :segment="member.segment" nested
				:open="memberOpen(member.id)" @toggle="toggleMember(member.id)" />
		</div>
	</section>
</template>

<style scoped>
@import '../../../styles/tool-blocks.css';
</style>
