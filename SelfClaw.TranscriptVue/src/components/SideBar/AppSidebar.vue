<script setup>
import { ref, computed, onMounted, onUnmounted } from 'vue';

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

const iconMap = {
	'new-chat': `<svg viewBox="0 0 20 20" fill="none"><path d="M10 4.5v11M4.5 10h11" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/></svg>`,
	search: `<svg viewBox="0 0 20 20" fill="none"><circle cx="9" cy="9" r="5.5" stroke="currentColor" stroke-width="1.7"/><path d="M14 14l3.2 3.2" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/></svg>`,
	plugins: `<svg viewBox="0 0 20 20" fill="none"><rect x="3.5" y="3.5" width="5" height="5" rx="1.2" stroke="currentColor" stroke-width="1.6"/><rect x="11.5" y="3.5" width="5" height="5" rx="1.2" stroke="currentColor" stroke-width="1.6"/><rect x="3.5" y="11.5" width="5" height="5" rx="1.2" stroke="currentColor" stroke-width="1.6"/><path d="M14 11.5v5M11.5 14h5" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>`,
	extensions: `<svg viewBox="0 0 20 20" fill="none"><path d="M10 3.2v3.4M10 13.4v3.4M3.2 10h3.4M13.4 10h3.4M5.4 5.4l2.4 2.4M12.2 12.2l2.4 2.4M14.6 5.4l-2.4 2.4M7.8 12.2l-2.4 2.4" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>`,
	automation: `<svg viewBox="0 0 20 20" fill="none"><circle cx="6" cy="10" r="2.6" stroke="currentColor" stroke-width="1.6"/><circle cx="14" cy="10" r="2.6" stroke="currentColor" stroke-width="1.6"/><path d="M8.6 10h2.8" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/></svg>`,
};

const contextIconMap = {
	folder: `<svg viewBox="0 0 20 20" fill="none"><path d="M3 6.2A1.7 1.7 0 0 1 4.7 4.5h3.1l1.6 1.7h5.9A1.7 1.7 0 0 1 17 7.9v5.9a1.7 1.7 0 0 1-1.7 1.7H4.7A1.7 1.7 0 0 1 3 13.8V6.2Z" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"/></svg>`,
	rename: `<svg viewBox="0 0 20 20" fill="none"><path d="M4.2 13.8 13.6 4.4a1.8 1.8 0 0 1 2.6 2.6l-9.4 9.4-3.4.8.8-3.4Z" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"/><path d="m12.5 5.5 2 2" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"/></svg>`,
	workingDirectory: `<svg viewBox="0 0 20 20" fill="none"><path d="M3.5 6.5h5l1.5 2h6.5v6A1.5 1.5 0 0 1 15 16H5a1.5 1.5 0 0 1-1.5-1.5v-8Z" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"/><path d="M3.5 8.5V5A1.5 1.5 0 0 1 5 3.5h3l1.5 2H15A1.5 1.5 0 0 1 16.5 7v1.5" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"/></svg>`,
	book: `<svg viewBox="0 0 20 20" fill="none"><path d="M4 4.8A1.8 1.8 0 0 1 5.8 3h10.7v12.2H5.8A1.8 1.8 0 0 0 4 17V4.8Z" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"/><path d="M7.5 6.5h6" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"/></svg>`,
	message: `<svg viewBox="0 0 20 20" fill="none"><path d="M4 5.5A2.5 2.5 0 0 1 6.5 3h7A2.5 2.5 0 0 1 16 5.5v4.8a2.5 2.5 0 0 1-2.5 2.5H9l-4.1 3.1v-3.3A2.5 2.5 0 0 1 4 10.3V5.5Z" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"/></svg>`,
	git: `<svg viewBox="0 0 20 20" fill="none"><path d="M7 4v7.2a2.8 2.8 0 1 0 2.8 2.8V6.8" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"/><path d="M9.8 6.8h2.5A2.7 2.7 0 0 1 15 9.5V11" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"/><circle cx="7" cy="4" r="1.5" stroke="currentColor" stroke-width="1.45"/><circle cx="15" cy="12.5" r="1.5" stroke="currentColor" stroke-width="1.45"/></svg>`,
	export: `<svg viewBox="0 0 20 20" fill="none"><path d="M10 3.5v8" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"/><path d="m6.8 8.7 3.2 3.2 3.2-3.2" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"/><path d="M4 13.8v1.7A1.5 1.5 0 0 0 5.5 17h9a1.5 1.5 0 0 0 1.5-1.5v-1.7" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"/></svg>`,
	clear: `<svg viewBox="0 0 20 20" fill="none"><path d="m10 3 6.5 5.4-6.5 5.4-6.5-5.4L10 3Z" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"/><path d="M6.8 11.2 10 14l3.2-2.8" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"/></svg>`,
	pin: `<svg viewBox="0 0 20 20" fill="none"><path d="m12.8 3.5 3.7 3.7-2.2 2.2.5 3.5-1 1-3.2-3.2-4.7 4.7-.8-.8 4.7-4.7-3.2-3.2 1-1 3.5.5 1.7-2.7Z" stroke="currentColor" stroke-width="1.35" stroke-linejoin="round"/></svg>`,
	trash: `<svg viewBox="0 0 20 20" fill="none"><path d="M4.5 6h11" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"/><path d="M8 6V4.5A1.5 1.5 0 0 1 9.5 3h1A1.5 1.5 0 0 1 12 4.5V6" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"/><path d="M6.2 6.5 7 16a1.5 1.5 0 0 0 1.5 1.3h3A1.5 1.5 0 0 0 13 16l.8-9.5" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"/><path d="M9 9.2v4.8M11 9.2v4.8" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"/></svg>`,
};

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

