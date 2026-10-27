<script setup>
import { Circle, CircleDot, CircleCheck } from 'lucide-vue-next';

// 纯展示列表：dock 与内联卡片共用同一份归一化条目与同一套状态样式。
// 传入的 items 必须已经过 renderers/todo.js 的 normalizeTodoItems。
defineProps({
	items: { type: Array, default: () => [] },
	// 内联卡片里行距与字号再收一档（见 todo-plan.css 的 .compact）。
	compact: { type: Boolean, default: false },
});

const statusIcons = { pending: Circle, in_progress: CircleDot, completed: CircleCheck };
const iconFor = (status) => statusIcons[status] || Circle;
</script>

<template>
	<ul class="todo-plan-list" :class="{ compact }">
		<li v-for="(item, index) in items" :key="`${index}:${item.content}`" class="todo-plan-item"
			:class="item.status">
			<component :is="iconFor(item.status)" class="todo-plan-item-icon" :size="compact ? 12 : 13"
				:stroke-width="1.9" aria-hidden="true" />
			<span class="todo-plan-item-copy">
				<span class="todo-plan-item-text">{{ item.content }}</span>
				<span v-if="item.status === 'in_progress' && item.activeForm" class="todo-plan-item-active">{{
					item.activeForm }}</span>
			</span>
		</li>
	</ul>
</template>

<style scoped>
@import '../../../styles/todo-plan.css';
</style>
