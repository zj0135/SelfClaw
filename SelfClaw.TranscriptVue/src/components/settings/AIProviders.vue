<script setup>
import { computed, reactive, ref } from 'vue';
import {
	Search,
	Plus,
	Trash2,
	ArrowUpRight,
	Eye,
	EyeOff,
	RefreshCw,
	Check,
	ChevronDown,
	SlidersHorizontal,
} from 'lucide-vue-next';
import AiProviderDialogs from './AiProviderDialogs.vue';
import { useAiProviderHost } from '../../composables/useAiProviderHost.js';
import { useToast } from '../../composables/useToast.js';

// Brand logos from @lobehub/icons-static-png (color variants where available).
import openaiLogo from '@lobehub/icons-static-png/light/openai.png';
import anthropicLogo from '@lobehub/icons-static-png/light/claude-color.png';
import geminiLogo from '@lobehub/icons-static-png/light/gemini-color.png';
import deepseekLogo from '@lobehub/icons-static-png/light/deepseek-color.png';
import openrouterLogo from '@lobehub/icons-static-png/light/openrouter.png';
import ollamaLogo from '@lobehub/icons-static-png/light/ollama.png';
import azureLogo from '@lobehub/icons-static-png/light/azure-color.png';

// Maps a provider kind (see logoKind in useAiProviderHost) to its brand logo asset.
const providerLogos = {
	openai: openaiLogo,
	anthropic: anthropicLogo,
	gemini: geminiLogo,
	deepseek: deepseekLogo,
	openrouter: openrouterLogo,
	ollama: ollamaLogo,
	azure: azureLogo,
};

function mk(name, id, ctx, out, inp, outp, cacheW, cacheR) {
	return { name, id, ctx, out, inp, outp, cacheW, cacheR, on: true };
}

const providers = reactive([
	{
		id: 'openai',
		name: 'OpenAI',
		sub: 'OpenAI Chat Completions 兼容',
		color: '#10a37f',
		enabled: true,
		key: 'sk-preview-demo',
		base: 'https://zyapi.tuluo.top:8888/v1',
		models: [
			mk('GPT 5.2', 'gpt-5.2', '391K', '63K', '$1.75', '$14', '$1.75', '$0.175'),
			mk('GPT-5.1', 'gpt-5.1', '391K', '63K', '$1.25', '$10', '$1.25', '$0.125'),
			mk('GPT-5', 'gpt-5', '391K', '63K', '$1.25', '$10', '$1.25', '$0.125'),
			mk('GPT-4.1', 'gpt-4.1', '1M', '32K', '$2.00', '$8', '$2.00', '$0.50'),
			mk('GPT-4o', 'gpt-4o', '128K', '16K', '$2.50', '$10', '$2.50', '$1.25'),
			mk('o4-mini', 'o4-mini', '200K', '100K', '$1.10', '$4.40', '$1.10', '$0.275'),
			mk('text-embedding-3-large', 'text-embedding-3-large', '8K', '—', '$0.13', '—', null, null),
		],
	},
	{
		id: 'routin',
		name: 'Routin AI',
		sub: '聚合路由 · 多模型转发',
		color: '#e0721b',
		enabled: false,
		models: Array.from({ length: 7 }, (_, i) =>
			mk(`Routin ${i + 1}`, `routin-${i + 1}`, '128K', '8K', '$0.40', '$1.20', null, null)),
		total: 87,
	},
	{
		id: 'routin2',
		name: 'Routin AI（套餐）',
		sub: '包月套餐 · 固定额度',
		color: '#e0721b',
		enabled: false,
		models: Array.from({ length: 5 }, (_, i) =>
			mk(`Routin Pack ${i + 1}`, `routin-pk-${i + 1}`, '128K', '8K', '$0.30', '$0.90', null, null)),
		total: 20,
	},
	{
		id: 'anthropic',
		name: 'Anthropic',
		sub: 'Claude Messages API',
		color: '#c9682a',
		enabled: false,
		models: [
			mk('Claude Opus 4.5', 'claude-opus-4-5', '200K', '64K', '$15.00', '$75', '$18.75', '$1.50'),
			mk('Claude Sonnet 4.5', 'claude-sonnet-4-5', '200K', '64K', '$3.00', '$15', '$3.75', '$0.30'),
			mk('Claude Haiku 4', 'claude-haiku-4', '200K', '32K', '$0.80', '$4', '$1.00', '$0.08'),
		],
		total: 10,
	},
	{
		id: 'longcat',
		name: 'LongCat',
		sub: '长上下文优化模型',
		color: '#6b5bd2',
		enabled: false,
		models: Array.from({ length: 6 }, (_, i) =>
			mk(`LongCat ${i + 1}`, `longcat-${i + 1}`, '512K', '32K', '$0.50', '$1.50', null, null)),
		total: 6,
	},
	{
		id: 'gemini',
		name: 'Google Gemini',
		sub: 'Gemini API · v1beta',
		color: '#1a73e8',
		enabled: false,
		models: [
			mk('Gemini 2.5 Pro', 'gemini-2.5-pro', '2M', '64K', '$1.25', '$10', null, null),
			mk('Gemini 2.5 Flash', 'gemini-2.5-flash', '1M', '64K', '$0.30', '$2.50', null, null),
			mk('Gemini 2.0 Flash', 'gemini-2.0-flash', '1M', '8K', '$0.10', '$0.40', null, null),
		],
		total: 13,
	},
	{
		id: 'deepseek',
		name: 'DeepSeek',
		sub: 'DeepSeek OpenAI 兼容',
		color: '#4d6bfe',
		enabled: false,
		models: [
			mk('DeepSeek V3.2', 'deepseek-chat', '128K', '8K', '$0.27', '$1.10', '$0.27', '$0.07'),
			mk('DeepSeek R1', 'deepseek-reasoner', '128K', '32K', '$0.55', '$2.19', '$0.55', '$0.14'),
		],
		total: 4,
	},
	{
		id: 'openrouter',
		name: 'OpenRouter',
		sub: '统一多供应商网关',
		color: '#3b3f46',
		enabled: false,
		models: Array.from({ length: 6 }, (_, i) =>
			mk(`Router Model ${i + 1}`, `or-model-${i + 1}`, '128K', '8K', '$0.60', '$1.80', null, null)),
		total: 54,
	},
	{
		id: 'ollama',
		name: 'Ollama',
		sub: '本地模型运行时',
		color: '#3a3a3a',
		enabled: false,
		models: [],
		total: 0,
	},
	{
		id: 'azure',
		name: 'Azure OpenAI',
		sub: 'Azure 部署端点',
		color: '#0078d4',
		enabled: false,
		models: Array.from({ length: 5 }, (_, i) =>
			mk(`Azure GPT ${i + 1}`, `azure-gpt-${i + 1}`, '128K', '16K', '$2.50', '$10', null, null)),
		total: 27,
	},
]);

