<script setup>
import { Info, Loader2 } from 'lucide-vue-next';
import { useDirectCompression } from '../../composables/useDirectCompression.js';
import CompressionToggle from './compression/CompressionToggle.vue';
import ThresholdField from './compression/ThresholdField.vue';
import AbsoluteThresholdField from './compression/AbsoluteThresholdField.vue';
import CompressionModelSelect from './compression/CompressionModelSelect.vue';

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
	<main class="compression-view sc-root sc-stage sc-page">
		<header class="sc-page-head sc-rise" style="--i: 0">
			<span class="sc-page-ghost" aria-hidden="true">Compress</span>
			<div>
				<span class="sc-page-kicker">DIRECT CONTEXT WINDOW</span>
				<h1 class="sc-page-title">上下文压缩</h1>
				<p class="sc-page-sub">长会话越过阈值时，把较早的历史压成一段可展开的摘要，再接上未受影响的近期内容。</p>
			</div>
			<span class="sync-state" :data-saving="saving ? 'true' : 'false'">
				<Loader2 v-if="saving" :size="12" :stroke-width="2" class="sync-spin" aria-hidden="true" />
				{{ saving ? '保存中' : loading ? '加载中' : '已同步' }}
			</span>
		</header>

		<div class="sc-page-body">
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

			<section class="comp-note sc-rise" style="--i: 4">
				<Info :size="15" :stroke-width="2" class="note-icon" aria-hidden="true" />
				<div>
					<p class="note-title">计数不可用时，本回合不压缩</p>
					<p class="note-body">
						是否越过阈值由本地分词器给出的 token 计数决定。计数不可用（分词器缺失或计数失败）时，本回合按完整历史发送、不压缩，也不会退回按字节估算——回合不会因此失败，开关看起来“没有生效”属于预期行为。
					</p>
				</div>
			</section>
		</div>
	</main>
</template>

<style scoped>
@import '../../styles/settings-console.css';

.compression-view {
	letter-spacing: 0;
}

.compression-view * {
	box-sizing: border-box;
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
	padding: 4px 18px 6px;
	border: 1px solid var(--sc-line);
	border-radius: 14px;
	background: var(--sc-panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.05);
}

.comp-note {
	display: flex;
	gap: 11px;
	margin-top: 18px;
	padding: 14px 16px;
	border: 1px solid var(--sc-line);
	border-left: 2px solid color-mix(in srgb, var(--sc-acid) 55%, transparent);
	border-radius: 10px;
	background: color-mix(in srgb, var(--sc-bg) 60%, transparent);
}

.note-icon {
	flex: 0 0 auto;
	margin-top: 1px;
	color: var(--sc-acid);
}

.note-title {
	margin: 0 0 5px;
	color: var(--sc-text);
	font-size: var(--fs-12);
	font-weight: 620;
}

.note-body {
	margin: 0;
	max-width: 72ch;
	color: var(--sc-mute);
	font-size: var(--fs-115);
	line-height: 1.75;
}
</style>
