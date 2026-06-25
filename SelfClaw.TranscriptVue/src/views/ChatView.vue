<script setup>
import { computed, nextTick, reactive, ref } from 'vue';
import TerminalPanel from '../components/TerminalPanel.vue';
import TranscriptPanel from '../components/TranscriptPanel.vue';
import { renderMessages } from '../renderers';

const emit = defineEmits(['preview-image']);

const state = reactive({
	items: [],
	conversations: [],
	selectedConversationId: null,
	autoScroll: false,
	isBusy: false,
	terminal: {
		isOpen: false,
		isRunning: false,
		cwd: '',
	},
});

const composerText = ref('');
const transcriptPanelRef = ref(null);
const terminalPanelRef = ref(null);
const composerShellRef = ref(null);
const openThoughts = ref(new Set());
const openToolSegments = ref(new Set());
const openToolGroups = ref(new Set());
const scrollFollowState = {
	transcript: true,
	transcriptPausedUntil: 0,
};

function post(message) {
	window.chrome?.webview?.postMessage(message);
}

const messagesHtml = computed(() =>
	renderMessages(state.items || [], openThoughts.value, openToolSegments.value, openToolGroups.value)
);
const canSend = computed(() => composerText.value.trim().length > 0 && !state.isBusy);
const isEmptyConversation = computed(() => (state.items || []).length === 0 && !state.isBusy);

function getTranscriptScrollEl() {
	return transcriptPanelRef.value?.getScrollEl?.() ?? null;
}

function snapshotScrollPosition(element) {
	return element
		? { top: element.scrollTop, nearBottom: element.scrollHeight - element.scrollTop - element.clientHeight < 40 }
		: null;
}

function restoreScrollPosition(element, snapshot) {
	if (!element || !snapshot || snapshot.nearBottom || shouldFollowTranscript()) {
		scrollTranscriptToBottom();
		return;
	}

	element.scrollTop = snapshot.top;
}

function scrollTranscriptToBottom() {
	const element = getTranscriptScrollEl();
	if (element) {
		element.scrollTop = element.scrollHeight;
	}
}

function pauseTranscriptFollow(durationMs = 1200) {
	scrollFollowState.transcript = false;
	scrollFollowState.transcriptPausedUntil = Date.now() + durationMs;
}

function shouldFollowTranscript() {
	if (Date.now() < scrollFollowState.transcriptPausedUntil) {
		return false;
	}

	return scrollFollowState.transcript;
}

function replaceState(payload) {
	const transcriptEl = getTranscriptScrollEl();
	const scrollSnapshot = snapshotScrollPosition(transcriptEl);
	const nextItems = Array.isArray(payload.items) ? payload.items : [];
	const nextBusy = Boolean(payload.isBusy);
	const wasEmpty = (state.items || []).length === 0 && !state.isBusy;
	const willBeEmpty = nextItems.length === 0 && !nextBusy;
	const previousComposerRect = wasEmpty && !willBeEmpty
		? composerShellRef.value?.getBoundingClientRect()
		: null;

	state.items = nextItems;
	state.conversations = Array.isArray(payload.conversations) ? payload.conversations : [];
	state.selectedConversationId = payload.selectedConversationId || null;
	state.autoScroll = Boolean(payload.autoScroll);
	state.isBusy = nextBusy;

	nextTick(() => {
		const composer = composerShellRef.value;
		if (composer && previousComposerRect) {
			const nextComposerRect = composer.getBoundingClientRect();
			const deltaY = previousComposerRect.top - nextComposerRect.top;

			if (Math.abs(deltaY) > 4) {
				composer.animate(
					[
						{ transform: `translateY(${deltaY}px)` },
						{ transform: 'translateY(0)' },
					],
					{
						duration: 850,
						easing: 'cubic-bezier(0.18, 0.86, 0.24, 1)',
					}
				);
			}
		}

		const currentTranscriptEl = getTranscriptScrollEl();
		if (state.autoScroll || shouldFollowTranscript()) {
			scrollTranscriptToBottom();
			return;
		}

		restoreScrollPosition(currentTranscriptEl, scrollSnapshot);
	});
}

function onTranscriptScroll(event) {
	const target = event.target instanceof HTMLElement ? event.target : null;
	if (!target) {
		return;
	}

	const nearBottom = target.scrollHeight - target.scrollTop - target.clientHeight < 40;
	scrollFollowState.transcript = nearBottom;
	if (!nearBottom) {
		pauseTranscriptFollow();
	}
}

function openImagePreview(preview) {
	emit('preview-image', preview);
}

function submitComposer() {
	const prompt = composerText.value.trim();
	if (!prompt || state.isBusy) {
		return;
	}

	post({ type: 'send-prompt', prompt });
	composerText.value = '';
}

function onComposerKeydown(event) {
	if (event.key !== 'Enter' || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) {
		return;
	}

	event.preventDefault();
	submitComposer();
}