const activeId = ref('openai');
const providerSearch = ref('');
const modelSearch = ref('');
const apiKeyVisible = ref(false);
const selectedCheckModel = ref('gpt-5.2');
const checking = ref(false);
const fetchingModels = ref(false);
const checkStatus = reactive({ visible: false, state: '', text: '' });
const { showToast } = useToast();

const activeProvider = computed(() => providers.find((provider) => provider.id === activeId.value) || providers[0]);

const {
	apiKeyInput,
	loadingState,
	mutating,
	providerDialogOpen,
	modelDialogOpen,
	modelDraft,
	customProtocols,
	providerDraft,
	pendingModelIds,
	selectProvider: selectProviderFromHost,
	markApiKeyDirty,
	saveApiKey: saveApiKeyToHost,
	saveApiBase: saveApiBaseToHost,
	setProviderEnabled: setProviderEnabledFromHost,
	deleteProvider,
	setAllModelsEnabled,
	setModelEnabled,
	deleteModel,
	fetchModelList: fetchModelListFromHost,
	checkConnectivity: checkConnectivityFromHost,
	openProviderConsole: openProviderConsoleFromHost,
	openProviderDialog,
	createCustomProvider,
	openModelDialog,
	createModel,
} = useAiProviderHost({
	providers,
	activeId,
	activeProvider,
	apiKeyVisible,
	selectedCheckModel,
	checking,
	fetchingModels,
	checkStatus,
	showToast,
	resetCheckStatus,
});

const providerGroups = computed(() => {
	const term = providerSearch.value.trim().toLowerCase();
	const matched = providers.filter((provider) => !term || provider.name.toLowerCase().includes(term));

	return [
		{ label: '已启用', en: 'ONLINE', providers: matched.filter((provider) => provider.enabled) },
		{ label: '已禁用', en: 'OFFLINE', providers: matched.filter((provider) => !provider.enabled) },
	].filter((group) => group.providers.length > 0);
});

const filteredModels = computed(() => {
	const provider = activeProvider.value;
	const term = modelSearch.value.trim().toLowerCase();

	return provider.models.filter((model) =>
		!term || model.name.toLowerCase().includes(term) || model.id.toLowerCase().includes(term));
});

// Ghost numeral rendered behind the detail header, e.g. "01".
const activeIndex = computed(() => {
	const index = providers.indexOf(activeProvider.value);
	return index >= 0 ? String(index + 1).padStart(2, '0') : '00';
});

const enabledTotal = computed(() => providers.filter((provider) => provider.enabled).length);

function totalCount(provider) {
	return provider?.total ?? provider?.models?.length ?? 0;
}

function enabledCount(provider) {
	return provider?.models?.filter((model) => model.on).length ?? 0;
}

function displayEnabledCount(provider) {
	return provider.enabled ? enabledCount(provider) : totalCount(provider);
}

