<script setup>
import { computed, markRaw, onMounted, onUnmounted, reactive, ref } from 'vue';
import AppSidebar from './components/sidebar/AppSidebar.vue';
import AppToast from './components/common/AppToast.vue';
import AppConfirmDialog from './components/common/AppConfirmDialog.vue';
import WindowControls from './components/shell/WindowControls.vue';
import ChatView from './views/ChatView.vue';
import SettingsView from './views/SettingsView.vue';
import ImageGenerationView from './views/ImageGenerationView.vue';
import TranslationView from './views/TranslationView.vue';
import AutomationView from './views/AutomationView.vue';
import { useHostBridge } from './composables/hostBridge.js';
import { useTranscriptBridge } from './composables/transcriptBridge.js';
import { useConversationNavigation } from './composables/useConversationNavigation.js';

const { on, post } = useHostBridge();
const transcript = useTranscriptBridge();

const viewRegistry = {
	chat: markRaw(ChatView),
	settings: markRaw(SettingsView),
	'image-generation': markRaw(ImageGenerationView),
	translation: markRaw(TranslationView),
	automation: markRaw(AutomationView),
};

const currentViewId = ref('chat');
const activeViewComponent = computed(() => viewRegistry[currentViewId.value] || ChatView);
const chatViewRef = ref(null);
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
const windowChrome = reactive({
	isMaximized: false,
});

const { navItems, sidebarActiveId, onSidebarAction, onSidebarSelect } = useConversationNavigation(currentViewId, chatViewRef);

on('window-state', (payload) => {
	windowChrome.isMaximized = Boolean(payload.isMaximized);
});

// 通知激活：宿主只负责还原窗口与推事件，视图切换发生在外壳；抽屉由 AutomationView 的
// sticky 订阅（replayLast）接管，所以这里只切视图。
on('automation/open-run', () => {
	currentViewId.value = 'automation';
});

function onWindowDragPointerDown(event) {
	if (event.button !== 0) {
		return;
	}

	event.preventDefault();
	post({ type: event.detail > 1 ? 'window-toggle-maximize' : 'window-drag' });
}

function onResizePointerDown(event, edge) {
	if (event.button !== 0) {
		return;
	}

	event.preventDefault();
	post({ type: 'window-resize', edge });
}

