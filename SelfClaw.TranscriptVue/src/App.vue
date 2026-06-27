<script setup>
import { computed, markRaw, onMounted, onUnmounted, reactive, ref } from 'vue';
import AppSidebar from './components/AppSidebar.vue';
import WindowControls from './components/WindowControls.vue';
import ChatView from './views/ChatView.vue';
import SettingsView from './views/SettingsView.vue';

const viewRegistry = {
	chat: markRaw(ChatView),
	settings: markRaw(SettingsView),
};

const currentViewId = ref('chat');
const activeViewComponent = computed(() => viewRegistry[currentViewId.value] || ChatView);
const activeViewRef = ref(null);
const imagePreview = ref(null);
const windowChrome = reactive({
	isMaximized: false,
});

const navItems = [
	{ id: 'new-chat', label: '新建对话', type: 'action' },
	{ id: 'search', label: '搜索', type: 'action' },
	{ id: 'plugins', label: '插件', type: 'action' },
	{ id: 'extensions', label: '扩展功能', type: 'action' },
	{ id: 'automation', label: '自动化', type: 'action' },
	{
		id: 'projects',
		label: '项目',
		type: 'group',
		children: [{ id: 'project-demo-1', label: '示例项目会话', type: 'conversation' }],
	},
	{
		id: 'conversations',
		label: '对话',
		type: 'group',
		children: [{ id: 'conversation-demo-1', label: '示例非项目会话', type: 'conversation' }],
	},
	{ id: 'settings', label: '设置', type: 'view' },
];

function post(message) {
	window.chrome?.webview?.postMessage(message);
}

function handleIncomingMessage(event) {
	const payload = event?.data;
	if (!payload || typeof payload !== 'object') {
		return;
	}

	if (payload.type === 'window-state') {
		windowChrome.isMaximized = Boolean(payload.isMaximized);
		return;
	}

	activeViewRef.value?.handleMessage?.(payload);
}

function onWindowDragPointerDown(event) {
	if (event.button !== 0) {
		return;
	}

	event.preventDefault();
	post({ type: event.detail > 1 ? 'window-toggle-maximize' : 'window-drag' });
}

function onWindowControlAction(action) {
	switch (action) {
		case 'terminal':
			post({ type: 'toggle-terminal' });
			break;
		case 'files':
			post({ type: 'toggle-files' });
			break;
		case 'browser':
			post({ type: 'toggle-browser' });
			break;
		case 'minimize':
			post({ type: 'window-minimize' });
			break;
		case 'toggle-maximize':
			post({ type: 'window-toggle-maximize' });
			break;
		case 'close':
			post({ type: 'window-close' });
			break;
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

function onDocumentKeydown(event) {
	if (event.key === 'Escape' && imagePreview.value) {
		closeImagePreview();
	}
}

function openImagePreview(preview) {
	imagePreview.value = preview;
}

function closeImagePreview() {
	imagePreview.value = null;
}

function onSidebarAction() {
	// 仅前端样式占位，不触发实际功能。
}

function onSidebarSelect(id) {
	// 仅切换顶部视图（设置），其他均为样式占位。
	if (id in viewRegistry) {
		currentViewId.value = id;
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
	<div class="app">
		<AppSidebar :items="navItems" :active-id="currentViewId" @select="onSidebarSelect" @action="onSidebarAction" />
		<div class="window-drag-region" aria-hidden="true" @pointerdown="onWindowDragPointerDown"></div>
		<WindowControls :is-maximized="windowChrome.isMaximized" @action="onWindowControlAction" />
		<main class="main">
			<component :is="activeViewComponent" ref="activeViewRef" @preview-image="openImagePreview" />
		</main>
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
	display: grid;
	grid-template-columns: 280px 1fr;
	background: var(--bg);
}

.window-drag-region {
	position: fixed;
	top: 0;
	left: 280px;
	right: 244px;
	z-index: 110;
	height: 46px;
	-webkit-user-select: none;
	user-select: none;
}

.main {
	position: relative;
	min-width: 0;
	height: 100%;
	overflow: hidden;
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
	padding: 58px min(11.5vw, 104px) 32px;
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
}
</style>
