<script setup>
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue';
import WorkspaceTreeNode from './WorkspaceTreeNode.vue';

const props = defineProps({
	stepsHeaderHtml: {
		type: String,
		default: '',
	},
	stepsPanelHtml: {
		type: String,
		default: '',
	},
	panelMode: {
		type: String,
		default: 'tools',
	},
	workspaceLabel: {
		type: String,
		default: '',
	},
	workspacePath: {
		type: String,
		default: '',
	},
	workspaceTreeEntries: {
		type: Array,
		default: () => [],
	},
	workspaceTreeLoading: {
		type: Boolean,
		default: false,
	},
	workspaceTreeLoaded: {
		type: Boolean,
		default: false,
	},
	workspaceTreeError: {
		type: String,
		default: '',
	},
	hasWorkspace: {
		type: Boolean,
		default: false,
	},
});

const emit = defineEmits([
	'set-panel-mode',
	'toggle-workspace-directory',
	'open-workspace-file',
	'open-workspace-entry-location',
]);

const scrollEl = ref(null);
const contextMenuEl = ref(null);

const contextMenu = reactive({
	open: false,
	x: 0,
	y: 0,
	path: '',
	isDirectory: false,
});

const toggleButtonLabel = computed(() => (
	props.panelMode === 'workspace'
		? '切换到工具视图'
		: '切换到工作区视图'
));

const contextMenuLabel = computed(() => (
	contextMenu.isDirectory
		? '打开目录所在位置'
		: '打开文件所在位置'
));

const contextMenuStyle = computed(() => ({
	left: `${contextMenu.x}px`,
	top: `${contextMenu.y}px`,
}));

function closeContextMenu() {
	contextMenu.open = false;
	contextMenu.x = 0;
	contextMenu.y = 0;
	contextMenu.path = '';
	contextMenu.isDirectory = false;
}

function togglePanelMode() {
	closeContextMenu();
	emit('set-panel-mode', props.panelMode === 'workspace' ? 'tools' : 'workspace');
}

function openContextMenu(payload) {
	const menuWidth = 192;
	const menuHeight = 52;
	const margin = 12;
	const nextX = Number(payload?.x || 0);
	const nextY = Number(payload?.y || 0);

	contextMenu.open = true;
	contextMenu.path = payload?.path || '';
	contextMenu.isDirectory = Boolean(payload?.isDirectory);
	contextMenu.x = Math.max(margin, Math.min(nextX, window.innerWidth - menuWidth - margin));
	contextMenu.y = Math.max(margin, Math.min(nextY, window.innerHeight - menuHeight - margin));
}

function onWorkspaceDirectoryToggle(relativePath) {
	closeContextMenu();
	emit('toggle-workspace-directory', relativePath);
}

function onWorkspaceFileOpen(relativePath) {
	closeContextMenu();
	emit('open-workspace-file', relativePath);
}

function onWorkspaceContextMenu(payload) {
	openContextMenu(payload);
}

function openWorkspaceEntryLocation() {
	if (!contextMenu.path) {
		closeContextMenu();
		return;
	}

	emit('open-workspace-entry-location', {
		path: contextMenu.path,
		isDirectory: contextMenu.isDirectory,
	});
	closeContextMenu();
}

function onDocumentPointerDown(event) {
	if (!contextMenu.open) {
		return;
	}

	if (contextMenuEl.value?.contains(event.target)) {
		return;
	}

	closeContextMenu();
}

function onDocumentKeydown(event) {
	if (event.key === 'Escape') {
		closeContextMenu();
	}
}

onMounted(() => {
	document.addEventListener('pointerdown', onDocumentPointerDown);
	document.addEventListener('keydown', onDocumentKeydown);
});

onUnmounted(() => {
	document.removeEventListener('pointerdown', onDocumentPointerDown);
	document.removeEventListener('keydown', onDocumentKeydown);
});

defineExpose({
	getScrollEl: () => scrollEl.value,
});
</script>

<template>
	<aside id="steps-panel-shell" class="panel steps-panel">
		<div class="steps-panel-head">
			<div class="steps-header-shell">
				<div v-if="props.panelMode === 'tools'" id="steps-header" class="steps-header" v-html="stepsHeaderHtml">
				</div>
				<div v-else class="steps-header workspace-header">
					<div class="workspace-header-copy">
						<div class="steps-title">工作区</div>
						<div v-if="props.workspacePath" class="workspace-tree-path">{{ props.workspacePath }}</div>
						<div v-else class="steps-subtitle">{{ props.workspaceLabel || '未绑定工作区' }}</div>
					</div>
				</div>

				<button class="steps-mode-toggle" type="button" :aria-label="toggleButtonLabel"
					:title="toggleButtonLabel" @click="togglePanelMode">
					<svg v-if="props.panelMode === 'workspace'" viewBox="0 0 20 20" fill="none" stroke="currentColor"
						stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
						<path d="M4 5.25h12" />
						<path d="M4 10h12" />
						<path d="M4 14.75h12" />
						<circle cx="6" cy="5.25" r=".75" fill="currentColor" stroke="none" />
						<circle cx="6" cy="10" r=".75" fill="currentColor" stroke="none" />
						<circle cx="6" cy="14.75" r=".75" fill="currentColor" stroke="none" />
					</svg>
					<svg v-else viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.5"
						stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
						<path
							d="M2.75 6.25h5.1l1.3 1.55h8.1v6a1.1 1.1 0 0 1-1.1 1.1H3.85a1.1 1.1 0 0 1-1.1-1.1V7.35a1.1 1.1 0 0 1 1.1-1.1Z" />
						<path d="M7.1 10.1h5.8" />
						<path d="M10 7.2v5.8" />
					</svg>
				</button>
			</div>
		</div>

		<div v-if="props.panelMode === 'tools'" id="steps-scroll" ref="scrollEl" class="steps-scroll"
			v-html="stepsPanelHtml"></div>

		<div v-else id="steps-scroll" ref="scrollEl" class="steps-scroll workspace-tree-scroll"
			@scroll.passive="closeContextMenu">
			<div v-if="!props.hasWorkspace" class="muted-placeholder">选择工作区后，这里会显示目录和文件树。</div>
			<div v-else-if="props.workspaceTreeLoading && !props.workspaceTreeLoaded" class="workspace-tree-state">
				正在加载目录...
			</div>
			<div v-else-if="props.workspaceTreeError && !props.workspaceTreeLoaded" class="workspace-tree-state error">
				{{ props.workspaceTreeError }}
			</div>
			<div v-else-if="props.workspaceTreeEntries.length === 0" class="muted-placeholder">当前工作区没有可显示的文件。</div>
			<div v-else class="workspace-tree-list">
				<WorkspaceTreeNode v-for="entry in props.workspaceTreeEntries" :key="entry.path" :node="entry"
					@toggle-directory="onWorkspaceDirectoryToggle" @open-file="onWorkspaceFileOpen"
					@show-context-menu="onWorkspaceContextMenu" />
			</div>
		</div>

		<div v-if="contextMenu.open" ref="contextMenuEl" class="workspace-context-menu" :style="contextMenuStyle"
			role="menu" @click.stop>
			<button class="workspace-context-menu-item" type="button" role="menuitem"
				@click="openWorkspaceEntryLocation">
				{{ contextMenuLabel }}
			</button>
		</div>
	</aside>
</template>