function providerIndex(provider) {
	const index = providers.indexOf(provider);
	return index >= 0 ? String(index + 1).padStart(2, '0') : '00';
}

function providerKind(provider) {
	return provider.kind || provider.catalogId || provider.id.replace(/2$/, '');
}

function providerLogo(provider) {
	// Unknown/custom connections fall back to the OpenAI brand logo.
	const src = providerLogos[providerKind(provider)] || providerLogos.openai;
	return `<img src="${src}" alt="" class="brand-logo" draggable="false" />`;
}

function resetCheckStatus() {
	checking.value = false;
	checkStatus.visible = false;
	checkStatus.state = '';
	checkStatus.text = '';
}
</script>

<template>
	<div class="ai-providers sc-root sc-stage">
		<aside class="provider-list">
			<div class="list-top sc-rise" style="--i: 0">
				<div class="list-kicker">
					<span>PROVIDER INDEX</span>
					<span class="list-kicker-count">{{ enabledTotal }}/{{ providers.length }} ON</span>
				</div>
				<div class="list-controls">
					<div class="search">
						<Search :size="14" :stroke-width="2" class="search-ico" aria-hidden="true" />
						<input v-model="providerSearch" type="text" placeholder="搜索服务商..." aria-label="搜索服务商" />
					</div>
					<button class="icon-btn" type="button" title="添加服务商" aria-label="添加服务商" @click="openProviderDialog">
						<Plus :size="16" :stroke-width="2.2" />
					</button>
				</div>
			</div>

			<div class="list-scroll scroll">
				<div v-if="loadingState" class="provider-empty">正在加载服务商…</div>
				<template v-else-if="providerGroups.length">
					<div v-for="group in providerGroups" :key="group.label" class="provider-group">
						<div class="grp-label">
							<span class="grp-dot" :class="{ live: group.en === 'ONLINE' }" aria-hidden="true"></span>
							<span>{{ group.label }}</span>
							<span class="grp-en">{{ group.en }}</span>
						</div>
						<button
							v-for="(provider, pi) in group.providers"
							:key="provider.id"
							type="button"
							class="prov sc-rise"
							:style="{ '--i': pi + 1 }"
							:class="{ active: provider.id === activeId, on: provider.enabled, disabled: !provider.enabled }"
							@click="selectProviderFromHost(provider.id)"
						>
							<span class="p-index">{{ providerIndex(provider) }}</span>
							<span class="p-logo" aria-hidden="true" v-html="providerLogo(provider)"></span>
							<span class="p-meta">
								<span class="p-name">{{ provider.name }}</span>
								<span class="p-sub">{{ displayEnabledCount(provider) }}/{{ totalCount(provider) }} 模型</span>
							</span>
							<span class="dot" aria-hidden="true"></span>
						</button>
					</div>
				</template>
				<div v-else class="provider-empty">没有匹配的服务商</div>
			</div>
		</aside>

		<main class="detail">
			<span class="ghost-num" aria-hidden="true">{{ activeIndex }}</span>

			<header class="detail-head sc-rise" style="--i: 0">
				<div class="dh-logo" aria-hidden="true" v-html="providerLogo(activeProvider)"></div>
				<div class="dh-meta">
					<div class="dh-kicker">PROVIDER / {{ activeIndex }}</div>
					<h2>{{ activeProvider.name }}</h2>
					<p>{{ activeProvider.sub }}</p>
				</div>
				<button
					v-if="activeProvider.connectionId"
					class="m-icon provider-delete"
					type="button"
					title="删除服务商连接"
					aria-label="删除服务商连接"
					:disabled="mutating"
					@click="deleteProvider"
				>
					<Trash2 :size="16" :stroke-width="1.9" />
				</button>
				<label class="switch big" title="启用此服务商">
					<input
						type="checkbox"
						:checked="activeProvider.enabled"
						:disabled="mutating"
						aria-label="启用此服务商"
						@change="setProviderEnabledFromHost($event.target.checked)"
					/>
					<span class="track"></span>
					<span class="knob"></span>
				</label>
			</header>

			<div class="detail-body scroll">
				<div v-if="activeProvider.authKind !== 1" class="field sc-rise" style="--i: 1">
					<div class="field-row">
						<label class="fl" for="api-key">API Key</label>
						<button class="help-link" type="button" :disabled="!activeProvider.getApiKeyUrl" @click="openProviderConsoleFromHost">
							<ArrowUpRight :size="13" :stroke-width="2" />
							获取 API Key
						</button>
					</div>
					<div class="input-wrap">
						<input
							id="api-key"
							v-model="apiKeyInput"
							class="input mono"
							:type="apiKeyVisible ? 'text' : 'password'"
							aria-label="API Key"
							:placeholder="activeProvider?.keyMask || '输入 API Key'"
							@input="markApiKeyDirty"
							@change="saveApiKeyToHost"
						/>
						<button
							class="reveal"
							type="button"
							:aria-label="apiKeyVisible ? '隐藏密钥' : '显示密钥'"
							:title="apiKeyVisible ? '隐藏密钥' : '显示密钥'"
							@click="apiKeyVisible = !apiKeyVisible"
						>
							<EyeOff v-if="apiKeyVisible" :size="16" :stroke-width="1.9" />
							<Eye v-else :size="16" :stroke-width="1.9" />
						</button>
					</div>
				</div>

				<div class="field sc-rise" style="--i: 2">
					<div class="field-row">
						<label class="fl" for="api-base">API 代理地址</label>
					</div>
					<input
						id="api-base"
						v-model="activeProvider.base"
						class="input mono"
						type="text"
						aria-label="API 代理地址"
						@change="saveApiBaseToHost"
					/>
					<p class="field-hint">自定义端点，用于代理或第三方兼容服务</p>
				</div>

				<div class="field sc-rise" style="--i: 3">
					<div class="field-row">
						<label class="fl" for="check-model">连通性检查</label>
					</div>
					<div class="check-row">
						<div class="select">
							<select id="check-model" v-model="selectedCheckModel" aria-label="选择检查模型">
								<option v-if="!activeProvider.models.length" value="">无可用模型</option>
								<option v-for="model in activeProvider.models" :key="model.profileId" :value="model.profileId">
									{{ model.name }}
								</option>
							</select>
							<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
						</div>
						<button class="btn" type="button" :disabled="checking || !activeProvider.connectionId || !activeProvider.models.length" @click="checkConnectivityFromHost">
							检查
						</button>
					</div>
					<div v-if="checkStatus.visible" class="check-status show" :class="checkStatus.state">
						<span v-if="checkStatus.state === 'loading'" class="spin" aria-hidden="true"></span>
						<Check v-else-if="checkStatus.state === 'ok'" :size="14" :stroke-width="2.4" />
						<span v-else class="err-dot" aria-hidden="true"></span>
						{{ checkStatus.text }}
					</div>
				</div>

				<div class="field models-field sc-rise" style="--i: 4">
					<div class="models">
						<div class="models-head">
							<div>
								<div class="mh-kicker">MODEL REGISTRY</div>
								<h3>模型列表</h3>
								<div class="count">共 {{ totalCount(activeProvider) }} 个模型，已启用 {{ enabledCount(activeProvider) }}</div>
							</div>
							<span class="count-pill">{{ enabledCount(activeProvider) }} / {{ totalCount(activeProvider) }}</span>
						</div>
						<div class="models-toolbar">
							<div class="search">
								<Search :size="14" :stroke-width="2" class="search-ico" aria-hidden="true" />
								<input v-model="modelSearch" type="text" placeholder="搜索模型..." aria-label="搜索模型" />
							</div>
							<button class="btn sm" type="button" :disabled="mutating || !activeProvider.connectionId || !activeProvider.models.length" @click="setAllModelsEnabled(true)">全部启用</button>
							<button class="btn sm" type="button" :disabled="mutating || !activeProvider.connectionId || !activeProvider.models.length" @click="setAllModelsEnabled(false)">全部禁用</button>
							<button class="btn sm fetch-models-btn" type="button" :disabled="fetchingModels || !activeProvider.connectionId || !activeProvider.supportsModelListing" @click="fetchModelListFromHost">
								<RefreshCw :size="13" :stroke-width="2" class="refresh-ico" :class="{ spinning: fetchingModels }" />
								获取模型列表
							</button>
							<button class="icon-btn add-model-btn" type="button" title="添加模型" aria-label="添加模型" :disabled="!activeProvider.connectionId" @click="openModelDialog">
								<Plus :size="15" :stroke-width="2.2" />
							</button>
						</div>

						<div v-if="!activeProvider.models.length" class="model-list">
							<div class="models-empty">尚未获取模型，点击“获取模型列表”加载</div>
						</div>
						<div v-else-if="!filteredModels.length" class="model-empty">没有匹配的模型</div>
						<div v-else class="model-list">
							<div v-for="model in filteredModels" :key="model.profileId" class="model">
								<div class="m-logo" aria-hidden="true" v-html="providerLogo(activeProvider)"></div>
								<div class="m-main">
									<div class="m-title">
										<span class="m-name">{{ model.name }}</span>
										<span class="m-id">{{ model.id }}</span>
									</div>
									<div class="m-tags">
										<span class="tag">{{ model.ctx }} 上下文</span>
										<span class="tag">{{ model.out }} 输出</span>
										<span v-if="model.outp !== '—'" class="price-tag">IN {{ model.inp }} / OUT {{ model.outp }}</span>
										<span v-if="model.cacheW && model.cacheR" class="price-tag dim">CACHE W{{ model.cacheW }} / R{{ model.cacheR }}</span>
									</div>
								</div>
								<div class="m-actions">
									<button class="m-icon" type="button" title="查看详情" aria-label="查看详情">
										<Eye :size="15" :stroke-width="1.9" />
									</button>
									<button class="m-icon" type="button" title="模型参数" aria-label="模型参数">
										<SlidersHorizontal :size="15" :stroke-width="1.9" />
									</button>
									<button
										class="m-icon model-delete"
										type="button"
										title="删除模型"
										aria-label="删除模型"
										:disabled="pendingModelIds.has(model.profileId)"
										@click="deleteModel(model)"
									>
										<Trash2 :size="15" :stroke-width="1.9" />
									</button>
									<label class="switch" title="启用模型">
										<input
											type="checkbox"
											:checked="model.on"
											:disabled="pendingModelIds.has(model.profileId)"
											aria-label="启用模型"
											@change="setModelEnabled(model, $event.target.checked)"
										/>
										<span class="track"></span>
										<span class="knob"></span>
									</label>
								</div>
							</div>
						</div>
					</div>
				</div>
			</div>
		</main>

		<AiProviderDialogs
			:provider-open="providerDialogOpen"
			:model-open="modelDialogOpen"
			:protocols="customProtocols"
			:provider-draft="providerDraft"
			:provider="activeProvider"
			:model-draft="modelDraft"
			:busy="mutating"
			@close-provider="providerDialogOpen = false"
			@submit-provider="createCustomProvider"
			@close-model="modelDialogOpen = false"
			@submit-model="createModel"
		/>
	</div>
