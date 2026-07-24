<script setup>
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { Folder, X, Plus, RefreshCw } from 'lucide-vue-next';

const props = defineProps({
	workspaceSelection: {
		type: Object,
		default: () => ({}),
	},
	loading: {
		type: Boolean,
		default: false,
	},
});

const emit = defineEmits(['refresh', 'select-root', 'select-path', 'browse']);

const isOpen = ref(false);
const selectorRef = ref(null);

const current = computed(() => props.workspaceSelection?.current || null);
const currentPath = computed(() => current.value?.path || '未选择工作目录');
const roots = computed(() => Array.isArray(props.workspaceSelection?.roots) ? props.workspaceSelection.roots : []);
const commonFolders = computed(() =>
	Array.isArray(props.workspaceSelection?.commonFolders) ? props.workspaceSelection.commonFolders : []);
const selectedId = computed(() => current.value?.id || '');
const selectedPath = computed(() => normalizePath(current.value?.path));
const hasRoots = computed(() => roots.value.length > 0);

function normalizePath(value) {
	return typeof value === 'string' ? value.trim().replace(/[\\\/]+$/, '').toLowerCase() : '';
}

function toggleOpen() {
	isOpen.value = !isOpen.value;
	if (isOpen.value) {
		emit('refresh', false);
	}
}

function close() {
	isOpen.value = false;
}

function refresh() {
	emit('refresh', true);
}

function selectRoot(root) {
	if (!root?.id) {
		return;
	}

	emit('select-root', root.id);
	close();
}

function selectPath(folder) {
	if (!folder?.path) {
		return;
	}

	emit('select-path', folder.path);
	close();
}

function browse() {
	emit('browse');
	close();
}

function isSelectedRoot(root) {
	return root?.id && selectedId.value === root.id;
}

function isSelectedPath(folder) {
	return normalizePath(folder?.path) === selectedPath.value;
}

function onDocumentPointerDown(event) {
	if (!isOpen.value || selectorRef.value?.contains(event.target)) {
		return;
	}

	close();
}

function onKeydown(event) {
	if (event.key === 'Escape') {
		close();
	}
}

onMounted(() => {
	document.addEventListener('pointerdown', onDocumentPointerDown);
});

onUnmounted(() => {
	document.removeEventListener('pointerdown', onDocumentPointerDown);
});
</script>

<template>
	<div ref="selectorRef" class="workspace-selector" @keydown="onKeydown">
		<button
			class="icon-btn workspace-trigger"
			:class="{ active: isOpen }"
			type="button"
			title="工作目录"
			aria-label="选择工作目录"
			:aria-expanded="isOpen ? 'true' : 'false'"
			@click.stop="toggleOpen"
		>
			<Folder :size="17" :stroke-width="1.7" aria-hidden="true" />
		</button>

		<div v-if="isOpen" class="workspace-card" role="dialog" aria-label="选择工作目录">
			<div class="workspace-card-header">
				<h2>选择工作目录</h2>
			<button class="workspace-close" type="button" title="关闭" aria-label="关闭" @click="close">
				<X :size="15" :stroke-width="2" aria-hidden="true" />
			</button>
			</div>

			<div class="workspace-current">
				<div class="workspace-label">当前工作目录</div>
			<div class="workspace-path-line">
				<Folder :size="15" :stroke-width="1.7" aria-hidden="true" />
				<span :title="currentPath">{{ currentPath }}</span>
			</div>
			</div>

			<div class="workspace-section">
				<div class="workspace-section-head">
				<span>桌面目录</span>
				<button class="workspace-refresh" type="button" :disabled="loading" @click="refresh">
					<RefreshCw :size="11" :stroke-width="2" :class="{ spinning: loading }" aria-hidden="true" />
					{{ loading ? 'refreshing' : 'refresh' }}
				</button>
				</div>
				<div class="workspace-chip-row">
					<button
						v-for="folder in commonFolders"
						:key="folder.id || folder.path"
						class="workspace-chip"
						:class="{ selected: isSelectedPath(folder) }"
						type="button"
						@click="selectPath(folder)"
					>
					<Folder :size="14" :stroke-width="1.7" aria-hidden="true" />
					<span>{{ folder.name || 'Desktop' }}</span>
				</button>
				<button class="workspace-chip workspace-chip-dashed" type="button" @click="browse">
					<Plus :size="14" :stroke-width="2" aria-hidden="true" />
					<span>选择其他目录</span>
				</button>
				</div>
			</div>

			<div v-if="hasRoots" class="workspace-section workspace-saved">
				<div class="workspace-section-head">
					<span>已保存目录</span>
				</div>
				<div class="workspace-root-list">
					<button
						v-for="root in roots"
						:key="root.id"
						class="workspace-root"
						:class="{ selected: isSelectedRoot(root) }"
						type="button"
						@click="selectRoot(root)"
					>
						<span class="workspace-root-name">{{ root.name }}</span>
						<span class="workspace-root-path">{{ root.path }}</span>
					</button>
				</div>
			</div>

			<p v-if="workspaceSelection?.error" class="workspace-error">{{ workspaceSelection.error }}</p>
		</div>
	</div>
