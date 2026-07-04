<script setup>
import { ref, computed } from 'vue';

const props = defineProps({
	items: {
		type: Array,
		default: () => [],
	},
	activeId: {
		type: String,
		default: null,
	},
});

const emit = defineEmits(['select', 'action']);

const expandedGroups = ref(new Set(['projects', 'conversations']));
const expandedFolders = ref(new Set());

const actionItems = computed(() => props.items.filter((i) => i.type === 'action'));
const groupItems = computed(() => props.items.filter((i) => i.type === 'group'));
const settingsItem = computed(() => props.items.find((i) => i.type === 'view' && i.id === 'settings'));
const settingsActive = computed(() => props.activeId === 'settings');

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

const iconMap = {
	'new-chat': `<svg viewBox="0 0 20 20" fill="none"><path d="M10 4.5v11M4.5 10h11" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/></svg>`,
	search: `<svg viewBox="0 0 20 20" fill="none"><circle cx="9" cy="9" r="5.5" stroke="currentColor" stroke-width="1.7"/><path d="M14 14l3.2 3.2" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/></svg>`,
	plugins: `<svg viewBox="0 0 20 20" fill="none"><rect x="3.5" y="3.5" width="5" height="5" rx="1.2" stroke="currentColor" stroke-width="1.6"/><rect x="11.5" y="3.5" width="5" height="5" rx="1.2" stroke="currentColor" stroke-width="1.6"/><rect x="3.5" y="11.5" width="5" height="5" rx="1.2" stroke="currentColor" stroke-width="1.6"/><path d="M14 11.5v5M11.5 14h5" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>`,
	extensions: `<svg viewBox="0 0 20 20" fill="none"><path d="M10 3.2v3.4M10 13.4v3.4M3.2 10h3.4M13.4 10h3.4M5.4 5.4l2.4 2.4M12.2 12.2l2.4 2.4M14.6 5.4l-2.4 2.4M7.8 12.2l-2.4 2.4" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>`,
	automation: `<svg viewBox="0 0 20 20" fill="none"><circle cx="6" cy="10" r="2.6" stroke="currentColor" stroke-width="1.6"/><circle cx="14" cy="10" r="2.6" stroke="currentColor" stroke-width="1.6"/><path d="M8.6 10h2.8" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/></svg>`,
};

const kbdMap = {
	search: 'Ctrl K',
};

function getIcon(id) {
	return iconMap[id] || '';
}
</script>

<template>
	<aside class="sidebar" aria-label="主导航">
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
			<button class="brand-collapse" type="button" title="折叠侧栏" aria-label="折叠侧栏" @click="emit('action', 'collapse')">
				<svg viewBox="0 0 20 20" fill="none">
					<path d="M12 5 7 10l5 5" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" />
					<path d="M4.5 4.5v11" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" />
				</svg>
			</button>
		</div>

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
							<button class="project-folder" type="button" @click="toggleFolder(folder.id)">
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
								:class="{ active: isNodeActive(session.id) }"
								type="button"
								@click="selectNode(session.id)"
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
							:class="{ active: isNodeActive(child.id) }"
							type="button"
							@click="selectNode(child.id)"
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
