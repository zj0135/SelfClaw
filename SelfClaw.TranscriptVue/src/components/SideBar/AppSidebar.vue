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
	File,
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
	ChevronLeft,
	RotateCw,
} from 'lucide-vue-next';
import { useWorkspaceTree } from '../../composables/useWorkspaceTree.js';

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
	hideRailTip();
	emit('toggle-collapse');
}

// 折叠态悬浮提示：fixed 定位 + 视口坐标，逃逸 rail-scroll 的 overflow 裁剪
const railTip = ref({
	open: false,
	label: '',
	kbd: '',
	top: 0,
	left: 0,
});

function showRailTip(event, label, kbd) {
	const rect = event.currentTarget.getBoundingClientRect();
	railTip.value = {
		open: true,
		label,
		kbd: kbd || '',
		top: rect.top + rect.height / 2,
		left: rect.right + 10,
	};
}

function hideRailTip() {
	railTip.value = { ...railTip.value, open: false };
}

// 折叠态：图标轨对应的可操作项（去掉会话记录，仅保留功能图标）
const railItems = computed(() => props.items.filter((i) => i.type === 'action' && i.id !== 'new-chat'));

// 中区有两种形态：会话列表（默认）与某个会话的工作目录树。
// 解构成顶层绑定，模板里才会自动解包 ref。
const {
	root: treeRoot,
	rows: treeRows,
	isOpen: treeOpen,
	rootLoading: treeLoading,
	rootError: treeError,
	rootLoaded: treeLoaded,
	open: openTree,
	close: closeTree,
	toggle: toggleTreeNode,
	reload: reloadTree,
} = useWorkspaceTree();

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

	const target = contextMenu.value.target;
	return sidebarMenuItems.filter((item) => {
		if (item.type === 'divider') {
			return true;
		}

		// 清空会话列表只对项目分组有意义。
		if (item.id === 'clear-conversations') {
			return target.kind === 'folder';
		}

		// 没有工作区根的会话（「对话」分组）没有工作目录可看。
		if (item.id === 'working-directory') {
			return Boolean(target.node?.workspaceRootId);
		}

		return true;
	});
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
	// 新建对话后会话列表才是该看的东西，否则新会话被目录树挡住、看不见。
	if (actionId === 'new-chat') {
		closeTree();
	}

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
				isManagedWorktree: Boolean(target.node.isManagedWorktree),
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
	} else if (item.id === 'working-directory') {
		openWorkingDirectory(target.node);
	}

	closeContextMenu();
}

// 会话节点带的是工作区根的名字与路径（不是会话标题）；项目分组节点的 label 可能是
// Git 仓库名，故同样优先用 workspaceRootName。
function openWorkingDirectory(node) {
	if (!node?.workspaceRootId) {
		return;
	}

	openTree({
		workspaceRootId: node.workspaceRootId,
		name: node.workspaceRootName || node.label || '工作目录',
		path: node.workspaceRootPath || '',
	});
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

// 目录树的悬浮提示：相对路径 + 文件大小，窄栏里名字被截断时仍能看全。
function describeTreeRow(row) {
	if (row.isDirectory || row.sizeBytes === null || row.sizeBytes === undefined) {
		return row.relativePath;
	}

	return `${row.relativePath} · ${formatBytes(row.sizeBytes)}`;
}

function formatBytes(value) {
	const bytes = Number(value);
	if (!Number.isFinite(bytes) || bytes < 0) {
		return '';
	}

	if (bytes < 1024) {
		return `${bytes} B`;
	}

	const units = ['KB', 'MB', 'GB', 'TB'];
	let size = bytes / 1024;
	let unitIndex = 0;
	while (size >= 1024 && unitIndex < units.length - 1) {
		size /= 1024;
		unitIndex += 1;
	}

	return `${size < 10 ? size.toFixed(1) : Math.round(size)} ${units[unitIndex]}`;
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
	window.addEventListener('blur', hideRailTip);
	window.addEventListener('resize', hideRailTip);
});

onUnmounted(() => {
	document.removeEventListener('click', onDocumentClick);
	document.removeEventListener('keydown', onDocumentKeydown);
	window.removeEventListener('blur', closeContextMenu);
	window.removeEventListener('resize', closeContextMenu);
	window.removeEventListener('blur', hideRailTip);
	window.removeEventListener('resize', hideRailTip);
});
</script>

