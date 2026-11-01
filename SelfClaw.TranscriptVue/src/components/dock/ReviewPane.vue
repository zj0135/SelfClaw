<script setup>
import { computed } from 'vue';
import { ChevronDown, ChevronUp, FileText, GitBranch, PanelRightClose, PanelRightOpen, RefreshCw } from 'lucide-vue-next';
import ReviewDiffView from './review/ReviewDiffView.vue';
import ReviewFileList from './review/ReviewFileList.vue';
import { useHostBridge } from '../../composables/hostBridge.js';
import { useGitDiff } from '../../composables/useGitDiff.js';
import { useReviewLayout } from '../../composables/useReviewLayout.js';

const props = defineProps({
	// 当前选中的 workspace 根 id：为空表示还没选工作目录，不向宿主发请求。
	workspaceRootId: { type: String, default: null },
	// dock 打开且停留在审查 tab 时为 true：变为可见即刷新，隐藏时不发请求。
	visible: { type: Boolean, default: false },
});

const bridge = useHostBridge();
const diff = useGitDiff(computed(() => props.workspaceRootId), computed(() => props.visible), bridge);
const { filesCollapsed, toggleFiles } = useReviewLayout();
const state = diff.state;
const files = diff.files;
const hasPrevious = diff.hasPrevious;
const hasNext = diff.hasNext;
const currentSet = computed(() => (state.scope === 'staged' ? state.staged : state.unstaged));
const emptyState = computed(() => {
	if (!props.workspaceRootId) return { title: '尚未选择工作目录', note: '在输入框下方选择一个目录后，这里会展示它的 Git 改动。' };
	if (!state.loadedOnce) return null;
	if (!state.isGitAvailable) return { title: '未找到 git.exe', note: '请安装 Git 并确保 git.exe 在 PATH 中，然后刷新。' };
	if (!state.isRepository) return { title: '当前 workspace 不在 Git 仓库中', note: '打开一个 Git 仓库目录后，这里会展示当前 workspace 作用域内的改动。' };
	if (!files.value.length) return { title: state.scope === 'staged' ? '没有已暂存的改动' : '没有未暂存的改动', note: '工作区很干净。' };
	return null;
});
</script>

<template>
	<div class="review-pane" aria-label="审查">
		<div class="pane-toolbar">
			<select class="scope-select" :value="state.scope" aria-label="改动范围" @change="diff.setScope($event.target.value)">
				<option value="unstaged" :data-count="state.unstaged.files.length">未暂存 ({{ state.unstaged.files.length }})</option>
				<option value="staged" :data-count="state.staged.files.length">已暂存 ({{ state.staged.files.length }})</option>
			</select>
			<span v-if="state.branchName" class="pane-branch" :title="state.repositoryRootPath">
				<GitBranch :size="12" :stroke-width="1.9" aria-hidden="true" />
				<span>{{ state.branchName }}</span>
			</span>
			<div class="pane-actions">
				<button class="pane-icon pane-step-prev" type="button" title="上一个文件" aria-label="上一个文件" :disabled="!hasPrevious"
					@click="diff.selectNeighbor(-1)">
					<ChevronUp :size="15" :stroke-width="1.9" />
				</button>
				<button class="pane-icon pane-step-next" type="button" title="下一个文件" aria-label="下一个文件" :disabled="!hasNext"
					@click="diff.selectNeighbor(1)">
					<ChevronDown :size="15" :stroke-width="1.9" />
				</button>
				<i class="pane-divider" aria-hidden="true"></i>
				<button class="pane-icon pane-refresh" type="button" title="刷新" aria-label="刷新改动" :disabled="state.loading || !workspaceRootId"
					@click="diff.refresh()">
					<RefreshCw :size="14" :class="{ spin: state.loading }" />
				</button>
				<button class="pane-icon pane-collapse-files" type="button" :title="filesCollapsed ? '展开文件列表' : '折叠文件列表'"
					:aria-label="filesCollapsed ? '展开文件列表' : '折叠文件列表'" :aria-pressed="filesCollapsed" @click="toggleFiles()">
					<component :is="filesCollapsed ? PanelRightOpen : PanelRightClose" :size="14" :stroke-width="1.8" />
				</button>
			</div>
		</div>
		<p v-if="state.error" class="pane-error" role="alert">{{ state.error }}</p>
		<div v-if="workspaceRootId && !state.loadedOnce && state.loading" class="pane-empty" role="status">加载中...</div>
		<div v-else-if="emptyState" class="review-empty" role="status">
			<FileText :size="36" :stroke-width="1.4" aria-hidden="true" />
			<strong>{{ emptyState.title }}</strong>
			<p>{{ emptyState.note }}</p>
		</div>
		<div v-else-if="state.loadedOnce" class="review-body" :class="{ 'files-collapsed': filesCollapsed }">
			<ReviewDiffView class="review-diff" :file="state.file" :loading="state.fileLoading" :error="state.fileError"
				:selected-path="state.selectedPath" />
			<ReviewFileList v-show="!filesCollapsed" class="review-files" :files="files" :selected-path="state.selectedPath"
				:at-file-limit="currentSet.atFileLimit" @select="diff.selectFile" @step="diff.selectNeighbor" />
		</div>
	</div>
