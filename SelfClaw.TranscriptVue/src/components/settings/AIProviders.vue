<script setup>
import { computed, onUnmounted, reactive, ref } from 'vue';
import AiProviderDialogs from './AiProviderDialogs.vue';
import { useAiProviderHost } from '../../composables/useAiProviderHost.js';

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
const toastState = reactive({ visible: false, text: '已保存' });
let toastTimer = null;

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
	handleMessage,
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

defineExpose({ handleMessage });

const providerGroups = computed(() => {
	const term = providerSearch.value.trim().toLowerCase();
	const matched = providers.filter((provider) => !term || provider.name.toLowerCase().includes(term));

	return [
		{ label: '已启用', providers: matched.filter((provider) => provider.enabled) },
		{ label: '已禁用', providers: matched.filter((provider) => !provider.enabled) },
	].filter((group) => group.providers.length > 0);
});

const filteredModels = computed(() => {
	const provider = activeProvider.value;
	const term = modelSearch.value.trim().toLowerCase();

	return provider.models.filter((model) =>
		!term || model.name.toLowerCase().includes(term) || model.id.toLowerCase().includes(term));
});

function totalCount(provider) {
	return provider?.total ?? provider?.models?.length ?? 0;
}

function enabledCount(provider) {
	return provider?.models?.filter((model) => model.on).length ?? 0;
}

