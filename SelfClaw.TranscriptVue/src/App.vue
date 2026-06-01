<script setup>
import { computed, nextTick, onMounted, onUnmounted, reactive, ref } from 'vue';
import TranscriptPanel from './components/TranscriptPanel.vue';
import { renderMessages } from './renderers';

const state = reactive({
	items: [],
	conversations: [],
	selectedConversationId: null,
	autoScroll: false,
	isBusy: false,
});

const composerText = ref('');
const transcriptPanelRef = ref(null);
const imagePreview = ref(null);
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

	state.items = Array.isArray(payload.items) ? payload.items : [];
	state.conversations = Array.isArray(payload.conversations) ? payload.conversations : [];
	state.selectedConversationId = payload.selectedConversationId || null;
	state.autoScroll = Boolean(payload.autoScroll);
	state.isBusy = Boolean(payload.isBusy);

	nextTick(() => {
		const currentTranscriptEl = getTranscriptScrollEl();
		if (state.autoScroll || shouldFollowTranscript()) {
			scrollTranscriptToBottom();
			return;
		}

		restoreScrollPosition(currentTranscriptEl, scrollSnapshot);
	});
}

function handleIncomingMessage(event) {
	const payload = event?.data;
	if (!payload || typeof payload !== 'object') {
		return;
	}

	if (payload.type === 'replaceState') {
		replaceState(payload);
	}
}

function handleDocumentClick(event) {
	const link = event.target instanceof Element ? event.target.closest('a[href]') : null;
	if (!link) {
		return;
	}

	const href = link.getAttribute('href');
	if (!href) {
		return;
	}

	event.preventDefault();
	post({ type: 'open-link', href });
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
	imagePreview.value = preview;
}

function closeImagePreview() {
	imagePreview.value = null;
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

function onDocumentKeydown(event) {
	if (event.key === 'Escape' && imagePreview.value) {
		closeImagePreview();
	}
}

onMounted(() => {
	window.chrome?.webview?.addEventListener('message', handleIncomingMessage);
	document.addEventListener('click', handleDocumentClick);
	document.addEventListener('keydown', onDocumentKeydown);
});

onUnmounted(() => {
	window.chrome?.webview?.removeEventListener('message', handleIncomingMessage);
	document.removeEventListener('click', handleDocumentClick);
	document.removeEventListener('keydown', onDocumentKeydown);
});
</script>

<template>
	<div class="app" :class="{ busy: state.isBusy }">
		<div class="workspace">
			<TranscriptPanel
				ref="transcriptPanelRef"
				:messages-html="messagesHtml"
				:items="state.items"
				:conversations="state.conversations"
				:selected-conversation-id="state.selectedConversationId"
				@scroll="onTranscriptScroll"
				@preview-image="openImagePreview"
				@transcript-click="onTranscriptClick"
				@transcript-keydown="onTranscriptKeydown"
			/>
			<section class="composer-shell" aria-label="消息输入">
				<div class="composer-grip" aria-hidden="true"></div>
				<textarea
					v-model="composerText"
					class="composer-input"
					rows="3"
					placeholder="让助手帮你处理项目..."
					:disabled="state.isBusy"
					@keydown="onComposerKeydown"
				></textarea>
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
								<path d="M3.5 6.5h4l1.4 1.6h7.6v6.4a2 2 0 0 1-2 2h-11a2 2 0 0 1-2-2v-6a2 2 0 0 1 2-2Z"></path>
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
		</div>
		<div v-if="imagePreview" class="image-preview-backdrop" @click.self="closeImagePreview">
			<div class="image-preview-dialog">
				<img :src="imagePreview.src" :alt="imagePreview.alt || 'Preview image'" />
			</div>
		</div>
	</div>
</template>

<style>
:root {
	color-scheme: light;
	--bg: #ffffff;
	--panel: #ffffff;
	--panel-soft: #f7f8fa;
	--panel-muted: #f1f3f6;
	--panel-elevated: #ffffff;
	--border: #e5e7eb;
	--border-strong: #d8dde5;
	--text: #171a1f;
	--muted: #6b7280;
	--muted-soft: #8a929e;
	--accent: #4f73c8;
	--accent-2: #375fae;
	--accent-rgb: 79, 115, 200;
	--success: #2f855a;
	--danger: #c24150;
	--shadow: 0 12px 30px rgba(23, 26, 31, 0.08);
	--font-ui: 'Segoe UI Variable Text', 'Segoe UI', sans-serif;
	--font-display: 'Segoe UI Variable Display', 'Segoe UI', sans-serif;
	--font-code: 'Cascadia Code', Consolas, monospace;
	--scroll-track: rgba(23, 26, 31, 0.04);
	--scroll-thumb: rgba(23, 26, 31, 0.14);
}

* {
	box-sizing: border-box;
}

html,
body,
#app {
	width: 100%;
	height: 100%;
	margin: 0;
	overflow: hidden;
	font-family: var(--font-ui);
	color: var(--text);
	background: var(--bg);
}