function toggleSetEntry(source, id) {
	const next = new Set(source.value);
	if (next.has(id)) {
		next.delete(id);
	} else {
		next.add(id);
	}

	source.value = next;
}

function handleTranscriptAction(target) {
	const actionElement = target instanceof Element ? target.closest('[data-action]') : null;
	if (!actionElement) {
		return false;
	}

	switch (actionElement.getAttribute('data-action')) {
		case 'toggle-thinking': {
			const id = actionElement.getAttribute('data-thinking-id');
			if (id) {
				toggleSetEntry(openThoughts, id);
			}
			return true;
		}
		case 'toggle-tool-segment': {
			const id = actionElement.getAttribute('data-tool-segment-id');
			if (id) {
				toggleSetEntry(openToolSegments, id);
			}
			return true;
		}
		case 'toggle-tool-group': {
			const id = actionElement.getAttribute('data-tool-group-id');
			if (id) {
				toggleSetEntry(openToolGroups, id);
			}
			return true;
		}
		default:
			return false;
	}
}

function onTranscriptClick(event) {
	if (handleTranscriptAction(event.target)) {
		event.preventDefault();
	}
}

function onTranscriptKeydown(event) {
	if (event.key !== 'Enter' && event.key !== ' ') {
		return;
	}

	if (handleTranscriptAction(event.target)) {
		event.preventDefault();
	}
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

defineExpose({
	handleMessage(payload) {
		if (payload.type === 'replaceState') {
			replaceState(payload);
		} else if (payload.type === 'terminal-state') {
			state.terminal.isOpen = Boolean(payload.isOpen);
			state.terminal.isRunning = Boolean(payload.isRunning);
			state.terminal.cwd = payload.cwd || '';
			nextTick(() => {
				terminalPanelRef.value?.fit?.();
				terminalPanelRef.value?.focus?.();
			});
		} else if (payload.type === 'terminal-output') {
			terminalPanelRef.value?.write?.(payload.data || '');
		} else if (payload.type === 'terminal-clear') {
			terminalPanelRef.value?.clear?.();
		} else if (payload.type === 'terminal-focus') {
			nextTick(() => terminalPanelRef.value?.focus?.());
		}
	},
});
</script>

<template>
	<div class="workspace" :class="{
		'empty-workspace': isEmptyConversation,
		'terminal-open': state.terminal.isOpen,
	}" @pointerdown="onWorkspacePointerDown" @focusin="onWorkspaceFocusIn">
		<TranscriptPanel v-if="!isEmptyConversation" ref="transcriptPanelRef" :messages-html="messagesHtml"
			@scroll="onTranscriptScroll" @preview-image="openImagePreview" @transcript-click="onTranscriptClick"
			@transcript-keydown="onTranscriptKeydown" />
		<section v-else class="empty-composer-stage" aria-label="新对话">
			<div class="empty-composer-copy">
				<h1>想聊些什么？</h1>
				<p>随意提问，也可以使用搜索与已选择的 MCP 工具。</p>
			</div>
		</section>
		<section ref="composerShellRef" class="composer-shell" aria-label="消息输入">
			<div class="composer-grip" aria-hidden="true"></div>
			<textarea v-model="composerText" class="composer-input" rows="3" placeholder="让助手帮你处理项目..."
				:disabled="state.isBusy" @keydown="onComposerKeydown"></textarea>
			<div class="composer-toolbar">
				<div class="composer-tools-left">
					<button class="composer-model" type="button" title="模型选择">
						<span class="model-dot" aria-hidden="true"></span>
						<span>Kimi K2.6 Code Preview</span>
					</button>
					<button class="icon-btn" type="button" title="参数">
						<span aria-hidden="true">⌘</span>
					</button>
					<button class="icon-btn icon-btn-strong" type="button" title="添加">
						<span aria-hidden="true">+</span>
					</button>
					<button class="icon-btn" type="button" title="文件">
						<svg viewBox="0 0 20 20" aria-hidden="true">
							<path d="M3.5 6.5h4l1.4 1.6h7.6v6.4a2 2 0 0 1-2 2h-11a2 2 0 0 1-2-2v-6a2 2 0 0 1 2-2Z">
							</path>
							<path d="M3.5 6.5V4.8a1.3 1.3 0 0 1 1.3-1.3h3.7l1.4 1.7h5.6a1 1 0 0 1 1 1v1.9"></path>
						</svg>
					</button>
				</div>
				<div class="composer-tools-right">
					<button class="composer-meter" type="button" title="上下文">
						<span class="meter-ring" aria-hidden="true"></span>
						<span>7%</span>
					</button>
					<button class="icon-btn" type="button" title="增强">
						<span aria-hidden="true">✦</span>
					</button>
					<button class="send-btn" type="button" :disabled="!canSend" @click="submitComposer">
						<span>开始</span>
						<svg viewBox="0 0 20 20" aria-hidden="true">
							<path d="M17 3 8.2 11.8"></path>
							<path d="M17 3 12.1 17 8.2 11.8 3 7.9 17 3Z"></path>
						</svg>
					</button>
				</div>
			</div>
		</section>
		<TerminalPanel ref="terminalPanelRef" :is-open="state.terminal.isOpen"
			:is-running="state.terminal.isRunning" :cwd="state.terminal.cwd" @ready="onTerminalReady"
			@input="onTerminalInput" @resize="onTerminalResize" @close="onTerminalClose"
			@restart="onTerminalRestart" @focus-change="onTerminalFocusChange" />
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
	grid-template-rows: minmax(128px, 0.78fr) auto 0 minmax(194px, 1fr);
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
	padding: 0 28px 26px;
	background: #ffffff;
}

