<script setup>
import { Loader2 } from 'lucide-vue-next';
import { useWorkspaceTools } from '../../../composables/useWorkspaceTools.js';
import ShellSelect from './ShellSelect.vue';

const { state, loading, saving, error, selectShell } = useWorkspaceTools();
</script>

<template>
	<div class="workspace-tools-section">
		<p v-if="error" class="tools-error" role="alert">{{ error }}</p>

		<div v-if="loading" class="tools-loading" role="status">
			<Loader2 :size="14" :stroke-width="2" class="sync-spin" aria-hidden="true" />
			正在加载命令执行工具设置…
		</div>

		<ShellSelect v-else :shell-kind="state.shellKind" :shells="state.shells" :saving="saving"
			@select="selectShell" />
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.workspace-tools-section {
	display: flex;
	flex-direction: column;
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
