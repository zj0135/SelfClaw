<script setup>
import { ref, computed, onMounted, onUnmounted } from 'vue';
import {
	Plus,
	Search,
	Puzzle,
	Blocks,
	Zap,
	FolderOpen,
	Folder,
	Pencil,
	FolderCog,
	BookOpen,
	MessageSquare,
	GitBranch,
	Download,
	Eraser,
	Pin,
	Trash2,
	Settings,
	PanelLeftClose,
	PanelLeftOpen,
	ChevronRight,
	PawPrint,
} from 'lucide-vue-next';

const props = defineProps({
	items: {
		type: Array,
		default: () => [],
	},
	activeId: {
		type: String,
		default: null,
	},
	collapsed: {
		type: Boolean,
		default: false,
	},
});

const emit = defineEmits(['select', 'action', 'toggle-collapse']);

function toggleCollapse() {
	emit('toggle-collapse');
}

// 折叠态：图标轨对应的可操作项（去掉会话记录，仅保留功能图标）
const railItems = computed(() => props.items.filter((i) => i.type === 'action' && i.id !== 'new-chat'));

const expandedGroups = ref(new Set(['projects', 'conversations']));
const expandedFolders = ref(new Set());
const contextMenu = ref({
	open: false,
	x: 0,
	y: 0,
	target: null,
});

const actionItems = computed(() => props.items.filter((i) => i.type === 'action'));
const groupItems = computed(() => props.items.filter((i) => i.type === 'group'));
const settingsItem = computed(() => props.items.find((i) => i.type === 'view' && i.id === 'settings'));
const settingsActive = computed(() => props.activeId === 'settings');
const contextMenuItems = computed(() => {
	if (!contextMenu.value.open || !contextMenu.value.target) {
		return [];
	}

	return sidebarMenuItems.filter((item) => item.type === 'divider' || item.id !== 'clear-conversations' || contextMenu.value.target.kind === 'folder');
});

function toggleGroup(groupId) {
	if (expandedGroups.value.has(groupId)) {
		expandedGroups.value.delete(groupId);
	} else {
		expandedGroups.value.add(groupId);
	}
}

function toggleFolder(folderId) {
	if (expandedFolders.value.has(folderId)) {
		expandedFolders.value.delete(folderId);
	} else {
		expandedFolders.value.add(folderId);
	}
}

function selectNode(nodeId) {
	emit('select', nodeId);
}

function selectSettings() {
	emit('select', 'settings');
}

function onAction(actionId) {
	emit('action', actionId);
}

function onGroupAdd(groupId) {
	emit('action', `add-${groupId}`);
}

function isGroupOpen(groupId) {
	return expandedGroups.value.has(groupId);
}

function isFolderOpen(folderId) {
	return expandedFolders.value.has(folderId);
}

function folderHasActiveChild(folder) {
	return Array.isArray(folder?.children) && folder.children.some((child) => child.id === props.activeId);
}

function isNodeActive(nodeId) {
	return props.activeId === nodeId;
}

function isContextTarget(nodeId) {
	return contextMenu.value.open && contextMenu.value.target?.node?.id === nodeId;
}

function openConversationMenu(event, conversation) {
	openContextMenu(event, {
		kind: 'conversation',
		node: conversation,
	});
}

function openFolderMenu(event, folder) {
	openContextMenu(event, {
		kind: 'folder',
		node: folder,
	});
}

function openContextMenu(event, target) {
	const menuWidth = 208;
	const menuHeight = target.kind === 'folder' ? 376 : 342;
	const padding = 8;
	const viewportWidth = window.innerWidth || document.documentElement.clientWidth || menuWidth;
	const viewportHeight = window.innerHeight || document.documentElement.clientHeight || menuHeight;
	const x = Math.min(Math.max(event.clientX, padding), Math.max(padding, viewportWidth - menuWidth - padding));
	const y = Math.min(Math.max(event.clientY, padding), Math.max(padding, viewportHeight - menuHeight - padding));

	contextMenu.value = {
		open: true,
		x,
		y,
		target,
	};
}

function closeContextMenu() {
	contextMenu.value = {
		open: false,
		x: 0,
		y: 0,
		target: null,
	};
}