function getIcon(id) {
	return iconMap[id] || '';
}

function getContextIcon(id) {
	return contextIconMap[id] || '';
}

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
				<svg viewBox="0 0 20 20" fill="none">
					<path d="M10 2.5 16.5 6v8L10 17.5 3.5 14V6L10 2.5Z" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round" />
					<path d="M7 9.2 9.3 11.5 13.2 7.4" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" />
				</svg>
			</span>
			<span class="brand-name">SelfClaw</span>
			<span class="brand-spacer"></span>
			<button
				class="brand-collapse"
				type="button"
				:title="collapsed ? '展开侧栏' : '折叠侧栏'"
				:aria-label="collapsed ? '展开侧栏' : '折叠侧栏'"
				@click="toggleCollapse"
			>
				<svg viewBox="0 0 20 20" fill="none">
					<rect x="3" y="4" width="14" height="12" rx="2.4" stroke="currentColor" stroke-width="1.5" />
					<path d="M12.5 4.6v10.8" stroke="currentColor" stroke-width="1.5" />
				</svg>
			</button>
		</div>

		<!-- ============ 折叠态：图标轨 ============ -->
		<nav class="rail" aria-label="折叠导航">
			<div class="rail-scroll">
				<button class="rail-new" type="button" title="新建对话" @click="onAction('new-chat')">
					<svg viewBox="0 0 20 20" fill="none"><path d="M10 4.5v11M4.5 10h11" stroke="currentColor" stroke-width="2" stroke-linecap="round" /></svg>
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
					<span class="ico" aria-hidden="true" v-html="getIcon(item.id)"></span>
					<span class="tip">{{ item.label }}<span v-if="kbdMap[item.id]" class="k">{{ kbdMap[item.id] }}</span></span>
				</button>
			</div>

			<div class="rail-bot">
				<button class="rail-btn" :class="{ active: settingsActive }" type="button" title="系统设置" @click="selectSettings">
					<span class="ico" aria-hidden="true">
						<svg viewBox="0 0 20 20" fill="none">
							<circle cx="10" cy="10" r="2.6" stroke="currentColor" stroke-width="1.6" />
							<path d="M10 2.8v2M10 15.2v2M17.2 10h-2M4.8 10h-2M14.9 5.1l-1.4 1.4M6.5 13.5l-1.4 1.4M14.9 14.9l-1.4-1.4M6.5 6.5 5.1 5.1" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" />
						</svg>
					</span>
					<span class="tip">系统设置</span>
				</button>
			</div>
		</nav>

		<!-- 上：功能按钮区 -->
		<div class="nav-top">
			<button class="btn-primary" type="button" @click="onAction('new-chat')">
				<svg viewBox="0 0 20 20" fill="none" aria-hidden="true">
					<path d="M10 4.5v11M4.5 10h11" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" />
				</svg>
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
					<span class="ico" aria-hidden="true" v-html="getIcon(item.id)"></span>
					<span class="label">{{ item.label }}</span>
					<span v-if="kbdMap[item.id]" class="kbd">{{ kbdMap[item.id] }}</span>
				</button>
			</div>
		</div>

		<!-- 中：项目节点 + 对话节点 -->
		<div class="nav-mid">
			<section v-for="group in groupItems" :key="group.id" class="group" :class="{ open: isGroupOpen(group.id) }">
				<button class="group-head" type="button" @click="toggleGroup(group.id)">
					<span class="group-chevron" aria-hidden="true">
						<svg viewBox="0 0 16 16" fill="none">
							<path d="M6 4l4 4-4 4" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" />
						</svg>
					</span>
					<span class="group-title">{{ group.label }}</span>
					<span class="group-count">{{ group.children?.length || 0 }}</span>
					<span class="group-add" role="button" :title="`新建${group.label}`" @click.stop="onGroupAdd(group.id)">
						<svg viewBox="0 0 16 16" fill="none">
							<path d="M8 4v8M4 8h8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" />
						</svg>
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
									<svg viewBox="0 0 16 16" fill="none">
										<path
											d="M2 4.5A1.5 1.5 0 0 1 3.5 3H6l1.5 1.5h5A1.5 1.5 0 0 1 14 6v6a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 2 12V4.5Z"
											stroke="currentColor"
											stroke-width="1.3"
											stroke-linejoin="round"
										/>
									</svg>
								</span>
								<span class="folder-name">{{ folder.label }}</span>
								<span class="folder-chevron" aria-hidden="true">
									<svg viewBox="0 0 16 16" fill="none">
										<path d="M6 4l4 4-4 4" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" />
									</svg>
								</span>
							</button>
							<div class="subfolder-body">
							<button
								v-for="session in folder.children"
								:key="session.id"
								class="node kind-chat"
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
							v-for="child in group.children"
							:key="child.id"
							class="node kind-chat"
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
					<svg viewBox="0 0 20 20" fill="none">
						<circle cx="10" cy="10" r="2.6" stroke="currentColor" stroke-width="1.6" />
						<path
							d="M10 2.8v2M10 15.2v2M17.2 10h-2M4.8 10h-2M14.9 5.1l-1.4 1.4M6.5 13.5l-1.4 1.4M14.9 14.9l-1.4-1.4M6.5 6.5 5.1 5.1"
							stroke="currentColor"
							stroke-width="1.5"
							stroke-linecap="round"
						/>
					</svg>
				</span>
				<span class="label">系统设置</span>
				<span class="chev" aria-hidden="true">
					<svg viewBox="0 0 16 16" fill="none">
						<path d="M6 4l4 4-4 4" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" />
					</svg>
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
					<span class="context-menu-icon" aria-hidden="true" v-html="getContextIcon(item.icon)"></span>
					<span class="context-menu-label">{{ item.label }}</span>
				</button>
			</template>
		</div>
	</aside>
