<script setup>
import { computed, markRaw, onMounted, onUnmounted, reactive, ref } from 'vue';
import AppSidebar from './components/SideBar/AppSidebar.vue';
import WindowControls from './components/Chat/WindowControls.vue';
import ChatView from './views/ChatView.vue';
import SettingsView from './views/SettingsView.vue';
import { useHostBridge } from './composables/hostBridge.js';

const { on, post } = useHostBridge();

const viewRegistry = {
	chat: markRaw(ChatView),
	settings: markRaw(SettingsView),
};

const currentViewId = ref('chat');
const activeViewComponent = computed(() => viewRegistry[currentViewId.value] || ChatView);
const imagePreview = ref(null);
const SIDEBAR_COLLAPSE_KEY = 'selfclaw:sidebar-collapsed';
const sidebarCollapsed = ref(readSidebarCollapsed());

function readSidebarCollapsed() {
	try {
		return localStorage.getItem(SIDEBAR_COLLAPSE_KEY) === 'true';
	} catch (_) {
		return false;
	}
}

function toggleSidebarCollapsed() {
	sidebarCollapsed.value = !sidebarCollapsed.value;
	try {
		localStorage.setItem(SIDEBAR_COLLAPSE_KEY, String(sidebarCollapsed.value));
	} catch (_) {
		// 忽略持久化失败
	}
}
const sidebarConversations = ref([]);
const selectedConversationId = ref(null);
const windowChrome = reactive({
	isMaximized: false,
});

function toConversationNode(conversation) {
	return {
		id: conversation.id,
		label: conversation.title || '未命名对话',
		time: conversation.timestamp || '',
		subtitle: conversation.subtitle || '',
		type: 'conversation',
	};
}

function hasWorkspace(conversation) {
	return Boolean(conversation?.workspaceRootId || conversation?.workspaceRootPath || conversation?.workspaceRootName);
}

function buildProjectGroups(conversations) {
	const groups = new Map();
	for (const conversation of conversations.filter(hasWorkspace)) {
		const key = conversation.workspaceRootId || conversation.workspaceRootPath || conversation.workspaceRootName || 'workspace';
		if (!groups.has(key)) {
			groups.set(key, {
				id: `workspace-${key}`,
				label: conversation.workspaceRootName || conversation.workspaceRootPath || '工作区',
				workspaceRootId: conversation.workspaceRootId || null,
				workspaceRootPath: conversation.workspaceRootPath || '',
				type: 'folder',
				children: [],
			});
		}

		groups.get(key).children.push(toConversationNode(conversation));
	}

	return Array.from(groups.values());
}

const navItems = computed(() => [
	{ id: 'new-chat', label: '新建对话', type: 'action' },
	{ id: 'search', label: '搜索', type: 'action' },
	{ id: 'plugins', label: '插件', type: 'action' },
	{ id: 'extensions', label: '扩展功能', type: 'action' },
	{ id: 'automation', label: '自动化', type: 'action' },
	{
		id: 'projects',
		label: '项目',
		type: 'group',
		children: buildProjectGroups(sidebarConversations.value),
	},
	{
		id: 'conversations',
		label: '对话',
		type: 'group',
		children: sidebarConversations.value.filter((conversation) => !hasWorkspace(conversation)).map(toConversationNode),
	},
	{ id: 'settings', label: '设置', type: 'view' },
]);

const sidebarActiveId = computed(() => (currentViewId.value === 'settings' ? 'settings' : selectedConversationId.value));

// window-state 与 replaceState 都是宿主持续广播的状态型 push；订阅即可，
// 侧边栏只从 replaceState 里取会话列表。ChatView 单独订阅 replaceState
// 渲染对话，故这里两处订阅者共存。
on('window-state', (payload) => {
	windowChrome.isMaximized = Boolean(payload.isMaximized);
});

on('replaceState', (payload) => {
	sidebarConversations.value = Array.isArray(payload.conversations) ? payload.conversations : [];
	selectedConversationId.value = payload.selectedConversationId || null;
});

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

function onSidebarAction(action) {
	const actionId = typeof action === 'string' ? action : action?.id;
	switch (actionId) {
		case 'new-chat':
		case 'add-conversations':
			currentViewId.value = 'chat';
			selectedConversationId.value = null;
			post({ type: 'new-chat' });
			break;
		case 'delete-conversation':
			if (action?.conversationId) {
				post({ type: 'delete-conversation', conversationId: action.conversationId });
			}
			break;
		case 'clear-conversations':
			if (Array.isArray(action?.conversationIds) && action.conversationIds.length > 0) {
				post({ type: 'clear-conversations', conversationIds: action.conversationIds });
			}
			break;
		case 'delete-workspace-root':
			if (action?.workspaceRootId) {
				post({ type: 'delete-workspace-root', workspaceRootId: action.workspaceRootId });
			}
			break;
		default:
			break;
	}
}

