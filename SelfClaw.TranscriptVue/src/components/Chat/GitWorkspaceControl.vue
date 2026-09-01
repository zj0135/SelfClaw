<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue';
import {
	AlertTriangle,
	Check,
	ChevronDown,
	GitBranch,
	GitMerge,
	GitPullRequestArrow,
	LoaderCircle,
	Plus,
	RefreshCw,
	RotateCcw,
	Trash2,
	Waypoints,
} from 'lucide-vue-next';

const props = defineProps({
	state: { type: Object, default: null },
	loading: { type: Boolean, default: false },
	error: { type: String, default: '' },
});

const emit = defineEmits(['action']);
const isOpen = ref(false);
const activeTab = ref('branches');
const branchName = ref('');
const rootRef = ref(null);
const branchInputRef = ref(null);

const localBranches = computed(() => (props.state?.branches || []).filter((branch) => !branch.isRemote));
const worktrees = computed(() => props.state?.worktrees || []);
const branchLabel = computed(() => props.state?.branchName || 'detached HEAD');

async function toggle() {
	isOpen.value = !isOpen.value;
	if (!isOpen.value) return;
	emit('action', { type: 'refresh' });
	await nextTick();
}

function createBranch() {
	const value = branchName.value.trim();
	if (!value || props.loading) return;
	emit('action', { type: 'create-branch', branchName: value });
	branchName.value = '';
}

function switchBranch(branch) {
	if (branch.isCurrent || branch.checkoutPath || props.loading) return;
	emit('action', { type: 'switch-branch', branchName: branch.name });
}

function deleteBranch(branch) {
	if (branch.isCurrent || branch.checkoutPath || props.loading) return;
	if (!window.confirm(`确认安全删除本地分支“${branch.name}”？未合并分支不会被删除。`)) return;
	emit('action', { type: 'delete-branch', branchName: branch.name });
}

function mergeWorktree() {
	if (props.loading || !window.confirm(`确认将“${branchLabel.value}”合并到“${props.state?.baseBranchName}”？`)) return;
	emit('action', { type: 'merge' });
}

function abortMerge() {
	if (props.loading && !props.state?.hasMergeConflicts) return;
	emit('action', { type: 'abort-merge' });
}

function onDocumentPointerDown(event) {
	if (isOpen.value && !rootRef.value?.contains(event.target)) isOpen.value = false;
}

onMounted(() => document.addEventListener('pointerdown', onDocumentPointerDown));
onUnmounted(() => document.removeEventListener('pointerdown', onDocumentPointerDown));
</script>