</template>

<style scoped>
.sidebar {
	height: 100%;
	display: flex;
	flex-direction: column;
	min-height: 0;
	background: #f4f5f7;
	border-right: 1px solid #d9dde4;
}

/* ---- 品牌行 ---- */
.brand {
	flex: 0 0 auto;
	display: flex;
	align-items: center;
	gap: 9px;
	padding: 16px 16px 12px;
}

.brand-mark {
	width: 26px;
	height: 26px;
	flex: 0 0 auto;
	display: grid;
	place-items: center;
	border-radius: 7px;
	background: #4f73c8;
	color: #fff;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.06);
}

.brand-mark svg {
	width: 15px;
	height: 15px;
}

.brand-name {
	font-family: 'Segoe UI Variable Display', 'Segoe UI', system-ui, sans-serif;
	font-size: 14px;
	font-weight: 650;
	letter-spacing: 0.01em;
	color: #111827;
}

.brand-spacer {
	flex: 1 1 auto;
}

.brand-collapse {
	width: 26px;
	height: 26px;
	display: grid;
	place-items: center;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: #6b7280;
}

.brand-collapse:hover {
	background: #e5e7eb;
	color: #111827;
}

.brand-collapse svg {
	width: 16px;
	height: 16px;
}

/* ================= 折叠态 ================= */
/* 展开态默认隐藏图标轨；折叠态隐藏完整导航 */
.rail {
	display: none;
}

.sidebar.collapsed .brand {
	padding: 16px 0 12px;
	justify-content: center;
}

.sidebar.collapsed .brand-mark,
.sidebar.collapsed .brand-name,
.sidebar.collapsed .brand-spacer {
	display: none;
}

.sidebar.collapsed .brand-collapse {
	margin: 0 auto;
}