<template>
	<aside class="sidebar" :class="{ collapsed }" aria-label="主导航">
		<!-- 顶栏：新建对话 + 折叠 -->
		<div class="head">
			<button class="head-new" type="button" @click="onAction('new-chat')">
				<span class="ico" aria-hidden="true">
					<Plus :size="14" :stroke-width="2.2" />
				</span>
				<span>新建对话</span>
			</button>
			<button class="head-collapse" type="button" :title="collapsed ? '展开侧栏' : '折叠侧栏'"
				:aria-label="collapsed ? '展开侧栏' : '折叠侧栏'" @click="toggleCollapse">
				<PanelLeftOpen v-if="collapsed" :size="16" :stroke-width="1.8" />
				<PanelLeftClose v-else :size="16" :stroke-width="1.8" />
			</button>
		</div>

		<!-- ============ 折叠态：图标轨 ============ -->
		<nav class="rail" aria-label="折叠导航">
			<div class="rail-scroll" @scroll.passive="hideRailTip">
				<button class="rail-new" type="button" :aria-label="'新建对话'" @mouseenter="showRailTip($event, '新建对话')"
					@mouseleave="hideRailTip" @click="onAction('new-chat')">
					<Plus :size="17" :stroke-width="2.2" aria-hidden="true" />
				</button>

				<span class="rail-sep" aria-hidden="true"></span>

				<button v-for="item in railItems" :key="item.id" class="rail-btn"
					:class="{ active: activeId === item.id }" type="button" :aria-label="item.label"
					@mouseenter="showRailTip($event, item.label, kbdMap[item.id])" @mouseleave="hideRailTip"
					@click="onAction(item.id)">
					<span class="ico" aria-hidden="true">
						<component :is="getIcon(item.id)" :size="16" :stroke-width="1.8" />
					</span>
				</button>
			</div>

			<div class="rail-bot">
				<button class="rail-btn" :class="{ active: settingsActive }" type="button" :aria-label="'系统设置'"
					@mouseenter="showRailTip($event, '系统设置')" @mouseleave="hideRailTip" @click="selectSettings">
					<span class="ico" aria-hidden="true">
						<Settings :size="16" :stroke-width="1.8" />
					</span>
				</button>
			</div>
		</nav>

		<!-- 折叠态悬浮提示（fixed，脱离滚动容器裁剪） -->
		<transition name="rail-tip">
			<div v-if="railTip.open" class="rail-tip" :style="{ top: `${railTip.top}px`, left: `${railTip.left}px` }"
				aria-hidden="true">
				{{ railTip.label }}<span v-if="railTip.kbd" class="k">{{ railTip.kbd }}</span>
			</div>
		</transition>

		<!-- 上：功能按钮区 -->
		<div class="nav-top">
			<div class="tool-list">
				<button v-for="item in actionItems.filter((a) => a.id !== 'new-chat')" :key="item.id" class="tool-btn"
					type="button" @click="onAction(item.id)">
					<span class="ico" aria-hidden="true">
						<component :is="getIcon(item.id)" :size="15" :stroke-width="1.8" />
					</span>
					<span class="label">{{ item.label }}</span>
					<span v-if="kbdMap[item.id]" class="kbd">{{ kbdMap[item.id] }}</span>
				</button>
			</div>
		</div>

		<!-- 中：工作目录树（右键菜单进入）或项目/对话节点 -->
		<div class="nav-mid">
			<template v-if="treeOpen">
				<div class="dir-head">
					<button class="dir-back" type="button" title="返回会话列表" aria-label="返回会话列表"
						@click="closeTree">
						<ChevronLeft :size="15" :stroke-width="2" />
					</button>
					<span class="dir-title" :title="treeRoot?.path || treeRoot?.name">
						{{ treeRoot?.path || treeRoot?.name }}
					</span>
					<button class="dir-refresh" type="button" title="刷新" aria-label="刷新目录"
						:disabled="treeLoading" @click="reloadTree">
						<RotateCw :size="13" :stroke-width="2" :class="{ spinning: treeLoading }" />
					</button>
				</div>

				<div v-if="treeError" class="dir-error">{{ treeError }}</div>
				<div v-else-if="treeLoading && !treeLoaded" class="empty-group">正在读取目录…</div>
				<div v-else-if="!treeRows.length" class="empty-group">该目录为空</div>

				<div v-else class="dir-tree" role="tree">
					<template v-for="row in treeRows" :key="row.key">
						<div v-if="row.kind === 'at-limit'" class="dir-more"
							:style="{ '--depth': row.depth }">
							每层最多显示 {{ row.limit }} 项
						</div>
						<button v-else-if="row.isDirectory" class="dir-row is-dir" type="button" role="treeitem"
							:aria-expanded="row.expanded" :style="{ '--depth': row.depth }"
							:title="describeTreeRow(row)" @click="toggleTreeNode(row.relativePath)">
							<span class="dir-chevron" :class="{ open: row.expanded }" aria-hidden="true">
								<RotateCw v-if="row.loading" :size="11" :stroke-width="2" class="spinning" />
								<ChevronRight v-else :size="12" :stroke-width="2" />
							</span>
							<span class="dir-ico" aria-hidden="true">
								<FolderOpen v-if="row.expanded" :size="13" :stroke-width="1.8" />
								<Folder v-else :size="13" :stroke-width="1.8" />
							</span>
							<span class="dir-name">{{ row.name }}</span>
						</button>
						<div v-else class="dir-row is-file" role="treeitem" :style="{ '--depth': row.depth }"
							:title="describeTreeRow(row)">
							<span class="dir-chevron" aria-hidden="true"></span>
							<span class="dir-ico" aria-hidden="true">
								<File :size="13" :stroke-width="1.8" />
							</span>
							<span class="dir-name">{{ row.name }}</span>
						</div>
						<div v-if="row.error" class="dir-error nested" :style="{ '--depth': row.depth + 1 }">
							{{ row.error }}
						</div>
					</template>
				</div>
			</template>

			<template v-else>
				<section v-for="(group, gi) in groupItems" :key="group.id" class="group"
					:class="{ open: isGroupOpen(group.id) }">
					<button class="group-head" type="button" @click="toggleGroup(group.id)">
						<span class="group-chevron" aria-hidden="true">
							<ChevronRight :size="13" :stroke-width="2" />
						</span>
						<span class="group-title">{{ group.label }}</span>
						<span class="group-count">{{ String(group.children?.length || 0).padStart(2, '0') }}</span>
						<span class="group-add" role="button" :title="`新建${group.label}`"
							@click.stop="onGroupAdd(group.id)">
							<Plus :size="13" :stroke-width="2.2" />
						</span>
					</button>

					<div class="group-body">
						<template v-if="group.id === 'projects'">
							<!-- 项目节点：三级结构 项目→目录→会话 -->
							<div v-for="folder in group.children" :key="folder.id" class="subfolder"
								:class="{ open: isFolderOpen(folder.id) || folderHasActiveChild(folder) }">
								<button class="project-folder" :class="{ 'menu-open': isContextTarget(folder.id) }"
									type="button" @click="toggleFolder(folder.id)"
									@contextmenu.prevent.stop="openFolderMenu($event, folder)">
									<span class="folder-ico" aria-hidden="true">
										<Folder :size="14" :stroke-width="1.8" />
									</span>
									<span class="folder-name">{{ folder.label }}</span>
									<span class="folder-chevron" aria-hidden="true">
										<ChevronRight :size="13" :stroke-width="2" />
									</span>
								</button>
								<div class="subfolder-body">
									<button v-for="(session, si) in folder.children" :key="session.id"
										class="node kind-chat sc-rise" :style="{ '--i': si }"
										:class="{ active: isNodeActive(session.id), 'menu-open': isContextTarget(session.id) }"
										type="button" @click="selectNode(session.id)"
										@contextmenu.prevent.stop="openConversationMenu($event, session)">
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
							<button v-for="(child, ci) in group.children" :key="child.id"
								class="node kind-chat sc-rise" :style="{ '--i': ci }"
								:class="{ active: isNodeActive(child.id), 'menu-open': isContextTarget(child.id) }"
								type="button" @click="selectNode(child.id)"
								@contextmenu.prevent.stop="openConversationMenu($event, child)">
								<span class="dot" aria-hidden="true"></span>
								<span class="ntext">{{ child.label }}</span>
								<span v-if="child.time" class="ntime">{{ child.time }}</span>
							</button>
							<div v-if="!group.children?.length" class="empty-group">暂无会话记录</div>
						</template>
					</div>
				</section>
			</template>
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

		<div v-if="contextMenu.open" class="context-menu" role="menu"
			:style="{ left: `${contextMenu.x}px`, top: `${contextMenu.y}px` }" @click.stop @contextmenu.prevent>
			<template v-for="item in contextMenuItems" :key="item.id">
				<div v-if="item.type === 'divider'" class="context-menu-divider" role="separator"></div>
				<button v-else class="context-menu-item" :class="{ danger: item.danger }" type="button" role="menuitem"
					@click="onContextMenuItem(item)">
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
/* --sb-* token 定义已移入 styles/tokens.css 的别名层：scoped 选择器会带上
   [data-v-x]，特异度高于全局 .sidebar，留在这里会把主题值压掉。 */