function onContextMenuItem(item) {
	const target = contextMenu.value.target;
	if (!target || item.type === 'divider') {
		return;
	}

	if (item.id === 'delete') {
		if (target.kind === 'conversation') {
			emit('action', {
				id: 'delete-conversation',
				conversationId: target.node.id,
			});
		} else if (target.node.workspaceRootId) {
			emit('action', {
				id: 'delete-workspace-root',
				workspaceRootId: target.node.workspaceRootId,
			});
		}
	} else if (item.id === 'clear-conversations' && target.kind === 'folder') {
		emit('action', {
			id: 'clear-conversations',
			conversationIds: Array.isArray(target.node.children) ? target.node.children.map((child) => child.id).filter(Boolean) : [],
		});
	}

	closeContextMenu();
}

function onDocumentClick(event) {
	if (event.target instanceof Element && event.target.closest('.context-menu')) {
		return;
	}

	closeContextMenu();
}

function onDocumentKeydown(event) {
	if (event.key === 'Escape') {
		closeContextMenu();
	}
}

// 主导航功能图标（action 项 id → Lucide 组件）
const iconMap = {
	'new-chat': Plus,
	search: Search,
	plugins: Puzzle,
	extensions: Blocks,
	automation: Zap,
};

// 右键菜单图标
const contextIconMap = {
	folder: FolderOpen,
	rename: Pencil,
	workingDirectory: FolderCog,
	book: BookOpen,
	message: MessageSquare,
	git: GitBranch,
	export: Download,
	clear: Eraser,
	pin: Pin,
	trash: Trash2,
};

function getIcon(id) {
	return iconMap[id] || Blocks;
}

function getContextIcon(id) {
	return contextIconMap[id] || Folder;
}

const sidebarMenuItems = [
	{ id: 'open-project', label: '打开项目', icon: 'folder' },
	{ id: 'rename', label: '重命名', icon: 'rename' },
	{ id: 'working-directory', label: '工作目录', icon: 'workingDirectory' },
	{ id: 'divider-a', type: 'divider' },
	{ id: 'project-docs', label: '项目档案', icon: 'book' },
	{ id: 'chat-channel', label: '聊天频道', icon: 'message' },
	{ id: 'git', label: 'Git', icon: 'git' },
	{ id: 'divider-b', type: 'divider' },
	{ id: 'export-json', label: '导出项目为 JSON', icon: 'export' },
	{ id: 'clear-conversations', label: '清空会话列表', icon: 'clear', danger: true },
	{ id: 'pin', label: '置顶', icon: 'pin' },
	{ id: 'divider-c', type: 'divider' },
	{ id: 'delete', label: '删除', icon: 'trash', danger: true },
];

const kbdMap = {
	search: 'Ctrl K',
};

onMounted(() => {
	document.addEventListener('click', onDocumentClick);
	document.addEventListener('keydown', onDocumentKeydown);
	window.addEventListener('blur', closeContextMenu);
	window.addEventListener('resize', closeContextMenu);
});

onUnmounted(() => {
	document.removeEventListener('click', onDocumentClick);
	document.removeEventListener('keydown', onDocumentKeydown);
	window.removeEventListener('blur', closeContextMenu);
	window.removeEventListener('resize', closeContextMenu);
});
</script>

