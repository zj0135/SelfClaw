<script setup>
import { computed } from 'vue';
import { Check, ShieldAlert, X } from 'lucide-vue-next';

// 工具调用确认栏：输入框上方，需要用户允许 / 拒绝 Direct 写操作时出现。只呈现与转发裁决，不持有状态。
const props = defineProps({
	approval: { type: Object, default: null },
});
const emit = defineEmits(['approve', 'reject']);

const title = computed(() => props.approval?.displayName || props.approval?.toolName || '工具调用');

// 参数原文可能很长，状态栏里只给一行预览，展开细节仍留在对话流的工具卡片里。
const detail = computed(() => {
	const raw = (props.approval?.argumentsJson || props.approval?.description || '').replace(/\s+/g, ' ').trim();
	return raw.length <= 120 ? raw : `${raw.slice(0, 120)}…`;
});

const source = computed(() => {
	const approval = props.approval;
	if (!approval?.sourceId) {
		return '';
	}

	const numericLabels = { 1: 'MCP', 2: 'Skill', 3: 'Plugin' };
	const kind = typeof approval.sourceKind === 'string'
		? approval.sourceKind
		: numericLabels[approval.sourceKind] || '扩展';
	return `${kind.toUpperCase()} · ${approval.sourceId}${approval.transportSummary ? ` · ${approval.transportSummary}` : ''}`;
});

const conversationLabel = computed(() => {
	const approval = props.approval;
	if (!approval?.conversationId) {
		return '';
	}

	return approval.conversationTitle || approval.conversationId.slice(0, 8);
});
</script>

<template>
	<transition name="approval-bar">
		<div v-if="props.approval" class="tool-approval-bar" role="alertdialog" aria-label="工具调用确认">
			<span class="tool-approval-icon" aria-hidden="true">
				<ShieldAlert :size="16" :stroke-width="1.9" />
			</span>
			<div class="tool-approval-copy">
				<span v-if="conversationLabel" class="tool-approval-source">会话：{{ conversationLabel }}</span>
				<span class="tool-approval-title">
					请求执行 <strong>{{ title }}</strong>
				</span>
				<span v-if="source" class="tool-approval-source">{{ source }}</span>
				<span v-if="detail" class="tool-approval-detail" :title="detail">{{ detail }}</span>
			</div>
			<div class="tool-approval-actions">
				<button class="tool-approval-btn reject" type="button" title="拒绝" @click="emit('reject', props.approval.toolExecutionId)">
					<X :size="14" :stroke-width="2.2" aria-hidden="true" />
					拒绝
				</button>
				<button class="tool-approval-btn approve" type="button" title="允许" @click="emit('approve', props.approval.toolExecutionId)">
					<Check :size="14" :stroke-width="2.4" aria-hidden="true" />
					允许
				</button>
			</div>
		</div>
	</transition>
</template>

<style scoped>
.tool-approval-bar {
	display: flex;
	align-items: center;
	gap: 10px;
	margin-bottom: 10px;
	padding: 9px 10px 9px 12px;
	border: 1px solid var(--caution-line);
	border-radius: 11px;
	background: var(--caution-tint);
}

.tool-approval-icon {
	display: inline-grid;
	place-items: center;
	width: 26px;
	height: 26px;
	flex: none;
	border-radius: 8px;
	color: var(--caution-fill);
	background: var(--caution-icon-bg);
}

.tool-approval-copy {
	min-width: 0;
	flex: 1 1 auto;
	display: flex;
	flex-direction: column;
	gap: 1px;
}

.tool-approval-title {
	max-width: 100%;
	overflow: hidden;
	color: var(--caution-text);
	font-size: var(--fs-125);
	line-height: 1.4;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.tool-approval-title strong {
	font-weight: 650;
	color: var(--caution-text-strong);
}

.tool-approval-detail {
	max-width: 100%;
	overflow: hidden;
	color: var(--caution-text-soft);
	font-family: var(--font-mono);
	font-size: var(--fs-11);
	line-height: 1.35;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.tool-approval-source {
	color: var(--caution-text-faint);
	font-size: var(--fs-10);
	font-weight: 600;
}

.tool-approval-actions {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	flex: none;
}

.tool-approval-btn {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	height: 30px;
	padding: 0 12px;
	border: 1px solid transparent;
	border-radius: 8px;
	font-size: var(--fs-125);
	font-weight: 600;
	line-height: 1;
	transition: background 0.15s, border-color 0.15s, color 0.15s;
}

.tool-approval-btn.reject {
	border-color: var(--border-strong);
	background: var(--panel);
	color: var(--muted);
}

.tool-approval-btn.reject:hover {
	border-color: var(--border-strong);
	color: var(--text-soft);
}

.tool-approval-btn.approve {
	background: var(--caution-fill);
	color: var(--caution-fill-ink);
}

.tool-approval-btn.approve:hover {
	background: var(--caution-fill-hover);
}

.approval-bar-enter-active,
.approval-bar-leave-active {
	transition: opacity 0.16s ease, transform 0.16s ease;
}

.approval-bar-enter-from,
.approval-bar-leave-to {
	opacity: 0;
	transform: translateY(4px);
}
</style>