<template>
	<div ref="rootRef" class="git-control">
		<button class="git-trigger" :class="{ active: isOpen, conflict: state?.hasMergeConflicts }" type="button"
			:title="branchLabel" :aria-expanded="isOpen" @click.stop="toggle">
			<GitBranch :size="13" :stroke-width="1.9" aria-hidden="true" />
			<span>{{ branchLabel }}</span>
			<i v-if="state?.isDirty" class="dirty-dot" title="存在未提交更改"></i>
			<LoaderCircle v-if="loading" class="spin" :size="12" aria-hidden="true" />
			<ChevronDown v-else :class="{ open: isOpen }" :size="12" :stroke-width="2" aria-hidden="true" />
		</button>

		<div v-if="isOpen" class="git-menu" role="dialog" aria-label="Git 管理">
			<header class="git-head">
				<div>
					<strong>{{ state?.repositoryName }}</strong>
					<span v-if="state?.aheadCount != null || state?.behindCount != null">
						领先 {{ state?.aheadCount || 0 }} · 落后 {{ state?.behindCount || 0 }}
					</span>
				</div>
				<button class="icon-action" type="button" title="刷新" :disabled="loading"
					@click="emit('action', { type: 'refresh' })">
					<RefreshCw :class="{ spin: loading }" :size="14" aria-hidden="true" />
				</button>
			</header>

			<div v-if="state?.hasMergeConflicts" class="conflict-banner" role="alert">
				<AlertTriangle :size="15" aria-hidden="true" />
				<span>基础分支存在合并冲突</span>
				<button type="button" @click="abortMerge">
					<RotateCcw :size="13" aria-hidden="true" />
					中止合并
				</button>
			</div>
			<p v-else-if="error" class="git-error" role="alert">{{ error }}</p>

			<nav class="git-tabs" aria-label="Git 视图">
				<button :class="{ active: activeTab === 'branches' }" type="button" @click="activeTab = 'branches'">
					<GitPullRequestArrow :size="13" aria-hidden="true" />
					分支
				</button>
				<button :class="{ active: activeTab === 'worktrees' }" type="button" @click="activeTab = 'worktrees'">
					<Waypoints :size="13" aria-hidden="true" />
					工作树
				</button>
			</nav>

			<div v-if="activeTab === 'branches'" class="git-body">
				<form class="branch-create" @submit.prevent="createBranch">
					<input ref="branchInputRef" v-model="branchName" type="text" placeholder="新分支名称" />
					<button type="submit" title="创建分支" :disabled="!branchName.trim() || loading">
						<Plus :size="14" aria-hidden="true" />
					</button>
				</form>
				<div class="branch-list">
					<div v-for="branch in localBranches" :key="branch.fullName" class="branch-row">
						<button class="branch-select" type="button"
							:disabled="branch.isCurrent || Boolean(branch.checkoutPath) || loading"
							:title="branch.checkoutPath ? `已在 ${branch.checkoutPath} 检出` : branch.name"
							@click="switchBranch(branch)">
							<Check v-if="branch.isCurrent" :size="14" aria-hidden="true" />
							<GitBranch v-else :size="14" aria-hidden="true" />
							<span>{{ branch.name }}</span>
						</button>
						<button v-if="!branch.isCurrent" class="branch-delete" type="button" title="安全删除分支"
							:disabled="Boolean(branch.checkoutPath) || loading" @click="deleteBranch(branch)">
							<Trash2 :size="13" aria-hidden="true" />
						</button>
					</div>
				</div>
			</div>

			<div v-else class="git-body worktree-list">
				<div v-for="worktree in worktrees" :key="worktree.path" class="worktree-row">
					<Waypoints :size="14" aria-hidden="true" />
					<div>
						<strong>{{ worktree.branchName || 'detached HEAD' }}</strong>
						<span :title="worktree.path">{{ worktree.path }}</span>
					</div>
					<i v-if="worktree.isCurrent">当前</i>
				</div>
				<p v-if="worktrees.length === 0" class="empty-state">没有可用工作树</p>
			</div>

			<footer v-if="state?.isManagedWorktree" class="merge-footer">
				<span>{{ state?.baseBranchName }} ← {{ branchLabel }}</span>
				<button type="button" :disabled="loading || state?.isDirty || state?.hasMergeConflicts"
					@click="mergeWorktree">
					<GitMerge :size="14" aria-hidden="true" />
					合并
				</button>
			</footer>
		</div>
	</div>
</template>

<style scoped>
.git-control {
	position: relative;
	min-width: 0;
	margin-left: auto;
}

.git-trigger {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	max-width: 210px;
	height: 28px;
	padding: 0 6px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--muted);
	font: 500 12px/1 var(--font-ui);
	cursor: pointer;
}

.git-trigger:hover,
.git-trigger.active {
	background: color-mix(in srgb, var(--text) 6%, transparent);
}

.git-trigger.conflict {
	color: var(--err-text);
}

.git-trigger span {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.dirty-dot {
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: var(--caution);
}

.git-menu {
	position: absolute;
	right: -7px;
	bottom: 36px;
	z-index: 92;
	width: min(392px, calc(100vw - 42px));
	border: 1px solid var(--border-strong);
	border-radius: 9px;
	background: var(--panel);
	box-shadow: 0 18px 50px rgba(var(--shadow-ink), 0.14), 0 3px 10px rgba(var(--shadow-ink), 0.05);
	overflow: hidden;
}

.git-head,
.merge-footer {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
	padding: 11px 12px;
}

.git-head>div {
	display: grid;
	gap: 3px;
	min-width: 0;
}

.git-head strong {
	color: var(--text-strong);
	font-size: var(--fs-125);
}

.git-head span,
.merge-footer span {
	color: var(--muted-soft);
	font-size: var(--fs-11);
}

.icon-action,
.branch-create button,
.branch-delete {
	display: inline-grid;
	place-items: center;
	width: 28px;
	height: 28px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--muted);
	cursor: pointer;
}

.icon-action:hover,
.branch-create button:hover,
.branch-delete:hover {
	background: var(--panel-muted);
	color: var(--text-strong);
}