<template>
	<aside class="sidebar" :class="{ collapsed }" aria-label="主导航">
		<!-- 品牌行 -->
		<div class="brand">
			<span class="brand-mark" aria-hidden="true">
				<PawPrint :size="15" :stroke-width="2.2" />
			</span>
			<span class="brand-copy">
				<span class="brand-name">SelfClaw</span>
				<span class="brand-kicker">AGENT CONSOLE</span>
			</span>
			<span class="brand-spacer"></span>
			<button
				class="brand-collapse"
				type="button"
				:title="collapsed ? '展开侧栏' : '折叠侧栏'"
				:aria-label="collapsed ? '展开侧栏' : '折叠侧栏'"
				@click="toggleCollapse"
			>
				<PanelLeftOpen v-if="collapsed" :size="16" :stroke-width="1.8" />
				<PanelLeftClose v-else :size="16" :stroke-width="1.8" />
			</button>
		</div>

		<!-- ============ 折叠态：图标轨 ============ -->
		<nav class="rail" aria-label="折叠导航">
			<div class="rail-scroll">
				<button class="rail-new" type="button" title="新建对话" @click="onAction('new-chat')">
					<Plus :size="17" :stroke-width="2.2" />
					<span class="tip">新建对话</span>
				</button>

				<span class="rail-sep" aria-hidden="true"></span>

				<button
					v-for="item in railItems"
					:key="item.id"
					class="rail-btn"
					:class="{ active: activeId === item.id }"
					type="button"
					:title="item.label"
					@click="onAction(item.id)"
				>
					<span class="ico" aria-hidden="true">
						<component :is="getIcon(item.id)" :size="16" :stroke-width="1.8" />
					</span>
					<span class="tip">{{ item.label }}<span v-if="kbdMap[item.id]" class="k">{{ kbdMap[item.id] }}</span></span>
				</button>
			</div>

			<div class="rail-bot">
				<button class="rail-btn" :class="{ active: settingsActive }" type="button" title="系统设置" @click="selectSettings">
					<span class="ico" aria-hidden="true">
						<Settings :size="16" :stroke-width="1.8" />
					</span>
					<span class="tip">系统设置</span>
				</button>
			</div>
		</nav>

		<!-- 上：功能按钮区 -->
		<div class="nav-top">
			<button class="btn-primary" type="button" @click="onAction('new-chat')">
				<Plus :size="15" :stroke-width="2.4" aria-hidden="true" />
				<span>新建对话</span>
			</button>

			<div class="tool-list">
				<button
					v-for="item in actionItems.filter((a) => a.id !== 'new-chat')"
					:key="item.id"
					class="tool-btn"
					type="button"
					@click="onAction(item.id)"
				>
					<span class="ico" aria-hidden="true">
						<component :is="getIcon(item.id)" :size="15" :stroke-width="1.8" />
					</span>
					<span class="label">{{ item.label }}</span>
					<span v-if="kbdMap[item.id]" class="kbd">{{ kbdMap[item.id] }}</span>
				</button>
			</div>
		</div>

		<!-- 中：项目节点 + 对话节点 -->
		<div class="nav-mid">
			<section v-for="(group, gi) in groupItems" :key="group.id" class="group" :class="{ open: isGroupOpen(group.id) }">
				<button class="group-head" type="button" @click="toggleGroup(group.id)">
					<span class="group-chevron" aria-hidden="true">
						<ChevronRight :size="13" :stroke-width="2" />
					</span>
					<span class="group-title">{{ group.label }}</span>
					<span class="group-count">{{ String(group.children?.length || 0).padStart(2, '0') }}</span>
					<span class="group-add" role="button" :title="`新建${group.label}`" @click.stop="onGroupAdd(group.id)">
						<Plus :size="13" :stroke-width="2.2" />
					</span>
				</button>

				<div class="group-body">
					<template v-if="group.id === 'projects'">
						<!-- 项目节点：三级结构 项目→目录→会话 -->
						<div v-for="folder in group.children" :key="folder.id" class="subfolder" :class="{ open: isFolderOpen(folder.id) || folderHasActiveChild(folder) }">
							<button
								class="project-folder"
								:class="{ 'menu-open': isContextTarget(folder.id) }"
								type="button"
								@click="toggleFolder(folder.id)"
								@contextmenu.prevent.stop="openFolderMenu($event, folder)"
							>
								<span class="folder-ico" aria-hidden="true">
									<Folder :size="14" :stroke-width="1.8" />
								</span>
								<span class="folder-name">{{ folder.label }}</span>
								<span class="folder-chevron" aria-hidden="true">
									<ChevronRight :size="13" :stroke-width="2" />
								</span>
							</button>
							<div class="subfolder-body">
								<button
									v-for="(session, si) in folder.children"
									:key="session.id"
									class="node kind-chat sc-rise"
									:style="{ '--i': si }"
									:class="{ active: isNodeActive(session.id), 'menu-open': isContextTarget(session.id) }"
									type="button"
									@click="selectNode(session.id)"
									@contextmenu.prevent.stop="openConversationMenu($event, session)"
								>
									<span class="dot" aria-hidden="true"></span>
									<span class="ntext">{{ session.label }}</span>
									<span v-if="session.time" class="ntime">{{ session.time }}</span>
								</button>
							</div>
						</div>
						<div v-if="!group.children?.length" class="empty-group">暂无项目会话</div>
					</template>

					<template v-else>
						<!-- 普通对话节点：二级结构 -->
						<button
							v-for="(child, ci) in group.children"
							:key="child.id"
							class="node kind-chat sc-rise"
							:style="{ '--i': ci }"
							:class="{ active: isNodeActive(child.id), 'menu-open': isContextTarget(child.id) }"
							type="button"
							@click="selectNode(child.id)"
							@contextmenu.prevent.stop="openConversationMenu($event, child)"
						>
							<span class="dot" aria-hidden="true"></span>
							<span class="ntext">{{ child.label }}</span>
							<span v-if="child.time" class="ntime">{{ child.time }}</span>
						</button>
						<div v-if="!group.children?.length" class="empty-group">暂无会话记录</div>
					</template>
				</div>
			</section>
		</div>

		<!-- 下：系统设置区 -->
		<div class="nav-bot">
			<button class="settings-btn" :class="{ active: settingsActive }" type="button" @click="selectSettings">
				<span class="ico" aria-hidden="true">
					<Settings :size="15" :stroke-width="1.8" />
				</span>
				<span class="label">系统设置</span>
				<span class="chev" aria-hidden="true">
					<ChevronRight :size="13" :stroke-width="2" />
				</span>
			</button>
		</div>

		<div
			v-if="contextMenu.open"
			class="context-menu"
			role="menu"
			:style="{ left: `${contextMenu.x}px`, top: `${contextMenu.y}px` }"
			@click.stop
			@contextmenu.prevent
		>
			<template v-for="item in contextMenuItems" :key="item.id">
				<div v-if="item.type === 'divider'" class="context-menu-divider" role="separator"></div>
				<button
					v-else
					class="context-menu-item"
					:class="{ danger: item.danger }"
					type="button"
					role="menuitem"
					@click="onContextMenuItem(item)"
				>
					<span class="context-menu-icon" aria-hidden="true">
						<component :is="getContextIcon(item.icon)" :size="14" :stroke-width="1.8" />
					</span>
					<span class="context-menu-label">{{ item.label }}</span>
				</button>
			</template>
		</div>
	</aside>
