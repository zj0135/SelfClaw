<script setup>
import { computed } from 'vue';
import { CirclePause, CirclePlay, ListOrdered, RotateCcw, X } from 'lucide-vue-next';
import { describeInputReason } from '../../renderers/conversationInputReasons.js';

const props = defineProps({
	state: { type: Object, required: true },
});
const emit = defineEmits(['cancel', 'retry', 'pause', 'resume']);

const visible = computed(() => props.state.items.length > 0 || props.state.paused || props.state.error);
const canResume = computed(() => props.state.paused && props.state.items.length > 0);

function statusLabel(item) {
	if (item.status === 'claimed') return '执行中';
	if (item.status === 'held') return '待处理';
	return '待发送';
}
</script>

<template>
	<section v-if="visible" class="conversation-queue" aria-label="待发送队列">
		<header class="queue-head">
			<span class="queue-title"><ListOrdered :size="14" /> 队列</span>
            <span v-if="state.paused" class="queue-pause-reason" role="status">{{ describeInputReason(state.pauseReason) }}</span>
			<button v-if="state.paused && canResume" class="queue-action" type="button" title="恢复队列"
				aria-label="恢复队列" @click="emit('resume')"><CirclePlay :size="14" /></button>
			<button v-else-if="!state.paused" class="queue-action" type="button" title="暂停队列"
				aria-label="暂停队列" @click="emit('pause')"><CirclePause :size="14" /></button>
		</header>
		<ul class="queue-list">
			<li v-for="item in state.items" :key="item.inputId" class="queue-item" :data-status="item.status">
				<span class="queue-preview">{{ item.preview }}</span>
				<span class="queue-status">{{ statusLabel(item) }}</span>
				<button v-if="item.status === 'held'" class="queue-action" type="button" title="重试"
					aria-label="重试" @click="emit('retry', item)"><RotateCcw :size="13" /></button>
				<button v-if="item.status === 'pending' || item.status === 'held'" class="queue-action" type="button"
					title="取消" aria-label="取消" @click="emit('cancel', item)"><X :size="13" /></button>
			</li>
		</ul>
        <p v-if="state.truncated" class="queue-truncated">仅显示部分输入。</p>
        <p v-if="state.error" role="alert">{{ state.error }}</p>
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
	font-weight: 600;
}

.queue-pause-reason {
	margin-left: auto;
	color: var(--warning, #d08b26);
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

.queue-preview {
	flex: 1;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.queue-status {
	color: var(--text-secondary, #888);
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

.queue-action:hover {
	background: rgba(127, 127, 127, 0.16);
}

.queue-truncated {
	margin: 0;
	color: var(--text-secondary, #888);
}
</style>
