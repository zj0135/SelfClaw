<script setup>
import { computed, ref } from 'vue';
import { ArrowDown, ArrowUp, X } from 'lucide-vue-next';
import MessageBlocks from '../Chat/transcript/MessageBlocks.vue';
import ActivityContentReader from './ActivityContentReader.vue';
import { useActivityDetailScroll } from '../../composables/useActivityDetailScroll.js';
import { activityStatusLabel, deliveryStatusLabel, activityErrorLabel } from '../../renderers/activityLabels.js';
const props = defineProps({ detail: { type: Object, required: true }, collapse: { type: Object, required: true } });
const emit = defineEmits(['preview-image']);
const scroll = ref(null);
const { onScroll } = useActivityDetailScroll(props.detail, scroll);
const displayed = props.detail.displayed;
const unplacedMessage = computed(() => ({ id: `${displayed.value?.taskId}:unplaced`, role: 'assistant', status: 'completed', segments: displayed.value?.unplacedTools || [] }));
</script>

<template>
	<aside class="task-detail" aria-label="任务详情">
		<header><strong>{{ displayed?.task.subagentName || '任务详情' }}</strong><button type="button" aria-label="关闭任务详情" title="关闭任务详情" @click="detail.closeDetail"><X :size="14" /></button></header>
		<div ref="scroll" class="detail-scroll" @scroll="onScroll">
			<div class="detail-content">
			<p v-if="detail.loading.value" class="detail-state" role="status">读取详情...</p><p v-if="detail.error.value" class="detail-error" role="alert">{{ activityErrorLabel(detail.error.value) }}</p>
			<template v-if="displayed">
				<div class="detail-meta"><span>{{ activityStatusLabel(displayed.task.phase) }}</span><span v-if="displayed.task.modelDisplayName">{{ displayed.task.modelDisplayName }}</span><span v-if="displayed.task.inputTokens != null">输入 {{ displayed.task.inputTokens }}</span><span v-if="displayed.task.outputTokens != null">输出 {{ displayed.task.outputTokens }}</span><span v-if="displayed.task.deliveryStatus !== 'none'">{{ deliveryStatusLabel(displayed.task.deliveryStatus) }}</span></div>
				<p v-if="displayed.historyCompleteness === 'partial'" class="history-warning">历史记录不完整</p>
				<p v-if="displayed.task.errorMessage || displayed.task.recordingError" class="detail-error">{{ displayed.task.errorMessage || displayed.task.recordingError }}</p>
				<p v-if="displayed.task.deliveryError" class="detail-error">{{ displayed.task.deliveryError }}</p>
				<p class="task-text">{{ displayed.taskText }}</p>
				<button v-if="displayed.earlierOffset != null" class="window-link" type="button" :disabled="detail.loading.value" @click="detail.readEarlier"><ArrowUp :size="12" />更早内容</button>
				<MessageBlocks v-if="displayed.message" :item="displayed.message" :collapse="collapse" compact @preview-image="emit('preview-image', $event)" />
				<template v-if="displayed.unplacedTools.length"><h4>已记录工具</h4><MessageBlocks :item="unplacedMessage" :collapse="collapse" compact /></template>
				<button v-if="displayed.laterOffset != null" class="window-link" type="button" :disabled="detail.loading.value" @click="detail.readLater"><ArrowDown :size="12" />后续内容</button>
				<ActivityContentReader :detail="displayed" :read-content="detail.readContent" />
			</template>
			</div>
		</div>
		<button v-if="detail.hasNewContent.value" class="new-content" type="button" :disabled="detail.loading.value" @click="detail.resumeLatest"><ArrowDown :size="13" />有新内容</button>
	</aside>
</template>

<style scoped>
.task-detail { display: flex; flex-direction: column; min-width: 0; min-height: 0; overflow: hidden; border-left: 1px solid var(--border); }
header { display: flex; align-items: center; justify-content: space-between; flex: 0 0 34px; padding: 0 10px; border-bottom: 1px solid var(--border); gap: 8px; }header strong { font-size: var(--fs-12); min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }header button { flex-shrink: 0; display: grid; place-items: center; width: 24px; height: 24px; border: 0; background: transparent; color: var(--muted); }
.detail-scroll { min-height: 0; padding: 10px 12px; overflow: auto; overscroll-behavior: contain; }
.detail-meta { display: flex; flex-wrap: wrap; gap: 3px 10px; font-size: var(--fs-10); color: var(--muted); }.task-text { white-space: pre-wrap; overflow-wrap: anywhere; font-size: var(--fs-11); color: var(--text-soft); margin: 9px 0; }
.history-warning { font-size: var(--fs-11); color: var(--warning, #9a6600); }.detail-error { font-size: var(--fs-11); color: var(--danger); overflow-wrap: anywhere; }.detail-state { color: var(--muted); font-size: var(--fs-12); }
.window-link, .new-content { display: flex; align-items: center; justify-content: center; gap: 5px; border: 0; background: transparent; color: var(--accent); padding: 8px; font-size: var(--fs-11); }.new-content { flex-shrink: 0; border-top: 1px solid var(--border); background: var(--panel-soft); }h4 { margin: 12px 0 4px; font-size: var(--fs-11); color: var(--muted); }
</style>
