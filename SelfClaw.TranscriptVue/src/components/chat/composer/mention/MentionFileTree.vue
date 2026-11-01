<script setup>
import { computed, watch } from 'vue';
import { ChevronRight, CornerDownLeft, File, Folder, FolderOpen, LoaderCircle } from 'lucide-vue-next';
import { useWorkspaceTree } from '../../../../composables/useWorkspaceTree.js';

// `@` 的树形文件列表：复用侧栏的懒加载树（workspace-tree/list，每层 ≤250、宿主已滤隐藏/构建目录）。
// 没有 query 时按树形浏览；有 query 时只在已加载过的条目里扁平匹配，不做宿主搜索。
const props = defineProps({
	query: { type: String, default: '' },
	workspaceSelection: { type: Object, default: () => ({}) },
	activeIndex: { type: Number, default: 0 },
});
const emit = defineEmits(['update:items', 'activate', 'choose']);

const tree = useWorkspaceTree();

const target = computed(() => {
	const current = props.workspaceSelection?.current;
	if (!current?.id || current.isFallback) {
		return null;
	}

	return { workspaceRootId: current.id, name: current.name, path: current.path };
});

watch(() => target.value?.workspaceRootId, (rootId) => {
	if (rootId) {
		tree.open(target.value);
	} else {
		tree.close();
	}
}, { immediate: true });

const normalizedQuery = computed(() => props.query.trim().toLowerCase());

const renderRows = computed(() => {
	const source = normalizedQuery.value ? flatMatches(normalizedQuery.value) : tree.rows.value;
	let index = -1;
	return source.map((row) => (row.kind === 'entry' ? { ...row, index: ++index } : { ...row, index: -1 }));
});

function flatMatches(query) {
	return tree.allEntries.value
		.filter((entry) => entry.relativePath.toLowerCase().includes(query))
		.sort((left, right) => left.relativePath.localeCompare(right.relativePath))
		.map((entry) => ({
			key: entry.relativePath,
			kind: 'entry',
			name: entry.name,
			relativePath: entry.relativePath,
			isDirectory: Boolean(entry.isDirectory),
			depth: 0,
			expanded: false,
			loading: false,
			error: '',
			flat: true,
		}));
}

const items = computed(() => renderRows.value.filter((row) => row.kind === 'entry'));
watch(items, (list) => emit('update:items', list), { immediate: true });

const emptyMessage = computed(() => {
	if (!target.value) {
		return '请先在下方状态栏选择工作目录';
	}

	if (tree.rootError.value) {
		return tree.rootError.value;
	}

	if (tree.rootLoading.value && !tree.rootLoaded.value) {
		return '正在读取目录…';
	}

	if (renderRows.value.length) {
		return '';
	}

	return normalizedQuery.value ? '已加载的目录里没有匹配项，展开更多目录后再试' : '该目录为空';
});

function onRowClick(row) {
	emit('activate', row.index);
	if (row.isDirectory && !row.flat) {
		tree.toggle(row.relativePath);
		return;
	}

	emit('choose', row);
}

// 键盘导航由 MentionPopover 调用：→ 展开、← 折叠、Enter 切换；扁平匹配结果没有层级，不展开。
function toggle(item) {
	if (item?.isDirectory && !item.flat) {
		tree.toggle(item.relativePath);
	}
}

function expand(item) {
	if (item?.isDirectory && !item.flat && !item.expanded) {
		tree.toggle(item.relativePath);
	}
}

function collapse(item) {
	if (item?.isDirectory && !item.flat && item.expanded) {
		tree.toggle(item.relativePath);
	}
}

defineExpose({ toggle, expand, collapse });
</script>

<template>
	<div class="file-tree" role="tree" aria-label="工作目录">
		<p v-if="emptyMessage" class="mention-empty" :class="{ 'mention-empty--error': Boolean(tree.rootError.value) }">
			{{ emptyMessage }}
		</p>
		<template v-for="row in renderRows" :key="row.key">
			<p v-if="row.kind === 'at-limit'" class="tree-more" :style="{ '--depth': row.depth }">每层最多显示 {{ row.limit }} 项</p>
			<div v-else class="mention-row" :class="{ active: row.index === props.activeIndex, 'is-dir': row.isDirectory }"
				:data-index="row.index" :style="{ '--depth': row.depth }" :title="row.relativePath" role="treeitem" tabindex="-1"
				:aria-expanded="row.isDirectory && !row.flat ? String(row.expanded) : undefined"
				:aria-selected="row.index === props.activeIndex" @mouseenter="emit('activate', row.index)" @click="onRowClick(row)">
				<span class="tree-chevron" :class="{ open: row.expanded, hidden: !row.isDirectory || row.flat }" aria-hidden="true">
					<LoaderCircle v-if="row.loading" :size="11" class="mention-spin" />
					<ChevronRight v-else :size="12" :stroke-width="2" />
				</span>
				<span class="mention-row-icon" aria-hidden="true">
					<FolderOpen v-if="row.isDirectory && row.expanded" :size="13" :stroke-width="1.8" />
					<Folder v-else-if="row.isDirectory" :size="13" :stroke-width="1.8" />
					<File v-else :size="13" :stroke-width="1.8" />
				</span>
				<span class="mention-row-copy">
					<span class="mention-row-name">{{ row.name }}</span>
					<span v-if="row.flat" class="mention-row-meta">{{ row.relativePath }}</span>
				</span>
				<button v-if="row.isDirectory" class="tree-ref" type="button" tabindex="-1" title="引用此目录（Tab）"
					@mousedown.prevent @click.stop="emit('choose', row)">
					<CornerDownLeft :size="11" :stroke-width="2.2" aria-hidden="true" />
					引用
				</button>
			</div>
			<p v-if="row.error" class="mention-empty mention-empty--error tree-row-error" :style="{ '--depth': row.depth + 1 }">
				{{ row.error }}
			</p>
		</template>
	</div>
</template>

<style scoped>
@import '../../../../styles/mention-list.css';

.file-tree {
	display: grid;
	gap: 1px;
}

.tree-chevron {
	display: grid;
	width: 12px;
	height: 12px;
	flex: none;
	place-items: center;
	color: var(--muted-soft);
	transition: transform 0.18s cubic-bezier(0.16, 1, 0.3, 1);
}

.tree-chevron.open {
	transform: rotate(90deg);
}

.tree-chevron.hidden {
	visibility: hidden;
}

.mention-row.is-dir .mention-row-icon {
	color: var(--text-soft);
}

.tree-ref {
	display: none;
	align-items: center;
	gap: 3px;
	flex: none;
	height: 20px;
	padding: 0 7px;
	border: 1px solid var(--border);
	border-radius: 6px;
	background: var(--panel);
	color: var(--muted);
	font-size: var(--fs-10);
	font-weight: 600;
	cursor: pointer;
	transition: color 0.12s, border-color 0.12s;
}

.mention-row.active .tree-ref,
.mention-row:hover .tree-ref {
	display: inline-flex;
}

.tree-ref:hover {
	border-color: var(--accent-line);
	color: var(--accent-2);
}

.tree-more {
	margin: 0;
	padding: 3px 8px 5px calc(36px + var(--depth, 0) * 14px);
	color: var(--muted-soft);
	font-size: var(--fs-10);
}

.tree-row-error {
	padding: 4px 8px 6px calc(8px + var(--depth, 0) * 14px);
	text-align: left;
}
</style>
