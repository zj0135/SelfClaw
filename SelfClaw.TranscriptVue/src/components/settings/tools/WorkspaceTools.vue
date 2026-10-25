<script setup>
import { Loader2 } from 'lucide-vue-next';
import { useWorkspaceTools } from '../../../composables/useWorkspaceTools.js';
import ShellSelect from './ShellSelect.vue';

const { state, loading, saving, error, selectShell } = useWorkspaceTools();
</script>

<template>
	<div class="workspace-tools-section">
		<div class="tools-desc-row">
			<p class="tools-desc">Direct 模式下代理与子代理执行 run_shell_command 使用的 shell，切换后下一回合生效；CLI 模式不受影响。</p>
			<span class="sync-state" :data-saving="saving ? 'true' : 'false'">
				<Loader2 v-if="saving || loading" :size="12" :stroke-width="2" class="sync-spin" aria-hidden="true" />
				{{ saving ? '保存中' : loading ? '加载中' : '已同步' }}
			</span>
		</div>

		<p v-if="error" class="tools-error" role="alert">{{ error }}</p>

		<div v-if="loading" class="tools-loading" role="status">
			<Loader2 :size="14" :stroke-width="2" class="sync-spin" aria-hidden="true" />
			正在加载命令执行工具设置…
		</div>

		<ShellSelect v-else :shell-kind="state.shellKind" :shells="state.shells" @select="selectShell" />
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.workspace-tools-section {
	display: flex;
	flex-direction: column;
}

.tools-desc-row {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 16px;
	margin-bottom: 12px;
}

.tools-desc {
	margin: 0;
	color: var(--sc-mute);
	font-size: var(--fs-12);
	line-height: 1.5;
}

.sync-state {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	padding: 4px 10px;
	border: 1px solid var(--sc-line);
	border-radius: 999px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.12em;
	white-space: nowrap;
}

.sync-state[data-saving='true'] {
	border-color: color-mix(in srgb, var(--sc-acid) 40%, transparent);
	color: var(--sc-acid);
}

.sync-spin {
	animation: sc-spin 1.1s linear infinite;
}

.tools-error {
	margin: 0 0 16px;
	padding: 10px 12px;
	border: 1px solid color-mix(in srgb, var(--sc-err) 32%, transparent);
	border-radius: 8px;
	background: var(--sc-err-soft);
	color: var(--sc-err);
	font-size: var(--fs-115);
}

.tools-loading {
	display: inline-flex;
	align-items: center;
	gap: 8px;
	color: var(--sc-mute);
	font-size: var(--fs-12);
}
</style>