function onSidebarSelect(id) {
	if (id in viewRegistry) {
		currentViewId.value = id;
		return;
	}

	if (sidebarConversations.value.some((conversation) => conversation.id === id)) {
		currentViewId.value = 'chat';
		post({ type: 'select-conversation', conversationId: id });
	}
}

onMounted(() => {
	document.addEventListener('click', handleDocumentClick);
	document.addEventListener('keydown', onDocumentKeydown);
});

onUnmounted(() => {
	document.removeEventListener('click', handleDocumentClick);
	document.removeEventListener('keydown', onDocumentKeydown);
});
</script>

<template>
	<div class="app" :class="{ 'sidebar-collapsed': sidebarCollapsed }">
		<AppSidebar
			:items="navItems"
			:active-id="sidebarActiveId"
			:collapsed="sidebarCollapsed"
			@select="onSidebarSelect"
			@action="onSidebarAction"
			@toggle-collapse="toggleSidebarCollapsed"
		/>
		<main class="main">
			<div class="main-header">
				<div class="window-drag-region" aria-hidden="true" @pointerdown="onWindowDragPointerDown"></div>
				<WindowControls :is-maximized="windowChrome.isMaximized" @action="onWindowControlAction" />
			</div>
			<div class="main-content">
				<component :is="activeViewComponent" @preview-image="openImagePreview" />
			</div>
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
	--card-border: #e3e6ec;
	--text: #171a1f;
	--muted: #6b7280;
	--muted-soft: #8a929e;
	--accent: #3b5bfd;
	--accent-2: #2f49d1;
	--accent-rgb: 59, 91, 253;
	--accent-soft: rgba(59, 91, 253, 0.08);
	--success: #0f9d63;
	--danger: #dc4545;
	--shadow: 0 12px 30px rgba(23, 26, 31, 0.08);
	--card-shadow: 0 1px 2px rgba(23, 26, 31, 0.05), 0 10px 26px rgba(23, 26, 31, 0.06);
	--font-ui: 'Segoe UI Variable Text', 'Segoe UI', sans-serif;
	--font-display: 'Segoe UI Variable Display', 'Segoe UI', sans-serif;
	--font-code: 'Cascadia Code', Consolas, monospace;
	--font-mono: 'JetBrains Mono', 'SF Mono', 'Cascadia Code', ui-monospace, Menlo, Consolas, monospace;
	--ease-out: cubic-bezier(0.22, 1, 0.36, 1);
	--ease-spring: cubic-bezier(0.34, 1.56, 0.64, 1);
	--scroll-track: transparent;
	--scroll-thumb: rgba(23, 26, 31, 0.16);
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
	border: 3px solid transparent;
	background-clip: padding-box;
	border-radius: 999px;
}

::-webkit-scrollbar-thumb:hover {
	background-color: rgba(23, 26, 31, 0.26);
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
	transition: grid-template-columns 240ms cubic-bezier(0.22, 0.82, 0.28, 1);
}

.app.sidebar-collapsed {
	grid-template-columns: 60px 1fr;
}

.window-drag-region {
	position: absolute;
	inset: 0;
	right: 244px;
	z-index: 110;
	-webkit-user-select: none;
	user-select: none;
}

.main {
	position: relative;
	min-width: 0;
	height: 100%;
	display: flex;
	flex-direction: column;
	overflow: hidden;
}

.main-header {
	position: relative;
	flex: 0 0 46px;
	height: 46px;
}