</template>

<style scoped>
.sidebar {
	--sb-bg: #fafbfd;
	--sb-line: rgba(19, 27, 45, 0.08);
	--sb-line-2: rgba(19, 27, 45, 0.14);
	--sb-text: #171a1f;
	--sb-soft: #454c59;
	--sb-mute: #6b7280;
	--sb-faint: #9aa1ad;
	--sb-hover: #eef0f4;
	--sb-raise: #f1f3f6;
	--sb-accent: #3b5bfd;
	--sb-accent-2: #2f49d1;
	--sb-accent-soft: rgba(59, 91, 253, 0.08);
	--sb-mono: 'JetBrains Mono', 'SF Mono', 'Cascadia Code', ui-monospace, Menlo, Consolas, monospace;
	--sb-ease-out: cubic-bezier(0.22, 1, 0.36, 1);
	--sb-ease-spring: cubic-bezier(0.34, 1.56, 0.64, 1);

	height: 100%;
	display: flex;
	flex-direction: column;
	min-height: 0;
	background: var(--sb-bg);
	border-right: 1px solid var(--sb-line);
	color: var(--sb-text);
}

@keyframes sb-rise {
	from {
		opacity: 0;
		transform: translateY(10px);
	}

	to {
		opacity: 1;
		transform: translateY(0);
	}
}

.sc-rise {
	animation: sb-rise 0.4s var(--sb-ease-out) both;
	animation-delay: calc(var(--i, 0) * 30ms);
}

/* ---- 品牌行 ---- */
.brand {
	flex: 0 0 auto;
	display: flex;
	align-items: center;
	gap: 10px;
	padding: 18px 16px 14px;
	border-bottom: 1px solid var(--sb-line);
}

.brand-mark {
	display: grid;
	width: 28px;
	height: 28px;
	flex: 0 0 auto;
	place-items: center;
	border-radius: 8px;
	background: var(--sb-accent);
	color: #fff;
	box-shadow: 0 4px 14px rgba(59, 91, 253, 0.32);
}

.brand-copy {
	display: flex;
	flex-direction: column;
	gap: 1px;
	min-width: 0;
}

.brand-name {
	font-size: 14px;
	font-weight: 680;
	letter-spacing: 0.01em;
	line-height: 1.2;
}

.brand-kicker {
	color: var(--sb-faint);
	font-family: var(--sb-mono);
	font-size: 8.5px;
	font-weight: 600;
	letter-spacing: 0.24em;
}

.brand-spacer {
	flex: 1;
}