.empty-composer-copy {
	text-align: center;
}

.empty-composer-copy h1 {
	margin: 0;
	color: #171a1f;
	font-family: var(--font-display);
	font-size: clamp(34px, 4vw, 44px);
	font-weight: 760;
	line-height: 1.1;
	letter-spacing: 0;
}

.empty-composer-copy p {
	margin: 14px 0 0;
	color: #9aa2ad;
	font-size: 14px;
	line-height: 1.6;
}

.composer-shell {
	position: relative;
	width: min(calc(100% - 72px), 820px);
	min-height: 160px;
	margin: 0 auto 14px;
	display: grid;
	grid-template-rows: 1fr auto;
	padding: 29px 18px 13px;
	border: 1px solid #d7dbe3;
	border-radius: 17px;
	background: #ffffff;
	box-shadow:
		0 1px 2px rgba(23, 26, 31, 0.06),
		0 10px 24px rgba(23, 26, 31, 0.06);
}

.empty-workspace .composer-shell {
	width: min(calc(100% - 72px), 728px);
	min-height: 138px;
	margin-bottom: 0;
	padding-top: 24px;
}

.composer-grip {
	position: absolute;
	top: 5px;
	left: 50%;
	width: 44px;
	height: 4px;
	border-radius: 99px;
	background: #dde1e7;
	transform: translateX(-50%);
}

.composer-input {
	width: 100%;
	min-height: 70px;
	resize: none;
	padding: 0 2px;
	border: 0;
	outline: none;
	background: transparent;
	color: #20242a;
	font: 14px/1.65 var(--font-ui);
}

.composer-input::placeholder {
	color: #8f9aab;
}

.composer-input:disabled {
	opacity: 0.68;
}

.composer-toolbar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 14px;
	min-height: 38px;
}

.composer-tools-left,
.composer-tools-right {
	display: inline-flex;
	align-items: center;
	gap: 12px;
	min-width: 0;
}

.composer-model,
.icon-btn,
.composer-meter,
.send-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	border: 0;
	background: transparent;
	color: #161a20;
}

.composer-model {
	gap: 8px;
	padding: 6px 4px;
	min-width: 0;
	font-size: 12px;
	white-space: nowrap;
}

.model-dot {
	width: 3px;
	height: 3px;
	border-radius: 50%;
	background: #1677ff;
}

.icon-btn {
	width: 24px;
	height: 24px;
	padding: 0;
	color: #111827;
	font-size: 18px;
	line-height: 1;
}

.icon-btn svg {
	width: 18px;
	height: 18px;
	fill: none;
	stroke: currentColor;
	stroke-width: 1.8;
	stroke-linecap: round;
	stroke-linejoin: round;
}

.icon-btn:not(.icon-btn-strong) {
	color: #555f6f;
}

.composer-meter {
	gap: 4px;
	width: 34px;
	height: 34px;
	border-radius: 50%;
	color: #6c7788;
	font-size: 8px;
}

.meter-ring {
	width: 10px;
	height: 10px;
	border-radius: 50%;
	border: 2px solid #e4e7ec;
	border-top-color: #20c970;
}

.send-btn {
	height: 44px;
	gap: 10px;
	padding: 0 14px 0 16px;
	border-radius: 18px;
	background: #8c8d91;
	color: #ffffff;
	font-size: 14px;
	font-weight: 700;
}

.send-btn svg {
	width: 17px;
	height: 17px;
	fill: none;
	stroke: currentColor;
	stroke-width: 1.8;
	stroke-linecap: round;
	stroke-linejoin: round;
}

.send-btn:disabled {
	cursor: default;
	opacity: 0.58;
}

@media (max-width: 960px) {
	.composer-shell {
		width: calc(100% - 28px);
	}

	.workspace.empty-workspace {
		grid-template-rows: minmax(112px, 0.7fr) auto 0 minmax(156px, 1fr);
	}

	.workspace.empty-workspace.terminal-open {
		grid-template-rows: minmax(84px, 1fr) auto 286px 0;
	}

	.empty-composer-stage {
		padding-inline: 18px;
		padding-bottom: 22px;
	}

	.empty-composer-copy h1 {
		font-size: 32px;
	}

	.empty-workspace .composer-shell {
		width: calc(100% - 28px);
	}
}
</style>