</template>

<style scoped>
@import './settings-console.css';

.ai-providers {
	position: relative;
	display: grid;
	grid-template-columns: 300px minmax(0, 1fr);
	width: 100%;
	height: 100%;
	min-height: 0;
	overflow: hidden;
	color: var(--sc-text);
	font-family: var(--sc-sans);
	font-size: 14px;
	line-height: 1.5;
}

.ai-providers * {
	box-sizing: border-box;
}

.scroll {
	scrollbar-width: thin;
	scrollbar-color: var(--sc-faint) transparent;
}

.scroll::-webkit-scrollbar {
	width: 9px;
	height: 9px;
}

.scroll::-webkit-scrollbar-thumb {
	background: var(--sc-raise);
	background-clip: padding-box;
	border: 2px solid transparent;
	border-radius: 99px;
}

.scroll::-webkit-scrollbar-thumb:hover {
	background: var(--sc-faint);
}

/* ── left index ─────────────────────────────────────────────── */
.provider-list {
	display: flex;
	min-height: 0;
	flex-direction: column;
	border-right: 1px solid var(--sc-line);
	background: color-mix(in srgb, var(--sc-panel) 72%, transparent);
}

.list-top {
	padding: 20px 16px 14px;
	border-bottom: 1px solid var(--sc-line);
}