function displayEnabledCount(provider) {
	return provider.enabled ? enabledCount(provider) : totalCount(provider);
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

function showToast(text) {
	toastState.text = text;
	toastState.visible = true;
	window.clearTimeout(toastTimer);
	toastTimer = window.setTimeout(() => {
		toastState.visible = false;
	}, 1900);
}

function eyeSvg(open) {
	return open
		? '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor"><path d="M9.9 4.24A9.1 9.1 0 0 1 12 4c6.5 0 10 7 10 7a13.2 13.2 0 0 1-1.67 2.68"/><path d="M6.6 6.6A13.3 13.3 0 0 0 2 11s3.5 7 10 7a9 9 0 0 0 5.4-1.6"/><path d="m2 2 20 20"/><path d="M9.9 9.9a3 3 0 0 0 4.2 4.2"/></svg>'
		: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z"/><circle cx="12" cy="12" r="3"/></svg>';
}

onUnmounted(() => {
	window.clearTimeout(toastTimer);
});
</script>

<template>
	<div class="ai-providers">
		<section class="provider-list">
			<div class="list-top">
				<div class="search">
					<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
						<circle cx="11" cy="11" r="8" />
						<path d="m21 21-4.3-4.3" />
					</svg>
					<input v-model="providerSearch" type="text" placeholder="搜索服务商..." aria-label="搜索服务商" />
				</div>
				<button class="icon-btn" type="button" title="添加服务商" aria-label="添加服务商" @click="openProviderDialog">
					<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
						<path d="M12 5v14M5 12h14" />
					</svg>
				</button>
			</div>

			<div class="list-scroll scroll">
				<div v-if="loadingState" class="provider-empty">正在加载服务商…</div>
				<template v-else-if="providerGroups.length">
					<div v-for="group in providerGroups" :key="group.label" class="provider-group">
						<div class="grp-label">{{ group.label }}</div>
						<button
							v-for="provider in group.providers"
							:key="provider.id"
							type="button"
							class="prov"
							:class="{ active: provider.id === activeId, on: provider.enabled, disabled: !provider.enabled }"
							@click="selectProviderFromHost(provider.id)"
						>
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
		</section>

		<main class="detail">
			<header class="detail-head">
				<div class="dh-logo" aria-hidden="true" v-html="providerLogo(activeProvider)"></div>
				<div class="dh-meta">
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
					<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
						<path d="M4 7h16M9 7V4h6v3m-8 0 1 13h8l1-13M10 11v5m4-5v5" />
					</svg>
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
				<div v-if="activeProvider.authKind !== 1" class="field">
					<div class="field-row">
						<label class="fl" for="api-key">API Key</label>
						<button class="help-link" type="button" :disabled="!activeProvider.getApiKeyUrl" @click="openProviderConsoleFromHost">
							<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
								<path d="M15 3h6v6" />
								<path d="M10 14 21 3" />
								<path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />
							</svg>
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
							v-html="eyeSvg(apiKeyVisible)"
						></button>
					</div>
				</div>

				<div class="field">
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

				<div class="field">
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
							<svg class="chev" viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
								<path d="m6 9 6 6 6-6" />
							</svg>
						</div>
						<button class="btn" type="button" :disabled="checking || !activeProvider.connectionId || !activeProvider.models.length" @click="checkConnectivityFromHost">
							检查
						</button>
					</div>
					<div v-if="checkStatus.visible" class="check-status show" :class="checkStatus.state">
						<span v-if="checkStatus.state === 'loading'" class="spin" aria-hidden="true"></span>
						<svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
							<path d="M20 6 9 17l-5-5" />
						</svg>
						{{ checkStatus.text }}
					</div>
				</div>

				<div class="field models-field">
					<div class="models">
						<div class="models-head">
							<div>
								<h3>模型列表</h3>
								<div class="count">共 {{ totalCount(activeProvider) }} 个模型，已启用 {{ enabledCount(activeProvider) }}</div>
							</div>
							<span class="count-pill">{{ enabledCount(activeProvider) }} / {{ totalCount(activeProvider) }}</span>
						</div>
						<div class="models-toolbar">
							<div class="search">
								<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
									<circle cx="11" cy="11" r="8" />
									<path d="m21 21-4.3-4.3" />
								</svg>
								<input v-model="modelSearch" type="text" placeholder="搜索模型..." aria-label="搜索模型" />
							</div>
							<button class="btn sm" type="button" :disabled="mutating || !activeProvider.connectionId || !activeProvider.models.length" @click="setAllModelsEnabled(true)">全部启用</button>
							<button class="btn sm" type="button" :disabled="mutating || !activeProvider.connectionId || !activeProvider.models.length" @click="setAllModelsEnabled(false)">全部禁用</button>
							<button class="btn sm fetch-models-btn" type="button" :disabled="fetchingModels || !activeProvider.connectionId || !activeProvider.supportsModelListing" @click="fetchModelListFromHost">
								<svg class="refresh-ico" :class="{ spinning: fetchingModels }" viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
									<path d="M21 12a9 9 0 1 1-2.64-6.36" />
									<path d="M21 3v5h-5" />
								</svg>
								获取模型列表
							</button>
							<button class="icon-btn add-model-btn" type="button" title="添加模型" aria-label="添加模型" :disabled="!activeProvider.connectionId" @click="openModelDialog">
								<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
									<path d="M12 5v14M5 12h14" />
								</svg>
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
										<span class="tag">
											<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
												<path d="M4 7V4h16v3M9 20h6M12 4v16" />
											</svg>
											{{ model.ctx }} 上下文
										</span>
										<span class="tag">
											<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
												<path d="M12 2v6m0 0 3-3m-3 3L9 5M5 12H2m20 0h-3M6 18l-2 2m16 0-2-2" />
											</svg>
											{{ model.out }} 输出
										</span>
										<span v-if="model.outp !== '—'" class="price-tag">输入 {{ model.inp }} / 输出 {{ model.outp }}</span>
										<span v-if="model.cacheW && model.cacheR" class="price-tag">缓存 写 {{ model.cacheW }} / 读 {{ model.cacheR }}</span>
									</div>
								</div>
								<div class="m-actions">
									<button class="m-icon" type="button" title="查看详情" aria-label="查看详情">
										<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
											<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z" />
											<circle cx="12" cy="12" r="3" />
										</svg>
									</button>
									<button class="m-icon" type="button" title="模型参数" aria-label="模型参数">
										<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" aria-hidden="true">
											<line x1="21" y1="6" x2="14" y2="6" />
											<line x1="10" y1="6" x2="3" y2="6" />
											<line x1="21" y1="12" x2="12" y2="12" />
											<line x1="8" y1="12" x2="3" y2="12" />
											<line x1="21" y1="18" x2="16" y2="18" />
											<line x1="12" y1="18" x2="3" y2="18" />
											<line x1="14" y1="4" x2="14" y2="8" />
											<line x1="8" y1="10" x2="8" y2="14" />
											<line x1="16" y1="16" x2="16" y2="20" />
										</svg>
									</button>
									<button
										class="m-icon model-delete"
										type="button"
										title="删除模型"
										aria-label="删除模型"
										:disabled="pendingModelIds.has(model.profileId)"
										@click="deleteModel(model)"
									>
										<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
											<path d="M4 7h16M9 7V4h6v3m-8 0 1 13h8l1-13" />
										</svg>
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

		<div class="toast" :class="{ show: toastState.visible }" role="status" aria-live="polite">
			<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
				<path d="M20 6 9 17l-5-5" />
			</svg>
			<span>{{ toastState.text }}</span>
		</div>

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
.ai-providers {
	--ap-bg: oklch(98% 0.003 250);
	--ap-surface: oklch(100% 0 0);
	--ap-surface-2: oklch(97.5% 0.004 250);
	--ap-fg: oklch(22% 0.012 255);
	--ap-fg-soft: oklch(38% 0.012 255);
	--ap-muted: oklch(56% 0.012 255);
	--ap-faint: oklch(70% 0.01 255);
	--ap-border: oklch(92.5% 0.005 255);
	--ap-border-2: oklch(88% 0.006 255);
	--ap-nav-bg: oklch(21% 0.014 260);
	--ap-accent: oklch(55% 0.16 255);
	--ap-accent-soft: oklch(96% 0.03 255);
	--ap-ok: oklch(63% 0.15 150);
	--ap-ok-soft: oklch(95% 0.04 150);
	--ap-price: oklch(48% 0.12 150);
	--ap-price-bg: oklch(95.5% 0.035 150);
	--ap-switch-on: oklch(26% 0.015 260);
	--ap-mono: 'SF Mono', 'JetBrains Mono', ui-monospace, 'Cascadia Code', Menlo, monospace;
	--ap-radius-sm: 7px;
	--ap-radius-md: 10px;
	--ap-radius-lg: 13px;
	--ap-shadow-sm: 0 1px 2px oklch(20% 0.02 255 / 0.06);
	--ap-shadow-md: 0 4px 14px oklch(20% 0.02 255 / 0.08), 0 1px 3px oklch(20% 0.02 255 / 0.05);

	position: relative;
	display: grid;
	grid-template-columns: 272px minmax(0, 1fr);
	width: 100%;
	height: 100%;
	min-height: 0;
	overflow: hidden;
	background: var(--ap-bg);
	color: var(--ap-fg);
	font-size: 14px;
	line-height: 1.5;
}

.ai-providers * {
	box-sizing: border-box;
}

.scroll {
	scrollbar-width: thin;
	scrollbar-color: var(--ap-border-2) transparent;
}

.scroll::-webkit-scrollbar {
	width: 9px;
	height: 9px;
}

.scroll::-webkit-scrollbar-thumb {
	background: var(--ap-border-2);
	background-clip: padding-box;
	border: 2px solid transparent;
	border-radius: 99px;
}

.scroll::-webkit-scrollbar-thumb:hover {
	background: var(--ap-faint);
}

.provider-list {
	display: flex;
	min-height: 0;
	flex-direction: column;
	border-right: 1px solid var(--ap-border);
	background: var(--ap-surface);
}

.list-top {
	display: flex;
	align-items: center;
	gap: 8px;
	padding: 18px 16px 12px;
}

.search {
	position: relative;
	display: flex;
	flex: 1;
	align-items: center;
	min-width: 0;
}

.search svg {
	position: absolute;
	left: 11px;
	width: 15px;
	height: 15px;
	color: var(--ap-faint);
	stroke-width: 1.8;
}

.search input {
	width: 100%;
	padding: 8px 10px 8px 33px;
	border: 1px solid var(--ap-border-2);
	border-radius: var(--ap-radius-sm);
	background: var(--ap-surface);
	color: var(--ap-fg);
	font: inherit;
	font-size: 13px;
	transition: border-color 0.14s, box-shadow 0.14s;
}

.search input::placeholder {
	color: var(--ap-faint);
}

.search input:focus {
	border-color: var(--ap-accent);
	outline: none;
	box-shadow: 0 0 0 3px var(--ap-accent-soft);
}

.icon-btn {
	display: grid;
	width: 34px;
	height: 34px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--ap-border-2);
	border-radius: var(--ap-radius-sm);
	background: var(--ap-surface);
	color: var(--ap-fg-soft);
	cursor: pointer;
	transition: background 0.14s, border-color 0.14s, color 0.14s;
}

