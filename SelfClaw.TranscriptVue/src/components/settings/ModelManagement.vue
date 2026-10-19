<script setup>
import { computed, ref } from 'vue';
import { Search, RefreshCw, SlidersHorizontal, Trash2, Image, Cpu, Sparkles, Eye, Layers } from 'lucide-vue-next';
import ModelConfigurationDialog from './ModelConfigurationDialog.vue';
import { useModelManagement } from '../../composables/useModelManagement.js';
import { formatPrice } from '../../composables/useAiProviderHost.js';
import { formatTokenCount } from '../../renderers/usageMeter.js';

const { models, search, loading, error, editing, deleting, filteredModels, load, saved, remove } = useModelManagement();

const filterMode = ref('all'); // 'all' | 'vision' | 'reasoning'

const displayModels = computed(() => {
	let list = filteredModels.value;
	if (filterMode.value === 'vision') {
		list = list.filter((m) => m.isMultimodal);
	} else if (filterMode.value === 'reasoning') {
		list = list.filter((m) => Boolean(m.reasoningEffort));
	}
	return list;
});

const multimodalCount = computed(() => models.value.filter((m) => m.isMultimodal).length);
const reasoningCount = computed(() => models.value.filter((m) => Boolean(m.reasoningEffort)).length);

function reasoningClass(effort) {
	if (!effort || effort === 'none') return 'level-default';
	if (effort === 'max' || effort === 'xhigh') return 'level-max';
	if (effort === 'high') return 'level-high';
	return 'level-mid';
}
</script>

