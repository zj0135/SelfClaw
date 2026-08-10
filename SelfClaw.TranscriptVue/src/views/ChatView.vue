<script setup>
import { computed, nextTick, onMounted, onUnmounted, reactive, ref } from 'vue';
import ComposerPanel from '../components/Chat/ComposerPanel.vue';
import TerminalPanel from '../components/Chat/TerminalPanel.vue';
import TranscriptPanel from '../components/Chat/TranscriptPanel.vue';
import { isSuperseded, useHostBridge } from '../composables/hostBridge.js';
import { useTranscriptCollapse } from '../composables/useTranscriptCollapse.js';
import { useTranscriptScroll } from '../composables/useTranscriptScroll.js';

const emit = defineEmits(['preview-image']);

const { on, post, requestLatest } = useHostBridge();

const state = reactive({
	items: [],
	conversations: [],
	selectedConversationId: null,
	autoScroll: false,
	isBusy: false,
	agentMode: 'cli',
	selectedAgentId: '',
	selectedAgentName: '',
	capabilityRevision: 0,
	activityText: '',
	pendingApproval: null,
	terminal: {
		isOpen: false,
		isRunning: false,
		cwd: '',
	},
	workspace: {
		current: null,
		roots: [],
		commonFolders: [],
		isLoading: false,
		error: '',
	},
});

const transcriptPanelRef = ref(null);
const terminalPanelRef = ref(null);
const composerShellRef = ref(null);
// 折叠状态的单一载体：在此顶层创建，一路传入每个 MessageContent。
// 跨会话切换、流式重建都存活；按稳定 id 记忆哪些块展开。
const collapse = useTranscriptCollapse();
const transcriptScroll = useTranscriptScroll(getTranscriptScrollEl);
const isEmptyConversation = computed(() => (state.items || []).length === 0 && !state.isBusy);

// ===== 回合执行状态（对话底部的「执行中 + 耗时」行） =====
// CLI 非流式返回时整个回合期间消息区可能毫无变化，
// 从 isBusy 上升沿开始计时，让用户知道回合仍在执行。
const busyClock = reactive({ startedAt: 0, now: 0 });
let busyTimer = null;

function startBusyClock() {
	busyClock.startedAt = Date.now();
	busyClock.now = busyClock.startedAt;
	if (!busyTimer) {
		busyTimer = setInterval(() => {
			busyClock.now = Date.now();
		}, 1000);
	}
}

function stopBusyClock() {
	busyClock.startedAt = 0;
	if (busyTimer) {
		clearInterval(busyTimer);
		busyTimer = null;
	}
}

function formatElapsedTime(ms) {
	const totalSeconds = Math.max(0, Math.floor(ms / 1000));
	const hours = Math.floor(totalSeconds / 3600);
	const minutes = Math.floor((totalSeconds % 3600) / 60);
	const seconds = totalSeconds % 60;
	if (hours > 0) {
		return `${hours}h ${minutes}m`;
	}

	if (minutes > 0) {
		return `${minutes}m ${seconds}s`;
	}

	return `${seconds}s`;
}

function hasRenderableContent(item) {
	return Array.isArray(item?.segments) && item.segments.length > 0;
}

// 末条助手消息还在展示「准备中」卡片时不重复显示执行状态，避免同屏两个活动指示。
const preparingIndicatorVisible = computed(() => {
	const items = state.items || [];
	const last = items[items.length - 1];
	return Boolean(last && last.role === 'assistant' && last.isThinking && !hasRenderableContent(last));
});

const turnStatus = computed(() => {
	if (!state.isBusy || !busyClock.startedAt || preparingIndicatorVisible.value) {
		return null;
	}

	return {
		label: '执行中',
		elapsedText: formatElapsedTime(busyClock.now - busyClock.startedAt),
	};
});

function getTranscriptScrollEl() {
	return transcriptPanelRef.value?.getScrollEl?.() ?? null;
}

function replaceState(payload) {
	const nextItems = Array.isArray(payload.items) ? payload.items : [];
	const nextBusy = Boolean(payload.isBusy);
	const nextConversationId = payload.selectedConversationId || null;
	const nextAutoScroll = Boolean(payload.autoScroll);
	const scrollSnapshot = transcriptScroll.captureBeforeUpdate(nextConversationId);
	if (nextBusy && !state.isBusy) {
		startBusyClock();
	} else if (!nextBusy) {
		stopBusyClock();
	}

	const wasEmpty = (state.items || []).length === 0 && !state.isBusy;
	const willBeEmpty = nextItems.length === 0 && !nextBusy;
	const previousComposerRect = wasEmpty && !willBeEmpty ? composerShellRef.value?.getShellEl()?.getBoundingClientRect() : null;

	state.items = nextItems;
	state.conversations = Array.isArray(payload.conversations) ? payload.conversations : [];
	state.selectedConversationId = nextConversationId;
	state.autoScroll = nextAutoScroll;
	state.isBusy = nextBusy;
	state.activityText = payload.activityText || '';
	state.agentMode = payload.agentMode || 'cli';
	state.selectedAgentId = payload.selectedAgentId || '';
	state.selectedAgentName = payload.selectedAgentName || '';
	state.capabilityRevision = Number(payload.capabilityRevision) || 0;

	nextTick(() => {
		const composerEl = composerShellRef.value?.getShellEl();
		if (composerEl && previousComposerRect) {
			const nextComposerRect = composerEl.getBoundingClientRect();
			const deltaY = previousComposerRect.top - nextComposerRect.top;

			if (Math.abs(deltaY) > 4) {
				composerEl.animate([{ transform: `translateY(${deltaY}px)` }, { transform: 'translateY(0)' }], {
					duration: 850,
					easing: 'cubic-bezier(0.18, 0.86, 0.24, 1)',
				});
			}
		}

		transcriptScroll.settleAfterUpdate(nextAutoScroll, scrollSnapshot);
	});
}

