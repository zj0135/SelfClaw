<script setup>
import { computed, markRaw, nextTick, onMounted, onUnmounted, reactive, ref } from 'vue';
import AppSidebar from './components/SideBar/AppSidebar.vue';
import AppToast from './components/common/AppToast.vue';
import AppConfirmDialog from './components/common/AppConfirmDialog.vue';
import PluginLauncher from './components/Plugins/PluginLauncher.vue';
import PluginDockHost from './components/Plugins/PluginDockHost.vue';
import PluginFloatingLayer from './components/Plugins/PluginFloatingLayer.vue';
import WindowControls from './components/Chat/WindowControls.vue';
import ChatView from './views/ChatView.vue';
import SettingsView from './views/SettingsView.vue';
import ImageGenerationView from './views/ImageGenerationView.vue';
import TranslationView from './views/TranslationView.vue';
import AutomationView from './views/AutomationView.vue';
import { useHostBridge } from './composables/hostBridge.js';
import { usePluginViews } from './composables/usePluginViews.js';
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

// ===== 插件视图（右侧停靠面板 + 窗口悬浮层） =====
const launcherOpen = ref(false);
const DOCK_WIDTH_KEY = 'selfclaw:dock-width';
const DOCK_HIDDEN_KEY = 'selfclaw:dock-hidden';
const FLOATS_HIDDEN_KEY = 'selfclaw:floats-hidden';
const dockWidth = ref(readDockWidth());
const dockHidden = ref(readDockHidden());
const floatsHidden = ref(readFloatsHidden());
const resizing = ref(false);

// 拖动停靠分隔条或整体收敛悬浮层时一律解除武装：悬浮帧的开关是内联样式，会盖掉
// `.app.resizing iframe { pointer-events: none }`，所以这里要用 JS 而不是 CSS 来收敛。
const pluginViews = usePluginViews({ isSuspended: () => resizing.value || floatsHidden.value });

// 标题栏两颗按钮各自是否有可显隐的内容（有已打开的视图）。没有时它们是禁用态，
// 不再退回启动器——打开视图只剩左侧「插件」那一个入口。
const dockAvailable = computed(() => pluginViews.dockedViews.value.length > 0);
const floatsAvailable = computed(() => pluginViews.floatingViews.value.length > 0);

// 隐藏是外壳的视图状态，不是视图的生命周期：帧与租约都留着，只是这一列不占位置。
// 因此右栏可见 = 有停靠视图 且 没被隐藏。
const dockVisible = computed(() => dockAvailable.value && !dockHidden.value);
const floatsVisible = computed(() => !floatsHidden.value);

function readDockWidth() {
	const stored = Number(localStorage.getItem(DOCK_WIDTH_KEY));
	return Number.isFinite(stored) && stored >= 280 ? Math.min(stored, 720) : 380;
}

function readDockHidden() {
	try {
		return localStorage.getItem(DOCK_HIDDEN_KEY) === 'true';
	} catch (_) {
		return false;
	}
}

function readFloatsHidden() {
	try {
		return localStorage.getItem(FLOATS_HIDDEN_KEY) === 'true';
	} catch (_) {
		return false;
	}
}

function setDockHidden(hidden) {
	dockHidden.value = hidden;
	try {
		localStorage.setItem(DOCK_HIDDEN_KEY, String(hidden));
	} catch (_) {
		// 忽略持久化失败
	}
}

// 悬浮层的显隐是用户级的退路：插件盖住整个界面时，标题栏这颗开关永远可点。
function setFloatsHidden(hidden) {
	floatsHidden.value = hidden;
	try {
		localStorage.setItem(FLOATS_HIDDEN_KEY, String(hidden));
	} catch (_) {
		// 忽略持久化失败
	}
}

function startDockResize(event) {
	if (event.button !== 0) return;
	resizing.value = true;
	const startX = event.clientX;
	const startWidth = dockWidth.value;

	function onMove(moveEvent) {
		dockWidth.value = Math.min(720, Math.max(280, startWidth + (startX - moveEvent.clientX)));
	}

	function onUp() {
		resizing.value = false;
		window.removeEventListener('pointermove', onMove);
		window.removeEventListener('pointerup', onUp);
		try {
			localStorage.setItem(DOCK_WIDTH_KEY, String(Math.round(dockWidth.value)));
		} catch (_) {
			// 忽略持久化失败
		}
	}

	window.addEventListener('pointermove', onMove);
	window.addEventListener('pointerup', onUp);
	event.preventDefault();
}