body {
	padding: 0;
}

::-webkit-scrollbar {
	width: 10px;
	height: 10px;
}

::-webkit-scrollbar-track {
	background: var(--scroll-track);
}

::-webkit-scrollbar-thumb {
	background: var(--scroll-thumb);
	border: 2px solid var(--bg);
	border-radius: 999px;
}

button {
	cursor: pointer;
	font: inherit;
}

.app {
	width: 100%;
	height: 100%;
	background: var(--bg);
}

.workspace {
	width: 100%;
	height: 100%;
	display: grid;
	grid-template-rows: minmax(0, 1fr) auto;
	background: #ffffff;
}

.panel,
.transcript-panel {
	height: auto;
	min-height: 0;
	display: flex;
	flex-direction: column;
	overflow: hidden;
	border: 0;
	background: transparent;
}

.transcript-scroll {
	min-height: 0;
	flex: 1 1 auto;
	display: flex;
	flex-direction: column;
	gap: 0;
	overflow-y: auto;
	overflow-x: hidden;
	overscroll-behavior: contain;
	padding: 28px min(11.5vw, 104px) 32px;
	scroll-padding-bottom: 32px;
	background: #ffffff;
}

.message-row {
	display: flex;
	align-items: flex-start;
	justify-content: flex-start;
	margin-bottom: 28px;
}

.message-row:last-child {
	margin-bottom: 0;
}

.message-main {
	min-width: 0;
	flex: 0 1 min(76%, 760px);
	max-width: min(76%, 760px);
}

.message-row.user {
	justify-content: flex-end;
}

.message-row.user .message-main {
	flex: 0 1 auto;
	max-width: min(58%, 620px);
}

.item {
	width: 100%;
	min-height: 0;
	position: relative;
	display: block;
	overflow: hidden;
	border: 0;
	background: transparent;
	box-shadow: none;
}

.item.message.assistant,
.item.message.system {
	border: 0;
	background: transparent;
	box-shadow: none;
}

.item.message.user {
	padding: 0;
	border: 1px solid #e1e4ea;
	border-radius: 17px;
	background: #ffffff;
	box-shadow:
		0 1px 2px rgba(23, 26, 31, 0.08),
		0 8px 18px rgba(23, 26, 31, 0.05);
}

.item.message:hover {
	border-color: transparent;
}

.item.message.user:hover {
	border-color: #d8dde5;
}

.header {
	display: flex;
	align-items: center;
	justify-content: flex-start;
	gap: 12px;
	padding: 0 0 7px;
	color: var(--muted-soft);
	font-size: 12px;
	line-height: 1.4;
}

.header.no-title {
	padding: 0;
}

.assistant-time-header {
	min-height: 17px;
	padding-bottom: 4px;
}

.user-time-header {
	position: absolute;
	right: 0;
	bottom: calc(100% + 5px);
	padding: 0;
}

.message-time {
	opacity: 0;
	color: #7f8a9a;
	font-size: 11px;
	line-height: 1.2;
	transition: opacity 120ms ease;
	pointer-events: none;
}

.message-row:hover .message-time,
.message-row:focus-within .message-time {
	opacity: 1;
}

.body {
	display: block;
	min-height: 32px;
	padding: 12px 16px 16px;
	color: var(--text);
	font-size: 14px;
	line-height: 1.72;
}

.body.body-segment {
	padding: 0 0 12px;
	font-size: 13.5px;
}

.body.body-segment.first {
	padding-top: 0;
}

.body.body-segment.last {
	padding-bottom: 0;
}

.message-row.user .body.body-segment {
	padding: 13px 16px;
	color: #05070a;
	font-size: 14px;
	line-height: 1.6;
}

.body > * {
	max-width: 100%;
}

.body p:first-child,
.body ul:first-child,
.body ol:first-child,
.body blockquote:first-child,
.body pre:first-child,
.body h1:first-child,
.body h2:first-child,
.body h3:first-child {
	margin-top: 0;
}

.body p:last-child,
.body ul:last-child,
.body ol:last-child,
.body blockquote:last-child,
.body pre:last-child {
	margin-bottom: 0;
}