<template>
	<main class="sc-root sc-stage sc-page management">
		<!-- Page Header -->
		<header class="sc-page-head">
			<div>
				<h1 class="sc-page-title">模型管理</h1>
			</div>
			<button
				type="button"
				class="refresh-btn"
				aria-label="刷新模型配置"
				title="刷新模型配置"
				:disabled="loading"
				@click="load"
			>
				<RefreshCw :size="14" :stroke-width="2.2" :class="{ spinning: loading }" />
				<span>{{ loading ? '刷新中…' : '刷新配置' }}</span>
			</button>
		</header>

		<div class="sc-page-body scroll">
			<!-- Telemetry Stats Grid -->
			<div class="stats-deck sc-rise" style="--i: 0">
				<div class="stat-card">
					<div class="stat-meta">
						<span class="stat-label">已配置共享模型</span>
					</div>
					<div class="stat-val-row">
						<span class="stat-val">{{ models.length }}</span>
					</div>
				</div>

				<div class="stat-card">
					<div class="stat-meta">
						<span class="stat-label">视觉与多模态就绪</span>
					</div>
					<div class="stat-val-row">
						<span class="stat-val ok">{{ multimodalCount }}</span>
					</div>
				</div>

				<div class="stat-card">
					<div class="stat-meta">
						<span class="stat-label">扩展深度思考</span>
					</div>
					<div class="stat-val-row">
						<span class="stat-val acid">{{ reasoningCount }}</span>
					</div>
				</div>
			</div>

			<!-- Filter & Command Bar -->
			<div class="toolbar sc-rise" style="--i: 1">
				<div class="search-wrap">
					<Search :size="14" class="search-ico" aria-hidden="true" />
					<input
						v-model="search"
						type="search"
						placeholder="搜索模型名称或部署 ID..."
						aria-label="搜索模型配置"
						class="search-input"
					/>
				</div>

				<div class="filter-pills">
					<button
						type="button"
						class="filter-pill"
						:class="{ active: filterMode === 'all' }"
						@click="filterMode = 'all'"
					>
						<span>全部 ({{ models.length }})</span>
					</button>
					<button
						type="button"
						class="filter-pill"
						:class="{ active: filterMode === 'vision' }"
						@click="filterMode = 'vision'"
					>
						<Image :size="12" />
						<span>多模态 ({{ multimodalCount }})</span>
					</button>
					<button
						type="button"
						class="filter-pill"
						:class="{ active: filterMode === 'reasoning' }"
						@click="filterMode = 'reasoning'"
					>
						<Sparkles :size="12" />
						<span>深度思考 ({{ reasoningCount }})</span>
					</button>
				</div>
			</div>

			<!-- Error Alert -->
			<div v-if="error" class="error-banner" role="alert">
				<span>{{ error }}</span>
			</div>

			<!-- Empty State -->
			<div v-if="loading && !models.length" class="empty-state" role="status">
				<div class="spin-dot"></div>
				<span>正在加载全局模型配置…</span>
			</div>

			<div v-else-if="!displayModels.length" class="empty-state">
				<Cpu :size="32" :stroke-width="1.6" class="empty-ico" />
				<span class="empty-title">{{ search ? '未找到匹配的模型配置' : '暂无共享模型配置' }}</span>
			</div>

			<!-- Models Table / Deck -->
			<div v-else class="table-container sc-rise" style="--i: 2">
				<div class="table-scroll">
					<table class="cyber-table">
						<thead>
							<tr>
								<th class="col-model">模型</th>
								<th class="col-limits">上下文 / 输出</th>
								<th class="col-reasoning">思考等级</th>
								<th class="col-pricing">
									<span>输入 / 输出</span>
									<small>USD / 1M</small>
								</th>
								<th class="col-cache">
									<span>缓存读 / 写</span>
									<small>USD / 1M</small>
								</th>
								<th class="col-actions text-right"><span class="sr-only">操作</span></th>
							</tr>
						</thead>
						<tbody>
							<tr v-for="model in displayModels" :key="model.model" class="table-row">
								<td class="col-model">
									<div class="model-identity">
										<div class="model-titleline">
											<span class="model-name-text">{{ model.name }}</span>
											<span v-if="model.isMultimodal" class="chip chip-vision" title="支持多模态图像理解">
												<Image :size="11" :stroke-width="2.4" />
												<span>多模态</span>
											</span>
										</div>
										<code class="model-id-chip">{{ model.model }}</code>
									</div>
								</td>
								<td class="col-limits">
									<div class="spec-pills">
										<span class="spec-pill" title="上下文长度">
											<span class="spec-k">CTX</span>
											<span class="spec-v">{{ formatTokenCount(model.contextLength) }}</span>
										</span>
										<span class="spec-pill" title="最大生成输出">
											<span class="spec-k">OUT</span>
											<span class="spec-v">{{ formatTokenCount(model.maxOutputTokens) }}</span>
										</span>
									</div>
								</td>
								<td class="col-reasoning">
									<span class="reasoning-badge" :class="reasoningClass(model.reasoningEffort)">
										{{ (model.reasoningEffort || 'DEFAULT').toUpperCase() }}
									</span>
								</td>
								<td class="col-pricing">
									<div class="rate-val mono">
										<span>{{ formatPrice(model.priceInPerMTok) }}</span>
										<span class="slash">/</span>
										<span>{{ formatPrice(model.priceOutPerMTok) }}</span>
									</div>
								</td>
								<td class="col-cache">
									<div class="rate-val mono cache-val">
										<span>{{ formatPrice(model.priceCacheReadPerMTok) }}</span>
										<span class="slash">/</span>
										<span>{{ formatPrice(model.priceCacheWritePerMTok) }}</span>
									</div>
								</td>
								<td class="col-actions text-right">
									<div class="actions-cluster">
										<button
											class="icon-action-btn"
											type="button"
											title="配置模型参数"
											:aria-label="`配置 ${model.model}`"
											@click="editing = model"
										>
											<SlidersHorizontal :size="15" :stroke-width="2" />
										</button>
										<button
											class="icon-action-btn danger"
											type="button"
											title="删除共享配置"
											:aria-label="`删除 ${model.model} 的共享配置`"
											:disabled="deleting !== null"
											@click="remove(model)"
										>
											<Trash2 :size="15" :stroke-width="2" />
										</button>
									</div>
								</td>
							</tr>
						</tbody>
					</table>
				</div>
			</div>
		</div>

		<!-- Edit Modal -->
		<ModelConfigurationDialog v-if="editing" :model="editing" @close="editing = null" @saved="saved" />
	</main>
</template>

<style scoped>
@import '../../styles/settings-console.css';

.management {
	letter-spacing: 0;
	background: var(--sc-bg);
}

.refresh-btn {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	height: 36px;
	padding: 0 14px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-12);
	font-weight: 600;
	cursor: pointer;
	transition: all 0.16s var(--sc-ease-spring);
}

.refresh-btn:hover:not(:disabled) {
	border-color: var(--sc-acid);
	color: var(--sc-acid);
	background: var(--sc-hover);
	transform: translateY(-1px);
}