.list-kicker {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
	margin-bottom: 14px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 10px;
	font-weight: 600;
	letter-spacing: 0.22em;
}

.list-kicker-count {
	color: var(--sc-acid);
	letter-spacing: 0.12em;
}

.list-controls {
	display: flex;
	align-items: center;
	gap: 8px;
}

.search {
	position: relative;
	display: flex;
	flex: 1;
	align-items: center;
	min-width: 0;
}

.search-ico {
	position: absolute;
	left: 11px;
	color: var(--sc-faint);
	pointer-events: none;
}

.search input {
	width: 100%;
	padding: 9px 10px 9px 33px;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: 13px;
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

.search input::placeholder {
	color: var(--sc-faint);
}

.search input:focus {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	outline: none;
	background: var(--sc-panel);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.icon-btn {
	display: grid;
	width: 36px;
	height: 36px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-soft);
	cursor: pointer;
	transition:
		background 0.16s,
		border-color 0.16s,
		color 0.16s,
		transform 0.12s var(--sc-ease-spring);
}

.icon-btn:hover {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
	transform: translateY(-1px);
}

.list-scroll {
	min-height: 0;
	flex: 1;
	overflow-y: auto;
	padding: 6px 12px 16px;
}

.grp-label {
	display: flex;
	align-items: center;
	gap: 7px;
	padding: 16px 8px 8px;
	color: var(--sc-mute);
	font-size: 11px;
	font-weight: 600;
	letter-spacing: 0.05em;
}

.grp-en {
	margin-left: auto;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 9px;
	font-weight: 500;
	letter-spacing: 0.2em;
}

.grp-dot {
	width: 5px;
	height: 5px;
	border-radius: 50%;
	background: var(--sc-faint);
}

.grp-dot.live {
	background: var(--sc-ok);
	box-shadow: 0 0 8px rgba(15, 157, 99, 0.45);
	animation: sc-blink 2.6s ease-in-out infinite;
}

.prov {
	position: relative;
	display: flex;
	align-items: center;
	width: 100%;
	gap: 10px;
	padding: 10px;
	border: 1px solid transparent;
	border-radius: 10px;
	background: transparent;
	color: inherit;
	text-align: left;
	cursor: pointer;
	user-select: none;
	transition:
		background 0.15s,
		border-color 0.15s,
		transform 0.15s var(--sc-ease-out);
}

.prov:hover {
	background: var(--sc-hover);
	transform: translateX(2px);
}

.prov.active {
	border-color: var(--sc-line-2);
	background: var(--sc-panel);
	box-shadow: 0 2px 10px rgba(23, 26, 31, 0.05);
}

.prov.active::before {
	position: absolute;
	top: 50%;
	left: -1px;
	width: 2px;
	height: 22px;
	transform: translateY(-50%);
	border-radius: 2px;
	background: var(--sc-acid);
	box-shadow: 0 0 10px rgba(59, 91, 253, 0.4);
	content: '';
}

.prov.disabled .p-logo,
.prov.disabled .p-meta {
	opacity: 0.55;
}

.p-index {
	width: 18px;
	flex: 0 0 auto;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 10px;
	letter-spacing: 0.06em;
	transition: color 0.15s;
}

.prov.active .p-index {
	color: var(--sc-acid);
}

.p-logo {
	display: grid;
	width: 34px;
	height: 34px;
	flex: 0 0 auto;
	place-items: center;
	overflow: hidden;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	background: var(--sc-panel);
}

.p-logo :deep(.brand-logo) {
	width: 22px;
	height: 22px;
	object-fit: contain;
}

.p-meta {
	display: grid;
	min-width: 0;
	flex: 1;
}

.p-name {
	overflow: hidden;
	color: var(--sc-text);
	font-size: 13.5px;
	font-weight: 560;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.p-sub {
	margin-top: 1px;
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: 10.5px;
	letter-spacing: 0.02em;
}

.dot {
	width: 6px;
	height: 6px;
	flex: 0 0 auto;
	border-radius: 50%;
	background: var(--sc-faint);
	transition: background 0.18s, box-shadow 0.18s;
}

.prov.on .dot {
	background: var(--sc-ok);
	box-shadow: 0 0 8px rgba(15, 157, 99, 0.45);
}

.provider-empty {
	padding: 30px 10px;
	color: var(--sc-mute);
	font-size: 13px;
	text-align: center;
}

/* ── detail ─────────────────────────────────────────────────── */
.detail {
	position: relative;
	display: flex;
	min-width: 0;
	min-height: 0;
	flex-direction: column;
	overflow: hidden;
}

.ghost-num {
	position: absolute;
	top: -30px;
	right: 8px;
	z-index: 0;
	color: transparent;
	font-family: var(--sc-mono);
	font-size: 220px;
	font-weight: 700;
	letter-spacing: -0.04em;
	line-height: 1;
	-webkit-text-stroke: 1px rgba(19, 27, 45, 0.08);
	pointer-events: none;
	user-select: none;
}

.detail-head {
	position: relative;
	z-index: 1;
	display: flex;
	align-items: center;
	gap: 16px;
	padding: 26px 34px 22px;
	border-bottom: 1px solid var(--sc-line);
}

.dh-logo {
	display: grid;
	width: 52px;
	height: 52px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line-2);
	border-radius: 13px;
	background: var(--sc-panel);
	box-shadow: 0 8px 24px rgba(23, 26, 31, 0.07);
}

.dh-logo :deep(.brand-logo) {
	width: 30px;
	height: 30px;
	object-fit: contain;
}

.dh-meta {
	min-width: 0;
	flex: 1;
}

.dh-kicker {
	margin-bottom: 4px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 10px;
	font-weight: 500;
	letter-spacing: 0.24em;
}

.dh-meta h2 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: 26px;
	font-weight: 640;
	letter-spacing: 0.01em;
	line-height: 1.15;
}