.brand-collapse {
	display: grid;
	width: 28px;
	height: 28px;
	place-items: center;
	border: 1px solid transparent;
	border-radius: 8px;
	background: transparent;
	color: var(--sb-mute);
	transition:
		background 0.15s,
		color 0.15s,
		border-color 0.15s;
}

.brand-collapse:hover {
	border-color: var(--sb-line);
	background: var(--sb-hover);
	color: var(--sb-text);
}

.sidebar.collapsed .brand {
	flex-direction: column;
	justify-content: center;
	gap: 8px;
	padding: 14px 0 10px;
}

.sidebar.collapsed .brand-copy,
.sidebar.collapsed .brand-spacer {
	display: none;
}

/* ---- 折叠态图标轨 ---- */
.rail {
	display: none;
}

.sidebar.collapsed .rail {
	display: flex;
	flex: 1;
	flex-direction: column;
	min-height: 0;
}

.sidebar.collapsed .nav-top,
.sidebar.collapsed .nav-mid,
.sidebar.collapsed .nav-bot {
	display: none;
}

.rail-scroll {
	flex: 1;
	display: flex;
	flex-direction: column;
	align-items: center;
	gap: 4px;
	padding: 12px 0;
	overflow-y: auto;
}

.rail-new {
	display: grid;
	width: 36px;
	height: 36px;
	place-items: center;
	border: 0;
	border-radius: 10px;
	background: var(--sb-accent);
	color: #fff;
	box-shadow: 0 4px 14px rgba(59, 91, 253, 0.3);
	transition:
		transform 0.14s var(--sb-ease-spring),
		box-shadow 0.15s;
}

.rail-new:hover {
	transform: translateY(-1px);
	box-shadow: 0 8px 20px rgba(59, 91, 253, 0.36);
}

.rail-sep {
	width: 20px;
	height: 1px;
	margin: 6px 0;
	background: var(--sb-line-2);
}

.rail-btn {
	position: relative;
	display: grid;
	width: 36px;
	height: 36px;
	place-items: center;
	border: 0;
	border-radius: 10px;
	background: transparent;
	color: var(--sb-mute);
	transition:
		background 0.15s,
		color 0.15s;
}

.rail-btn:hover {
	background: var(--sb-hover);
	color: var(--sb-text);
}

.rail-btn.active {
	background: var(--sb-accent-soft);
	color: var(--sb-accent);
}

.rail-bot {
	display: flex;
	justify-content: center;
	padding: 10px 0 14px;
	border-top: 1px solid var(--sb-line);
}

/* 折叠态悬浮提示 */
.tip {
	position: absolute;
	left: calc(100% + 10px);
	top: 50%;
	z-index: 90;
	display: inline-flex;
	align-items: center;
	gap: 7px;
	padding: 5px 10px;
	transform: translateY(-50%) translateX(-4px);
	border: 1px solid var(--sb-line-2);
	border-radius: 7px;
	background: #fff;
	box-shadow: 0 8px 24px rgba(23, 26, 31, 0.12);
	color: var(--sb-text);
	font-size: 11.5px;
	font-weight: 550;
	white-space: nowrap;
	opacity: 0;
	pointer-events: none;
	transition:
		opacity 0.14s,
		transform 0.18s var(--sb-ease-out);
}

.rail-new,
.rail-btn {
	position: relative;
}

.rail-new:hover .tip,
.rail-btn:hover .tip {
	opacity: 1;
	transform: translateY(-50%) translateX(0);
}

.tip .k {
	color: var(--sb-faint);
	font-family: var(--sb-mono);
	font-size: 10px;
}

/* ---- 上：功能按钮区 ---- */
.nav-top {
	flex: 0 0 auto;
	padding: 14px 12px 6px;
}

.btn-primary {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 7px;
	width: 100%;
	height: 38px;
	border: 1px solid var(--sb-accent);
	border-radius: 10px;
	background: var(--sb-accent);
	color: #fff;
	font-size: 13px;
	font-weight: 640;
	letter-spacing: 0.02em;
	transition:
		transform 0.12s var(--sb-ease-spring),
		box-shadow 0.16s,
		background 0.16s;
}

.btn-primary:hover {
	background: var(--sb-accent-2);
	transform: translateY(-1px);
	box-shadow: 0 10px 24px rgba(59, 91, 253, 0.28);
}

.btn-primary:active {
	transform: translateY(0);
}

.tool-list {
	display: flex;
	flex-direction: column;
	gap: 1px;
	margin-top: 10px;
}