.main-content {
	position: relative;
	min-height: 0;
	flex: 1 1 auto;
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
	background: transparent;
	/* 顶部渐隐：消息滚出可视区时柔和消失，避免在画布顶缘生硬截断 */
	-webkit-mask-image: linear-gradient(to bottom, transparent 0, #000 34px);
	mask-image: linear-gradient(to bottom, transparent 0, #000 34px);
}

.message-row {
	display: flex;
	align-items: flex-start;
	justify-content: flex-start;
	margin-bottom: 28px;
	animation: message-in 420ms cubic-bezier(0.22, 1, 0.36, 1) both;
}

@keyframes message-in {
	from {
		opacity: 0;
		transform: translateY(7px);
	}

	to {
		opacity: 1;
		transform: none;
	}
}

@media (prefers-reduced-motion: reduce) {
	.message-row {
		animation: none;
	}
}

.message-row:last-child,
.message-row:has(+ .turn-status-row) {
	margin-bottom: 0;
}

/* ===== 回合执行状态行（对话底部：绿点 + 执行中 + 耗时） ===== */
.turn-status-row {
	display: flex;
	align-items: center;
	gap: 8px;
	margin-top: 14px;
	padding: 2px 0;
	flex: none;
}

.turn-status-dot {
	width: 7px;
	height: 7px;
	border-radius: 50%;
	background: var(--success);
	animation: turn-status-pulse 1.6s ease-out infinite;
}

@keyframes turn-status-pulse {
	0% {
		box-shadow: 0 0 0 0 rgba(15, 157, 99, 0.32);
	}

	70% {
		box-shadow: 0 0 0 6px rgba(15, 157, 99, 0);
	}

	100% {
		box-shadow: 0 0 0 0 rgba(15, 157, 99, 0);
	}
}

.turn-status-label {
	color: #5f6a78;
	font-size: 12.5px;
	font-weight: 600;
	letter-spacing: 0.01em;
}

.turn-status-time {
	color: #9aa2ad;
	font-family: var(--font-mono);
	font-size: 11.5px;
	font-weight: 500;
	font-variant-numeric: tabular-nums;
}

@media (prefers-reduced-motion: reduce) {
	.turn-status-dot {
		animation: none;
	}
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
	border: 1px solid var(--card-border);
	/* 右下角收小：不依赖头像也能读出「这是你说的」方向感 */
	border-radius: 16px 16px 6px 16px;
	background: #ffffff;
	box-shadow: var(--card-shadow);
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
	font-family: var(--font-mono);
	font-size: 10.5px;
	line-height: 1.2;
	letter-spacing: 0.02em;
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

.body>* {
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

.message-cancelled {
	margin: 8px 0 0;
	color: var(--muted);
	font-size: 12px;
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
	border-left: 3px solid rgba(var(--accent-rgb), 0.35);
	color: var(--muted);
}

pre {
	margin: 0.85rem 0;
	padding: 12px 14px;
	overflow: auto;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: #f5f7fa;
	color: #1f2937;
	font-size: 13px;
}

code {
	font-family: var(--font-mono);
	font-size: 13px;
}

:not(pre)>code {
	padding: 2px 6px;
	border-radius: 5px;
	background: #eef2f7;
	color: #263142;
}

table {
	width: 100%;
	overflow: hidden;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: #ffffff;
	border-collapse: collapse;
}

th,
td {
	padding: 10px 12px;
	border: 1px solid var(--border);
	text-align: left;
}

th {
	background: #f7f9fc;
	font-weight: 650;
}

a {
	color: var(--accent-2);
	font-weight: 650;
	text-decoration: none;
}

a:hover {
	text-decoration: underline;
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
	border: 1px solid rgba(var(--accent-rgb), 0.3);
	border-radius: 6px;
	background: var(--accent-soft);
	color: var(--accent-2);
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

/* ===== 思考 / 工具调用：浅灰圆角卡片行（状态图标 + 主副标签 + 右侧箭头） ===== */
.thinking-block {
	margin: 0;
	overflow: hidden;
	border: 1px solid #edeff3;
	border-radius: 12px;
	background: #f7f8fa;
	transition: border-color 0.15s;
}

.thinking-block:not(.pending):hover {
	border-color: #e0e4ea;
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
	padding: 9px 12px;
	border: 0;
	background: transparent;
	color: #3d4654;
	text-align: left;
}

.thinking-summary.passive {
	cursor: default;
}

.thinking-spark {
	display: inline-grid;
	place-items: center;
	width: 18px;
	height: 18px;
	color: #707c8c;
	flex: none;
}

.thinking-spark svg {
	width: 13px;
	height: 13px;
}

.thinking-spark.live {
	color: var(--accent);
	animation: spark-pulse 1.5s ease-in-out infinite;
}

@keyframes spark-pulse {
	50% {
		transform: scale(0.78);
		opacity: 0.6;
	}
}

.thinking-label {
	font-size: 12.5px;
	font-weight: 600;
	color: #22262c;
	letter-spacing: 0.01em;
}

.thinking-chevron {
	margin-left: auto;
	color: #99a2b0;
	font-size: 14px;
	transition: transform 140ms ease;
}

.thinking-block.open .thinking-chevron {
	transform: rotate(90deg);
	color: var(--text);
}

.thinking-content {
	display: none;
	padding: 0 12px 11px;
}

.thinking-block.open .thinking-content {
	display: block;
}

.thinking-markdown {
	padding: 6px 0 2px 12px;
	border-left: 2px solid #d9e2ef;
	color: #7f8b9c;
	font-size: 12px;
	line-height: 1.7;
}

.thinking-placeholder {
	margin: 0;
	color: var(--muted-soft);
	font-size: 12px;
}

.preparing-indicator {
	display: flex;
	align-items: center;
	gap: 9px;
	padding: 6px 0;
	font-size: 12.5px;
	font-weight: 600;
	letter-spacing: 0.01em;
}

.shimmer-text {
	background: linear-gradient(90deg, #93a3bb 25%, #33465f 50%, #93a3bb 75%);
	background-size: 200% 100%;
	-webkit-background-clip: text;
	background-clip: text;
	color: transparent;
	animation: shimmer-text-sweep 1.8s linear infinite;
}

@keyframes shimmer-text-sweep {
	0% {
		background-position: 200% 0;
	}

	100% {
		background-position: -200% 0;
	}
}

.tool-segment {
	padding: 0;
}

.tool-segment+.tool-segment {
	margin-top: 8px;
}

.tool-segment.last {
	padding-bottom: 2px;
}

.tool-block,
.tool-group-block {
	overflow: hidden;
	border: 1px solid #edeff3;
	border-radius: 12px;
	background: #f7f8fa;
	box-shadow: none;
	transition: border-color 0.15s;
}

.tool-block:hover,
.tool-group-block:hover {
	border-color: #e0e4ea;
}

/* 组内嵌套的工具卡片：灰卡片里的白色子卡片 */
.tool-block.nested {
	border: 1px solid #e8ebf0;
	border-radius: 9px;
	background: #ffffff;
}

.tool-summary,
.tool-group-summary {
	width: 100%;
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 10px;
	padding: 9px 12px;
	border: 0;
	background: transparent;
	color: #3d4654;
	text-align: left;
}

.tool-summary.nested {
	padding: 7px 10px;
}

.tool-summary-main,
.tool-group-summary-main {
	min-width: 0;
	flex: 1 1 auto;
	display: inline-flex;
	align-items: center;
	gap: 9px;
}

/* 状态图标：成功绿勾 / 失败红叉 / 取消灰杠 / 进行中转圈 */
.tool-status-icon {
	display: inline-grid;
	place-items: center;
	width: 18px;
	height: 18px;
	border-radius: 50%;
	flex: none;
}

.tool-status-icon svg {
	width: 11px;
	height: 11px;
}

.tool-status-icon.completed {
	background: rgba(15, 157, 99, 0.12);
	color: #0f9d63;
}

.tool-status-icon.failed {
	background: rgba(220, 69, 69, 0.1);
	color: var(--danger);
}

.tool-status-icon.cancelled {
	background: #eceff3;
	color: #8a929e;
}

.tool-status-icon.spinning {
	width: 13px;
	height: 13px;
	margin: 2.5px;
	border: 2px solid #d5dce6;
	border-top-color: var(--accent);
	background: transparent;
	animation: tool-spin 0.8s linear infinite;
}

@keyframes tool-spin {
	to {
		transform: rotate(360deg);
	}
}

.inline-tool-label,
.tool-group-label {
	min-width: 0;
	flex: 0 1 auto;
	overflow: hidden;
	text-overflow: ellipsis;
	color: #22262c;
	font-size: 12.5px;
	font-weight: 600;
	line-height: 1.4;
	white-space: nowrap;
}

.tool-summary-detail {
	min-width: 0;
	flex: 0 1 auto;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	color: #8f99a8;
	font-size: 12px;
	line-height: 1.4;
}

.tool-summary-side,
.tool-group-summary-side {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	flex: 0 0 auto;
	color: #99a2b0;
}

.tool-summary-duration {
	font-size: 11px;
	color: inherit;
}

.tool-summary-chevron,
.tool-group-chevron {
	color: inherit;
	font-size: 14px;
	transition: transform 140ms ease;
}

.tool-block.open .tool-summary-chevron,
.tool-group-block.open .tool-group-chevron {
	transform: rotate(90deg);
}

.tool-group-details {
	display: none;
	margin: 0;
	padding: 2px 10px 10px;
}

.tool-group-block.open .tool-group-details {
	display: grid;
	gap: 6px;
}

.tool-details {
	display: none;
	padding: 2px 12px 12px;
}

.tool-block.open .tool-details {
	display: block;
}

.tool-details-header {
	padding: 0 0 6px;
	color: var(--muted-soft);
	font-family: var(--font-mono);
	font-size: 10px;
	font-weight: 700;
	letter-spacing: 0.1em;
	text-transform: uppercase;
}

.tool-details-body {
	border: 1px solid #e6eaf1;
	border-radius: 8px;
	background: #ffffff;
}

.tool-block.nested .tool-details-body {
	border-color: #eceff3;
	background: #f8fafc;
}

.tool-details-pre {
	max-height: 280px;
	margin: 0;
	padding: 12px 13px;
	border: 0;
	background: transparent;
	box-shadow: none;
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