.dh-meta p {
	margin: 3px 0 0;
	color: var(--sc-mute);
	font-size: 12.5px;
}

.detail-body {
	position: relative;
	z-index: 1;
	min-height: 0;
	max-width: 880px;
	flex: 1;
	overflow-y: auto;
	padding: 28px 34px 72px;
}

.field {
	margin-bottom: 28px;
}

.models-field {
	margin-bottom: 0;
}

.field-row {
	display: flex;
	align-items: center;
	justify-content: space-between;
	margin-bottom: 10px;
}

.fl {
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: 11px;
	font-weight: 600;
	letter-spacing: 0.18em;
	text-transform: uppercase;
}

.help-link {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 0;
	border: 0;
	background: transparent;
	color: var(--sc-acid);
	cursor: pointer;
	font: inherit;
	font-size: 12.5px;
	font-weight: 500;
	text-decoration: none;
	opacity: 0.9;
	transition: opacity 0.15s, transform 0.15s var(--sc-ease-out);
}

.help-link:hover {
	opacity: 1;
	transform: translateX(2px);
}

.help-link:disabled {
	opacity: 0.35;
	cursor: default;
	transform: none;
}

.field-hint {
	margin: 8px 0 0;
	color: var(--sc-mute);
	font-size: 12px;
}