function onWindowControlAction(action) {
	switch (action) {
		case 'terminal':
			post({ type: 'toggle-terminal' });
			break;
		case 'minimize':
			post({ type: 'window-minimize' });
			break;
		case 'toggle-maximize':
			post({ type: 'window-toggle-maximize' });
			break;
		case 'minimize-to-tray':
			post({ type: 'window-minimize-to-tray' });
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
				<WindowControls
					:is-maximized="windowChrome.isMaximized"
					@action="onWindowControlAction"
				/>
			</div>
			<div v-if="transcript.error.value" class="transcript-recovery" role="alert">
				{{ transcript.error.value }} <button type="button" @click="transcript.resynchronize">Reload</button>
			</div>
			<div class="main-body">
				<div class="main-content">
					<component :is="activeViewComponent" ref="chatViewRef" @preview-image="openImagePreview" />
				</div>
			</div>
		</main>
		<div v-if="imagePreview" class="image-preview-backdrop" @click.self="closeImagePreview">
			<div class="image-preview-dialog">
				<img :src="imagePreview.src" :alt="imagePreview.alt || 'Preview image'" />
			</div>
		</div>
		<AppConfirmDialog />
		<AppToast />
		<template v-if="!windowChrome.isMaximized">
			<div class="resize-edge resize-top" @pointerdown="onResizePointerDown($event, 'top')"></div>
			<div class="resize-edge resize-bottom" @pointerdown="onResizePointerDown($event, 'bottom')"></div>
			<div class="resize-edge resize-left" @pointerdown="onResizePointerDown($event, 'left')"></div>
			<div class="resize-edge resize-right" @pointerdown="onResizePointerDown($event, 'right')"></div>
			<div class="resize-edge resize-top-left" @pointerdown="onResizePointerDown($event, 'top-left')"></div>
			<div class="resize-edge resize-top-right" @pointerdown="onResizePointerDown($event, 'top-right')"></div>
			<div class="resize-edge resize-bottom-left" @pointerdown="onResizePointerDown($event, 'bottom-left')"></div>
			<div class="resize-edge resize-bottom-right" @pointerdown="onResizePointerDown($event, 'bottom-right')"></div>
		</template>
	</div>
</template>

<style>
/* 全局 token（原 :root 块）已移入 styles/tokens.css，由 main.js 引入。 */

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
	width: var(--scrollbar-size);
	height: var(--scrollbar-size);
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
	background-color: var(--scroll-thumb-hover);
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
	right: 180px;
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
	border-bottom: 1px solid var(--border);
	z-index: 460;
}

/* 标题栏之下才分左右：面板与对话区并排，窗口按钮那一行横贯整个主区。 */
.main-body {
	display: flex;
	min-height: 0;
	flex: 1 1 auto;
}

.main-content {
	position: relative;
	min-width: 0;
	min-height: 0;
	flex: 1 1 auto;
	overflow: hidden;
}

.panel,
.transcript-panel {
	position: relative;
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
	/* 横向内边距 = 对话列留白 − 滚动条占位（scrollbar-gutter 在两侧对称预留），
	   正文可用宽度因此恒等于 composer 的 min(100% − --chat-inline-inset, --chat-content-width)，
	   两块上下左右边缘完全重合，不再靠 vw 比例各自缩放。 */
	padding: 58px calc((var(--chat-inline-inset) - 2 * var(--scrollbar-size)) / 2) 32px;
	scroll-padding-bottom: 32px;
	scrollbar-gutter: stable both-edges;
	background: transparent;
	/* 顶部渐隐：消息滚出可视区时柔和消失，避免在画布顶缘生硬截断 */
	-webkit-mask-image: linear-gradient(to bottom, transparent 0, #000 34px);
	mask-image: linear-gradient(to bottom, transparent 0, #000 34px);
}

.transcript-content {
	display: flex;
	min-width: 0;
	flex: 0 0 auto;
	flex-direction: column;
	/* 内容列与 composer 共用 --chat-content-width，上下两块在任何窗口宽度下都对齐。 */
	width: min(100%, var(--chat-content-width));
	margin-inline: auto;
}

.message-row {
	display: flex;
	align-items: flex-start;
	justify-content: flex-start;
	margin-bottom: 28px;
	content-visibility: auto;
	contain-intrinsic-size: auto 180px;
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
		box-shadow: 0 0 0 0 color-mix(in srgb, var(--success) 32%, transparent);
	}

	70% {
		box-shadow: 0 0 0 6px transparent;
	}

	100% {
		box-shadow: 0 0 0 0 transparent;
	}
}

.turn-status-label {
	color: var(--muted);
	font-size: var(--fs-125);
	font-weight: 600;
	letter-spacing: 0.01em;
}

.turn-status-time {
	color: var(--faint);
	font-family: var(--font-mono);
	font-size: var(--fs-115);
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
	/* 助手/系统消息铺满内容列：正文右边缘与输入框右边缘对齐，不再按 76% 收窄留出空档。 */
	flex: 1 1 auto;
	max-width: 100%;
}

.message-row.user {
	justify-content: flex-end;
}

.message-row.user .message-main {
	position: relative;
	display: flex;
	flex-direction: column;
	align-items: flex-end;
	gap: 6px;
	flex: 0 1 auto;
	max-width: min(58%, calc(var(--chat-content-width) - 108px));
}

.message-row.user .item.message.user {
	width: fit-content;
	max-width: 100%;
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
	background: var(--panel);
	box-shadow: var(--card-shadow);
}

.item.message:hover {
	border-color: transparent;
}

.item.message.user:hover {
	border-color: var(--border-strong);
}

.header {
	display: flex;
	align-items: center;
	justify-content: flex-start;
	gap: 12px;
	padding: 0 0 7px;
	color: var(--muted-soft);
	font-size: var(--fs-12);
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
	color: var(--muted-soft);
	font-family: var(--font-mono);
	font-size: var(--fs-105);
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
	font-size: var(--fs-14);
	line-height: 1.72;
}

.body.body-segment {
	padding: 0 0 12px;
	font-size: var(--fs-135);
}

.body.body-segment.first {
	padding-top: 0;
}

.body.body-segment.last {
	padding-bottom: 0;
}

.message-row.user .body.body-segment {
	padding: 13px 16px;
	color: var(--text-strong);
	font-size: var(--fs-14);
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

.message-cancelled {
	margin: 8px 0 0;
	color: var(--muted);
	font-size: var(--fs-12);
}

.message-blocked {
	margin: 8px 0 0;
	color: var(--accent-2);
	font-size: var(--fs-12);
}

h1,
h2,
h3 {
	margin-bottom: 0.55em;
	font-family: var(--font-display);
	line-height: 1.2;
}

/* 消息正文里的标题属于阅读内容，跟随界面字号缩放。保留 rem 基数是为了不改动
   既有比例，只在外面套一层 scale。 */
h1 {
	font-size: calc(1.5rem * var(--ui-font-scale));
}

h2 {
	font-size: calc(1.22rem * var(--ui-font-scale));
}

h3 {
	font-size: calc(1.05rem * var(--ui-font-scale));
}

ul,
ol {
	padding-left: 1.35rem;
}

blockquote {
	margin: 0;
	padding: 0.2rem 0 0.2rem 1rem;
	border-left: 3px solid color-mix(in srgb, var(--accent) 35%, transparent);
	color: var(--muted);
}

pre {
	margin: 0.85rem 0;
	padding: 12px 14px;
	overflow: auto;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--data-surface);
	color: var(--data-ink);
	font-size: var(--fs-13);
}

code {
	font-family: var(--font-mono);
	font-size: var(--fs-13);
}

:not(pre) > code {
	padding: 2px 6px;
	border-radius: 5px;
	background: var(--data-inline-surface);
	color: var(--data-inline-ink);
}

table {
	width: 100%;
	overflow: hidden;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--panel);
	border-collapse: collapse;
}

th,
td {
	padding: 10px 12px;
	border: 1px solid var(--border);
	text-align: left;
}

th {
	background: var(--panel-soft);
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
	border: 1px solid var(--accent-line);
	border-radius: 6px;
	background: var(--accent-soft);
	color: var(--accent-2);
	font-size: var(--fs-13);
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
	display: flex;
	flex-direction: row;
	flex-wrap: wrap;
	align-items: center;
	gap: 6px;
	padding: 0;
}

.message-row.user .message-attachments {
	justify-content: flex-end;
}

.message-attachment {
	position: relative;
	width: 56px;
	height: 56px;
	flex: 0 0 56px;
	margin: 0;
	padding: 0;
	overflow: hidden;
	border: 1px solid color-mix(in srgb, var(--text) 14%, transparent);
	border-radius: 8px;
	background: var(--panel-muted);
	cursor: pointer;
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.06);
	transition: border-color 0.16s ease, transform 0.16s cubic-bezier(0.34, 1.4, 0.64, 1), box-shadow 0.16s ease;
}

