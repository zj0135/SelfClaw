<script setup>
import { Search, RefreshCw, SlidersHorizontal, Trash2, Image, Cpu } from 'lucide-vue-next';
import ModelConfigurationDialog from './ModelConfigurationDialog.vue';
import { useModelManagement } from '../../composables/useModelManagement.js';
import { formatTokens, formatPrice } from '../../composables/useAiProviderHost.js';

const { models, search, loading, error, editing, deleting, filteredModels, load, saved, remove } = useModelManagement();
</script>

<template>
	<main class="sc-root sc-stage sc-page management">
		<header class="sc-page-head">
			<div>
				<h1 class="sc-page-title">模型管理</h1>
				<p class="sc-page-sub">{{ models.length }} 个模型配置</p>
			</div>
		</header>
		<div class="sc-page-body">
			<div class="toolbar">
				<label class="search"
					><Search :size="16" /><input v-model="search" type="search" placeholder="搜索模型名称或 ID" aria-label="搜索模型配置" /></label
				><button type="button" class="icon-button" aria-label="刷新模型配置" title="刷新模型配置" :disabled="loading" @click="load">
					<RefreshCw :size="16" />
				</button>
			</div>
			<p v-if="error" class="error" role="alert">{{ error }}</p>
			<div v-if="loading && !models.length" class="empty" role="status">正在加载模型配置...</div>
			<div v-else-if="!filteredModels.length" class="empty">
				<Cpu :size="28" /><span>{{ search ? '没有匹配的模型' : '暂无模型配置' }}</span>
			</div>
			<div v-else class="table-scroll">
				<table>
					<thead>
						<tr>
							<th>模型</th>
							<th>上下文 / 输出</th>
							<th>思考等级</th>
							<th>输入 / 输出 <small>USD / 1M</small></th>
							<th>缓存读 / 写 <small>USD / 1M</small></th>
							<th><span class="sr-only">操作</span></th>
						</tr>
					</thead>
					<tbody>
						<tr v-for="model in filteredModels" :key="model.model">
							<td class="model-name">
								<div>
									<strong>{{ model.name }}</strong
									><Image v-if="model.isMultimodal" :size="14" aria-label="支持图像输入" />
								</div>
								<code>{{ model.model }}</code>
							</td>
							<td>{{ formatTokens(model.contextLength) }} / {{ formatTokens(model.maxOutputTokens) }}</td>
							<td>{{ model.reasoningEffort || '默认' }}</td>
							<td>{{ formatPrice(model.priceInPerMTok) }} / {{ formatPrice(model.priceOutPerMTok) }}</td>
							<td>{{ formatPrice(model.priceCacheReadPerMTok) }} / {{ formatPrice(model.priceCacheWritePerMTok) }}</td>
							<td>
								<div class="actions">
									<button class="icon-button" type="button" title="模型参数" :aria-label="`配置 ${model.model}`" @click="editing = model">
										<SlidersHorizontal :size="16" /></button
									><button
										class="icon-button danger"
										type="button"
										title="删除共享配置"
										:aria-label="`删除 ${model.model} 的共享配置`"
										:disabled="deleting !== null"
										@click="remove(model)"
									>
										<Trash2 :size="16" />
									</button>
								</div>
							</td>
						</tr>
					</tbody>
				</table>
			</div>
		</div>
		<ModelConfigurationDialog v-if="editing" :model="editing" @close="editing = null" @saved="saved" />
	</main>
</template>

<style scoped>
@import '../../styles/settings-console.css';
.management {
	letter-spacing: 0;
}
.toolbar {
	display: flex;
	align-items: center;
	gap: 12px;
	padding-bottom: 24px;
}
.search {
	display: flex;
	align-items: center;
	gap: 10px;
	width: min(380px, 100%);
	min-width: 0;
	height: 38px;
	box-sizing: border-box;
	padding: 0 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 6px;
	color: var(--sc-mute);
}
.search input {
	width: 100%;
	min-width: 0;
	border: 0;
	outline: 0;
	background: transparent;
	color: var(--sc-text);
	font: inherit;
	font-size: 13px;
}
.search:focus-within {
	outline: 2px solid var(--sc-acid);
	outline-offset: 2px;
}
.icon-button {
	display: inline-grid;
	place-items: center;
	width: 34px;
	height: 34px;
	flex: 0 0 34px;
	padding: 0;
	border: 1px solid var(--sc-line);
	border-radius: 6px;
	color: var(--sc-mute);
	background: transparent;
	cursor: pointer;
}
.icon-button:hover {
	color: var(--sc-text);
	background: var(--sc-hover);
}
.icon-button:disabled {
	opacity: 0.4;
	cursor: wait;
}
.icon-button:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 2px;
}
.danger:hover,
.error {
	color: #c73a3a;
}
.error {
	font-size: 13px;
}
.empty {
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: center;
	gap: 16px;
	min-height: 220px;
	color: var(--sc-mute);
	font-size: 14px;
}
.table-scroll {
	overflow-x: auto;
}
table {
	width: 100%;
	border-collapse: collapse;
	text-align: left;
	font-size: 12px;
}
th {
	height: 44px;
	font-weight: 500;
	color: var(--sc-mute);
	background: var(--sc-hover);
}
th,
td {
	padding: 12px;
	border-bottom: 1px solid var(--sc-line);
	white-space: nowrap;
}
th small {
	display: block;
	font-size: 10px;
	margin-top: 4px;
}
.model-name {
	min-width: 150px;
	max-width: 280px;
	white-space: normal;
	overflow-wrap: anywhere;
}
.model-name div {
	display: flex;
	align-items: center;
	gap: 8px;
}
.model-name svg {
	flex: 0 0 14px;
	color: #168565;
}
strong {
	font-size: 13px;
	font-weight: 550;
}
code {
	display: block;
	margin-top: 5px;
	font-size: 11px;
	font-family: var(--sc-mono);
	color: var(--sc-mute);
}
.actions {
	display: flex;
	gap: 6px;
}
.sr-only {
	position: absolute;
	width: 1px;
	height: 1px;
	overflow: hidden;
	clip-path: inset(50%);
}
</style>