.refresh-btn:disabled {
	opacity: 0.5;
	cursor: wait;
}

.spinning {
	animation: sc-spin 0.8s linear infinite;
}

/* Telemetry Stats Grid */
.stats-deck {
	display: grid;
	grid-template-columns: repeat(3, minmax(0, 1fr));
	gap: 16px;
	margin-bottom: 24px;
}

.stat-card {
	display: flex;
	flex-direction: column;
	justify-content: space-between;
	padding: 16px 20px;
	border: 1px solid var(--sc-line);
	border-radius: 12px;
	background: var(--sc-panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.03);
	transition: all 0.2s var(--sc-ease-out);
}

.stat-card:hover {
	border-color: var(--sc-line-2);
	box-shadow: 0 8px 24px rgba(var(--shadow-ink), 0.05);
	transform: translateY(-1px);
}

.stat-meta {
	display: flex;
	flex-direction: column;
	gap: 3px;
}

.stat-kicker {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.16em;
}

.stat-label {
	color: var(--sc-soft);
	font-size: var(--fs-12);
	font-weight: 550;
}

.stat-val-row {
	display: flex;
	align-items: baseline;
	gap: 8px;
	margin-top: 10px;
}

.stat-val {
	font-family: var(--sc-mono);
	font-size: var(--fs-25, 25px);
	font-weight: 700;
	line-height: 1;
	color: var(--sc-text);
}

.stat-val.ok {
	color: var(--sc-ok);
}

.stat-val.acid {
	color: var(--sc-acid);
}

.stat-unit {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.12em;
}

/* Toolbar */
.toolbar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 16px;
	margin-bottom: 20px;
	flex-wrap: wrap;
}

.search-wrap {
	position: relative;
	display: flex;
	align-items: center;
	flex: 1 1 280px;
	min-width: 0;
}

.search-ico {
	position: absolute;
	left: 12px;
	color: var(--sc-faint);
	pointer-events: none;
}

.search-input {
	width: 100%;
	height: 38px;
	padding: 0 12px 0 34px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-125);
	outline: none;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.search-input:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

.filter-pills {
	display: inline-flex;
	align-items: center;
	gap: 6px;
}

.filter-pill {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	height: 34px;
	padding: 0 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 99px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	font-family: inherit;
	font-size: var(--fs-115);
	font-weight: 550;
	cursor: pointer;
	transition: all 0.16s var(--sc-ease-out);
}

.filter-pill:hover {
	color: var(--sc-text);
	border-color: var(--sc-faint);
}

.filter-pill.active {
	border-color: var(--sc-acid);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	font-weight: 600;
}

/* Error Banner */
.error-banner {
	padding: 12px 16px;
	margin-bottom: 20px;
	border: 1px solid color-mix(in srgb, var(--sc-err) 30%, transparent);
	border-radius: 8px;
	background: var(--sc-err-soft);
	color: var(--sc-err);
	font-size: var(--fs-125);
}

/* Empty State */
.empty-state {
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	gap: 10px;
	padding: 64px 24px;
	border: 1px dashed var(--sc-line-2);
	border-radius: 14px;
	background: color-mix(in srgb, var(--sc-panel) 40%, var(--sc-raise));
	color: var(--sc-mute);
	text-align: center;
}

.empty-ico {
	color: var(--sc-faint);
	opacity: 0.6;
}

.empty-title {
	font-size: var(--fs-14);
	font-weight: 600;
	color: var(--sc-soft);
}

.empty-sub {
	font-size: var(--fs-12);
	max-width: 44ch;
	line-height: 1.5;
}

.spin-dot {
	width: 24px;
	height: 24px;
	border: 2px solid var(--sc-line-2);
	border-top-color: var(--sc-acid);
	border-radius: 50%;
	animation: sc-spin 0.8s linear infinite;
}

/* Table Container */
.table-container {
	border: 1px solid var(--sc-line);
	border-radius: 14px;
	background: var(--sc-panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.04);
	overflow: hidden;
}

.table-scroll {
	overflow-x: auto;
}

.cyber-table {
	width: 100%;
	border-collapse: collapse;
	text-align: left;
	font-size: var(--fs-125);
}

.cyber-table thead {
	background: var(--sc-raise);
	border-bottom: 1px solid var(--sc-line);
}

