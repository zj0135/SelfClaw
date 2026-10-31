<script setup>
import { computed } from 'vue';
import { ChevronRight } from 'lucide-vue-next';
import ToolStatusIcon from './ToolStatusIcon.vue';
import TodoPlanList from '../todo/TodoPlanList.vue';
import { normalizeTodoItems, todoCounts, todoProgressLabel } from '../../../renderers/todo.js';

const props = defineProps({
	id: { type: String, required: true },
	// 内联 todo 段的 payload：{ kind:'todo', text, status, detailText, todo:{ items } }
	segment: { type: Object, required: true },
	// 父级（useTranscriptCollapse）持有的开合状态：默认折叠。
	open: { type: Boolean, default: false },
});

const emit = defineEmits(['toggle']);

const status = computed(() => props.segment.status || 'completed');
const items = computed(() => normalizeTodoItems(props.segment.todo));
const label = computed(() => props.segment.text || '更新任务计划');

// 计划被清空（items 为空数组）时只留一行说明，不给一个点了也没内容的按钮。
const empty = computed(() => items.value.length === 0);
const progressChip = computed(() => {
	const progress = todoProgressLabel(todoCounts(items.value));
	return progress && !label.value.includes(progress) ? progress : '';
});

const failureDetail = computed(() => (status.value === 'failed' ? String(props.segment.detailText || '').trim() : ''));
</script>

<template>
	<section class="todo-block" :class="[status, { open }]" :data-todo-segment-id="id">
		<button v-if="!empty" class="todo-summary" type="button" :aria-expanded="open ? 'true' : 'false'"
			@click="emit('toggle')">
			<span class="todo-summary-main">
				<ToolStatusIcon :status="status" />
				<span class="inline-todo-label">{{ label }}</span>
			</span>
			<span class="todo-summary-side">
				<span v-if="progressChip" class="todo-summary-progress">{{ progressChip }}</span>
				<ChevronRight class="todo-summary-chevron" :size="14" :stroke-width="2" aria-hidden="true" />
			</span>
		</button>
		<div v-else class="todo-summary">
			<span class="todo-summary-main">
				<ToolStatusIcon :status="status" />
				<span class="inline-todo-label">已清空任务计划</span>
			</span>
		</div>
		<transition name="todo-details">
			<div v-if="open && !empty" class="todo-details">
				<TodoPlanList :items="items" compact />
				<p v-if="failureDetail" class="todo-details-note">{{ failureDetail }}</p>
			</div>
		</transition>
	</section>
</template>

<style scoped>
@import '../../../styles/todo-plan.css';
</style>