</template>

<style scoped>
.review-pane {
	display: flex;
	flex-direction: column;
	min-height: 0;
	overflow: hidden;
}

.pane-toolbar {
	display: flex;
	flex: 0 0 38px;
	align-items: center;
	gap: 8px;
	padding: 0 6px 0 8px;
	border-bottom: 1px solid var(--border);
}

.scope-select {
	flex: none;
	width: 112px;
	height: 26px;
	padding: 0 6px;
	border: 1px solid var(--border-strong);
	border-radius: 6px;
	background: var(--panel);
	color: var(--text);
	font-family: inherit;
	font-size: var(--fs-12);
	font-weight: 400;
	cursor: pointer;
}

.scope-select:hover {
	border-color: var(--muted-soft);
}

.scope-select:focus-visible {
	outline: none;
	border-color: var(--accent);
	box-shadow: 0 0 0 3px var(--accent-soft);
}

.pane-branch {
	display: inline-flex;
	flex: 1 1 auto;
	align-items: center;
	gap: 4px;
	min-width: 0;
	color: var(--muted);
	font-size: var(--fs-105);
}

.pane-branch span {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.pane-actions {
	display: inline-flex;
	flex: none;
	align-items: center;
	gap: 2px;
	margin-left: auto;
}

.pane-divider {
	width: 1px;
	height: 14px;
	margin: 0 3px;
	background: var(--border);
}

.pane-icon {
	display: grid;
	flex: none;
	place-items: center;
	width: 24px;
	height: 24px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--muted);
	transition: background 0.14s, color 0.14s;
}

.pane-icon:hover:not(:disabled) {
	background: var(--panel-hover);
	color: var(--text);
}

.pane-icon:disabled {
	opacity: 0.4;
	cursor: default;
}

.pane-collapse-files[aria-pressed='true'] {
	background: var(--accent-soft);
	color: var(--accent);
}

.pane-error {
	margin: 0;
	padding: 8px 10px;
	border-bottom: 1px solid var(--border);
	color: var(--danger);
	font-size: var(--fs-11);
	overflow-wrap: anywhere;
}

.pane-empty {
	padding: 18px 12px;
	color: var(--muted);
	font-size: var(--fs-12);
}

.review-empty {
	display: flex;
	flex: 1 1 auto;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	gap: 10px;
	padding: 24px 20px;
	color: var(--faint);
	text-align: center;
	animation: review-rise 0.5s var(--ease-out) both;
}

.review-empty strong {
	color: var(--text);
	font-size: var(--fs-13);
	font-weight: 600;
}

.review-empty p {
	max-width: 26em;
	margin: 0;
	color: var(--muted);
	font-size: var(--fs-12);
	line-height: 1.6;
}

.review-body {
	display: grid;
	flex: 1 1 auto;
	grid-template-columns: minmax(0, 1fr) clamp(168px, 38%, 240px);
	min-height: 0;
}

.review-body.files-collapsed {
	grid-template-columns: minmax(0, 1fr);
}

.review-diff {
	min-width: 0;
	min-height: 0;
}

.review-files {
	min-height: 0;
	border-left: 1px solid var(--border);
}

.spin {
	animation: review-spin 1s linear infinite;
}

@keyframes review-spin {
	to {
		transform: rotate(360deg);
	}
}

@keyframes review-rise {
	from {
		opacity: 0;
		transform: translateY(10px);
	}

	to {
		opacity: 1;
		transform: none;
	}
}

@media (prefers-reduced-motion: reduce) {
	.spin,
	.review-empty {
		animation: none;
	}
}
</style>