.cyber-table th {
	padding: 12px 18px;
	font-family: var(--sc-mono);
	font-size: var(--fs-105);
	font-weight: 650;
	color: var(--sc-faint);
	letter-spacing: 0.08em;
	text-transform: uppercase;
	white-space: nowrap;
}

.cyber-table th small {
	display: block;
	font-size: var(--fs-9);
	font-weight: 500;
	letter-spacing: 0.04em;
	opacity: 0.7;
}

.table-row {
	border-bottom: 1px solid var(--sc-line);
	transition: background 0.15s;
}

.table-row:last-child {
	border-bottom: 0;
}

.table-row:hover {
	background: color-mix(in srgb, var(--sc-hover) 80%, transparent);
}

.cyber-table td {
	padding: 14px 18px;
	vertical-align: middle;
	white-space: nowrap;
}

/* Column Specifics */
.col-model {
	min-width: 200px;
	max-width: 320px;
}

.model-identity {
	display: flex;
	flex-direction: column;
	gap: 4px;
	min-width: 0;
}

.model-titleline {
	display: flex;
	align-items: center;
	gap: 8px;
	flex-wrap: wrap;
}

.model-name-text {
	font-size: var(--fs-135);
	font-weight: 620;
	color: var(--sc-text);
}

.chip {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	padding: 1px 6px;
	border-radius: 4px;
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.08em;
}

.chip-vision {
	border: 1px solid color-mix(in srgb, var(--sc-ok) 35%, transparent);
	background: var(--sc-ok-soft);
	color: var(--sc-ok);
}

.model-id-chip {
	display: inline-block;
	padding: 1px 6px;
	border: 1px solid var(--sc-line);
	border-radius: 4px;
	background: var(--sc-raise);
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-11);
	max-width: 100%;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.spec-pills {
	display: flex;
	align-items: center;
	gap: 6px;
}

.spec-pill {
	display: inline-flex;
	align-items: baseline;
	gap: 4px;
	padding: 2px 7px;
	border: 1px solid var(--sc-line);
	border-radius: 5px;
	background: var(--sc-raise);
	font-family: var(--sc-mono);
	font-size: var(--fs-11);
}

.spec-k {
	color: var(--sc-faint);
	font-size: var(--fs-9);
	font-weight: 600;
}

.spec-v {
	color: var(--sc-text);
	font-weight: 600;
}

.reasoning-badge {
	display: inline-block;
	padding: 3px 8px;
	border-radius: 5px;
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-weight: 650;
	letter-spacing: 0.06em;
}

.reasoning-badge.level-max {
	border: 1px solid color-mix(in srgb, var(--sc-acid) 45%, transparent);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
}

.reasoning-badge.level-high {
	border: 1px solid color-mix(in srgb, var(--sc-acid) 30%, transparent);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
}

.reasoning-badge.level-mid {
	border: 1px solid var(--sc-line-2);
	background: var(--sc-raise);
	color: var(--sc-soft);
}

.reasoning-badge.level-default {
	border: 1px solid var(--sc-line);
	background: transparent;
	color: var(--sc-faint);
}

.rate-val {
	font-family: var(--sc-mono);
	font-size: var(--fs-12);
	color: var(--sc-text);
}

.cache-val {
	color: var(--sc-mute);
}

.slash {
	margin: 0 4px;
	color: var(--sc-faint);
}

.actions-cluster {
	display: inline-flex;
	align-items: center;
	gap: 6px;
}

.icon-action-btn {
	display: grid;
	place-items: center;
	width: 32px;
	height: 32px;
	border: 1px solid var(--sc-line);
	border-radius: 7px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	cursor: pointer;
	transition: all 0.16s var(--sc-ease-spring);
}

.icon-action-btn:hover {
	border-color: var(--sc-line-2);
	background: var(--sc-hover);
	color: var(--sc-text);
	transform: translateY(-1px);
}

.icon-action-btn.danger:hover {
	border-color: color-mix(in srgb, var(--sc-err) 40%, transparent);
	background: var(--sc-err-soft);
	color: var(--sc-err);
}

.icon-action-btn:disabled {
	opacity: 0.4;
	cursor: wait;
	transform: none;
}

.text-right {
	text-align: right;
}

.sr-only {
	position: absolute;
	width: 1px;
	height: 1px;
	overflow: hidden;
	clip-path: inset(50%);
}

@media (max-width: 800px) {
	.stats-deck {
		grid-template-columns: 1fr;
	}
}
</style>