.input {
	width: 100%;
	padding: 12px 14px;
	border: 1px solid var(--sc-line);
	border-radius: 10px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: 13.5px;
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

.input::placeholder {
	color: var(--sc-faint);
}

.input:focus {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	outline: none;
	background: var(--sc-panel);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.input.mono {
	font-family: var(--sc-mono);
	font-size: 12.5px;
	letter-spacing: 0.02em;
}

.input-wrap {
	position: relative;
}

.input-wrap .input {
	padding-right: 44px;
}

.reveal {
	position: absolute;
	top: 50%;
	right: 6px;
	display: grid;
	width: 32px;
	height: 32px;
	place-items: center;
	transform: translateY(-50%);
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--sc-mute);
	cursor: pointer;
	transition: background 0.15s, color 0.15s;
}

.reveal:hover {
	background: var(--sc-hover);
	color: var(--sc-text);
}

.check-row {
	display: flex;
	align-items: stretch;
	gap: 10px;
}

.select {
	position: relative;
	flex: 1;
}

.select select {
	width: 100%;
	padding: 12px 38px 12px 14px;
	appearance: none;
	border: 1px solid var(--sc-line);
	border-radius: 10px;
	background: var(--sc-panel);
	color: var(--sc-text);
	cursor: pointer;
	font: inherit;
	font-size: 13.5px;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.select select:focus {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	outline: none;
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.select .chev {
	position: absolute;
	top: 50%;
	right: 13px;
	transform: translateY(-50%);
	color: var(--sc-mute);
	pointer-events: none;
}

.btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 7px;
	white-space: nowrap;
	padding: 0 18px;
	border: 1px solid var(--sc-line-2);
	border-radius: 10px;
	background: var(--sc-panel);
	color: var(--sc-text);
	cursor: pointer;
	font: inherit;
	font-size: 13.5px;
	font-weight: 560;
	transition:
		background 0.16s,
		border-color 0.16s,
		color 0.16s,
		transform 0.12s var(--sc-ease-spring),
		opacity 0.16s;
}

.btn:hover:not(:disabled) {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
	transform: translateY(-1px);
}

.btn:disabled,
.icon-btn:disabled {
	cursor: default;
	opacity: 0.55;
}

.btn:disabled:hover {
	transform: none;
}

.btn.sm {
	height: 36px;
	padding: 0 14px;
	font-size: 12.5px;
}

.check-status {
	display: inline-flex;
	align-items: center;
	gap: 8px;
	margin-top: 10px;
	font-family: var(--sc-mono);
	font-size: 12px;
	font-weight: 500;
	letter-spacing: 0.02em;
}

.check-status .spin {
	width: 13px;
	height: 13px;
	border: 2px solid var(--sc-acid-soft);
	border-top-color: var(--sc-acid);
	border-radius: 50%;
	animation: sc-spin 0.7s linear infinite;
}

.check-status.ok {
	color: var(--sc-ok);
}

.check-status.error {
	color: var(--sc-err);
}

.err-dot {
	width: 7px;
	height: 7px;
	border-radius: 50%;
	background: var(--sc-err);
	box-shadow: 0 0 8px rgba(220, 69, 69, 0.4);
}

/* ── model registry ─────────────────────────────────────────── */
.models {
	container-type: inline-size;
	overflow: hidden;
	border: 1px solid var(--sc-line);
	border-radius: 14px;
	background: var(--sc-panel);
}

.models-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 18px 20px 14px;
}

.mh-kicker {
	margin-bottom: 5px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 9.5px;
	font-weight: 500;
	letter-spacing: 0.24em;
}

.models-head h3 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: 17px;
	font-weight: 630;
}

.models-head .count {
	margin-top: 3px;
	color: var(--sc-mute);
	font-size: 12px;
}

.count-pill {
	padding: 5px 12px;
	border: 1px solid color-mix(in srgb, var(--sc-acid) 35%, transparent);
	border-radius: 99px;
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-size: 11.5px;
	font-weight: 600;
	letter-spacing: 0.04em;
}

.models-toolbar {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 9px;
	padding: 0 20px 16px;
}

.models-toolbar .search {
	min-width: 176px;
	flex: 1 1 176px;
}

.models-toolbar .btn.sm {
	min-width: 0;
}

.add-model-btn {
	width: 36px;
	height: 36px;
}

@container (max-width: 620px) {
	.models-toolbar .search {
		flex-basis: 100%;
	}

	.models-toolbar .btn.sm {
		flex: 1 1 calc(50% - 5px);
		padding-inline: 10px;
	}

	.models-toolbar .fetch-models-btn {
		flex-basis: calc(100% - 45px);
	}

	.add-model-btn {
		flex: 0 0 36px;
	}
}