.message-attachment:hover {
	border-color: color-mix(in srgb, var(--accent) 55%, transparent);
	transform: translateY(-1px) scale(1.02);
	box-shadow: 0 4px 10px rgba(var(--shadow-ink), 0.14);
}

.message-attachment-image {
	display: block;
	width: 100%;
	height: 100%;
	object-fit: cover;
	object-position: center;
	background: var(--panel-muted);
	cursor: zoom-in;
	transition: opacity 0.15s ease;
}

.message-attachment:hover .message-attachment-image {
	opacity: 0.94;
}

.message-attachment-image.missing {
	display: grid;
	place-items: center;
	width: 100%;
	height: 100%;
	background: repeating-linear-gradient(135deg,
		color-mix(in srgb, var(--text) 7%, transparent) 0 4px,
		color-mix(in srgb, var(--text) 3%, transparent) 4px 8px);
	color: var(--muted);
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

/* ===== 思考 / 工具调用：无卡片文本行（状态图标 + 主副标签 + 右侧元信息） ===== */

.preparing-indicator {
	display: flex;
	align-items: center;
	gap: 9px;
	padding: 6px 0;
	font-size: var(--fs-125);
	font-weight: 600;
	letter-spacing: 0.01em;
}

.shimmer-text {
	background: linear-gradient(90deg, var(--shimmer-dim) 25%, var(--shimmer-bright) 50%, var(--shimmer-dim) 75%);
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

/* 状态图标：成功绿勾 / 失败红叉 / 已拦截盾牌 / 取消灰杠 / 进行中转圈。
   一律是无底色的裸字形，配色与旋转由状态类决定，图形由 lucide 组件提供。 */
.tool-status-icon {
	display: inline-grid;
	place-items: center;
	width: 18px;
	height: 18px;
	flex: none;
}

.tool-status-icon svg {
	width: 13px;
	height: 13px;
}

.tool-status-icon.completed {
	color: var(--success);
}

.tool-status-icon.failed {
	color: var(--danger);
}

.tool-status-icon.cancelled {
	color: var(--muted-soft);
}

.tool-status-icon.spinning {
	color: var(--accent);
	animation: tool-spin 0.9s linear infinite;
}

@keyframes tool-spin {
	to {
		transform: rotate(360deg);
	}
}

.image-preview-backdrop {
	position: fixed;
	inset: 0;
	z-index: 1000;
	display: flex;
	align-items: center;
	justify-content: center;
	padding: 24px;
	background: var(--overlay-strong);
	backdrop-filter: blur(8px);
}

.image-preview-dialog img {
	display: block;
	max-width: min(96vw, 1600px);
	max-height: 92vh;
	border-radius: 8px;
	box-shadow: 0 24px 80px var(--overlay-shadow);
}

@media (max-width: 960px) {
	/* 留白收紧已由 tokens.css 的 --chat-inline-inset 统一负责，这里只管消息气泡本身。 */
	.message-row.user .message-main {
		max-width: 100%;
		flex-basis: 100%;
	}
}

.resize-edge {
	position: fixed;
	z-index: 9999;
}

/* 四条边从角上让开一个角区的宽度，避免和斜向热区互相抢命中。 */
.resize-top,
.resize-bottom {
	left: 12px;
	right: 12px;
	height: var(--resize-edge);
	cursor: ns-resize;
}

.resize-top {
	top: 0;
}

.resize-bottom {
	bottom: 0;
}

.resize-left,
.resize-right {
	top: 12px;
	bottom: 12px;
	width: var(--resize-edge);
	cursor: ew-resize;
}

.resize-left {
	left: 0;
}

.resize-right {
	right: 0;
}

/* 角区做成 12×12 的方块，比边宽一些，斜向拖动才好点中。 */
.resize-top-left,
.resize-top-right,
.resize-bottom-left,
.resize-bottom-right {
	width: 12px;
	height: 12px;
}

.resize-top-left {
	top: 0;
	left: 0;
	cursor: nwse-resize;
}

.resize-top-right {
	top: 0;
	right: 0;
	cursor: nesw-resize;
}

.resize-bottom-left {
	bottom: 0;
	left: 0;
	cursor: nesw-resize;
}

.resize-bottom-right {
	bottom: 0;
	right: 0;
	cursor: nwse-resize;
}
</style>