/* 折叠时隐藏完整导航的三段 */
.sidebar.collapsed .nav-top,
.sidebar.collapsed .nav-mid,
.sidebar.collapsed .nav-bot {
	display: none;
}

/* 折叠态图标轨 */
.sidebar.collapsed .rail {
	flex: 1 1 auto;
	min-height: 0;
	display: flex;
	flex-direction: column;
	align-items: center;
	padding: 2px 0 12px;
}

.rail-scroll {
	flex: 1 1 auto;
	min-height: 0;
	width: 100%;
	display: flex;
	flex-direction: column;
	align-items: center;
	gap: 4px;
	/* overflow 保持 visible，让右侧浮出的 tooltip 不被裁切；图标数量少无需滚动 */
	overflow: visible;
	padding: 2px 0;
}

/* 折叠态主操作：新建对话（accent 圆钮） */
.rail-new {
	position: relative;
	width: 38px;
	height: 38px;
	margin-bottom: 6px;
	display: grid;
	place-items: center;
	border: 0;
	border-radius: 11px;
	background: #4f73c8;
	color: #fff;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.1);
	transition: background 120ms ease, transform 80ms ease;
}

.rail-new:hover {
	background: #375fae;
}

.rail-new:active {
	transform: translateY(1px);
}

.rail-new svg {
	width: 20px;
	height: 20px;
}

.rail-sep {
	width: 26px;
	height: 1px;
	margin: 4px 0 6px;
	background: #e5e7eb;
}

/* 折叠态图标钮 */
.rail-btn {
	position: relative;
	width: 38px;
	height: 38px;
	display: grid;
	place-items: center;
	border: 0;
	border-radius: 10px;
	background: transparent;
	color: #6b7280;
	transition: background 110ms ease, color 110ms ease;
}

.rail-btn:hover {
	background: #e5e7eb;
	color: #111827;
}

.rail-btn .ico {
	display: inline-flex;
	align-items: center;
	justify-content: center;
}

/* v-html 注入的 SVG 不带 scoped 属性，需用 :deep() 穿透，否则塌成 0 尺寸 */
.rail-btn .ico :deep(svg),
.rail-btn > svg {
	width: 20px;
	height: 20px;
}

/* 选中态：accent 底 + 左侧指示条 */
.rail-btn.active {
	background: #eaf0fb;
	color: #375fae;
}

.rail-btn.active::before {
	content: '';
	position: absolute;
	left: -11px;
	top: 50%;
	transform: translateY(-50%);
	width: 3px;
	height: 20px;
	border-radius: 999px;
	background: #4f73c8;
}

/* tooltip：悬停时右侧浮出标签，保证图标可读可操作 */
.rail-btn .tip,
.rail-new .tip {
	position: absolute;
	left: calc(100% + 12px);
	top: 50%;
	transform: translateY(-50%) translateX(-4px);
	padding: 5px 9px;
	border-radius: 7px;
	background: #22262c;
	color: #fff;
	font-size: 12px;
	font-weight: 500;
	line-height: 1;
	white-space: nowrap;
	opacity: 0;
	pointer-events: none;
	box-shadow: 0 6px 18px rgba(23, 26, 31, 0.22);
	transition: opacity 130ms ease, transform 130ms ease;
	z-index: 60;
}

.rail-btn .tip::before,
.rail-new .tip::before {
	content: '';
	position: absolute;
	right: 100%;
	top: 50%;
	transform: translateY(-50%);
	border: 5px solid transparent;
	border-right-color: #22262c;
}

.rail-btn:hover .tip,
.rail-new:hover .tip {
	opacity: 1;
	transform: translateY(-50%) translateX(0);
}

.rail-btn .tip .k,
.rail-new .tip .k {
	margin-left: 7px;
	color: #9aa2ad;
	font-variant-numeric: tabular-nums;
}

.rail-bot {
	flex: 0 0 auto;
	display: flex;
	flex-direction: column;
	align-items: center;
	gap: 4px;
	padding-top: 6px;
	margin-top: 4px;
	width: 100%;
}

/* ================= 上：功能按钮区 ================= */
.nav-top {
	flex: 0 0 auto;
	padding: 4px 12px 12px;
	border-bottom: 1px solid #e5e7eb;
}

.btn-primary {
	width: 100%;
	display: inline-flex;
	align-items: center;
	gap: 9px;
	padding: 10px 12px;
	margin-bottom: 8px;
	border: 0;
	border-radius: 9px;
	background: #4f73c8;
	color: #fff;
	font-size: 13px;
	font-weight: 600;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.06);
	transition:
		background 120ms ease,
		transform 80ms ease;
}

