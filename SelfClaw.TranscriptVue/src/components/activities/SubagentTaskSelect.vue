<script setup>
import { computed, ref, watch } from 'vue';
import { ChevronLeft, ChevronRight } from 'lucide-vue-next';
import { activityTimingLabel } from '../../renderers/activityLabels.js';

const props = defineProps({
	tasks: { type: Array, default: () => [] },
	selectedTaskId: { type: String, default: null },
	// 当前选中任务的完整对象：翻页后它可能不在本页列表里，靠它补一个占位项并显示阶段 / 时长。
	selectedTask: { type: Object, default: null },
	now: { type: Number, default: 0 },
	pageIndex: { type: Number, default: 0 },
	pageLoading: { type: Boolean, default: false },
	hasNextPage: { type: Boolean, default: false },
});
const emit = defineEmits(['select', 'page']);

const selectRef = ref(null);
const current = computed(() => props.tasks.find((task) => task.taskId === props.selectedTaskId) ?? props.selectedTask ?? null);
const missingFromPage = computed(() => Boolean(props.selectedTaskId) && !props.tasks.some((task) => task.taskId === props.selectedTaskId));
const empty = computed(() => !props.tasks.length && !props.selectedTask);
const paged = computed(() => props.pageIndex > 0 || props.hasNextPage);
// 框外只放时长：阶段文字来回跳观感差，停止键与时长已足够说明状态；阶段仍以 data-phase 挂在选项上。
const timing = computed(() => (current.value ? activityTimingLabel(current.value, props.now) : ''));
// 下拉框里只放名称；阶段与时长在框外，免得选项又长又挤。
const optionLabel = (task) => `${task.subagentName || task.subagentId}${task.attempt > 1 ? ` #${task.attempt}` : ''}`;

// 原生 select 的 value 要在 option 列表渲染完之后再同步，否则翻页换了选项会显示成空。
watch(() => [props.selectedTaskId, props.tasks], () => {
	if (selectRef.value) selectRef.value.value = props.selectedTaskId ?? '';
}, { flush: 'post' });

function onChange(event) {
	emit('select', event.target.value || null);
}
</script>

<template>
	<div class="task-picker" :aria-busy="pageLoading">
		<select ref="selectRef" class="task-select" :value="selectedTaskId ?? ''" :disabled="empty" aria-label="选择子代理任务"
			@change="onChange">
			<option v-if="empty" value="">暂无任务</option>
			<option v-if="missingFromPage && selectedTask" :value="selectedTask.taskId" :data-status="selectedTask.status"
				:data-phase="selectedTask.phase" data-missing="true">
				{{ optionLabel(selectedTask) }}（不在本页）
			</option>
			<option v-for="task in tasks" :key="task.taskId" :value="task.taskId" :data-status="task.status" :data-phase="task.phase">
				{{ optionLabel(task) }}
			</option>
		</select>
		<span v-if="timing" class="task-timing" :title="timing">{{ timing }}</span>
		<span v-if="paged" class="task-pager">
			<button type="button" title="上一页" aria-label="上一页任务" :disabled="!pageIndex || pageLoading" @click="emit('page', -1)">
				<ChevronLeft :size="14" />
			</button>
			<span>{{ pageLoading ? '…' : pageIndex + 1 }}</span>
			<button type="button" title="下一页" aria-label="下一页任务" :disabled="!hasNextPage || pageLoading" @click="emit('page', 1)">
				<ChevronRight :size="14" />
			</button>
		</span>
	</div>
</template>

<style scoped>
.task-picker { display: flex; flex: 1 1 auto; align-items: center; gap: 8px; min-width: 0; }
.task-select { flex: 0 1 auto; width: 150px; min-width: 96px; max-width: 46%; height: 26px; padding: 0 6px; border: 1px solid var(--border-strong); border-radius: 6px; background: var(--panel); color: var(--text); font-family: inherit; font-size: var(--fs-12); font-weight: 400; cursor: pointer; }
.task-select:hover { border-color: var(--muted-soft); }
.task-select:focus-visible { outline: none; border-color: var(--accent); box-shadow: 0 0 0 3px var(--accent-soft); }
.task-select:disabled { color: var(--muted); cursor: default; }
.task-timing { flex: 0 1 auto; min-width: 0; overflow: hidden; color: var(--muted); font-family: var(--font-mono); font-size: var(--fs-105); text-overflow: ellipsis; white-space: nowrap; }
.task-pager { flex: none; display: inline-flex; align-items: center; gap: 2px; color: var(--muted); font-size: var(--fs-11); }
.task-pager button { display: grid; place-items: center; width: 22px; height: 22px; border: 0; background: transparent; color: var(--text); }
.task-pager button:disabled { opacity: .35; }
</style>
