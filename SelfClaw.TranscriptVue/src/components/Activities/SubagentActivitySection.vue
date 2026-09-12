<script setup>
import { ChevronLeft, ChevronRight } from 'lucide-vue-next';
import SubagentTaskRow from './SubagentTaskRow.vue';
import SubagentTaskDetail from './SubagentTaskDetail.vue';
import { useActivityClock } from '../../composables/useActivityClock.js';
import { activityErrorLabel } from '../../renderers/activityLabels.js';
defineProps({ section: { type: Object, required: true }, activity: { type: Object, required: true }, collapse: { type: Object, required: true }, scrollPositions: { type: Map, required: true } });
const emit = defineEmits(['select', 'preview-image']);
const now = useActivityClock();
</script>

<template>
	<div class="subagent-section" :class="{ 'with-detail': activity.selectedTaskId.value }">
		<div class="task-list" :aria-busy="activity.pageLoading.value">
			<div class="task-counts">
				<span>{{ section.counts.total }} 个任务</span>
				<span v-if="section.counts.failed" class="failed">{{ section.counts.failed }} 个失败</span>
				<span v-if="section.counts.queued">{{ section.counts.queued }} 个排队中</span>
			</div>
			<div class="task-rows">
				<SubagentTaskRow v-for="task in section.tasks" :key="task.taskId" :task="task"
					:selected="activity.selectedTaskId.value === task.taskId" :cancelling="activity.cancelling.value.has(task.taskId)"
					:now="now" @select="emit('select', $event)" @cancel="activity.cancelTask" />
			</div>
			<footer v-if="activity.pageIndex.value || section.nextCursor">
				<button type="button" title="上一页" aria-label="上一页任务" :disabled="!activity.pageIndex.value || activity.pageLoading.value" @click="activity.changePage(-1)"><ChevronLeft :size="14" /></button>
				<span>{{ activity.pageLoading.value ? '读取中...' : activity.pageIndex.value + 1 }}</span>
				<button type="button" title="下一页" aria-label="下一页任务" :disabled="!section.nextCursor || activity.pageLoading.value" @click="activity.changePage(1)"><ChevronRight :size="14" /></button>
			</footer>
			<p v-if="activity.commandError.value" role="alert">{{ activityErrorLabel(activity.commandError.value) }}</p>
		</div>
		<SubagentTaskDetail v-if="activity.selectedTaskId.value" :detail="activity.detail.value" :loading="activity.detailLoading.value"
			:invalidated="activity.detailInvalidated.value" :error="section.detailError" :collapse="collapse" :scroll-positions="scrollPositions"
			:read-content="activity.readContent" @close="emit('select', null)" @latest="activity.selectTask(activity.selectedTaskId.value)"
			@window="(offset, version) => activity.selectTask(activity.selectedTaskId.value, offset, version)" @preview-image="emit('preview-image', $event)" />
	</div>
</template>

<style scoped>
.subagent-section { display: grid; grid-template-columns: minmax(0, 1fr); min-height: 0; overflow: hidden; }
.with-detail { grid-template-columns: minmax(150px, .8fr) minmax(200px, 1.2fr); }
.task-list { display: flex; flex-direction: column; min-height: 0; min-width: 0; }.task-rows { overflow: auto; min-height: 0; overscroll-behavior: contain; }.task-counts { display: flex; flex-wrap: wrap; gap: 4px 10px; padding: 7px 10px; font-size: var(--fs-10); color: var(--muted); border-bottom: 1px solid var(--border); }.failed { color: var(--danger); }footer { display: flex; justify-content: center; align-items: center; gap: 10px; padding: 5px; border-top: 1px solid var(--border); font-size: var(--fs-11); }footer button { width: 25px; height: 25px; display: grid; place-items: center; border: 0; background: transparent; color: var(--text); }button:disabled { opacity: .35; }p { font-size: var(--fs-11); color: var(--danger); padding: 6px 10px; margin: 0; }
@container activity-stage (max-width: 480px) {
	.with-detail { grid-template-columns: minmax(0, 1fr); grid-template-rows: minmax(50px, 130px) minmax(100px, 1fr); }
	.with-detail :deep(.task-detail) { border-left: 0; border-top: 1px solid var(--border); }
}
</style>
