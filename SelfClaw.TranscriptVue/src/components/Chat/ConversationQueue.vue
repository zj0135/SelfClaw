<script setup>
import { computed } from 'vue';
import { Check, CirclePause, CirclePlay, CornerDownRight, ListOrdered, Pencil, RotateCcw, X } from 'lucide-vue-next';
import { describeInputReason } from '../../renderers/conversationInputReasons.js';

const props = defineProps({
	state: { type: Object, required: true },
	editing: { type: Object, default: null },
	notice: { type: String, default: '' },
});
const emit = defineEmits(['cancel', 'retry', 'promote', 'pause', 'resume', 'edit', 'save-edit', 'cancel-edit', 'update:prompt']);

// 队列卡片只呈现“等待中”的输入：正在执行（claimed）的输入属于当前回合、由转录呈现，不在卡片里显示。
// 它仍保留在宿主快照里，供 composable 判断 steer 何时真正离开列表（消费 vs 转 Held）。
const waitingItems = computed(() => props.state.items.filter(item => item.status !== 'claimed'));
const visible = computed(() => waitingItems.value.length > 0 || props.state.paused || props.state.error || props.notice);
const canResume = computed(() => props.state.paused && waitingItems.value.length > 0);
const steerHint = computed(() => props.state.steerReason ? describeInputReason(props.state.steerReason, '') : '');
const promoteTip = computed(() => props.state.canSteer ? '调整当前任务' : (steerHint.value || '当前回合不能调整'));

// 卡片只渲染等待中的项，因此这里没有“执行中”态。
function statusLabel(item) {
	if (item.status === 'held') return '待处理';
	return item.kind === 'steer' ? '等待生效' : '待发送';
}

function heldReason(item) {
	return describeInputReason(item.reasonCode, item.errorMessage || '');
}

function editPrompt(event) {
	emit('update:prompt', event.target.value);
}
</script>

<template>
	<section v-if="visible" class="conversation-queue" aria-label="待发送队列">
		<header class="queue-head">
			<span class="queue-title">
				<ListOrdered :size="14" aria-hidden="true" />
				队列<template v-if="waitingItems.length"> · {{ waitingItems.length }}</template>
			</span>
			<span v-if="state.paused" class="queue-pause-reason" role="status">
				{{ describeInputReason(state.pauseReason, '队列已暂停，请检查待发送项') }}
			</span>
			<span v-else-if="steerHint" class="queue-steer-hint" :title="steerHint">{{ steerHint }}</span>
			<button v-if="state.paused && canResume" class="queue-action" type="button" title="恢复队列"
				aria-label="恢复队列" @click="emit('resume')"><CirclePlay :size="14" aria-hidden="true" /></button>
			<button v-else-if="!state.paused" class="queue-action" type="button" title="暂停队列"
				aria-label="暂停队列" @click="emit('pause')"><CirclePause :size="14" aria-hidden="true" /></button>
		</header>
		<ul class="queue-list">
			<li v-for="item in waitingItems" :key="item.inputId" class="queue-item" :data-status="item.status"
				:data-kind="item.kind">
				<template v-if="editing && editing.inputId === item.inputId">
					<textarea class="queue-editor" rows="2" :value="editing.prompt" :disabled="editing.saving"
						aria-label="编辑待发送内容" @input="editPrompt"></textarea>
					<div class="queue-editor-actions">
						<button class="queue-action" type="button" title="保存" aria-label="保存编辑"
							:disabled="editing.saving" @click="emit('save-edit')"><Check :size="13"
								aria-hidden="true" /></button>
						<button class="queue-action" type="button" title="取消编辑" aria-label="取消编辑"
							:disabled="editing.saving" @click="emit('cancel-edit')"><X :size="13"
								aria-hidden="true" /></button>
					</div>
				</template>
				<template v-else>
					<span v-if="item.kind === 'steer'" class="queue-kind">调整</span>
					<span class="queue-preview" :title="item.preview">{{ item.preview }}</span>
					<span v-if="item.status === 'held'" class="queue-reason">{{ heldReason(item) }}</span>
					<span class="queue-status">{{ statusLabel(item) }}</span>
					<button v-if="item.status === 'held'" class="queue-action" type="button" title="重试" aria-label="重试"
						@click="emit('retry', item)"><RotateCcw :size="13" aria-hidden="true" /></button>
					<button v-if="item.kind === 'follow-up'" class="queue-action" type="button"
						title="编辑" aria-label="编辑" @click="emit('edit', item)"><Pencil :size="13"
							aria-hidden="true" /></button>
					<button v-if="item.kind === 'follow-up' && item.status === 'pending'" class="queue-action"
						type="button" :title="promoteTip" aria-label="调整当前任务" :disabled="!state.canSteer"
						@click="emit('promote', item)"><CornerDownRight :size="14" aria-hidden="true" /></button>
					<button v-if="item.status === 'pending' || item.status === 'held'" class="queue-action" type="button"
						title="取消" aria-label="取消" @click="emit('cancel', item)"><X :size="13"
							aria-hidden="true" /></button>
				</template>
			</li>
		</ul>
		<p v-if="state.truncated" class="queue-truncated">仅显示部分输入。</p>
		<p v-if="notice" class="queue-notice" role="status">{{ notice }}</p>
		<p v-if="state.error" class="queue-error" role="alert">{{ state.error }}</p>
	</section>
