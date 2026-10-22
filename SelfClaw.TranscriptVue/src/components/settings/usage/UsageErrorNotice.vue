<script setup>
import { RefreshCw, TriangleAlert } from 'lucide-vue-next';

defineProps({
	message: { type: String, default: '' },
});
defineEmits(['retry']);
</script>

<template>
	<section class="usage-card error-card" role="alert">
		<div class="error-icon">
			<TriangleAlert :size="18" :stroke-width="1.8" aria-hidden="true" />
		</div>
		<div class="error-copy">
			<span class="usage-kicker">Error</span>
			<h3 class="usage-card-title">用量统计加载失败</h3>
			<p class="error-message">{{ message }}</p>
		</div>
		<button type="button" class="error-retry" @click="$emit('retry')">
			<RefreshCw :size="13" :stroke-width="2" aria-hidden="true" />
			<span>重试</span>
		</button>
	</section>
</template>

<style scoped>
@import '../../../styles/usage-dashboard.css';

.error-card {
	display: flex;
	align-items: flex-start;
	gap: 14px;
	border-color: color-mix(in srgb, var(--sc-err) 34%, var(--sc-line));
}

.error-icon {
	display: grid;
	place-items: center;
	width: 34px;
	height: 34px;
	border: 1px solid color-mix(in srgb, var(--sc-err) 30%, transparent);
	border-radius: 9px;
	background: var(--sc-err-soft);
	color: var(--sc-err);
}

.error-copy {
	display: flex;
	flex: 1;
	flex-direction: column;
	gap: 6px;
	min-width: 0;
}

.error-message {
	margin: 0;
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-11);
	line-height: 1.6;
	overflow-wrap: anywhere;
}

.error-retry {
	display: inline-flex;
	flex: none;
	align-items: center;
	gap: 6px;
	min-height: 28px;
	padding: 5px 11px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-11);
	font-weight: 600;
	cursor: pointer;
	transition:
		border-color 0.16s,
		background 0.16s;
}

.error-retry:hover {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
}

.error-retry:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 2px;
}
</style>