.icon-btn svg {
	width: 17px;
	height: 17px;
	stroke-width: 1.9;
}

.icon-btn:hover {
	border-color: var(--ap-accent);
	background: var(--ap-accent);
	color: #fff;
}

.list-scroll {
	min-height: 0;
	flex: 1;
	overflow-y: auto;
	padding: 6px 12px 16px;
}

.grp-label {
	padding: 14px 6px 7px;
	color: var(--ap-muted);
	font-size: 11px;
	font-weight: 600;
	letter-spacing: 0.04em;
}

.prov {
	position: relative;
	display: flex;
	align-items: center;
	width: 100%;
	gap: 11px;
	padding: 10px;
	border: 1px solid transparent;
	border-radius: var(--ap-radius-md);
	background: transparent;
	color: inherit;
	text-align: left;
	cursor: pointer;
	user-select: none;
	transition: background 0.13s, border-color 0.13s, box-shadow 0.13s;
}

.prov:hover {
	background: var(--ap-surface-2);
}

.prov.active {
	border-color: var(--ap-border-2);
	background: var(--ap-surface);
	box-shadow: var(--ap-shadow-sm);
}

.prov.disabled .p-logo,
.prov.disabled .p-meta {
	opacity: 0.62;
}

.p-logo {
	display: grid;
	width: 36px;
	height: 36px;
	flex: 0 0 auto;
	place-items: center;
	overflow: hidden;
	border: 1px solid var(--ap-border);
	border-radius: 9px;
	background: var(--ap-surface-2);
	color: var(--ap-fg);
	font-size: 15px;
	font-weight: 700;
}