h1,
h2,
h3 {
	margin-bottom: 0.55em;
	font-family: var(--font-display);
	line-height: 1.2;
}

h1 {
	font-size: 1.5rem;
}

h2 {
	font-size: 1.22rem;
}

h3 {
	font-size: 1.05rem;
}

ul,
ol {
	padding-left: 1.35rem;
}

blockquote {
	margin: 0;
	padding: 0.2rem 0 0.2rem 1rem;
	border-left: 3px solid #ccd7ee;
	color: var(--muted);
}

pre {
	margin: 0.85rem 0;
	padding: 12px 14px;
	overflow: auto;
	border: 1px solid var(--border);
	border-radius: 8px;
	background: #f6f8fb;
	color: #1f2937;
	font-size: 13px;
}

code {
	font-family: var(--font-code);
	font-size: 13px;
}

:not(pre) > code {
	padding: 2px 6px;
	border-radius: 5px;
	background: #eef2f7;
	color: #263142;
}

table {
	width: 100%;
	overflow: hidden;
	border: 1px solid var(--border);
	border-radius: 8px;
	background: #ffffff;
	border-collapse: collapse;
}

th,
td {
	padding: 10px 12px;
	border: 1px solid var(--border);
	text-align: left;
}

a {
	color: var(--accent-2);
	font-weight: 650;
	text-decoration: none;
}

a:hover {
	text-decoration: underline;
}

.empty {
	margin: 8px auto 0;
	padding: 44px 28px;
	width: min(100%, 720px);
	border: 1px dashed #d5dae3;
	border-radius: 8px;
	background: #fafbfc;
	color: var(--muted);
	text-align: center;
}

.empty strong {
	display: block;
	margin-bottom: 8px;
	color: var(--text);
	font-family: var(--font-display);
	font-size: 1.08rem;
	font-weight: 650;
}

.message-flow {
	display: flex;
	flex-direction: column;
	gap: 8px;
}

.message-skill-chip {
	margin: 0 2px;
	vertical-align: -4px;
}

.composer-inline-skill {
	display: inline-flex;
	align-items: center;
	max-width: 220px;
	min-height: 24px;
	gap: 5px;
	margin: 0 2px;
	padding: 2px 7px 2px 6px;
	border: 1px solid #c9d6ee;
	border-radius: 6px;
	background: #f3f7ff;
	color: #375fae;
	font-size: 13px;
	font-weight: 600;
	line-height: 1.35;
	user-select: all;
	white-space: nowrap;
}

.composer-inline-skill-icon {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	flex: 0 0 auto;
}

.composer-inline-skill-icon svg {
	width: 14px;
	height: 14px;
}

