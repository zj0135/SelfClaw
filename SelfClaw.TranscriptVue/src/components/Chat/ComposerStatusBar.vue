<script setup>
import { computed } from 'vue';
import { Monitor, Waypoints } from 'lucide-vue-next';
import ComposerWorkspacePicker from './ComposerWorkspacePicker.vue';
import GitWorkspaceControl from './GitWorkspaceControl.vue';

const props = defineProps({
	workspaceSelection: { type: Object, default: () => ({}) },
	workspaceMode: { type: String, default: 'local' },
	gitLoading: { type: Boolean, default: false },
	gitError: { type: String, default: '' },
});

const emit = defineEmits([
	'refresh',
	'select-root',
	'browse',
	'delete-root',
	'update:workspace-mode',
	'git-action',
]);

const gitState = computed(() => props.workspaceSelection?.current?.git || null);
const isGitRepository = computed(() => Boolean(gitState.value?.isRepository));
const isManagedWorktree = computed(() => Boolean(gitState.value?.isManagedWorktree));
const effectiveMode = computed(() => (isManagedWorktree.value ? 'worktree' : props.workspaceMode));

function setWorkspaceMode(mode) {
	if (isManagedWorktree.value || !isGitRepository.value) return;
	emit('update:workspace-mode', mode);
}
</script>

<template>
	<div class="composer-statusbar" aria-label="工作区上下文">
		<ComposerWorkspacePicker :workspace-selection="workspaceSelection" @refresh="emit('refresh', $event)"
			@select-root="emit('select-root', $event)" @browse="emit('browse')"
			@delete-root="emit('delete-root', $event)" />

		<div v-if="isGitRepository" class="workspace-mode" aria-label="工作区模式">
			<button class="mode-option" :class="{ active: effectiveMode === 'local' }" type="button"
				:disabled="isManagedWorktree" @click="setWorkspaceMode('local')">
				<Monitor :size="12" :stroke-width="1.9" aria-hidden="true" />
				本地
			</button>
			<button class="mode-option" :class="{ active: effectiveMode === 'worktree' }" type="button"
				:disabled="isManagedWorktree" @click="setWorkspaceMode('worktree')">
				<Waypoints :size="12" :stroke-width="1.9" aria-hidden="true" />
				工作树
			</button>
		</div>
		<span v-else class="local-context">
			<Monitor :size="13" :stroke-width="1.8" aria-hidden="true" />
			本地目录
		</span>

		<GitWorkspaceControl v-if="isGitRepository" :state="gitState" :loading="gitLoading" :error="gitError"
			@action="emit('git-action', $event)" />
	</div>

	<p v-if="effectiveMode === 'worktree' && gitState?.isDirty && !isManagedWorktree" class="dirty-warning">
		当前未提交更改不会进入新工作树
	</p>
</template>

<style scoped>
.composer-statusbar {
	position: relative;
	display: flex;
	align-items: center;
	gap: 12px;
	height: 36px;
	margin-top: -1px;
	padding: 0 12px;
	border: 1px solid color-mix(in srgb, var(--text) 10%, transparent);
	border-top-color: var(--line-2);
	border-radius: 0 0 16px 16px;
	background: var(--panel);
	box-shadow: 0 8px 24px rgba(var(--shadow-ink), 0.04);
	clip-path: inset(-420px -48px -48px -48px);
	transition: border-color 0.18s, box-shadow 0.18s;
}

:global(.composer-shell:focus-within + .composer-statusbar) {
	border-color: color-mix(in srgb, var(--accent) 45%, transparent);
	border-top-color: var(--line-2);
	box-shadow: 0 12px 32px rgba(var(--shadow-ink), 0.07), 0 0 0 3px color-mix(in srgb, var(--accent) 10%, transparent);
}

.workspace-mode {
	display: inline-grid;
	grid-template-columns: 1fr 1fr;
	height: 26px;
	padding: 2px;
	border: 1px solid var(--border);
	border-radius: 7px;
	background: var(--bg-canvas);
}

.mode-option {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 4px;
	min-width: 58px;
	padding: 0 7px;
	border: 0;
	border-radius: 5px;
	background: transparent;
	color: var(--muted);
	font: 500 11.5px/1 var(--font-ui);
	cursor: pointer;
}

.mode-option.active {
	background: var(--panel);
	color: var(--text-strong);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.1);
}

.mode-option:disabled {
	cursor: default;
}

.local-context {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	color: var(--muted);
	font-size: var(--fs-12);
}

.dirty-warning {
	position: absolute;
	right: 12px;
	bottom: -25px;
	z-index: 4;
	margin: 0;
	color: var(--caution-fill);
	font-size: var(--fs-115);
	line-height: 20px;
}

@media (max-width: 700px) {
	.composer-statusbar {
		gap: 7px;
		padding-inline: 8px;
	}

	.mode-option {
		min-width: 38px;
		padding-inline: 5px;
	}

	.mode-option svg {
		display: none;
	}
}
</style>