.tool-btn {
	display: flex;
	align-items: center;
	gap: 10px;
	width: 100%;
	height: 33px;
	padding: 0 9px;
	border: 1px solid transparent;
	border-radius: 8px;
	background: transparent;
	color: var(--sb-mute);
	font-size: 12.5px;
	font-weight: 540;
	text-align: left;
	transition:
		background 0.14s,
		color 0.14s,
		transform 0.14s var(--sb-ease-out);
}

.tool-btn:hover {
	background: var(--sb-hover);
	color: var(--sb-text);
	transform: translateX(2px);
}

.tool-btn .ico {
	display: grid;
	width: 16px;
	height: 16px;
	place-items: center;
	flex: none;
}

.tool-btn .label {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.kbd {
	color: var(--sb-faint);
	font-family: var(--sb-mono);
	font-size: 9.5px;
	letter-spacing: 0.04em;
}

/* ---- 中：分组与节点 ---- */
.nav-mid {
	flex: 1;
	min-height: 0;
	overflow-y: auto;
	padding: 6px 12px 14px;
}

.nav-mid::-webkit-scrollbar {
	width: 9px;
}

.nav-mid::-webkit-scrollbar-thumb {
	background: var(--sb-raise);
	background-clip: padding-box;
	border: 2px solid transparent;
	border-radius: 99px;
}

.group {
	margin-top: 12px;
}

.group-head {
	display: flex;
	align-items: center;
	gap: 7px;
	width: 100%;
	height: 28px;
	padding: 0 7px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--sb-mute);
	transition:
		background 0.14s,
		color 0.14s;
}

.group-head:hover {
	background: var(--sb-hover);
	color: var(--sb-soft);
}

.group-chevron {
	display: grid;
	width: 13px;
	height: 13px;
	place-items: center;
	flex: none;
	color: var(--sb-faint);
	transition: transform 0.2s var(--sb-ease-out);
}

.group.open .group-chevron {
	transform: rotate(90deg);
}

