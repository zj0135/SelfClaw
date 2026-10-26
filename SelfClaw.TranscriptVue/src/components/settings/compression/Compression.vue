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
		<p v-if="error" class="comp-error" role="alert">{{ error }}</p>

		<div v-if="loading" class="comp-loading" role="status">
			<Loader2 :size="14" :stroke-width="2" class="sync-spin" aria-hidden="true" />
			正在加载上下文压缩设置…
		</div>

		<section v-else class="comp-panel">
			<CompressionToggle :enabled="policy.enabled" :index="0" @update:enabled="setEnabled" />
			<div class="comp-sub-fields" :class="{ 'is-disabled': !policy.enabled }">
				<ThresholdField :ratio="policy.thresholdRatio" :index="1" @update:ratio="setThresholdRatio" />
				<AbsoluteThresholdField :value="policy.absoluteThresholdTokens" :index="2"
					@update:value="setAbsoluteThresholdTokens" />
				<CompressionModelSelect :model-profile-id="policy.modelProfileId" :models="models" :loading="modelsLoading"
					:error="modelsError" :index="3" @update:model-profile-id="setModelProfileId" />
			</div>
		</section>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.compression-section {
	display: flex;
	flex-direction: column;
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
	padding: 2px 16px;
	border: 1px solid var(--sc-line-2);
	border-radius: 12px;
	background: var(--sc-panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.04);
}

.comp-sub-fields {
	display: flex;
	flex-direction: column;
	transition: opacity 0.2s var(--sc-ease-out);
}

.comp-sub-fields.is-disabled {
	opacity: 0.45;
	pointer-events: none;
}
</style>
