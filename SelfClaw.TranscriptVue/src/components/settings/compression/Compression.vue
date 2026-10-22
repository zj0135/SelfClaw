<script setup>
import { Loader2 } from 'lucide-vue-next';
import { useDirectCompression } from '../../../composables/useDirectCompression.js';
import CompressionToggle from './CompressionToggle.vue';
import ThresholdField from './ThresholdField.vue';
import AbsoluteThresholdField from './AbsoluteThresholdField.vue';
import CompressionModelSelect from './CompressionModelSelect.vue';

const {
	policy,
	models,
	loading,
	modelsLoading,
	saving,
	error,
	modelsError,
	setEnabled,
	setThresholdRatio,
	setAbsoluteThresholdTokens,
	setModelProfileId,
} = useDirectCompression();
</script>

<template>
	<div class="compression-section">
		<div class="comp-desc-row">
			<span class="sync-state" :data-saving="saving ? 'true' : 'false'">
				<Loader2 v-if="saving" :size="12" :stroke-width="2" class="sync-spin" aria-hidden="true" />
				{{ saving ? '保存中' : loading ? '加载中' : '已同步' }}
			</span>
		</div>

		<p v-if="error" class="comp-error" role="alert">{{ error }}</p>

		<div v-if="loading" class="comp-loading" role="status">
			<Loader2 :size="14" :stroke-width="2" class="sync-spin" aria-hidden="true" />
			正在加载上下文压缩设置…
		</div>

		<section v-else class="comp-panel">
			<CompressionToggle :enabled="policy.enabled" :index="0" @update:enabled="setEnabled" />
			<ThresholdField :ratio="policy.thresholdRatio" :index="1" @update:ratio="setThresholdRatio" />
			<AbsoluteThresholdField :value="policy.absoluteThresholdTokens" :index="2"
				@update:value="setAbsoluteThresholdTokens" />
			<CompressionModelSelect :model-profile-id="policy.modelProfileId" :models="models" :loading="modelsLoading"
				:error="modelsError" :index="3" @update:model-profile-id="setModelProfileId" />
		</section>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.compression-section {
	display: flex;
	flex-direction: column;
}

.comp-desc-row {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 16px;
	margin-bottom: 12px;
}

.comp-desc {
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

.comp-error {
	margin: 0 0 16px;
	padding: 10px 12px;
	border: 1px solid color-mix(in srgb, var(--sc-err) 32%, transparent);
	border-radius: 8px;
	background: var(--sc-err-soft);
	color: var(--sc-err);
	font-size: var(--fs-115);
}

.comp-loading {
	display: inline-flex;
	align-items: center;
	gap: 8px;
	color: var(--sc-mute);
	font-size: var(--fs-12);
}

.comp-panel {
	display: flex;
	flex-direction: column;
}
</style>