.composer-inline-skill-name {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.message-attachments {
	display: grid;
	grid-template-columns: repeat(auto-fit, minmax(128px, 184px));
	gap: 10px;
	padding: 0;
}

.message-attachment {
	margin: 0;
	overflow: hidden;
	border: 1px solid var(--border);
	border-radius: 8px;
	background: #ffffff;
}

.message-attachment-image {
	display: block;
	width: 100%;
	max-height: min(280px, 42vh);
	height: auto;
	object-fit: contain;
	object-position: center;
	background: var(--panel-muted);
	cursor: zoom-in;
}

.message-attachment-image.missing {
	aspect-ratio: 4 / 3;
	min-height: 128px;
	background: var(--panel-muted);
}

.body.body-segment img,
.thinking-markdown img {
	display: block;
	max-width: min(100%, 560px);
	max-height: min(420px, 52vh);
	width: auto;
	height: auto;
	margin: 10px 0;
	border-radius: 8px;
	object-fit: contain;
	cursor: zoom-in;
}

.message-attachment figcaption {
	display: grid;
	gap: 2px;
	padding: 8px 9px 9px;
}

.message-attachment-name {
	color: var(--text);
	font-size: 12px;
	font-weight: 650;
}

.message-attachment-size {
	color: var(--muted);
	font-size: 11px;
}

.thinking-block {
	margin: 0;
	padding-top: 2px;
	overflow: visible;
	border: 0;
	background: transparent;
}

.thinking-block.last {
	margin-bottom: 6px;
}

.thinking-summary {
	width: 100%;
	display: flex;
	align-items: center;
	justify-content: flex-start;
	gap: 9px;
	padding: 4px 0;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: #65758b;
	text-align: left;
}

.thinking-summary.passive {
	cursor: default;
}

.thinking-summary:not(.passive):hover {
	color: #405875;
	background: transparent;
}

.thinking-label {
	font-size: 13px;
	font-weight: 600;
	letter-spacing: 0.01em;
}

.thinking-dot {
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: #8fa1bc;
	opacity: 0.95;
}

.thinking-dot.live {
	background: var(--accent);
}

.thinking-chevron {
	margin-left: auto;
	color: #8a929e;
	font-size: 13px;
	transition: transform 140ms ease;
}

.thinking-block.open .thinking-chevron {
	transform: rotate(90deg);
	color: var(--text);
}

.thinking-content {
	display: none;
	padding: 9px 0 1px;
}

.thinking-block.open .thinking-content {
	display: block;
}

.thinking-markdown {
	padding: 6px 0 6px 12px;
	border-left: 2px solid #d5e0f1;
	color: #8a96a7;
	font-size: 12px;
	line-height: 1.7;
}

.thinking-placeholder {
	margin: 0;
	color: var(--muted-soft);
	font-size: 12px;
}

.tool-segment {
	padding: 0;
}

.tool-segment + .tool-segment {
	margin-top: 6px;
}

.tool-segment.last {
	padding-bottom: 2px;
}

.tool-block,
.tool-group-block {
	overflow: visible;
	border: 0;
	background: transparent;
	box-shadow: none;
}

.tool-summary,
.tool-group-summary {
	width: 100%;
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 10px;
	padding: 5px 0;
	border: 0;
	background: transparent;
	color: #4f6580;
	text-align: left;
}

.tool-summary:hover,
.tool-group-summary:hover {
	color: #4f6580;
}

.tool-summary-main,
.tool-group-summary-main {
	min-width: 0;
	flex: 1 1 auto;
	display: inline-flex;
	align-items: center;
}

.tool-summary-main::before,
.tool-group-summary-main::before {
	content: '';
	width: 5px;
	height: 5px;
	flex: 0 0 auto;
	margin-right: 8px;
	border-radius: 50%;
	background: #9aa6b5;
}

.inline-tool-label,
.tool-group-label {
	min-width: 0;
	color: inherit;
	font-size: 12px;
	font-weight: 600;
	line-height: 1.4;
	white-space: normal;
	word-break: break-word;
}

.tool-summary-side,
.tool-group-summary-side {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	flex: 0 0 auto;
	color: #7a8797;
}

.tool-summary-duration {
	font-size: 11px;
	color: inherit;
}

.tool-summary-chevron,
.tool-group-chevron {
	color: inherit;
	font-size: 13px;
	transition: transform 140ms ease;
}

.tool-block.open .tool-summary-chevron,
.tool-group-block.open .tool-group-chevron {
	transform: rotate(90deg);
}

.tool-group-details {
	display: none;
	margin: 9px 0 4px;
	padding-left: 14px;
	border-left: 2px solid #dce5f4;
}

.tool-group-block.open .tool-group-details {
	display: block;
}

.tool-details {
	display: none;
	padding: 8px 0 12px;
}

.tool-block.open .tool-details {
	display: block;
}

.tool-details-header {
	padding: 0 0 6px;
	color: var(--muted-soft);
	font-size: 10.5px;
	font-weight: 700;
	letter-spacing: 0.06em;
	text-transform: uppercase;
}

.tool-details-body {
	border: 0;
	border-radius: 6px;
	background: #f8fafc;
}

.tool-details-pre {
	max-height: 280px;
	margin: 0;
	padding: 12px 13px;
	border: 0;
	background: transparent;
	font-size: 11.5px;
	line-height: 1.6;
}

.tool-details-footer {
	padding-top: 6px;
	justify-content: flex-start;
}

.tool-details-status {
	font-size: 11px;
}

.graph-view {
	width: 100%;
	height: 100%;
	min-height: 0;
}

.graph-canvas {
	position: relative;
	width: 100%;
	height: 100%;
	overflow: hidden;
	background: #ffffff;
}

.graph-svg {
	width: 100%;
	height: 100%;
	display: block;
}

.graph-toolbar,
.graph-hud {
	position: absolute;
	z-index: 3;
	display: flex;
	align-items: center;
	border: 1px solid var(--border);
	border-radius: 8px;
	background: rgba(255, 255, 255, 0.94);
	box-shadow: 0 8px 20px rgba(23, 26, 31, 0.08);
}

.graph-toolbar {
	top: 14px;
	right: 14px;
	gap: 8px;
	padding: 7px 8px;
}

.graph-toolbar-label {
	color: var(--muted);
	font-size: 11px;
}

.graph-toolbar-value {
	color: var(--text);
	font-size: 12px;
}

.graph-toolbar-divider {
	width: 1px;
	height: 18px;
	background: var(--border);
}

.graph-toolbar-btn {
	width: 26px;
	height: 26px;
	display: grid;
	place-items: center;
	padding: 0;
	border: 1px solid var(--border);
	border-radius: 6px;
	background: #ffffff;
	color: var(--text);
}

.graph-toolbar-btn:disabled {
	cursor: default;
	opacity: 0.42;
}

.graph-toolbar-btn svg {
	width: 14px;
	height: 14px;
	fill: none;
	stroke: currentColor;
	stroke-width: 1.8;
	stroke-linecap: round;
}

.graph-hud {
	left: 14px;
	bottom: 14px;
	gap: 14px;
	padding: 9px 11px;
}

.graph-hud-target,
.graph-hud-stat {
	display: grid;
	gap: 2px;
}

.graph-hud-label,
.graph-hud-stat span {
	color: var(--muted);
	font-size: 10px;
	font-weight: 700;
	letter-spacing: 0.06em;
}

.graph-hud-target strong,
.graph-hud-stat strong {
	color: var(--text);
	font-size: 12px;
}

.graph-hud-stats {
	display: flex;
	gap: 10px;
}

.graph-grid-small,
.graph-grid-large {
	fill: none;
	stroke: #e7ebf1;
	stroke-width: 1;
}

.graph-grid-large {
	stroke: #d8dee8;
}

.graph-checker-base,
.graph-checker-accent {
	fill: #ffffff;
}

.graph-surface {
	fill: #ffffff;
}

.graph-grid {
	fill: url(#graph-grid-large);
	opacity: 0.75;
}

.graph-edge {
	fill: none;
	stroke: #b8c4d6;
	stroke-width: 3;
	stroke-linecap: round;
	opacity: 0.72;
}

.graph-edge.active {
	stroke: var(--accent);
	opacity: 0.95;
}

.graph-edge-trace {
	fill: none;
	stroke: var(--accent);
	stroke-width: 5;
	stroke-linecap: round;
	stroke-dasharray: 16 84;
}

.graph-packet-glow,
.graph-burst {
	fill: rgba(var(--accent-rgb), 0.16);
}

.graph-packet-core {
	fill: var(--accent);
}

.graph-node-halo {
	fill: rgba(var(--accent-rgb), 0.08);
	opacity: 0;
}

.graph-node.active .graph-node-halo,
.graph-node.selected .graph-node-halo {
	opacity: 1;
}

.graph-node-outer {
	fill: #ffffff;
	stroke: #cdd7e6;
	stroke-width: 2;
}

.graph-node.active .graph-node-outer,
.graph-node.selected .graph-node-outer {
	stroke: var(--accent);
}

.graph-node-inner {
	fill: #f5f8fc;
	stroke: #e1e7f0;
	stroke-width: 1;
}

.graph-icon-stroke {
	fill: none;
	stroke: #51637a;
	stroke-width: 3;
	stroke-linecap: round;
	stroke-linejoin: round;
}

.graph-satellite-ring {
	fill: #ffffff;
	stroke: #cdd7e6;
	stroke-width: 2;
}

.graph-satellite-core {
	fill: #edf3ff;
}

.graph-satellite-label,
.graph-node-label,
.graph-node-status {
	fill: #2f3b4a;
	text-anchor: middle;
	font-family: var(--font-ui);
}

.graph-satellite-label {
	font-size: 10px;
	font-weight: 700;
}

.graph-node-label {
	font-size: 13px;
	font-weight: 700;
}

.graph-node-status {
	fill: var(--muted);
	font-size: 11px;
}

.graph-empty-hint {
	position: absolute;
	inset: auto 24px 32px;
	color: var(--muted);
	font-size: 13px;
	text-align: center;
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

.image-preview-backdrop {
	position: fixed;
	inset: 0;
	z-index: 1000;
	display: flex;
	align-items: center;
	justify-content: center;
	padding: 24px;
	background: rgba(23, 26, 31, 0.42);
	backdrop-filter: blur(8px);
}

.image-preview-dialog img {
	display: block;
	max-width: min(96vw, 1600px);
	max-height: 92vh;
	border-radius: 8px;
	box-shadow: 0 24px 80px rgba(23, 26, 31, 0.28);
}

@media (max-width: 960px) {
	.message-main,
	.message-row.user .message-main {
		max-width: 100%;
		flex-basis: 100%;
	}

	.transcript-scroll {
		padding-inline: 24px;
	}

	.composer-shell {
		width: calc(100% - 28px);
	}
}
</style>