.p-logo :deep(svg) {
	width: 22px;
	height: 22px;
}

.p-logo :deep(.brand-logo) {
	width: 24px;
	height: 24px;
	object-fit: contain;
}

.p-meta {
	display: grid;
	min-width: 0;
	flex: 1;
}

.p-name {
	overflow: hidden;
	color: var(--ap-fg);
	font-size: 13.5px;
	font-weight: 560;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.p-sub {
	margin-top: 1px;
	color: var(--ap-muted);
	font-size: 11.5px;
}

.dot {
	width: 8px;
	height: 8px;
	flex: 0 0 auto;
	border-radius: 50%;
	background: var(--ap-border-2);
}

.prov.on .dot {
	background: var(--ap-ok);
	box-shadow: 0 0 0 3px var(--ap-ok-soft);
}

.provider-empty {
	padding: 30px 10px;
	color: var(--ap-muted);
	font-size: 13px;
	text-align: center;
}

.detail {
	display: flex;
	min-width: 0;
	min-height: 0;
	flex-direction: column;
	background: var(--ap-surface);
}

.detail-head {
	display: flex;
	align-items: center;
	gap: 14px;
	padding: 20px 30px;
	border-bottom: 1px solid var(--ap-border);
}

.dh-logo {
	display: grid;
	width: 42px;
	height: 42px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--ap-border);
	border-radius: 11px;
	background: var(--ap-surface-2);
}

.dh-logo :deep(svg),
.dh-logo :deep(.brand-logo) {
	width: 26px;
	height: 26px;
	object-fit: contain;
}

.dh-meta {
	min-width: 0;
	flex: 1;
}

.dh-meta h2 {
	margin: 0;
	color: var(--ap-fg);
	font-size: 18px;
	font-weight: 620;
	letter-spacing: 0;
	line-height: 1.25;
}

.dh-meta p {
	margin: 1px 0 0;
	color: var(--ap-muted);
	font-size: 12.5px;
}

.detail-body {
	min-height: 0;
	max-width: 860px;
	flex: 1;
	overflow-y: auto;
	padding: 26px 30px 60px;
}

.field {
	margin-bottom: 26px;
}

.models-field {
	margin-bottom: 0;
}

.field-row {
	display: flex;
	align-items: center;
	justify-content: space-between;
	margin-bottom: 9px;
}

.fl {
	color: var(--ap-fg);
	font-size: 13.5px;
	font-weight: 600;
	letter-spacing: 0;
}

.help-link {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 0;
	border: 0;
	background: transparent;
	color: var(--ap-accent);
	cursor: pointer;
	font: inherit;
	font-size: 12.5px;
	font-weight: 500;
	text-decoration: none;
}

.help-link svg {
	width: 13px;
	height: 13px;
	stroke-width: 2;
}

.help-link:hover {
	text-decoration: underline;
}

.field-hint {
	margin: 7px 0 0;
	color: var(--ap-muted);
	font-size: 12px;
}

.input {
	width: 100%;
	padding: 11px 13px;
	border: 1px solid var(--ap-border-2);
	border-radius: var(--ap-radius-md);
	background: var(--ap-surface);
	color: var(--ap-fg);
	font: inherit;
	font-size: 13.5px;
	transition: border-color 0.14s, box-shadow 0.14s;
}

.input::placeholder {
	color: var(--ap-faint);
}