const openViewKeys = computed(() => pluginViews.openViews.value.map((view) => view.key));

// 从左侧导航打开就是要看见它。已经打开过的停靠视图走这条路只是重新激活并取消隐藏，
// 所以启动器里的条目在隐藏态下必须仍然可点——否则视图全开时右栏就没有出路了。
// 打开失败（例如宿主的上限）时启动器不关：错误消息就显示在那里，否则悬浮视图失败时用户什么也看不到。
async function openPluginView(key) {
	const view = pluginViews.available.value.find((candidate) => candidate.key === key);
	const opened = await pluginViews.open(key);
	if (!opened) return;
	launcherOpen.value = false;
	// 打开一个视图就是要看见它：停靠视图展开右栏，悬浮视图取消整体隐藏。
	if (view?.slot === 'right') setDockHidden(false);
	if (view?.slot === 'floating') setFloatsHidden(false);
}

function hideDock() {
	setDockHidden(true);
}

// 标题栏两颗按钮就是纯显隐开关：现在看得见就收起，看不见就展开。没有可显隐的内容时
// 按钮本来是禁用态，所以这里不必再兜底（打开视图的入口只在左侧「插件」）。
function toggleFloats() {
	setFloatsHidden(!floatsHidden.value);
}

function toggleDock() {
	setDockHidden(dockVisible.value);
}

function openPluginSettings() {
	launcherOpen.value = false;
	currentViewId.value = 'settings';
}

// 视图上下文由宿主推送（plugin-host/context），usePluginViews 自行订阅。外壳这里只转发
// transcript：它本来就是外壳收到的负载，没有第二个来源可以跟它对不上。
transcript.on((payload) => {
	pluginViews.publishTranscript({ items: payload.items || [], revision: payload.revision });
}, { critical: false });

pluginViews.onInsertPrompt.value = (text) => chatViewRef.value?.insertPrompt?.(text);

const { navItems, sidebarActiveId, onSidebarAction, onSidebarSelect } = useConversationNavigation(
	currentViewId, chatViewRef, () => { launcherOpen.value = true; });

on('window-state', (payload) => {
	windowChrome.isMaximized = Boolean(payload.isMaximized);
});

function onWindowDragPointerDown(event) {
	if (event.button !== 0) {
		return;
	}

	event.preventDefault();
	post({ type: event.detail > 1 ? 'window-toggle-maximize' : 'window-drag' });
}

