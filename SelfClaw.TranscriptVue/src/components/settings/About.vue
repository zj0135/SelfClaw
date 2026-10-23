<script setup>
import { computed } from 'vue';
import { RefreshCw } from 'lucide-vue-next';
import { useUsageOverview } from '../../composables/useUsageOverview.js';
import UsageEmptyState from './usage/UsageEmptyState.vue';
import UsageErrorNotice from './usage/UsageErrorNotice.vue';
import UsageHeatmap from './usage/UsageHeatmap.vue';
import UsageModelRanking from './usage/UsageModelRanking.vue';
import UsageRangeTabs from './usage/UsageRangeTabs.vue';
import UsageSummaryStrip from './usage/UsageSummaryStrip.vue';
import UsageToolRanking from './usage/UsageToolRanking.vue';
import UsageTrendChart from './usage/UsageTrendChart.vue';

const { range, overview, loading, error, refresh } = useUsageOverview();

const hasActivity = computed(() => (overview.value?.lifetime?.summary?.totalTokens ?? 0) > 0);
</script>

<template>
	<main class="about-page sc-root sc-stage sc-page">
		<header class="sc-page-head sc-rise" style="--i: 0">
			<div>
				<h1 class="sc-page-title">应用用量</h1>
			</div>
			<button type="button" class="usage-refresh" :disabled="loading" @click="refresh">
				<RefreshCw :size="14" :stroke-width="2" :class="{ 'is-spinning': loading }" aria-hidden="true" />
				<span>刷新</span>
			</button>
		</header>

		<div class="sc-page-body">
			<section class="usage-section">
				<header class="usage-head sc-rise" style="--i: 1">
					<UsageRangeTabs v-model="range" />
				</header>

				<template v-if="hasActivity">
					<UsageSummaryStrip :summary="overview.lifetime.summary" class="sc-rise" style="--i: 2" />
					<UsageHeatmap :days="overview.lifetime.heatmap.days" class="sc-rise" style="--i: 3" />
					<div class="usage-grid sc-rise" style="--i: 4">
						<UsageTrendChart :daily="overview.rangeData.daily" :models="overview.rangeData.models" :range="range" />
						<UsageModelRanking :models="overview.rangeData.models" />
					</div>
					<UsageToolRanking v-if="overview.rangeData.tools?.length" :tools="overview.rangeData.tools"
						class="sc-rise" style="--i: 5" />
				</template>
				<UsageErrorNotice v-else-if="error" :message="error" class="sc-rise" style="--i: 2" @retry="refresh" />
				<UsageEmptyState v-else-if="!loading" class="sc-rise" style="--i: 2" />
			</section>
		</div>
	</main>
</template>

<style scoped>
@import '../../styles/settings-console.css';
@import '../../styles/usage-dashboard.css';

.usage-refresh {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	min-height: 32px;
	padding: 6px 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 9px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	font-family: inherit;
	font-size: var(--fs-12);
	font-weight: 600;
	cursor: pointer;
	white-space: nowrap;
	transition:
		border-color 0.16s,
		background 0.16s,
		color 0.16s,
		transform 0.12s var(--sc-ease-spring);
}

.usage-refresh:hover:not(:disabled) {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
	color: var(--sc-text);
	transform: translateY(-1px);
}

.usage-refresh:disabled {
	cursor: default;
	opacity: 0.7;
}

.usage-refresh:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 2px;
}

.usage-refresh .is-spinning {
	animation: sc-spin 0.9s linear infinite;
}

.usage-section {
	display: flex;
	flex-direction: column;
	gap: 14px;
	margin-top: 0;
}

.usage-head {
	display: flex;
	align-items: center;
	justify-content: flex-end;
}

.usage-grid {
	display: grid;
	grid-template-columns: 1.35fr minmax(320px, 0.9fr);
	gap: 16px;
	align-items: stretch;
}

@media (max-width: 1100px) {
	.usage-grid {
		grid-template-columns: minmax(0, 1fr);
	}
}
</style>
