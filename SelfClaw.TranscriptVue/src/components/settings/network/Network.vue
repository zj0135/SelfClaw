<script setup>
import { computed } from 'vue';
import { Loader2 } from 'lucide-vue-next';
import { PROXY_MODES, formatEndpoint, useNetworkProxy } from '../../../composables/useNetworkProxy.js';

const { state, loading, saving, error, setMode, setEndpoint } = useNetworkProxy();

const showEndpoint = computed(() => state.value.mode === 'Custom');
const endpointValue = computed(() => formatEndpoint(state.value));

function handleModeChange(event) {
	setMode(event.target.value);
}

function handleEndpointChange(event) {
	setEndpoint(event.target.value);
}
</script>

<template>
	<div class="network-section">
		<p v-if="error" class="net-error" role="alert">{{ error }}</p>

		<div v-if="loading" class="net-loading" role="status">
			<Loader2 :size="14" :stroke-width="2" class="sync-spin" aria-hidden="true" />
			正在加载网络代理设置…
		</div>

		<div v-else class="net-row">
			<div class="row-meta">
				<span class="row-label">网络代理</span>
			</div>
			<div class="row-control">
				<input v-if="showEndpoint" class="net-input" type="text"
					placeholder="127.0.0.1:7890" :value="endpointValue"
					aria-label="代理地址与端口" @change="handleEndpointChange" />
				<select class="net-select" :value="state.mode" aria-label="选择网络代理" :disabled="saving"
					@change="handleModeChange">
					<option v-for="option in PROXY_MODES" :key="option.id" :value="option.id">
						{{ option.label }}
					</option>
				</select>
			</div>
		</div>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.network-section {
	display: flex;
	flex-direction: column;
}

.sync-spin {
	animation: sc-spin 1.1s linear infinite;
}

.net-error {
	margin: 0 0 16px;
	padding: 10px 12px;
	border: 1px solid color-mix(in srgb, var(--sc-err) 32%, transparent);
	border-radius: 8px;
	background: var(--sc-err-soft);
	color: var(--sc-err);
	font-size: var(--fs-115);
}

.net-loading {
	display: inline-flex;
	align-items: center;
	gap: 8px;
	color: var(--sc-mute);
	font-size: var(--fs-12);
	padding: 14px 0;
}

.net-row {
	display: grid;
	grid-template-columns: minmax(0, 1fr) minmax(0, auto);
	align-items: center;
	gap: 18px;
	padding: 14px 0;
	border-bottom: 1px solid var(--sc-line);
}

.row-meta {
	display: flex;
	flex-direction: column;
	min-width: 0;
}

.row-label {
	color: var(--sc-text);
	font-size: var(--fs-13);
	font-weight: 600;
}

.row-control {
	display: flex;
	align-items: center;
	justify-content: flex-end;
	gap: 8px;
	min-width: 0;
}

.net-select {
	min-width: 200px;
	padding: 7px 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-125);
	cursor: pointer;
	transition:
		border-color 0.16s,
		box-shadow 0.16s;
}

.net-select:hover {
	border-color: var(--sc-faint);
}

.net-select:focus-visible {
	outline: none;
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.net-input {
	width: 180px;
	height: 34px;
	padding: 0 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-12);
	font-variant-numeric: tabular-nums;
	transition:
		border-color 0.16s,
		box-shadow 0.16s;
}

.net-input:hover {
	border-color: var(--sc-faint);
}

.net-input:focus-visible {
	outline: none;
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

@media (max-width: 720px) {
	.net-row {
		grid-template-columns: minmax(0, 1fr);
		gap: 10px;
	}

	.row-control {
		justify-content: flex-start;
		flex-wrap: wrap;
	}
}
</style>