.input:focus {
	border-color: var(--ap-accent);
	outline: none;
	box-shadow: 0 0 0 3px var(--ap-accent-soft);
}

.input.mono {
	font-family: var(--ap-mono);
	font-size: 13px;
	letter-spacing: 0.01em;
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
	color: var(--ap-muted);
	cursor: pointer;
	transition: background 0.14s, color 0.14s;
}

.reveal :deep(svg) {
	width: 18px;
	height: 18px;
	stroke-width: 1.8;
}

.reveal:hover {
	background: var(--ap-surface-2);
	color: var(--ap-fg);
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
	padding: 11px 38px 11px 13px;
	appearance: none;
	border: 1px solid var(--ap-border-2);
	border-radius: var(--ap-radius-md);
	background: var(--ap-surface);
	color: var(--ap-fg);
	cursor: pointer;
	font: inherit;
	font-size: 13.5px;
	transition: border-color 0.14s, box-shadow 0.14s;
}

.select select:focus {
	border-color: var(--ap-accent);
	outline: none;
	box-shadow: 0 0 0 3px var(--ap-accent-soft);
}

.select .chev {
	position: absolute;
	top: 50%;
	right: 13px;
	width: 16px;
	height: 16px;
	transform: translateY(-50%);
	color: var(--ap-muted);
	pointer-events: none;
	stroke-width: 2;
}

.btn {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	white-space: nowrap;
	padding: 0 18px;
	border: 1px solid var(--ap-border-2);
	border-radius: var(--ap-radius-md);
	background: var(--ap-surface);
	color: var(--ap-fg);
	cursor: pointer;
	font: inherit;
	font-size: 13.5px;
	font-weight: 560;
	transition: background 0.14s, border-color 0.14s, color 0.14s, box-shadow 0.14s, opacity 0.14s;
}

.btn svg {
	width: 15px;
	height: 15px;
	stroke-width: 1.9;
}

.btn:hover:not(:disabled) {
	border-color: var(--ap-faint);
	background: var(--ap-surface-2);
}

.btn:disabled,
.icon-btn:disabled {
	cursor: default;
	opacity: 0.62;
}

.btn.sm {
	height: 34px;
	padding: 0 13px;
	font-size: 12.5px;
}

.check-status {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	margin-top: 9px;
	font-size: 12.5px;
	font-weight: 500;
}

.check-status .spin {
	width: 14px;
	height: 14px;
	border: 2px solid var(--ap-accent-soft);
	border-top-color: var(--ap-accent);
	border-radius: 50%;
	animation: spin 0.7s linear infinite;
}

.check-status.ok {
	color: var(--ap-price);
}

.check-status.ok svg {
	width: 15px;
	height: 15px;
	stroke-width: 2.2;
}

.check-status.error {
	color: oklch(55% 0.19 25);
}

.check-status.error svg {
	width: 15px;
	height: 15px;
	stroke-width: 2.2;
}

.help-link:disabled {
	opacity: 0.45;
	cursor: default;
}

.models {
	container-type: inline-size;
	overflow: hidden;
	border: 1px solid var(--ap-border);
	border-radius: var(--ap-radius-lg);
	background: var(--ap-surface-2);
}

.models-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 16px 18px;
}

.models-head h3 {
	margin: 0;
	color: var(--ap-fg);
	font-size: 14px;
	font-weight: 620;
}

.models-head .count {
	margin-top: 2px;
	color: var(--ap-muted);
	font-size: 12px;
}

.count-pill {
	padding: 4px 11px;
	border: 1px solid var(--ap-border-2);
	border-radius: 99px;
	background: var(--ap-surface);
	color: var(--ap-price);
	font-family: var(--ap-mono);
	font-size: 12px;
	font-weight: 600;
}

.models-toolbar {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 9px;
	padding: 0 18px 16px;
}

.models-toolbar .search {
	min-width: 176px;
	flex: 1 1 176px;
}

.models-toolbar .btn.sm {
	min-width: 0;
	justify-content: center;
}

.add-model-btn {
	width: 34px;
	height: 34px;
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
		flex-basis: calc(100% - 43px);
	}

	.add-model-btn {
		flex: 0 0 34px;
	}
}

.refresh-ico.spinning {
	animation: spin 0.8s linear infinite;
}

.model-list {
	border-top: 1px solid var(--ap-border);
}

