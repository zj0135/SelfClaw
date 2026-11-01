<script setup>
import { computed, watch } from 'vue';
import { LoaderCircle, RefreshCw, Square } from 'lucide-vue-next';
import SubagentTaskDetail from '../activities/SubagentTaskDetail.vue';
import SubagentTaskSelect from '../activities/SubagentTaskSelect.vue';
import { useActivityClock } from '../../composables/useActivityClock.js';
import { useActivityDetail } from '../../composables/useActivityDetail.js';
import { useActivityPanel } from '../../composables/useActivityPanel.js';
import { useActivityPreferences } from '../../composables/useActivityPreferences.js';
import { useSideDock } from '../../composables/useSideDock.js';
import { useSubagentActivity } from '../../composables/useSubagentActivity.js';
import { activityErrorLabel } from '../../renderers/activityLabels.js';

const props = defineProps({
	parentConversationId: { type: String, default: null },
	// dock 打开且停留在子代理 tab 时为 true；切走时清详情、保留阅读位置（useActivityDetail 语义）。
	visible: { type: Boolean, default: false },
});
const emit = defineEmits(['preview-image']);

const dock = useSideDock();
const now = useActivityClock();
const parent = computed(() => props.parentConversationId);
const visible = computed(() => props.visible);
const panel = useActivityPanel(parent);
const activity = useSubagentActivity(panel);
const preferences = useActivityPreferences(parent);
const detail = useActivityDetail(panel, preferences, visible);
const section = computed(() => panel.state.value.sections.find((item) => item.kind === 'subagents') ?? null);
const tasks = computed(() => section.value?.tasks ?? []);
const selectedTaskId = computed(() => detail.selectedTaskId.value);
// 选中任务的完整对象：详情到了用详情里的（带实时阶段 / token），否则用宿主回显的 selectedTask；翻页后它可能不在本页。
const selectedTask = computed(() => detail.displayed.value?.task ?? section.value?.selectedTask
	?? tasks.value.find((task) => task.taskId === selectedTaskId.value) ?? null);
const cancelling = computed(() => Boolean(selectedTaskId.value && activity.cancelling.value.has(selectedTaskId.value)));
const emptyText = computed(() => {
	if (!props.parentConversationId) return '开始一个会话后，这里会显示子代理活动';
	return tasks.value.length ? '选择一个任务查看活动' : '暂无子代理活动';
});

// 面板收起时订阅仍热（本组件常驻挂载），把任务存在感写给悬浮快捷栏的角点。
watch(() => section.value?.counts, (counts) => dock.setSubagentCounts(counts), { immediate: true });

// 下拉框驱动的面板不该空着：可见且没有选中时自动选列表第一项（选择记入偏好，下次可见时恢复）。
watch([visible, tasks, selectedTaskId], ([isVisible, list, selected]) => {
	if (isVisible && !selected && list.length) detail.selectTask(list[0].taskId);
}, { immediate: true });
</script>

<template>
	<div class="subagent-pane" aria-label="子代理活动">
		<div class="pane-toolbar">
			<SubagentTaskSelect :tasks="tasks" :selected-task-id="selectedTaskId" :selected-task="selectedTask" :now="now"
				:page-index="activity.pageIndex.value" :page-loading="activity.pageLoading.value"
				:has-next-page="Boolean(section?.nextCursor)" @select="detail.selectTask" @page="activity.changePage" />
			<!-- 停止键固定在右侧、刷新键左边：红色实心方块，只在选中任务可取消时出现。 -->
			<button v-if="selectedTask?.canCancel" class="task-cancel" type="button" :disabled="cancelling" title="停止任务"
				aria-label="停止任务" @click="activity.cancelTask(selectedTask)">
				<LoaderCircle v-if="cancelling" :size="14" class="spin" />
				<Square v-else :size="11" :stroke-width="0" fill="currentColor" />
			</button>
			<button class="pane-refresh" type="button" title="刷新活动" aria-label="刷新活动" :disabled="panel.loading.value" @click="panel.refresh()">
				<RefreshCw :size="14" :class="{ spin: panel.loading.value }" />
			</button>
		</div>
		<p v-if="panel.error.value" class="pane-error" role="alert">{{ activityErrorLabel(panel.error.value) }}</p>
		<p v-if="activity.commandError.value" class="pane-error" role="alert">{{ activityErrorLabel(activity.commandError.value) }}</p>
		<div v-if="panel.loading.value && !section" class="pane-empty" role="status">加载中...</div>
		<SubagentTaskDetail v-else-if="selectedTaskId" :key="parentConversationId" class="pane-detail" :detail="detail"
			:collapse="preferences.collapse" @preview-image="emit('preview-image', $event)" />
		<div v-else class="pane-empty">{{ emptyText }}</div>
	</div>
</template>

<style scoped>
.subagent-pane { display: flex; flex-direction: column; min-height: 0; overflow: hidden; }
.pane-toolbar { display: flex; flex: 0 0 38px; align-items: center; gap: 6px; padding: 0 6px 0 8px; border-bottom: 1px solid var(--border); }
.pane-refresh { display: grid; place-items: center; width: 24px; height: 24px; flex: none; border: 0; border-radius: 6px; background: transparent; color: var(--muted); }
.pane-refresh:hover { background: var(--panel-hover); color: var(--text); }
.pane-refresh:disabled { opacity: .5; }
.task-cancel { display: grid; place-items: center; width: 24px; height: 24px; flex: none; border: 0; border-radius: 6px; background: transparent; color: var(--danger); }
.task-cancel:hover { background: color-mix(in srgb, var(--danger) 12%, transparent); }
.task-cancel:disabled { opacity: .5; }
.pane-error { margin: 0; padding: 8px 10px; border-bottom: 1px solid var(--border); color: var(--danger); font-size: var(--fs-11); overflow-wrap: anywhere; }
.pane-empty { padding: 18px 12px; color: var(--muted); font-size: var(--fs-12); }
.pane-detail { flex: 1 1 auto; min-height: 0; }
.spin { animation: activity-spin 1s linear infinite; }
@keyframes activity-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .spin { animation: none; } }
</style>
