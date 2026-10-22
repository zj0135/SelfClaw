<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue';
import { Check, ChevronDown, ChevronRight, Folder, Search, Trash2 } from 'lucide-vue-next';

const props = defineProps({
	workspaceSelection: { type: Object, default: () => ({}) },
});

const emit = defineEmits(['refresh', 'select-root', 'browse', 'delete-root']);
const isOpen = ref(false);
const searchText = ref('');
const rootRef = ref(null);
const searchInputRef = ref(null);
const hoverRootId = ref(null);
const current = computed(() => props.workspaceSelection?.current || null);
const roots = computed(() => Array.isArray(props.workspaceSelection?.roots) ? props.workspaceSelection.roots : []);
const filteredRoots = computed(() => {
	const keyword = searchText.value.trim().toLowerCase();
	return keyword
		? roots.value.filter((root) => `${root.name || ''} ${root.path || ''}`.toLowerCase().includes(keyword))
		: roots.value;
});

async function toggle() {
	isOpen.value = !isOpen.value;
	if (!isOpen.value) return;
	searchText.value = '';
	emit('refresh', false);
	await nextTick();
	searchInputRef.value?.focus();
}

function selectRoot(root) {
	if (!root?.id) return;
	emit('select-root', root.id);
	isOpen.value = false;
}

function browse() {
	emit('browse');
	isOpen.value = false;
}

function requestDelete(root, event) {
	event.stopPropagation();
	emit('delete-root', root.id);
}

function onDocumentPointerDown(event) {
	if (isOpen.value && !rootRef.value?.contains(event.target)) isOpen.value = false;
}

onMounted(() => document.addEventListener('pointerdown', onDocumentPointerDown));
onUnmounted(() => document.removeEventListener('pointerdown', onDocumentPointerDown));
</script>

<template>
	<div ref="rootRef" class="workspace-picker">
		<button class="workspace-trigger" :class="{ active: isOpen }" type="button"
			:title="current?.path || current?.name" :aria-expanded="isOpen" @click.stop="toggle">
			<Folder :size="13" :stroke-width="1.8" aria-hidden="true" />
			<span>{{ current?.name || '未选择工作目录' }}</span>
			<ChevronDown :class="{ open: isOpen }" :size="12" :stroke-width="2" aria-hidden="true" />
		</button>

		<div v-if="isOpen" class="workspace-menu" role="dialog" aria-label="选择工作目录">
			<label class="workspace-search">
				<Search :size="13" :stroke-width="2" aria-hidden="true" />
				<input ref="searchInputRef" v-model="searchText" type="text" placeholder="搜索工作目录" />
			</label>
			<div class="workspace-list">
				<button v-for="root in filteredRoots" :key="root.id" class="workspace-row"
					:class="{ selected: root.id === current?.id, 'is-managed': root.isManagedWorktree }" type="button"
					:title="root.path" @mousemove="hoverRootId = root.id" @mouseleave="hoverRootId = null"
					@click="selectRoot(root)">
					<Folder :size="14" :stroke-width="1.7" aria-hidden="true" />
					<span>{{ root.name }}</span>
					<Check v-if="root.id === current?.id && !(hoverRootId === root.id && !root.isManagedWorktree)"
						:size="14" :stroke-width="2.2" aria-hidden="true" />
					<Trash2 v-if="hoverRootId === root.id && !root.isManagedWorktree" class="row-delete" :size="14"
						:stroke-width="2" role="button" aria-hidden="true" title="删除目录记录"
						@click.stop="requestDelete(root, $event)" />
				</button>
				<p v-if="filteredRoots.length === 0" class="empty-row">没有匹配的工作目录</p>
			</div>
			<button class="workspace-row browse-row" type="button" @click="browse">
				<Folder :size="14" :stroke-width="1.7" aria-hidden="true" />
				<span>添加工作目录</span>
				<ChevronRight :size="14" :stroke-width="2" aria-hidden="true" />
			</button>
		</div>
	</div>
</template>

<style scoped>
.workspace-picker {
	position: relative;
	min-width: 0;
}

.workspace-trigger {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	max-width: 230px;
	height: 28px;
	margin-left: -5px;
	padding: 0 6px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--muted);
	font: 500 12px/1.4 var(--font-ui);
	cursor: pointer;
}

.workspace-trigger:hover,
.workspace-trigger.active {
	background: color-mix(in srgb, var(--text) 6%, transparent);
}

.workspace-trigger span {
	min-width: 0;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.workspace-trigger svg {
	flex: none;
}

.workspace-trigger svg.open {
	transform: rotate(180deg);
}

.workspace-menu {
	position: absolute;
	left: -7px;
	bottom: 36px;
	z-index: 90;
	width: 288px;
	padding: 9px;
	border: 1px solid var(--border-strong);
	border-radius: 9px;
	background: var(--panel);
	box-shadow: 0 18px 50px rgba(var(--shadow-ink), 0.14), 0 3px 10px rgba(var(--shadow-ink), 0.05);
}

.workspace-search {
	display: flex;
	align-items: center;
	gap: 7px;
	height: 34px;
	padding: 0 9px;
	border: 1px solid var(--card-border);
	border-radius: 7px;
	color: var(--faint);
}

.workspace-search:focus-within {
	border-color: color-mix(in srgb, var(--accent) 45%, transparent);
}

.workspace-search input {
	flex: 1;
	min-width: 0;
	border: 0;
	outline: none;
	background: transparent;
	color: var(--text-strong);
	font: 12.5px/1 var(--font-ui);
}

.workspace-list {
	max-height: 220px;
	margin-top: 7px;
	overflow-y: auto;
	overscroll-behavior: contain;
}

.workspace-row {
	display: flex;
	align-items: center;
	gap: 8px;
	width: 100%;
	height: 34px;
	padding: 0 8px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--text-soft);
	font: 12.5px/1 var(--font-ui);
	text-align: left;
	cursor: pointer;
}

.workspace-row:hover,
.workspace-row.selected {
	background: var(--panel-muted);
}

.workspace-row span {
	flex: 1;
	min-width: 0;
	overflow: hidden;
	font-weight: 550;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.workspace-row svg {
	flex: none;
	color: var(--muted-soft);
}

.row-delete {
	color: var(--faint);
	transition: color 0.15s ease;
}

.row-delete:hover {
	color: var(--danger);
}

.browse-row {
	margin-top: 7px;
	border-top: 1px solid var(--border);
	border-radius: 0 0 6px 6px;
}

.empty-row {
	margin: 10px 8px;
	color: var(--faint);
	font-size: var(--fs-12);
}

@media (max-width: 700px) {
	.workspace-trigger {
		max-width: 112px;
	}
}
</style>