function openImagePreview(preview) {
	emit('preview-image', preview);
}

function submitComposer(prompt) {
	if (!prompt || state.isBusy) {
		return;
	}

	// 发送自己的消息时重新开启跟随，让新回合从底部开始。
	transcriptScroll.resumeFollow();
	post({ type: 'send-prompt', prompt });
}

function stopGeneration() {
	if (!state.isBusy) {
		return;
	}

	post({ type: 'stop-generation' });
}

function resolveToolApproval(toolExecutionId, approved) {
	if (!toolExecutionId) {
		return;
	}

	// 乐观清除：C# 侧解析后会再发一次 toolApprovalClear 或下一条 toolApprovalRequest，
	// 这里先隐藏当前栏，避免按钮点击到状态回传之间的空档里重复点击。
	if (state.pendingApproval?.toolExecutionId === toolExecutionId) {
		state.pendingApproval = null;
	}

	post({ type: 'resolve-tool-approval', toolExecutionId, approved });
}

function setWorkspaceLoading(isLoading) {
	state.workspace.isLoading = isLoading;
	if (isLoading) {
		state.workspace.error = '';
	}
}

async function applyWorkspaceRequest(type, payload = {}) {
	setWorkspaceLoading(true);
	try {
		const response = await requestLatest('workspace-selection', type, payload);
		applyWorkspaceSelection(response);
	} catch (error) {
		if (isSuperseded(error)) {
			return;
		}

		state.workspace.error = error?.message || '工作区请求失败。';
		state.workspace.isLoading = false;
	}
}

function requestWorkspaceSelection(refresh = false) {
	return applyWorkspaceRequest('get-workspace-selection', { refresh: Boolean(refresh) });
}

function selectWorkspaceRoot(workspaceRootId) {
	if (!workspaceRootId) {
		return;
	}

	return applyWorkspaceRequest('select-workspace-root', { workspaceRootId });
}

function browseWorkspaceFolder() {
	return applyWorkspaceRequest('browse-workspace-folder');
}

function applyWorkspaceSelection(payload) {
	state.workspace.current = payload.current || null;
	state.workspace.roots = Array.isArray(payload.roots) ? payload.roots : [];
	state.workspace.commonFolders = Array.isArray(payload.commonFolders) ? payload.commonFolders : [];
	state.workspace.error = payload.error || '';
	state.workspace.isLoading = false;
}

function onWorkspacePointerDown(event) {
	if (event.target instanceof Element && event.target.closest('.terminal-panel')) {
		return;
	}

	onTerminalFocusChange(false);
}

function onWorkspaceFocusIn(event) {
	if (event.target instanceof Element && event.target.closest('.terminal-panel')) {
		return;
	}

	onTerminalFocusChange(false);
}

function onTerminalReady(size) {
	post({
		type: 'terminal-ready',
		cols: size.cols,
		rows: size.rows,
	});
}

function onTerminalInput(data) {
	post({
		type: 'terminal-input',
		data,
	});
}

function onTerminalResize(size) {
	post({
		type: 'terminal-resize',
		cols: size.cols,
		rows: size.rows,
	});
}

function onTerminalClose() {
	post({ type: 'terminal-close' });
}

function onTerminalRestart() {
	post({ type: 'terminal-restart' });
}

function onTerminalFocusChange(isFocused) {
	post({
		type: 'terminal-focus-change',
		isFocused,
	});
}

// replaceState 带 replayLast：从设置页切回时本视图重新挂载，靠缓存的最近一份
// 快照立即回放，避免对话区空白等待下一次推送。
on('replaceState', replaceState, { replayLast: true });

on('terminal-state', (payload) => {
	state.terminal.isOpen = Boolean(payload.isOpen);
	state.terminal.isRunning = Boolean(payload.isRunning);
	state.terminal.cwd = payload.cwd || '';
	nextTick(() => {
		terminalPanelRef.value?.fit?.();
		terminalPanelRef.value?.focus?.();
	});
});

on('terminal-output', (payload) => {
	terminalPanelRef.value?.write?.(payload.data || '');
});

on('terminal-clear', () => {
	terminalPanelRef.value?.clear?.();
});

on('terminal-focus', () => {
	nextTick(() => terminalPanelRef.value?.focus?.());
});