.model {
	display: flex;
	align-items: center;
	gap: 14px;
	padding: 14px 18px;
	border-bottom: 1px solid var(--ap-border);
	background: var(--ap-surface);
}

.model:last-child {
	border-bottom: 0;
}

.m-logo {
	display: grid;
	width: 34px;
	height: 34px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--ap-border);
	border-radius: 8px;
	background: var(--ap-surface-2);
}

.m-logo :deep(svg) {
	width: 20px;
	height: 20px;
}

.m-logo :deep(.brand-logo) {
	width: 22px;
	height: 22px;
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
	color: var(--ap-fg);
	font-size: 14px;
	font-weight: 600;
}

.m-id {
	padding: 2px 7px;
	border: 1px solid var(--ap-border-2);
	border-radius: 5px;
	background: var(--ap-surface-2);
	color: var(--ap-fg-soft);
	font-family: var(--ap-mono);
	font-size: 11px;
}

.m-tags {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 7px;
	margin-top: 8px;
}

.tag {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	color: var(--ap-muted);
	font-size: 11.5px;
}

.tag svg {
	width: 13px;
	height: 13px;
	color: var(--ap-faint);
	stroke-width: 1.8;
}

.price-tag {
	padding: 3px 8px;
	border-radius: 6px;
	background: var(--ap-price-bg);
	color: var(--ap-price);
	font-size: 11.5px;
	font-weight: 560;
	letter-spacing: 0;
}

.m-actions {
	display: flex;
	align-items: center;
	gap: 12px;
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
	color: var(--ap-faint);
	cursor: pointer;
	transition: background 0.13s, color 0.13s;
}

.m-icon svg {
	width: 17px;
	height: 17px;
	stroke-width: 1.8;
}

.m-icon:hover {
	background: var(--ap-surface-2);
	color: var(--ap-fg-soft);
}

.provider-delete,
.model-delete {
	color: oklch(62% 0.11 25);
}

.provider-delete:hover,
.model-delete:hover {
	background: oklch(96% 0.025 25);
	color: oklch(52% 0.19 25);
}

.m-icon:disabled {
	opacity: 0.45;
	cursor: default;
}

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
	border-radius: 99px;
	background: var(--ap-border-2);
	transition: background 0.18s;
}

.knob {
	position: absolute;
	top: 3px;
	left: 3px;
	width: 18px;
	height: 18px;
	border-radius: 50%;
	background: #fff;
	box-shadow: 0 1px 3px rgba(0, 0, 0, 0.25);
	transition: transform 0.18s;
}

.switch input:checked + .track {
	background: var(--ap-switch-on);
}

.switch input:checked + .track + .knob {
	transform: translateX(18px);
}

.switch.big {
	width: 46px;
	height: 26px;
}

.switch.big .knob {
	width: 20px;
	height: 20px;
}

.switch.big input:checked + .track + .knob {
	transform: translateX(20px);
}

.models-empty,
.model-empty {
	padding: 40px;
	color: var(--ap-muted);
	font-size: 13px;
	text-align: center;
}

.toast {
	position: absolute;
	left: 50%;
	bottom: 24px;
	z-index: 50;
	display: flex;
	align-items: center;
	gap: 9px;
	padding: 10px 18px;
	transform: translateX(-50%) translateY(20px);
	border-radius: 10px;
	background: var(--ap-nav-bg);
	color: #fff;
	box-shadow: var(--ap-shadow-md);
	font-size: 13px;
	font-weight: 500;
	opacity: 0;
	pointer-events: none;
	transition: opacity 0.2s, transform 0.2s;
}

.toast svg {
	width: 16px;
	height: 16px;
	color: var(--ap-ok);
	stroke-width: 2;
}

.toast.show {
	transform: translateX(-50%) translateY(0);
	opacity: 1;
}

@keyframes spin {
	to {
		transform: rotate(360deg);
	}
}

@media (max-width: 980px) {
	.ai-providers {
		grid-template-columns: 244px minmax(0, 1fr);
	}

	.detail-body {
		padding: 22px 22px 48px;
	}

	.models-toolbar .search {
		flex-basis: 100%;
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
		border-bottom: 1px solid var(--ap-border);
	}

	.detail {
		min-height: 720px;
	}

	.detail-head {
		padding: 18px;
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

@media (prefers-reduced-motion: reduce) {
	.ai-providers *,
	.ai-providers *::before,
	.ai-providers *::after {
		animation-duration: 0.001ms !important;
		transition-duration: 0.001ms !important;
	}
}
</style>