.group-title {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	font-size: 11px;
	font-weight: 650;
	letter-spacing: 0.05em;
	text-align: left;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.group-count {
	color: var(--sb-faint);
	font-family: var(--sb-mono);
	font-size: 9.5px;
	font-weight: 500;
	letter-spacing: 0.08em;
}

.group-add {
	display: grid;
	width: 20px;
	height: 20px;
	place-items: center;
	border-radius: 6px;
	color: var(--sb-faint);
	opacity: 0;
	transition:
		opacity 0.14s,
		background 0.14s,
		color 0.14s;
}

.group-head:hover .group-add {
	opacity: 1;
}

.group-add:hover {
	background: var(--sb-accent-soft);
	color: var(--sb-accent);
}

.group-body {
	display: none;
	padding: 3px 0 0 13px;
}

.group.open .group-body {
	display: block;
}

/* 项目目录节点 */
.subfolder {
	margin-top: 2px;
}

.project-folder {
	display: flex;
	align-items: center;
	gap: 8px;
	width: 100%;
	height: 32px;
	padding: 0 8px;
	border: 1px solid transparent;
	border-radius: 8px;
	background: transparent;
	color: var(--sb-soft);
	font-size: 12.5px;
	font-weight: 560;
	text-align: left;
	transition:
		background 0.14s,
		border-color 0.14s,
		color 0.14s;
}

.project-folder:hover,
.project-folder.menu-open {
	background: var(--sb-hover);
	color: var(--sb-text);
}

.folder-ico {
	display: grid;
	width: 16px;
	height: 16px;
	place-items: center;
	flex: none;
	color: var(--sb-faint);
}

.folder-name {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.folder-chevron {
	display: grid;
	width: 13px;
	height: 13px;
	place-items: center;
	flex: none;
	color: var(--sb-faint);
	transition: transform 0.2s var(--sb-ease-out);
}

.subfolder.open .folder-chevron {
	transform: rotate(90deg);
}

.subfolder-body {
	display: none;
	padding: 1px 0 2px 15px;
}

.subfolder.open .subfolder-body {
	display: block;
}

/* 会话节点 */
.node {
	position: relative;
	display: flex;
	align-items: center;
	gap: 8px;
	width: 100%;
	height: 31px;
	padding: 0 8px;
	border: 1px solid transparent;
	border-radius: 8px;
	background: transparent;
	color: var(--sb-mute);
	font-size: 12.5px;
	font-weight: 500;
	text-align: left;
	transition:
		background 0.14s,
		border-color 0.14s,
		color 0.14s;
}

.node:hover,
.node.menu-open {
	background: var(--sb-hover);
	color: var(--sb-text);
}

.node .dot {
	width: 5px;
	height: 5px;
	flex: none;
	border-radius: 50%;
	background: var(--sb-faint);
	transition:
		background 0.15s,
		box-shadow 0.15s;
}

.node.active {
	border-color: var(--sb-line);
	background: #fff;
	color: var(--sb-text);
	box-shadow: 0 1px 4px rgba(23, 26, 31, 0.05);
}

.node.active::before {
	position: absolute;
	top: 50%;
	left: -1px;
	width: 2px;
	height: 16px;
	transform: translateY(-50%);
	border-radius: 2px;
	background: var(--sb-accent);
	box-shadow: 0 0 8px rgba(59, 91, 253, 0.45);
	content: '';
}

.node.active .dot {
	background: var(--sb-accent);
	box-shadow: 0 0 6px rgba(59, 91, 253, 0.5);
}

.ntext {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.ntime {
	flex: none;
	color: var(--sb-faint);
	font-family: var(--sb-mono);
	font-size: 9.5px;
	letter-spacing: 0.02em;
}

.empty-group {
	padding: 8px 8px 4px;
	color: var(--sb-faint);
	font-size: 11.5px;
}

/* ---- 下：设置 ---- */
.nav-bot {
	flex: 0 0 auto;
	padding: 10px 12px 14px;
	border-top: 1px solid var(--sb-line);
}

.settings-btn {
	display: flex;
	align-items: center;
	gap: 10px;
	width: 100%;
	height: 36px;
	padding: 0 9px;
	border: 1px solid transparent;
	border-radius: 9px;
	background: transparent;
	color: var(--sb-mute);
	font-size: 12.5px;
	font-weight: 560;
	transition:
		background 0.14s,
		border-color 0.14s,
		color 0.14s;
}

.settings-btn:hover {
	background: var(--sb-hover);
	color: var(--sb-text);
}

.settings-btn.active {
	border-color: var(--sb-line);
	background: #fff;
	color: var(--sb-accent);
	box-shadow: 0 1px 4px rgba(23, 26, 31, 0.05);
}

.settings-btn .ico {
	display: grid;
	width: 16px;
	height: 16px;
	place-items: center;
	flex: none;
}

.settings-btn .label {
	flex: 1;
	text-align: left;
}

.settings-btn .chev {
	color: var(--sb-faint);
	transition: transform 0.18s var(--sb-ease-out);
}

.settings-btn:hover .chev {
	transform: translateX(2px);
}

/* ---- 右键菜单 ---- */
.context-menu {
	position: fixed;
	z-index: 300;
	min-width: 208px;
	padding: 5px;
	border: 1px solid var(--sb-line-2);
	border-radius: 11px;
	background: #fff;
	box-shadow:
		0 2px 6px rgba(23, 26, 31, 0.06),
		0 18px 44px rgba(23, 26, 31, 0.14);
	animation: menu-pop 0.18s var(--sb-ease-out);
}

@keyframes menu-pop {
	from {
		opacity: 0;
		transform: translateY(4px) scale(0.98);
	}

	to {
		opacity: 1;
		transform: none;
	}
}

.context-menu-item {
	display: flex;
	align-items: center;
	gap: 9px;
	width: 100%;
	height: 32px;
	padding: 0 9px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--sb-soft);
	font-size: 12.5px;
	font-weight: 530;
	text-align: left;
	transition:
		background 0.12s,
		color 0.12s;
}

.context-menu-item:hover {
	background: var(--sb-hover);
	color: var(--sb-text);
}

.context-menu-item.danger {
	color: #d04545;
}

.context-menu-item.danger:hover {
	background: rgba(220, 69, 69, 0.08);
	color: #c23333;
}

.context-menu-icon {
	display: grid;
	width: 15px;
	height: 15px;
	place-items: center;
	flex: none;
}

.context-menu-divider {
	height: 1px;
	margin: 5px 8px;
	background: var(--sb-line);
}

@media (prefers-reduced-motion: reduce) {
	.sidebar *,
	.sidebar *::before,
	.sidebar *::after {
		animation-duration: 0.001ms !important;
		transition-duration: 0.001ms !important;
	}
}
</style>