</template>

<style scoped>
.workspace-selector {
	position: relative;
	display: inline-flex;
}

.workspace-trigger.active,
.workspace-trigger:hover {
	background: #f3f4f6;
	color: #171a1f;
}

.workspace-card {
	position: absolute;
	left: 0;
	bottom: calc(100% + 12px);
	z-index: 80;
	width: min(560px, calc(100vw - 48px));
	padding: 14px 16px 16px;
	border: 1px solid #dfe3ea;
	border-radius: 14px;
	background: #ffffff;
	box-shadow:
		0 18px 50px rgba(23, 26, 31, 0.12),
		0 3px 10px rgba(23, 26, 31, 0.05);
	color: #171a1f;
}

.workspace-card-header {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
	margin-bottom: 12px;
}

.workspace-card-header h2 {
	margin: 0;
	color: #171a1f;
	font-size: 16px;
	font-weight: 700;
	line-height: 1.3;
	letter-spacing: 0;
}

.workspace-close {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 28px;
	height: 28px;
	padding: 0;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: #4b5563;
}

.workspace-close:hover {
	background: #f3f4f6;
	color: #111827;
}

.workspace-path-line svg {
	flex: none;
	color: #9aa2ad;
}

.workspace-chip svg {
	flex: none;
}

.workspace-current {
	padding: 9px 10px;
	border: 1px solid #eceff3;
	border-radius: 8px;
	background: #fbfcfd;
}

.workspace-label,
.workspace-section-head {
	color: #9aa2ad;
	font-size: 11px;
	line-height: 1.4;
}

.workspace-path-line {
	display: flex;
	align-items: center;
	gap: 7px;
	min-width: 0;
	margin-top: 5px;
	color: #5f6876;
	font-size: 12px;
	line-height: 1.4;
}

.workspace-path-line span {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.workspace-section {
	margin-top: 12px;
}

.workspace-section-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 10px;
	margin-bottom: 7px;
}

.workspace-refresh {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 0;
	border: 0;
	background: transparent;
	color: #9aa2ad;
	font-family: var(--font-mono, ui-monospace, monospace);
	font-size: 10.5px;
	letter-spacing: 0.04em;
}

.workspace-refresh:hover:not(:disabled) {
	color: var(--accent, #3b5bfd);
}

.workspace-refresh:disabled {
	cursor: default;
	opacity: 0.58;
}

.workspace-refresh .spinning {
	animation: ws-spin 0.8s linear infinite;
}

@keyframes ws-spin {
	to {
		transform: rotate(360deg);
	}
}

.workspace-chip-row {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 8px;
}

.workspace-chip {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	max-width: 210px;
	min-height: 30px;
	padding: 6px 10px;
	border: 1px solid #d6dbe3;
	border-radius: 8px;
	background: #ffffff;
	color: #394150;
	font-size: 12px;
	font-weight: 600;
	line-height: 1.35;
	transition:
		background 0.15s,
		border-color 0.15s,
		color 0.15s;
}

.workspace-chip span {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.workspace-chip:hover {
	border-color: #9ca3af;
	background: #f7f8fa;
	color: #171a1f;
}

.workspace-chip.selected {
	border-color: rgba(59, 91, 253, 0.4);
	background: rgba(59, 91, 253, 0.07);
	color: var(--accent-2, #2f49d1);
}

.workspace-chip-dashed {
	border-style: dashed;
	color: #6b7280;
}

.workspace-saved {
	padding-top: 2px;
}

.workspace-root-list {
	display: grid;
	gap: 6px;
	max-height: 168px;
	overflow: auto;
	padding-right: 2px;
}

.workspace-root {
	display: grid;
	gap: 2px;
	width: 100%;
	min-height: 44px;
	padding: 7px 9px;
	border: 1px solid transparent;
	border-radius: 8px;
	background: transparent;
	text-align: left;
}

.workspace-root:hover {
	border-color: #e1e4ea;
	background: #f7f8fa;
}

.workspace-root.selected {
	border-color: rgba(59, 91, 253, 0.35);
	background: rgba(59, 91, 253, 0.06);
}

.workspace-root-name {
	min-width: 0;
	overflow: hidden;
	color: #252a32;
	font-size: 12px;
	font-weight: 650;
	line-height: 1.35;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.workspace-root-path {
	min-width: 0;
	overflow: hidden;
	color: #8a929e;
	font-size: 11px;
	line-height: 1.35;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.workspace-error {
	margin: 10px 0 0;
	color: #c24150;
	font-size: 12px;
	line-height: 1.4;
}

@media (max-width: 640px) {
	.workspace-card {
		left: -52px;
		width: calc(100vw - 32px);
	}
}
</style>
