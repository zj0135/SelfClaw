<script setup>
import { computed, ref } from 'vue';
import { ChevronDown, RefreshCw } from 'lucide-vue-next';
import { useUsageOverview } from '../../composables/useUsageOverview.js';
import { unknownText } from '../../renderers/usageFormat.js';
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
const isNoteOpen = ref(false);

const app = computed(() => overview.value?.app ?? null);
const versionText = computed(() => (app.value?.version ? `v${app.value.version}` : unknownText));
const buildText = computed(() => app.value?.build ?? unknownText);
const runtimeText = computed(() => app.value?.runtime ?? unknownText);
const osText = computed(() => app.value?.os ?? unknownText);
const timeZoneText = computed(() => overview.value?.timeZone ?? unknownText);
</script>

<template>
	<main class="about-page sc-root sc-stage sc-page">
		<header class="sc-page-head sc-rise" style="--i: 0">
			<div>
				<h1 class="sc-page-title">关于</h1>
				<p class="sc-page-sub">SELFCLAW / ABOUT</p>
			</div>
			<button type="button" class="usage-refresh" :disabled="loading" @click="refresh">
				<RefreshCw :size="14" :stroke-width="2" :class="{ 'is-spinning': loading }" aria-hidden="true" />
				<span>刷新</span>
			</button>
		</header>

		<div class="sc-page-body">
			<section class="about-hero sc-rise" style="--i: 1">
				<div class="hero-identity">
					<span class="hero-word">SELFCLAW</span>
					<p class="hero-sub">Windows 桌面 AI 编程助手</p>
				</div>
				<dl class="hero-meta">
					<div class="hero-meta-item">
						<dt>VERSION</dt>
						<dd>{{ versionText }}</dd>
					</div>
					<div class="hero-meta-item">
						<dt>BUILD</dt>
						<dd>{{ buildText }}</dd>
					</div>
					<div class="hero-meta-item">
						<dt>RUNTIME</dt>
						<dd>{{ runtimeText }}</dd>
					</div>
					<div class="hero-meta-item">
						<dt>OS</dt>
						<dd>{{ osText }}</dd>
					</div>
				</dl>
			</section>

			<section class="usage-section">
				<header class="usage-head sc-rise" style="--i: 2">
					<div class="usage-heading">
						<span class="usage-kicker">USAGE / 应用用量</span>
						<h2 class="usage-title">你的 Agent 用量</h2>
					</div>
					<UsageRangeTabs v-model="range" />
				</header>

				<template v-if="hasActivity">
					<UsageSummaryStrip :summary="overview.lifetime.summary" class="sc-rise" style="--i: 3" />
					<UsageHeatmap :days="overview.lifetime.heatmap.days" class="sc-rise" style="--i: 4" />
					<div class="usage-grid sc-rise" style="--i: 5">
						<UsageTrendChart :daily="overview.rangeData.daily" :models="overview.rangeData.models" :range="range" />
						<UsageModelRanking :models="overview.rangeData.models" />
					</div>
					<UsageToolRanking v-if="overview.rangeData.tools?.length" :tools="overview.rangeData.tools"
						class="sc-rise" style="--i: 6" />
				</template>
				<UsageErrorNotice v-else-if="error" :message="error" class="sc-rise" style="--i: 3" @retry="refresh" />
				<UsageEmptyState v-else-if="!loading" class="sc-rise" style="--i: 3" />

				<div class="usage-note sc-rise" style="--i: 7">
					<button type="button" class="usage-note-toggle" :aria-expanded="isNoteOpen" @click="isNoteOpen = !isNoteOpen">
						<ChevronDown :size="13" :stroke-width="2" :class="{ 'is-open': isNoteOpen }" aria-hidden="true" />
						<span>数据说明</span>
					</button>
					<div v-show="isNoteOpen" class="usage-note-body">
						<p>统计来自本地 <code>turn_usage</code>，仅包含供应商上报的用量。未上报的 CLI 回合不计入。</p>
						<p>含子代理与自动化回合；按本机时区（{{ timeZoneText }}）的自然日分桶，范围切换只影响趋势与排行。</p>
					</div>
				</div>
			</section>
		</div>
	</main>
</template>

<style scoped>
@import '../../styles/settings-console.css';
@import './usage/usage-dashboard.css';

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

.about-hero {
	display: flex;
	flex-wrap: wrap;
	align-items: flex-end;
	justify-content: space-between;
	gap: 14px 24px;
}

.hero-identity {
	display: flex;
	flex-direction: column;
	gap: 4px;
	min-width: 0;
}

.hero-word {
	color: var(--sc-text);
	font-family: var(--sc-display);
	font-size: var(--fs-26);
	font-weight: 700;
	letter-spacing: 0.14em;
	line-height: 1.05;
}

.hero-sub {
	margin: 0;
	color: var(--sc-mute);
	font-size: var(--fs-12);
	letter-spacing: 0.02em;
}

.hero-meta {
	display: flex;
	flex-wrap: wrap;
	gap: 8px 22px;
	margin: 0;
}

.hero-meta-item {
	display: flex;
	flex-direction: column;
	gap: 2px;
	min-width: 0;
}

.hero-meta dt {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.14em;
}

.hero-meta dd {
	margin: 0;
	max-width: 34ch;
	overflow: hidden;
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.usage-section {
	display: flex;
	flex-direction: column;
	gap: 16px;
	margin-top: 28px;
}

.usage-head {
	display: flex;
	flex-wrap: wrap;
	align-items: flex-end;
	justify-content: space-between;
	gap: 12px;
}

.usage-heading {
	display: flex;
	flex-direction: column;
	gap: 3px;
	min-width: 0;
}

.usage-title {
	margin: 0;
	color: var(--sc-text);
	font-size: var(--fs-17);
	font-weight: 650;
	letter-spacing: 0.01em;
}

.usage-grid {
	display: grid;
	grid-template-columns: 1.35fr minmax(320px, 0.9fr);
	gap: 16px;
	align-items: stretch;
}

.usage-note {
	display: flex;
	flex-direction: column;
	gap: 8px;
	padding-top: 4px;
	border-top: 1px dashed var(--sc-line);
}

.usage-note-toggle {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	align-self: flex-start;
	padding: 4px 0;
	border: 0;
	background: transparent;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	letter-spacing: 0.06em;
	cursor: pointer;
	transition: color 0.16s;
}

.usage-note-toggle:hover {
	color: var(--sc-text);
}

.usage-note-toggle:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 2px;
}

.usage-note-toggle .is-open {
	transform: rotate(180deg);
}

.usage-note-body {
	display: flex;
	flex-direction: column;
	gap: 6px;
	max-width: 72ch;
}

.usage-note-body p {
	margin: 0;
	color: var(--sc-mute);
	font-size: var(--fs-11);
	line-height: 1.7;
}

.usage-note-body code {
	padding: 1px 4px;
	border-radius: 4px;
	background: var(--sc-raise);
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
}

/* 内容区被 272px 侧栏挤占，用视口断点代替容器查询时窗口要比内容宽得多才会真的变窄，
   所以这一步放得足够晚，避免两栏在仍然宽裕的内容区里被压到 320px 下限。 */
@media (max-width: 1100px) {
	.usage-grid {
		grid-template-columns: minmax(0, 1fr);
	}
}
</style>