.btn-primary:hover {
	background: #375fae;
}

.btn-primary:active {
	transform: translateY(1px);
}

.btn-primary svg {
	width: 17px;
	height: 17px;
}

.tool-list {
	display: flex;
	flex-direction: column;
	gap: 2px;
}

.tool-btn {
	width: 100%;
	display: inline-flex;
	align-items: center;
	gap: 11px;
	padding: 8px 11px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: #374151;
	font-size: 13px;
	font-weight: 500;
	text-align: left;
}

.tool-btn:hover {
	background: #e5e7eb;
	color: #111827;
}

.tool-btn .ico {
	width: 18px;
	height: 18px;
	flex: 0 0 auto;
	display: inline-flex;
	align-items: center;
	justify-content: center;
	color: #6b7280;
}

.tool-btn:hover .ico {
	color: #375fae;
}

.tool-btn .ico svg {
	width: 18px;
	height: 18px;
}

.tool-btn .label {
	flex: 1 1 auto;
	min-width: 0;
}

.tool-btn .kbd {
	flex: 0 0 auto;
	font-size: 10.5px;
	color: #8a929e;
	letter-spacing: 0.04em;
	opacity: 0;
	transition: opacity 120ms ease;
}

.tool-btn:hover .kbd {
	opacity: 1;
}

/* ================= 中：节点区 ================= */
.nav-mid {
	flex: 1 1 auto;
	min-height: 0;
	overflow-y: auto;
	overflow-x: hidden;
	overscroll-behavior: contain;
	padding: 10px 8px 12px;
}

.nav-mid::-webkit-scrollbar {
	width: 10px;
}

.nav-mid::-webkit-scrollbar-thumb {
	background: rgba(23, 26, 31, 0.14);
	border: 3px solid #f4f5f7;
	border-radius: 999px;
}

.group {
	margin-bottom: 6px;
}

.group-head {
	width: 100%;
	display: flex;
	align-items: center;
	gap: 7px;
	padding: 6px 8px 6px 9px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: #6b7280;
	text-align: left;
}

.group-head:hover {
	background: #eceef2;
	color: #374151;
}

.group-chevron {
	width: 14px;
	height: 14px;
	flex: 0 0 auto;
	color: #8a929e;
	transition: transform 140ms ease;
}

.group.open .group-chevron {
	transform: rotate(90deg);
}

.group-chevron svg {
	width: 14px;
	height: 14px;
}

.group-title {
	flex: 1 1 auto;
	font-size: 11px;
	font-weight: 700;
	letter-spacing: 0.07em;
	text-transform: uppercase;
}

.group-count {
	flex: 0 0 auto;
	min-width: 18px;
	height: 17px;
	padding: 0 6px;
	display: inline-flex;
	align-items: center;
	justify-content: center;
	border-radius: 999px;
	background: #f1f3f6;
	color: #6b7280;
	font-size: 10.5px;
	font-weight: 600;
	font-variant-numeric: tabular-nums;
}

.group-add {
	flex: 0 0 auto;
	width: 20px;
	height: 20px;
	place-items: center;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: #6b7280;
	display: grid;
}

/* .group-head:hover .group-add {
	display: grid;
} */

.group-add:hover {
	background: #e5e7eb;
	color: #375fae;
}

.group-add svg {
	width: 14px;
	height: 14px;
}

.group-body {
	display: flex;
	flex-direction: column;
	gap: 1px;
	padding: 2px 0 4px;
}

.group:not(.open) .group-body {
	display: none;
}

/* 节点项 */
.node {
	position: relative;
	width: 100%;
	display: flex;
	align-items: center;
	gap: 9px;
	padding: 7px 10px 7px 14px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: #374151;
	font-size: 13px;
	font-weight: 450;
	text-align: left;
}

.node:hover {
	background: #e5e7eb;
	color: #111827;
}

.node.menu-open {
	background: #fff;
	color: #111827;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.06);
}

.node.active {
	background: #fff;
	color: #111827;
	font-weight: 550;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.06);
}