</template>

<style scoped>
.conversation-queue {
	display: flex;
	flex-direction: column;
	gap: 6px;
	margin: 0 0 8px;
	padding: 10px 14px;
	border: 1px solid color-mix(in srgb, var(--text) 10%, transparent);
	border-radius: 14px;
	background: var(--panel);
	box-shadow: 0 1px 2px rgba(var(--shadow-ink), 0.04);
	font-size: 12px;
}

.queue-head {
	display: flex;
	align-items: center;
	gap: 8px;
	color: var(--text-secondary, #888);
}

.queue-title {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	flex: none;
	font-weight: 600;
}

.queue-pause-reason {
	min-width: 0;
	overflow: hidden;
	margin-left: auto;
	color: var(--warning, #d08b26);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.queue-steer-hint {
	min-width: 0;
	overflow: hidden;
	margin-left: auto;
	color: var(--muted-soft, #999);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.queue-list {
	list-style: none;
	margin: 0;
	padding: 0;
	display: flex;
	flex-direction: column;
	gap: 4px;
}

.queue-item {
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 4px 6px;
	border-radius: 6px;
	background: var(--surface-raised, rgba(127, 127, 127, 0.08));
}

.queue-item[data-status='held'] {
	outline: 1px solid var(--warning, #d08b26);
}

.queue-item[data-kind='steer'] {
	background: color-mix(in srgb, var(--accent) 8%, var(--surface-raised, rgba(127, 127, 127, 0.08)));
}

.queue-kind {
	flex: none;
	padding: 1px 5px;
	border-radius: 4px;
	background: color-mix(in srgb, var(--accent) 16%, transparent);
	color: var(--accent);
	font-size: 10px;
	font-weight: 600;
}

.queue-preview {
	flex: 1;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.queue-reason {
	flex: none;
	max-width: 45%;
	overflow: hidden;
	color: var(--warning, #d08b26);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.queue-status {
	flex: none;
	color: var(--text-secondary, #888);
}

.queue-editor {
	flex: 1;
	min-width: 0;
	resize: vertical;
	padding: 4px 6px;
	border: 1px solid color-mix(in srgb, var(--accent) 40%, transparent);
	border-radius: 6px;
	background: var(--panel);
	color: var(--text);
	font: 12px/1.5 var(--font-ui);
}

.queue-editor-actions {
	display: inline-flex;
	align-items: center;
	gap: 2px;
	flex: none;
}

.queue-action {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	border: 0;
	background: transparent;
	color: inherit;
	cursor: pointer;
	padding: 2px;
	border-radius: 4px;
}

.queue-action:hover:not(:disabled) {
	background: rgba(127, 127, 127, 0.16);
}

.queue-action:disabled {
	opacity: 0.35;
	cursor: default;
}

.queue-truncated {
	margin: 0;
	color: var(--text-secondary, #888);
}

.queue-notice {
	margin: 0;
	color: var(--accent);
}

.queue-error {
	margin: 0;
	color: var(--err-text, #d05a5a);
}
</style>
