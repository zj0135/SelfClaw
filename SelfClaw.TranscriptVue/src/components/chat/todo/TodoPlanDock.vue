<script setup>
import { computed, useId } from 'vue';
import { ChevronUp, ListChecks } from 'lucide-vue-next';
import TodoPlanList from './TodoPlanList.vue';
import {
	normalizeTodoItems,
	todoActiveItem,
	todoAllCompleted,
	todoCounts,
	todoProgressLabel,
} from '../../../renderers/todo.js';

const props = defineProps({
	// 顶层渲染状态里的计划：{ items: [...] }；后端无计划时整个字段省略（null）。
	todo: { type: Object, default: null },
	// 回合是否在跑：只影响视觉语气（进行中的图标/进度提亮），不影响可见性。
	busy: { type: Boolean, default: false },
	open: { type: Boolean, default: false },
});

const emit = defineEmits(['toggle']);

const items = computed(() => normalizeTodoItems(props.todo));
const counts = computed(() => todoCounts(items.value));
const progressLabel = computed(() => todoProgressLabel(counts.value));
const progressWidth = computed(() => (counts.value.total > 0
	? `${Math.round((counts.value.completed / counts.value.total) * 100)}%`
	: '0%'));
// 无计划不渲染；全部完成也不渲染——收尾的历史留在转录的内联卡片里，dock 只服务「当下」。
const visible = computed(() => items.value.length > 0 && !todoAllCompleted(items.value));
// 折叠时头部只剩这一行，用户看得见 2/5 却看不见「在做什么」——「正在进行的那一步」
// 必须顶到标题旁，否则 dock 的核心信息要靠展开才拿得到。展开后列表行已经标出该项，
// 头部不再重复。
const activeLabel = computed(() => {
	if (props.open) {
		return '';
	}

	const active = todoActiveItem(items.value);
	return active ? active.activeForm || active.content : '';
});
const listId = useId();
</script>

<template>
	<transition name="todo-plan-dock">
		<section v-if="visible" class="todo-plan-dock" :class="{ open, busy }" aria-label="任务计划">
			<button class="todo-plan-head" type="button" :aria-expanded="open ? 'true' : 'false'"
				:aria-controls="listId" @click="emit('toggle')">
				<span class="todo-plan-head-main">
					<ListChecks class="todo-plan-head-icon" :size="14" :stroke-width="1.9" aria-hidden="true" />
					<span class="todo-plan-title">任务计划</span>
					<!-- 播报区同时包住「在做哪一步」与计数：只播计数会漏掉「同一计数下换了当前项」这种更新。 -->
					<span class="todo-plan-status" role="status" aria-live="polite">
						<span v-if="activeLabel" class="todo-plan-active">{{ activeLabel }}</span>
						<span class="todo-plan-progress">{{ progressLabel }}</span>
					</span>
				</span>
				<ChevronUp class="todo-plan-chevron" :size="14" :stroke-width="2" aria-hidden="true" />
			</button>
			<span class="todo-plan-meter" aria-hidden="true">
				<span class="todo-plan-meter-fill" :style="{ width: progressWidth }"></span>
			</span>
			<transition name="todo-plan-body">
				<div v-if="open" :id="listId" class="todo-plan-scroll">
					<TodoPlanList :items="items" />
				</div>
			</transition>
		</section>
	</transition>
</template>

<style scoped>
@import '../../../styles/todo-plan.css';
</style>
