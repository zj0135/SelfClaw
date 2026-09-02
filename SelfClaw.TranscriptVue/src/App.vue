<script setup>
import { computed, markRaw, nextTick, onMounted, onUnmounted, reactive, ref } from 'vue';
import AppSidebar from './components/SideBar/AppSidebar.vue';
import AppToast from './components/common/AppToast.vue';
import PluginLauncher from './components/Plugins/PluginLauncher.vue';
import PluginPanelHost from './components/Plugins/PluginPanelHost.vue';
import WindowControls from './components/Chat/WindowControls.vue';
import ChatView from './views/ChatView.vue';
import SettingsView from './views/SettingsView.vue';
import { useHostBridge } from './composables/hostBridge.js';
import { usePluginPanels } from './composables/usePluginPanels.js';

const { on, post } = useHostBridge();

const viewRegistry = {
	chat: markRaw(ChatView),
	settings: markRaw(SettingsView),
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
const sidebarConversations = ref([]);
const selectedConversationId = ref(null);
const windowChrome = reactive({
	isMaximized: false,
});

// ===== 插件面板（右侧栏） =====
const panels = usePluginPanels();
const launcherOpen = ref(false);
const PANEL_WIDTH_KEY = 'selfclaw:panel-width';
const PANEL_HIDDEN_KEY = 'selfclaw:panel-hidden';
const panelWidth = ref(readPanelWidth());
const panelHidden = ref(readPanelHidden());
const resizing = ref(false);

// 隐藏是外壳的视图状态，不是面板的生命周期：标签与租约都留着，只是这一列不占位置。
// 因此右栏可见 = 有标签 且 没被隐藏。
const panelVisible = computed(() => panels.isOpen.value && !panelHidden.value);

function readPanelWidth() {
	const stored = Number(localStorage.getItem(PANEL_WIDTH_KEY));
	return Number.isFinite(stored) && stored >= 280 ? Math.min(stored, 720) : 380;
}

function readPanelHidden() {
	try {
		return localStorage.getItem(PANEL_HIDDEN_KEY) === 'true';
	} catch (_) {
		return false;
	}
}

function setPanelHidden(hidden) {
	panelHidden.value = hidden;
	try {
		localStorage.setItem(PANEL_HIDDEN_KEY, String(hidden));
	} catch (_) {
		// 忽略持久化失败
	}
}

function startPanelResize(event) {
	if (event.button !== 0) return;
	resizing.value = true;
	const startX = event.clientX;
	const startWidth = panelWidth.value;

	function onMove(moveEvent) {
		panelWidth.value = Math.min(720, Math.max(280, startWidth + (startX - moveEvent.clientX)));
	}

	function onUp() {
		resizing.value = false;
		window.removeEventListener('pointermove', onMove);
		window.removeEventListener('pointerup', onUp);
		try {
			localStorage.setItem(PANEL_WIDTH_KEY, String(Math.round(panelWidth.value)));
		} catch (_) {
			// 忽略持久化失败
		}
	}

	window.addEventListener('pointermove', onMove);
	window.addEventListener('pointerup', onUp);
	event.preventDefault();
}

const openPanelKeys = computed(() => panels.tabs.value.map((tab) => tab.key));

// 从左侧导航打开就是要看见它。已经打开过的面板走这条路只是重新激活并取消隐藏，
// 所以启动器里的条目在隐藏态下必须仍然可点——否则面板全开时右栏就没有出路了。
async function openPanel(key) {
	launcherOpen.value = false;
	setPanelHidden(false);
	await panels.open(key);
}

function hidePanels() {
	setPanelHidden(true);
}

// 标题栏那个按钮是纯粹的显隐开关，但一个标签都没有时「展开」没有东西可展开——
// 那种情况下退回启动器，让用户先挑一个面板，否则点了会毫无反应。
function togglePanels() {
	if (panelVisible.value) {
		setPanelHidden(true);
		return;
	}

	if (panels.isOpen.value) {
		setPanelHidden(false);
		return;
	}

	launcherOpen.value = true;
}

function openPluginSettings() {
	launcherOpen.value = false;
	currentViewId.value = 'settings';
}

// 面板上下文由宿主推送（plugin-host/context），usePluginPanels 自行订阅。外壳这里只转发
// transcript：它本来就是外壳收到的负载，没有第二个来源可以跟它对不上。
on('replaceState', (payload) => {
	panels.publishTranscript({ items: payload.items || [] });
});

panels.onInsertPrompt.value = (text) => chatViewRef.value?.insertPrompt?.(text);

function toConversationNode(conversation) {
	return {
		id: conversation.id,
		label: conversation.title || '未命名对话',
		time: conversation.timestamp || '',
		type: 'conversation',
		isManagedWorktree: Boolean(conversation.isManagedWorktree),
		workspaceRootId: conversation.workspaceRootId || null,
		// 右键「工作目录」要用工作区根的名字与路径，不是会话标题。
		workspaceRootName: conversation.workspaceRootName || '',
		workspaceRootPath: conversation.workspaceRootPath || '',
	};
}

function hasWorkspace(conversation) {
	return Boolean(conversation?.workspaceRootId || conversation?.workspaceRootPath || conversation?.workspaceRootName);
}

function buildProjectGroups(conversations) {
	const groups = new Map();
	for (const conversation of conversations.filter(hasWorkspace)) {
		const key = conversation.gitRepositoryId || conversation.workspaceRootId || conversation.workspaceRootPath || conversation.workspaceRootName || 'workspace';
		if (!groups.has(key)) {
			groups.set(key, {
				id: `workspace-${key}`,
				label: conversation.gitRepositoryName || conversation.workspaceRootName || conversation.workspaceRootPath || '工作区',
				workspaceRootId: conversation.workspaceRootId || null,
				workspaceRootName: conversation.workspaceRootName || '',
				workspaceRootPath: conversation.workspaceRootPath || '',
				gitRepositoryId: conversation.gitRepositoryId || null,
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
			togglePanels();
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
		case 'add-projects':
			currentViewId.value = 'chat';
			nextTick(() => chatViewRef.value?.browseWorkspaceFolder());
			break;
		case 'plugins':
			launcherOpen.value = true;
			break;
		case 'delete-conversation':
			if (action?.conversationId) {
				let removeManagedWorktree = false;
				if (action.isManagedWorktree) {
					if (!window.confirm('确认删除该会话？工作树可以继续保留。')) break;
					removeManagedWorktree = window.confirm('是否同时安全移除工作树？仅已合并且无未提交更改时可移除。');
				}

				post({
					type: 'delete-conversation',
					conversationId: action.conversationId,
					removeManagedWorktree,
				});
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
	<div class="app" :class="{ 'sidebar-collapsed': sidebarCollapsed, resizing }"
		:style="{ '--panel-width': `${panelWidth}px` }">
		<AppSidebar :items="navItems" :active-id="sidebarActiveId" :collapsed="sidebarCollapsed"
			@select="onSidebarSelect" @action="onSidebarAction" @toggle-collapse="toggleSidebarCollapsed" />
		<main class="main">
			<div class="main-header">
				<div class="window-drag-region" aria-hidden="true" @pointerdown="onWindowDragPointerDown"></div>
				<WindowControls :is-maximized="windowChrome.isMaximized" :panel-visible="panelVisible"
					@action="onWindowControlAction" />
			</div>
			<div class="main-body">
				<div class="main-content">
					<component :is="activeViewComponent" ref="chatViewRef" @preview-image="openImagePreview" />
				</div>
				<div v-if="panelVisible" class="panel-resizer" role="separator" aria-orientation="vertical"
					aria-label="调整面板宽度" @pointerdown="startPanelResize"></div>
				<!-- v-show 而非 v-if：隐藏不该卸载 iframe，否则每次收起都要让插件重新加载并重走
					 握手，收起再展开就不再是一个廉价动作。没有标签时 panelVisible 同样为假，
					 这一列就只是个不占位的空壳。 -->
				<PluginPanelHost v-show="panelVisible" :tabs="panels.tabs.value"
					:active-key="panels.activeKey.value" :error="panels.error.value"
					@activate="(key) => (panels.activeKey.value = key)" @close="panels.close" @hide="hidePanels"
					@register="panels.registerFrame" />
			</div>
		</main>
		<PluginLauncher :open="launcherOpen" :panels="panels.available.value" :open-keys="openPanelKeys"
			@close="launcherOpen = false" @select="openPanel" @manage="openPluginSettings" />
		<div v-if="imagePreview" class="image-preview-backdrop" @click.self="closeImagePreview">
			<div class="image-preview-dialog">
				<img :src="imagePreview.src" :alt="imagePreview.alt || 'Preview image'" />
			</div>
		</div>
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
.panel-resizer {
	position: relative;
	width: 1px;
	flex: none;
	background: var(--border);
	cursor: col-resize;
	transition: background 0.14s;
}

.panel-resizer::after {
	position: absolute;
	top: 0;
	bottom: 0;
	left: -4px;
	width: 9px;
	content: '';
}

.panel-resizer:hover {
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

.main-body>.plugin-panel-host {
	width: var(--panel-width, 380px);
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

/* ===== 思考 / 工具调用：浅灰圆角卡片行（状态图标 + 主副标签 + 右侧箭头） ===== */
.thinking-block {
	margin: 0;
	overflow: hidden;
	border: 1px solid var(--card-line);
	border-radius: 12px;
	background: var(--card-surface);
	transition: border-color 0.15s;
}

.thinking-block:not(.pending):hover {
	border-color: var(--card-line-hover);
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
	color: var(--text-soft);
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
	color: var(--muted);
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
	font-size: var(--fs-125);
	font-weight: 600;
	color: var(--text-strong);
	letter-spacing: 0.01em;
}

.thinking-chevron {
	margin-left: auto;
	color: var(--faint);
	font-size: var(--fs-14);
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
	border-left: 2px solid var(--quote-line);
	color: var(--muted);
	font-size: var(--fs-12);
	line-height: 1.7;
}

.thinking-placeholder {
	margin: 0;
	color: var(--muted-soft);
	font-size: var(--fs-12);
}

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
	border: 1px solid var(--card-line);
	border-radius: 12px;
	background: var(--card-surface);
	box-shadow: none;
	transition: border-color 0.15s;
}

.tool-block:hover,
.tool-group-block:hover {
	border-color: var(--card-line-hover);
}

/* 组内嵌套的工具卡片：比外层卡片再浮起一档 */
.tool-block.nested {
	border: 1px solid var(--card-nested-line);
	border-radius: 9px;
	background: var(--card-nested-surface);
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
	color: var(--text-soft);
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
	background: color-mix(in srgb, var(--success) 12%, transparent);
	color: var(--success);
}

.tool-status-icon.failed {
	background: color-mix(in srgb, var(--danger) 10%, transparent);
	color: var(--danger);
}

.tool-status-icon.cancelled {
	background: var(--panel-muted);
	color: var(--muted-soft);
}

.tool-status-icon.spinning {
	width: 13px;
	height: 13px;
	margin: 2.5px;
	border: 2px solid var(--spinner-track);
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
	color: var(--text-strong);
	font-size: var(--fs-125);
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
	color: var(--muted-soft);
	font-size: var(--fs-12);
	line-height: 1.4;
}

.tool-summary-side,
.tool-group-summary-side {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	flex: 0 0 auto;
	color: var(--faint);
}

.tool-summary-duration {
	font-size: var(--fs-11);
	color: inherit;
}

.tool-summary-chevron,
.tool-group-chevron {
	color: inherit;
	font-size: var(--fs-14);
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
	font-size: var(--fs-10);
	font-weight: 700;
	letter-spacing: 0.1em;
	text-transform: uppercase;
}

.tool-details-body {
	border: 1px solid var(--card-nested-line);
	border-radius: 8px;
	background: var(--card-nested-surface);
}

.tool-block.nested .tool-details-body {
	border-color: var(--card-line);
	background: var(--panel-soft);
}

.tool-details-pre {
	max-height: 280px;
	margin: 0;
	padding: 12px 13px;
	border: 0;
	background: transparent;
	box-shadow: none;
	font-size: var(--fs-115);
	line-height: 1.6;
}

.tool-details-footer {
	padding-top: 6px;
	justify-content: flex-start;
}

.tool-details-status {
	font-size: var(--fs-11);
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