.refresh-ico.spinning {
	animation: sc-spin 0.8s linear infinite;
}

.model-list {
	border-top: 1px solid var(--sc-line);
}

.model {
	display: flex;
	align-items: center;
	gap: 14px;
	padding: 14px 20px;
	border-bottom: 1px solid var(--sc-line);
	background: transparent;
	transition: background 0.15s;
}

.model:hover {
	background: rgba(19, 27, 45, 0.025);
}

.model:last-child {
	border-bottom: 0;
}

.m-logo {
	display: grid;
	width: 32px;
	height: 32px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
}

.m-logo :deep(.brand-logo) {
	width: 20px;
	height: 20px;
	object-fit: contain;
}

.m-main {
	min-width: 0;
	flex: 1;
}

.m-title {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 9px;
}

.m-name {
	font-size: 14px;
	font-weight: 600;
}

.m-id {
	padding: 2px 7px;
	border: 1px solid var(--sc-line);
	border-radius: 5px;
	background: var(--sc-raise);
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: 10.5px;
	letter-spacing: 0.02em;
}

.m-tags {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 7px;
	margin-top: 8px;
}

.tag {
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: 10.5px;
	letter-spacing: 0.02em;
}

.price-tag {
	padding: 3px 8px;
	border: 1px solid color-mix(in srgb, var(--sc-ok) 30%, transparent);
	border-radius: 6px;
	background: var(--sc-ok-soft);
	color: var(--sc-ok);
	font-family: var(--sc-mono);
	font-size: 10.5px;
	font-weight: 560;
	letter-spacing: 0.02em;
}

.price-tag.dim {
	border-color: var(--sc-line);
	background: var(--sc-raise);
	color: var(--sc-soft);
}

.m-actions {
	display: flex;
	align-items: center;
	gap: 10px;
	flex: 0 0 auto;
}

.m-icon {
	display: grid;
	width: 30px;
	height: 30px;
	place-items: center;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--sc-faint);
	cursor: pointer;
	transition: background 0.14s, color 0.14s, transform 0.14s var(--sc-ease-spring);
}

.m-icon:hover {
	background: var(--sc-hover);
	color: var(--sc-soft);
	transform: translateY(-1px);
}

.provider-delete,
.model-delete {
	color: color-mix(in srgb, var(--sc-err) 80%, var(--sc-mute));
}

.provider-delete:hover,
.model-delete:hover {
	background: var(--sc-err-soft);
	color: var(--sc-err);
}

.m-icon:disabled {
	opacity: 0.4;
	cursor: default;
	transform: none;
}

/* ── switches ───────────────────────────────────────────────── */
.switch {
	position: relative;
	width: 42px;
	height: 24px;
	flex: 0 0 auto;
	cursor: pointer;
}

.switch input {
	position: absolute;
	z-index: 2;
	width: 100%;
	height: 100%;
	margin: 0;
	cursor: pointer;
	opacity: 0;
}

.track {
	position: absolute;
	inset: 0;
	border: 1px solid var(--sc-line-2);
	border-radius: 99px;
	background: var(--sc-raise);
	transition: background 0.2s, border-color 0.2s;
}

.knob {
	position: absolute;
	top: 3px;
	left: 3px;
	width: 18px;
	height: 18px;
	border-radius: 50%;
	background: #fff;
	box-shadow: 0 1px 3px rgba(23, 26, 31, 0.22);
	transition:
		transform 0.22s var(--sc-ease-spring),
		background 0.2s;
}

.switch input:checked + .track {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
}

.switch input:checked + .track + .knob {
	transform: translateX(18px);
	background: #fff;
}

.switch.big {
	width: 48px;
	height: 27px;
}

.switch.big .knob {
	width: 21px;
	height: 21px;
}

.switch.big input:checked + .track + .knob {
	transform: translateX(21px);
}

.models-empty,
.model-empty {
	padding: 44px;
	color: var(--sc-mute);
	font-size: 13px;
	text-align: center;
}

@media (max-width: 980px) {
	.ai-providers {
		grid-template-columns: 260px minmax(0, 1fr);
	}

	.detail-body {
		padding: 24px 24px 56px;
	}

	.models-toolbar .search {
		flex-basis: 100%;
	}

	.ghost-num {
		font-size: 150px;
	}
}

@media (max-width: 760px) {
	.ai-providers {
		grid-template-columns: 1fr;
		overflow-y: auto;
	}

	.provider-list {
		min-height: 280px;
		border-right: 0;
		border-bottom: 1px solid var(--sc-line);
	}

	.detail {
		min-height: 720px;
	}

	.detail-head {
		padding: 20px;
	}

	.check-row,
	.models-toolbar,
	.model {
		align-items: stretch;
		flex-direction: column;
	}

	.m-actions {
		justify-content: flex-end;
	}
}
</style>