.node .ntext {
	flex: 1 1 auto;
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.node .ntime {
	flex: 0 0 auto;
	font-size: 10.5px;
	color: #8a929e;
	font-variant-numeric: tabular-nums;
}

/* 对话节点圆点 */
.node.kind-chat .dot {
	width: 7px;
	height: 7px;
	flex: 0 0 auto;
	border-radius: 50%;
	background: #b6bdc7;
}

.node.active .dot {
	background: #4f73c8;
}

.empty-group {
	padding: 9px 12px 10px 30px;
	color: #9aa2ad;
	font-size: 12px;
	line-height: 1.45;
}

/* 项目目录：文件夹 */
.project-folder {
	position: relative;
	width: 100%;
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 7px 10px 7px 12px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: #374151;
	font-size: 13px;
	font-weight: 550;
	text-align: left;
}

.project-folder:hover {
	background: #e5e7eb;
	color: #111827;
}

.project-folder.menu-open {
	background: #fff;
	color: #111827;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.06);
}

.project-folder .folder-ico {
	width: 16px;
	height: 16px;
	flex: 0 0 auto;
	color: #4f73c8;
}

.project-folder .folder-ico svg {
	width: 16px;
	height: 16px;
}

.project-folder .folder-name {
	flex: 1 1 auto;
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.project-folder .folder-chevron {
	width: 14px;
	height: 14px;
	flex: 0 0 auto;
	color: #8a929e;
}

.project-folder .folder-chevron svg {
	width: 14px;
	height: 14px;
}

.subfolder.open .project-folder .folder-chevron {
	transform: rotate(90deg);
}

/* 项目下的会话列表 */
.subfolder-body {
	display: flex;
	flex-direction: column;
	gap: 1px;
	padding: 2px 0 4px 18px;
}

.subfolder:not(.open) .subfolder-body {
	display: none;
}

.subfolder-body .node {
	padding-left: 10px;
	font-size: 12.5px;
}

.context-menu {
	position: fixed;
	z-index: 1000;
	width: 208px;
	padding: 4px 0;
	border: 1px solid #d8dde5;
	border-radius: 8px;
	background: #fff;
	box-shadow:
		0 1px 2px rgba(23, 26, 31, 0.08),
		0 16px 40px rgba(23, 26, 31, 0.16);
	color: #1f2937;
	overflow: hidden;
}

.context-menu-item {
	width: 100%;
	height: 34px;
	display: flex;
	align-items: center;
	gap: 10px;
	padding: 0 12px;
	border: 0;
	background: transparent;
	color: inherit;
	font-size: 14px;
	font-weight: 450;
	line-height: 1;
	text-align: left;
}

.context-menu-item:hover {
	background: #f3f4f6;
	color: #111827;
}

.context-menu-item.danger {
	color: #ef4444;
}

.context-menu-item.danger:hover {
	background: #fff1f2;
	color: #dc2626;
}

.context-menu-icon {
	width: 16px;
	height: 16px;
	flex: 0 0 auto;
	display: inline-flex;
	align-items: center;
	justify-content: center;
	color: #6b7280;
}

.context-menu-item:hover .context-menu-icon,
.context-menu-item.danger .context-menu-icon {
	color: currentColor;
}

.context-menu-icon svg {
	width: 16px;
	height: 16px;
}

.context-menu-label {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.context-menu-divider {
	height: 1px;
	margin: 4px 0;
	background: #e5e7eb;
}

/* ================= 下：系统设置区 ================= */
.nav-bot {
	flex: 0 0 auto;
	padding: 10px 12px 12px;
	border-top: 1px solid #e5e7eb;
	background: #f4f5f7;
}

.settings-btn {
	width: 100%;
	display: inline-flex;
	align-items: center;
	gap: 11px;
	padding: 9px 11px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: #374151;
	font-size: 13px;
	font-weight: 500;
	text-align: left;
}

.settings-btn:hover {
	background: #e5e7eb;
	color: #111827;
}

.settings-btn.active {
	background: #fff;
	color: #111827;
	box-shadow: 0 1px 2px rgba(23, 26, 31, 0.06);
}

.settings-btn .ico {
	width: 18px;
	height: 18px;
	flex: 0 0 auto;
	display: inline-flex;
	align-items: center;
	justify-content: center;
	color: #6b7280;
}

.settings-btn:hover .ico,
.settings-btn.active .ico {
	color: #375fae;
}

.settings-btn .ico svg {
	width: 18px;
	height: 18px;
}

.settings-btn .label {
	flex: 1 1 auto;
}

.settings-btn .chev {
	width: 15px;
	height: 15px;
	color: #8a929e;
}

.settings-btn .chev svg {
	width: 15px;
	height: 15px;
}
</style>