on('toolApprovalRequest', (payload) => {
	state.pendingApproval = {
		toolExecutionId: payload.toolExecutionId,
		toolName: payload.toolName || '',
		displayName: payload.displayName || '',
		description: payload.description || '',
		argumentsJson: payload.argumentsJson || '',
		sourceKind: payload.sourceKind,
		sourceId: payload.sourceId || '',
		transportSummary: payload.transportSummary || '',
		annotationsJson: payload.annotationsJson || '',
	};
});

on('toolApprovalClear', () => {
	state.pendingApproval = null;
});

onMounted(() => {
	requestWorkspaceSelection(false);
});

onUnmounted(() => {
	stopBusyClock();
});
</script>

<template>
	<div class="workspace" :class="{
		'empty-workspace': isEmptyConversation,
		'terminal-open': state.terminal.isOpen,
	}" @pointerdown="onWorkspacePointerDown" @focusin="onWorkspaceFocusIn">
		<TranscriptPanel v-if="!isEmptyConversation" ref="transcriptPanelRef" :items="state.items"
			:collapse="collapse" :activity-text="state.activityText" :turn-status="turnStatus"
			@content-resize="transcriptScroll.onContentResize" @scroll="transcriptScroll.onScroll"
			@preview-image="openImagePreview" />
		<section v-else class="empty-composer-stage" aria-label="新对话">
			<div class="empty-composer-copy">
				<div class="empty-kicker">SELFCLAW · READY</div>
				<h1>想聊些什么？</h1>
				<p>随意提问，或使用命令/工具。</p>
			</div>
		</section>
		<ComposerPanel
			ref="composerShellRef"
			:busy="state.isBusy"
			:workspace-selection="state.workspace"
			:agent-mode="state.agentMode"
			:selected-agent-id="state.selectedAgentId"
			:selected-agent-name="state.selectedAgentName"
			:capability-revision="state.capabilityRevision"
			:pending-approval="state.pendingApproval"
			@submit="submitComposer"
			@stop="stopGeneration"
			@request-workspace="requestWorkspaceSelection"
			@select-workspace-root="selectWorkspaceRoot"
			@browse-workspace-folder="browseWorkspaceFolder"
			@approve-tool="(id) => resolveToolApproval(id, true)"
			@reject-tool="(id) => resolveToolApproval(id, false)"
		/>
		<TerminalPanel ref="terminalPanelRef" :is-open="state.terminal.isOpen" :is-running="state.terminal.isRunning"
			:cwd="state.terminal.cwd" @ready="onTerminalReady" @input="onTerminalInput" @resize="onTerminalResize"
			@close="onTerminalClose" @restart="onTerminalRestart" @focus-change="onTerminalFocusChange" />
	</div>
</template>

<style>
.workspace {
	width: 100%;
	height: 100%;
	display: grid;
	grid-template-rows: minmax(0, 1fr) auto 0;
	background: #ffffff;
	transition: grid-template-rows 850ms cubic-bezier(0.18, 0.86, 0.24, 1);
}

.workspace.empty-workspace {
	grid-template-rows: minmax(128px, 0.78fr) auto 0 minmax(160px, 1fr);
	align-items: stretch;
}

.workspace.terminal-open {
	grid-template-rows: minmax(0, 1fr) auto 286px;
}

.workspace.empty-workspace.terminal-open {
	grid-template-rows: minmax(96px, 1fr) auto 286px 0;
}

.empty-composer-stage {
	min-height: 0;
	display: flex;
	align-items: flex-end;
	justify-content: center;
	padding: 0 28px 28px;
	background: transparent;
}

.empty-composer-copy {
	text-align: center;
	animation: empty-rise 0.6s cubic-bezier(0.22, 1, 0.36, 1) both;
}

@keyframes empty-rise {
	from {
		opacity: 0;
		transform: translateY(14px);
	}

	to {
		opacity: 1;
		transform: none;
	}
}

.empty-kicker {
	margin-bottom: 14px;
	color: #9aa1ad;
	font-family: var(--font-mono, 'Cascadia Code', ui-monospace, monospace);
	font-size: 10px;
	font-weight: 600;
	letter-spacing: 0.28em;
}

.empty-composer-copy h1 {
	margin: 0;
	color: #171a1f;
	font-family: var(--font-display);
	font-size: clamp(32px, 4vw, 42px);
	font-weight: 700;
	line-height: 1.1;
	letter-spacing: -0.02em;
}

.empty-composer-copy p {
	margin: 12px 0 0;
	color: #9aa2ad;
	font-size: 14px;
	line-height: 1.6;
}

@media (max-width: 960px) {
	.workspace.empty-workspace {
		grid-template-rows: minmax(112px, 0.7fr) auto 0 minmax(140px, 1fr);
	}

	.workspace.empty-workspace.terminal-open {
		grid-template-rows: minmax(84px, 1fr) auto 286px 0;
	}

	.empty-composer-stage {
		padding-inline: 18px;
		padding-bottom: 22px;
	}

	.empty-composer-copy h1 {
		font-size: 30px;
	}
}
</style>