.sidebar {
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

/* ---- 顶栏：新建对话 + 折叠 ---- */
.head {
	flex: 0 0 auto;
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 16px 12px 0;
}

.head-new {
	display: flex;
	align-items: center;
	justify-content: center;
	gap: 7px;
	flex: 1;
	min-width: 0;
	height: 34px;
	border: 1px solid var(--sb-line);
	border-radius: 9px;
	background: var(--panel);
	color: var(--sb-text);
	font-size: var(--fs-125);
	font-weight: 620;
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.04);
	transition:
		border-color 0.14s,
		box-shadow 0.16s,
		transform 0.12s var(--sb-ease-spring);
}

.head-new:hover {
	border-color: var(--sb-line-2);
	box-shadow: 0 4px 12px rgba(var(--shadow-ink), 0.07);
	transform: translateY(-1px);
}

.head-new:active {
	transform: translateY(0);
}

.head-new .ico {
	display: grid;
	width: 16px;
	height: 16px;
	place-items: center;
	flex: none;
	color: var(--sb-mute);
	transition: color 0.14s;
}

.head-new:hover .ico {
	color: var(--sb-accent);
}

.head-collapse {
	display: grid;
	width: 32px;
	height: 32px;
	flex: 0 0 auto;
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

.head-collapse:hover {
	border-color: var(--sb-line);
	background: var(--sb-hover);
	color: var(--sb-text);
}

.sidebar.collapsed .head {
	justify-content: center;
	padding: 14px 0 10px;
}

.sidebar.collapsed .head-new {
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
	overflow-x: hidden;
}

.rail-new {
	display: grid;
	width: 36px;
	height: 36px;
	place-items: center;
	border: 1px solid var(--sb-line);
	border-radius: 10px;
	background: var(--panel);
	color: var(--sb-soft);
	transition:
		background 0.15s,
		border-color 0.15s,
		color 0.15s;
}

.rail-new:hover {
	border-color: var(--sb-line-2);
	background: var(--sb-hover);
	color: var(--sb-accent);
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

/* 折叠态悬浮提示：fixed 定位，不受 rail-scroll 的 overflow 裁剪 */
.rail-tip {
	position: fixed;
	z-index: 400;
	display: inline-flex;
	align-items: center;
	gap: 7px;
	padding: 5px 10px;
	transform: translateY(-50%);
	border: 1px solid var(--sb-line-2);
	border-radius: 7px;
	background: var(--panel);
	box-shadow: 0 8px 24px rgba(var(--shadow-ink), 0.12);
	color: var(--sb-text);
	font-size: var(--fs-115);
	font-weight: 550;
	white-space: nowrap;
	pointer-events: none;
}

.rail-tip .k {
	color: var(--sb-faint);
	font-family: var(--sb-mono);
	font-size: var(--fs-10);
}

.rail-tip-enter-active,
.rail-tip-leave-active {
	transition:
		opacity 0.14s,
		transform 0.18s var(--sb-ease-out);
}

.rail-tip-enter-from,
.rail-tip-leave-to {
	opacity: 0;
	transform: translateY(-50%) translateX(-4px);
}

/* ---- 上：功能按钮区 ---- */
.nav-top {
	flex: 0 0 auto;
	padding: 10px 12px 6px;
}

.tool-list {
	display: flex;
	flex-direction: column;
	gap: 1px;
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
	font-size: var(--fs-125);
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
	font-size: var(--fs-95);
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
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.05em;
	text-align: left;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.group-count {
	color: var(--sb-faint);
	font-family: var(--sb-mono);
	font-size: var(--fs-95);
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
	font-size: var(--fs-125);
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
	font-size: var(--fs-125);
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
	background: var(--panel);
	color: var(--sb-text);
	box-shadow: 0 1px 4px rgba(var(--shadow-ink), 0.05);
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
	box-shadow: 0 0 8px color-mix(in srgb, var(--accent) 45%, transparent);
	content: '';
}

.node.active .dot {
	background: var(--sb-accent);
	box-shadow: 0 0 6px color-mix(in srgb, var(--accent) 50%, transparent);
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
	font-size: var(--fs-95);
	letter-spacing: 0.02em;
}

.empty-group {
	padding: 8px 8px 4px;
	color: var(--sb-faint);
	font-size: var(--fs-115);
}

/* ---- 中区：工作目录树 ---- */
.dir-head {
	display: flex;
	align-items: center;
	gap: 4px;
	margin: 6px 0;
	padding: 0 2px 0 0;
}

.dir-back,
.dir-refresh {
	display: grid;
	width: 24px;
	height: 24px;
	place-items: center;
	flex: none;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--sb-mute);
	transition:
		background 0.14s,
		color 0.14s;
}

.dir-back:hover,
.dir-refresh:hover:not(:disabled) {
	background: var(--sb-hover);
	color: var(--sb-text);
}

.dir-refresh:disabled {
	color: var(--sb-faint);
}

.dir-title {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	color: var(--sb-text);
	font-size: var(--fs-125);
	font-weight: 620;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.dir-tree {
	padding-bottom: 4px;
}

.dir-row {
	display: flex;
	align-items: center;
	gap: 5px;
	width: 100%;
	height: 27px;
	padding: 0 6px 0 calc(4px + var(--depth, 0) * 12px);
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--sb-mute);
	font-size: var(--fs-12);
	font-weight: 500;
	text-align: left;
}

.dir-row.is-dir {
	color: var(--sb-soft);
	transition:
		background 0.12s,
		color 0.12s;
}

.dir-row.is-dir:hover {
	background: var(--sb-hover);
	color: var(--sb-text);
}

.dir-chevron {
	display: grid;
	width: 12px;
	height: 12px;
	place-items: center;
	flex: none;
	color: var(--sb-faint);
	transition: transform 0.18s var(--sb-ease-out);
}

.dir-chevron.open {
	transform: rotate(90deg);
}

.dir-ico {
	display: grid;
	width: 14px;
	height: 14px;
	place-items: center;
	flex: none;
	color: var(--sb-faint);
}

.dir-row.is-dir .dir-ico {
	color: var(--sb-mute);
}

.dir-name {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.dir-error {
	padding: 7px 8px;
	border-radius: 7px;
	background: rgba(220, 69, 69, 0.07);
	color: #c23333;
	font-size: var(--fs-115);
	overflow-wrap: anywhere;
}

.dir-error.nested {
	margin-left: calc(4px + var(--depth, 0) * 12px);
}

.dir-more {
	padding: 4px 6px 6px calc(21px + var(--depth, 0) * 12px);
	color: var(--sb-faint);
	font-size: var(--fs-11);
}

.spinning {
	animation: dir-spin 0.9s linear infinite;
}

@keyframes dir-spin {
	to {
		transform: rotate(360deg);
	}
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
	font-size: var(--fs-125);
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
	background: var(--panel);
	color: var(--sb-accent);
	box-shadow: 0 1px 4px rgba(var(--shadow-ink), 0.05);
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
	background: var(--panel);
	box-shadow:
		0 2px 6px rgba(var(--shadow-ink), 0.06),
		0 18px 44px rgba(var(--shadow-ink), 0.14);
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
	font-size: var(--fs-125);
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
	color: var(--danger);
}

.context-menu-item.danger:hover {
	background: color-mix(in srgb, var(--danger) 8%, transparent);
	color: var(--err-text);
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