.conflict-banner {
	display: flex;
	align-items: center;
	gap: 7px;
	margin: 0 10px 8px;
	padding: 8px 9px;
	border: 1px solid color-mix(in srgb, var(--danger) 22%, transparent);
	border-radius: 7px;
	background: color-mix(in srgb, var(--danger) 5%, transparent);
	color: var(--err-text);
	font-size: var(--fs-115);
}

.conflict-banner span {
	flex: 1;
}

.conflict-banner button,
.merge-footer button {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	height: 28px;
	padding: 0 9px;
	border: 1px solid var(--border-strong);
	border-radius: 6px;
	background: var(--panel);
	color: var(--text-soft);
	font: 550 11.5px/1 var(--font-ui);
	cursor: pointer;
}

.git-error {
	margin: 0 12px 8px;
	color: var(--err-text);
	font-size: var(--fs-115);
}

.git-tabs {
	display: flex;
	gap: 15px;
	height: 34px;
	padding: 0 12px;
	border-bottom: 1px solid var(--border);
}

.git-tabs button {
	position: relative;
	display: inline-flex;
	align-items: center;
	gap: 5px;
	border: 0;
	background: transparent;
	color: var(--muted-soft);
	font: 550 11.5px/1 var(--font-ui);
	cursor: pointer;
}

.git-tabs button.active {
	color: var(--text-strong);
}

.git-tabs button.active::after {
	position: absolute;
	right: 0;
	bottom: -1px;
	left: 0;
	height: 2px;
	background: var(--fill-strong);
	content: '';
}

.git-body {
	padding: 9px 10px;
}

.branch-create {
	display: flex;
	height: 32px;
	border: 1px solid var(--border);
	border-radius: 7px;
}

.branch-create:focus-within {
	border-color: color-mix(in srgb, var(--accent) 45%, transparent);
}

.branch-create input {
	flex: 1;
	min-width: 0;
	padding: 0 9px;
	border: 0;
	outline: 0;
	background: transparent;
	color: var(--text-strong);
	font: 12px/1 var(--font-ui);
}

.branch-list {
	max-height: 220px;
	margin-top: 7px;
	overflow: auto;
}

.branch-row {
	display: flex;
	align-items: center;
	height: 33px;
	border-radius: 6px;
}

.branch-row:hover {
	background: var(--panel-muted);
}

.branch-select {
	display: flex;
	align-items: center;
	gap: 7px;
	flex: 1;
	min-width: 0;
	height: 100%;
	padding: 0 7px;
	border: 0;
	background: transparent;
	color: var(--text-soft);
	font: 500 12px/1 var(--font-ui);
	text-align: left;
	cursor: pointer;
}

.branch-select:disabled {
	cursor: default;
}

.branch-select span {
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.branch-delete {
	opacity: 0;
}

.branch-row:hover .branch-delete,
.branch-delete:focus-visible {
	opacity: 1;
}

.worktree-list {
	display: grid;
	gap: 3px;
	max-height: 268px;
	overflow: auto;
}

.worktree-row {
	display: grid;
	grid-template-columns: auto minmax(0, 1fr) auto;
	align-items: center;
	gap: 8px;
	padding: 7px;
	border-radius: 6px;
}

.worktree-row:hover {
	background: var(--panel-muted);
}

.worktree-row>div {
	display: grid;
	gap: 3px;
	min-width: 0;
}

.worktree-row strong {
	color: var(--text-soft);
	font-size: var(--fs-12);
}

.worktree-row span {
	overflow: hidden;
	color: var(--muted-soft);
	font-size: var(--fs-105);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.worktree-row i {
	color: var(--muted);
	font-size: var(--fs-105);
	font-style: normal;
}

.empty-state {
	margin: 12px 7px;
	color: var(--faint);
	font-size: var(--fs-12);
}

.merge-footer {
	border-top: 1px solid var(--border);
}

.merge-footer button {
	border-color: var(--fill-strong);
	background: var(--fill-strong);
	color: var(--fill-strong-ink);
}

.merge-footer button:disabled,
button:disabled {
	opacity: 0.45;
	cursor: not-allowed;
}

.spin {
	animation: spin 0.8s linear infinite;
}

@keyframes spin {
	to {
		transform: rotate(360deg);
	}
}
</style>
