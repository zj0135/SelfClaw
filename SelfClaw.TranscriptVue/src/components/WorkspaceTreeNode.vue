<script setup>
import { computed } from 'vue';

defineOptions({
	name: 'WorkspaceTreeNode',
});

const props = defineProps({
	node: {
		type: Object,
		required: true,
	},
	depth: {
		type: Number,
		default: 0,
	},
});

const emit = defineEmits(['toggle-directory', 'open-file', 'show-context-menu']);

const rowStyle = computed(() => ({
	paddingLeft: `${12 + props.depth * 16}px`,
}));

function formatSize(sizeBytes) {
	const size = Number(sizeBytes || 0);
	if (!Number.isFinite(size) || size <= 0) {
		return '';
	}

	if (size >= 1024 * 1024) {
		return `${(size / (1024 * 1024)).toFixed(size >= 10 * 1024 * 1024 ? 0 : 1)} MB`;
	}

	if (size >= 1024) {
		return `${Math.max(1, Math.round(size / 1024))} KB`;
	}

	return `${size} B`;
}

function onActivate() {
	if (props.node.isDirectory) {
		emit('toggle-directory', props.node.path);
		return;
	}

	emit('open-file', props.node.path);
}

function onContextMenu(event) {
	emit('show-context-menu', {
		path: props.node.path,
		isDirectory: Boolean(props.node.isDirectory),
		x: event.clientX,
		y: event.clientY,
	});
}
</script>

<template>
	<div class="workspace-tree-node">
		<button
			class="workspace-tree-row workspace-tree-row-button"
			:class="{ 'workspace-tree-row-file': !node.isDirectory }"
			type="button"
			:style="rowStyle"
			:aria-expanded="node.isDirectory ? (node.isExpanded ? 'true' : 'false') : undefined"
			aria-haspopup="menu"
			@click="onActivate"
			@contextmenu.prevent="onContextMenu"
		>
			<span
				v-if="node.isDirectory"
				class="workspace-tree-chevron"
				:class="{ open: node.isExpanded }"
				aria-hidden="true"
			>
				<svg viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
					<path d="m4 2 4 4-4 4" />
				</svg>
			</span>
			<span v-else class="workspace-tree-spacer" aria-hidden="true"></span>

			<span class="workspace-tree-icon" :class="{ 'workspace-tree-icon-file': !node.isDirectory }" aria-hidden="true">
				<svg
					v-if="node.isDirectory"
					viewBox="0 0 18 18"
					fill="none"
					stroke="currentColor"
					stroke-width="1.4"
					stroke-linecap="round"
					stroke-linejoin="round"
				>
					<path d="M1.75 5.25h5l1.4 1.6h8.1v6.9a1 1 0 0 1-1 1H2.75a1 1 0 0 1-1-1v-7.6a1 1 0 0 1 1-1Z" />
				</svg>
				<svg
					v-else
					viewBox="0 0 18 18"
					fill="none"
					stroke="currentColor"
					stroke-width="1.4"
					stroke-linecap="round"
					stroke-linejoin="round"
				>
					<path d="M5.25 1.75h5.5l3.5 3.5v10a1 1 0 0 1-1 1h-8a1 1 0 0 1-1-1v-12a1 1 0 0 1 1-1Z" />
					<path d="M10.75 1.75v3.5h3.5" />
				</svg>
			</span>

			<span class="workspace-tree-name">{{ node.name }}</span>
			<span v-if="!node.isDirectory && formatSize(node.sizeBytes)" class="workspace-tree-meta">{{ formatSize(node.sizeBytes) }}</span>
		</button>

		<div v-if="node.isDirectory && node.isExpanded" class="workspace-tree-children">
			<div v-if="node.isLoading" class="workspace-tree-inline-state">正在加载...</div>
			<div v-else-if="node.loadError" class="workspace-tree-inline-state error">{{ node.loadError }}</div>
			<div v-else-if="node.isLoaded && node.children.length === 0" class="workspace-tree-inline-state">空目录</div>
			<template v-else>
				<WorkspaceTreeNode
					v-for="child in node.children"
					:key="child.path"
					:node="child"
					:depth="depth + 1"
					@toggle-directory="emit('toggle-directory', $event)"
					@open-file="emit('open-file', $event)"
					@show-context-menu="emit('show-context-menu', $event)"
				/>
			</template>
		</div>
	</div>
</template>