// 缩放热区做在网页里：WPF 侧留白已归零，WebView2 铺满整个窗口，父窗口在它上面收不到鼠标。
// 落到边缘的 pointerdown 交给宿主发 WM_NCLBUTTONDOWN + 方位码，走系统自己的 resize 循环——
// 与标题栏拖动同一条路。指针要在按下瞬间就交给系统，所以不做 setPointerCapture。
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
		// 右栏显隐全在前端，不必往宿主跑一趟。
		case 'toggle-panel':
			toggleDock();
			break;
		case 'toggle-floating':
			toggleFloats();
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
	<div class="app" :class="{ 'sidebar-collapsed': sidebarCollapsed, resizing }"
		:style="{ '--dock-width': `${dockWidth}px` }">
		<AppSidebar :items="navItems" :active-id="sidebarActiveId" :collapsed="sidebarCollapsed"
			@select="onSidebarSelect" @action="onSidebarAction" @toggle-collapse="toggleSidebarCollapsed" />
		<main class="main">
			<div class="main-header" data-anchor="titlebar">
				<div class="window-drag-region" aria-hidden="true" @pointerdown="onWindowDragPointerDown"></div>
				<WindowControls :is-maximized="windowChrome.isMaximized" :panel-visible="dockVisible"
					:panel-available="dockAvailable" :floating-visible="floatsVisible"
					:floating-available="floatsAvailable"
					@action="onWindowControlAction" />
			</div>
			<div v-if="transcript.error.value" class="transcript-recovery" role="alert">
				{{ transcript.error.value }} <button type="button" @click="transcript.resynchronize">Reload</button>
			</div>
			<div class="main-body">
				<div class="main-content">
					<component :is="activeViewComponent" ref="chatViewRef" @preview-image="openImagePreview" />
					<!-- 悬浮层只覆盖主对话区：它不能盖住侧栏与右栏。这不只是视觉要求——实测指针在子 iframe
						 上时父文档收不到任何 pointermove，悬浮层若与右栏的插件帧重叠，命中测试就永远不会
						 运行，那一帧也就永远不会被唤醒（点了没反应）。见 docs/plugin-view-system-design.md §2.3。 -->
					<PluginFloatingLayer :hidden="!floatsVisible" :views="pluginViews.floatingViews.value"
						:armed-key="pluginViews.armedKey.value" @register="pluginViews.registerFrame" />
				</div>
				<div v-if="dockVisible" class="dock-resizer" role="separator" aria-orientation="vertical"
					aria-label="调整面板宽度" @pointerdown="startDockResize"></div>
				<!-- v-show 而非 v-if：隐藏不该卸载 iframe，否则每次收起都要让插件重新加载并重走
					 握手，收起再展开就不再是一个廉价动作。没有停靠视图时 dockVisible 同样为假，
					 这一列就只是个不占位的空壳。 -->
				<PluginDockHost v-show="dockVisible" :views="pluginViews.dockedViews.value"
					:active-key="pluginViews.activeKey.value" :error="pluginViews.error.value"
					@activate="pluginViews.activate" @close="pluginViews.close" @hide="hideDock"
					@register="pluginViews.registerFrame" />
			</div>
		</main>
		<PluginLauncher :open="launcherOpen" :views="pluginViews.available.value" :open-keys="openViewKeys"
			:error="pluginViews.error.value" @close="launcherOpen = false" @select="openPluginView"
			@close-view="pluginViews.close" @manage="openPluginSettings" />
		<div v-if="imagePreview" class="image-preview-backdrop" @click.self="closeImagePreview">
			<div class="image-preview-dialog">
				<img :src="imagePreview.src" :alt="imagePreview.alt || 'Preview image'" />
			</div>
		</div>
		<AppConfirmDialog />
		<AppToast />
		<!-- 最大化时窗口贴满工作区，边缘不该再能拖，所以整组热区连同 DOM 一起摘掉。 -->
		<template v-if="!windowChrome.isMaximized">
			<div class="resize-edge resize-top" @pointerdown="onResizePointerDown($event, 'top')"></div>
			<div class="resize-edge resize-bottom" @pointerdown="onResizePointerDown($event, 'bottom')"></div>
			<div class="resize-edge resize-left" @pointerdown="onResizePointerDown($event, 'left')"></div>
			<div class="resize-edge resize-right" @pointerdown="onResizePointerDown($event, 'right')"></div>
			<div class="resize-edge resize-top-left" @pointerdown="onResizePointerDown($event, 'top-left')"></div>
			<div class="resize-edge resize-top-right" @pointerdown="onResizePointerDown($event, 'top-right')"></div>
			<div class="resize-edge resize-bottom-left" @pointerdown="onResizePointerDown($event, 'bottom-left')"></div>
			<div class="resize-edge resize-bottom-right" @pointerdown="onResizePointerDown($event, 'bottom-right')">
			</div>
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

.app.resizing {
	cursor: col-resize;
	transition: none;
	user-select: none;
}

.app.resizing iframe {
	pointer-events: none;
}

/* 视觉上就是一条 1px 分割线，与侧栏那条对齐；命中区靠 ::after 向两侧各撑出几像素。 */
.dock-resizer {
	position: relative;
	width: 1px;
	flex: none;
	background: var(--border);
	cursor: col-resize;
	transition: background 0.14s;
}

.dock-resizer::after {
	position: absolute;
	top: 0;
	bottom: 0;
	left: -4px;
	width: 9px;
	content: '';
}

.dock-resizer:hover {
	background: var(--accent);
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
	border-bottom: 1px solid var(--border);
	/* 460 = 悬浮层（450）之上：窗口拖拽区与控制按钮（含「隐藏悬浮视图」）必须永远可点，
	   这是插件盖住整个界面时的第一道退路。条带本身透明，插件的视觉内容仍然看得见。 */
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

.main-body>.plugin-dock-host {
	width: var(--dock-width, 380px);
	flex: none;
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

.transcript-content {
	display: flex;
	min-width: 0;
	flex: 0 0 auto;
	flex-direction: column;
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

:not(pre)>code {
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
	background: var(--panel);
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
	font-size: var(--fs-12);
	font-weight: 650;
}

.message-attachment-size {
	color: var(--muted);
	font-size: var(--fs-11);
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

.tool-status-icon.blocked {
	color: var(--accent-2);
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

	.message-main,
	.message-row.user .message-main {
		max-width: 100%;
		flex-basis: 100%;
	}

	.transcript-scroll {
		padding-inline: 24px;
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
