<script setup>
import { computed, reactive, ref } from 'vue';
import {
	Search,
	Plus,
	Trash2,
	ArrowUpRight,
	Eye,
	EyeOff,
	Check,
	ChevronDown,
	Activity,
	KeyRound,
	Globe,
} from 'lucide-vue-next';
import AiProviderDialogs from './AiProviderDialogs.vue';
import ModelConfigurationDialog from './ModelConfigurationDialog.vue';
import AiProviderModelList from './AiProviderModelList.vue';
import { useAiProviderHost } from '../../composables/useAiProviderHost.js';
import { useToast } from '../../composables/useToast.js';

// Brand logos from @lobehub/icons-static-png (color variants where available).
import openaiLogo from '@lobehub/icons-static-png/light/openai.png';
import anthropicLogo from '@lobehub/icons-static-png/light/claude-color.png';
import ollamaLogo from '@lobehub/icons-static-png/light/ollama.png';

// Maps a catalog id to its brand logo asset. Custom connections fall back to OpenAI.
const providerLogos = {
	openai: openaiLogo,
	anthropic: anthropicLogo,
	ollama: ollamaLogo,
};

// Filled by applyState in useAiProviderHost on mount; empty until the host replies,
// so the detail pane is guarded by v-if="activeProvider".
const providers = reactive([]);

const activeId = ref('');
const providerSearch = ref('');
const apiKeyVisible = ref(false);
// Set from the active provider's first profile once host state loads.
const selectedCheckModel = ref('');
const checking = ref(false);
const fetchingModels = ref(false);
const checkStatus = reactive({ visible: false, state: '', text: '' });
const { showToast } = useToast();
const configuringModel = ref(null);
const configurationTab = ref('parameters');

const activeProvider = computed(() => providers.find((provider) => provider.id === activeId.value) || providers[0]);

const {
	loadState,
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
	return providerLogos[providerKind(provider)] || providerLogos.openai;
}

function resetCheckStatus() {
	checking.value = false;
	checkStatus.visible = false;
	checkStatus.state = '';
	checkStatus.text = '';
}

function openConfiguration(model, tab = 'parameters') {
	configurationTab.value = tab;
	configuringModel.value = model;
}

function configurationSaved() {
	configuringModel.value = null;
	showToast('模型配置已保存');
	loadState();
}
</script>

<template>
	<div class="ai-providers sc-root sc-stage">
		<!-- Left: Providers navigation index -->
		<aside class="provider-list">
			<div class="list-top sc-rise" style="--i: 0">
				<div class="list-controls">
					<div class="search">
						<Search :size="14" :stroke-width="2" class="search-ico" aria-hidden="true" />
						<input v-model="providerSearch" type="text" placeholder="搜索服务商..." aria-label="搜索服务商" />
					</div>
					<button class="icon-btn" type="button" title="添加自定义服务商" aria-label="添加服务商" @click="openProviderDialog">
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
							<span class="p-logo" aria-hidden="true">
								<img :src="providerLogo(provider)" alt="" class="brand-logo" draggable="false" />
							</span>
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

		<!-- Right: Active Provider Detail -->
		<main class="detail">
			<div v-if="!activeProvider" class="detail-empty">
				{{ loadingState ? '正在加载服务商…' : '没有可用的服务商' }}
			</div>

			<template v-else>
				<!-- Header Card -->
				<header class="detail-head sc-rise" style="--i: 0">
					<div class="dh-logo" aria-hidden="true">
						<img :src="providerLogo(activeProvider)" alt="" class="brand-logo" draggable="false" />
					</div>
					<div class="dh-meta">
						<div class="dh-status-row">
							<span class="status-pill" :class="activeProvider.enabled ? 'online' : 'offline'">
								<span class="status-led"></span>
								{{ activeProvider.enabled ? '已启用' : '已禁用' }}
							</span>
						</div>
						<h2>{{ activeProvider.name }}</h2>
					</div>

					<div class="dh-actions">
						<button
							v-if="activeProvider.connectionId"
							class="m-icon provider-delete"
							type="button"
							title="删除此服务商连接"
							aria-label="删除服务商连接"
							:disabled="mutating"
							@click="deleteProvider"
						>
							<Trash2 :size="16" :stroke-width="1.9" />
						</button>
						<label class="switch big" title="启用/禁用此服务商">
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
					</div>
				</header>

				<!-- Detail Content Stack -->
				<div class="detail-body scroll">
					<!-- Section 1: Credentials & Endpoint -->
					<section class="deck-card sc-rise" style="--i: 1">
						<div class="card-head">
							<h4 class="card-title">凭证与端点配置</h4>
						</div>

						<div class="deck-fields">
							<!-- API Key -->
							<div v-if="activeProvider.authKind !== 1" class="field-item">
								<div class="field-topline">
									<label class="fl" for="api-key">
										<KeyRound :size="13" :stroke-width="2" class="fl-ico" />
										<span>API Key</span>
									</label>
									<button
										class="help-link-chip"
										type="button"
										:disabled="!activeProvider.getApiKeyUrl"
										@click="openProviderConsoleFromHost"
									>
										<span>获取 API Key</span>
										<ArrowUpRight :size="12" :stroke-width="2.2" />
									</button>
								</div>
								<div class="input-wrap">
									<input
										id="api-key"
										v-model="apiKeyInput"
										class="input mono"
										:type="apiKeyVisible ? 'text' : 'password'"
										aria-label="API Key"
										:placeholder="activeProvider?.keyMask || '输入 API Key / Token 密钥'"
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

							<!-- API Base URL -->
							<div class="field-item">
								<div class="field-topline">
									<label class="fl" for="api-base">
										<Globe :size="13" :stroke-width="2" class="fl-ico" />
										<span>API 代理与端点地址 (Base URL)</span>
									</label>
								</div>
								<div class="input-wrap">
									<input
										id="api-base"
										v-model="activeProvider.base"
										class="input mono"
										type="text"
										aria-label="API 代理地址"
										placeholder="https://api.example.com/v1"
										@change="saveApiBaseToHost"
									/>
								</div>
							</div>
						</div>
					</section>

					<!-- Section 2: Connectivity Diagnostic -->
					<section class="deck-card sc-rise" style="--i: 2">
						<div class="card-head">
							<h4 class="card-title">连通性检查与状态探测</h4>
						</div>

						<div class="check-box">
							<div class="check-row">
								<div class="select-wrap">
									<select id="check-model" v-model="selectedCheckModel" aria-label="选择检查模型">
										<option v-if="!activeProvider.models.length" value="">无可用测试模型</option>
										<option v-for="model in activeProvider.models" :key="model.profileId" :value="model.profileId">
											{{ model.name }}
										</option>
									</select>
									<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
								</div>
								<button
									class="btn-check"
									type="button"
									:disabled="checking || !activeProvider.connectionId || !activeProvider.models.length"
									@click="checkConnectivityFromHost"
								>
									<Activity :size="14" :stroke-width="2.2" :class="{ spinning: checking }" />
									<span>{{ checking ? '检查中…' : '检查' }}</span>
								</button>
							</div>

							<div v-if="checkStatus.visible" class="check-status show" :class="checkStatus.state">
								<span v-if="checkStatus.state === 'loading'" class="spin" aria-hidden="true"></span>
								<Check v-else-if="checkStatus.state === 'ok'" :size="14" :stroke-width="2.4" />
								<span v-else class="err-dot" aria-hidden="true"></span>
								<span class="status-msg">{{ checkStatus.text }}</span>
							</div>
						</div>
					</section>

					<!-- Section 3: Model Registry -->
					<div class="sc-rise" style="--i: 3">
						<AiProviderModelList
							class="models-field"
							:provider="activeProvider"
							:logo="providerLogo(activeProvider)"
							:busy="mutating"
							:fetching="fetchingModels"
							:pending-model-ids="pendingModelIds"
							@set-all-enabled="setAllModelsEnabled"
							@fetch-models="fetchModelListFromHost"
							@add-model="openModelDialog"
							@configure="openConfiguration"
							@delete-model="deleteModel"
							@set-enabled="setModelEnabled"
						/>
					</div>
				</div>
			</template>
		</main>

		<!-- Dialogs -->
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
		<ModelConfigurationDialog
			v-if="configuringModel"
			:model="configuringModel"
			:initial-tab="configurationTab"
			@close="configuringModel = null"
			@saved="configurationSaved"
		/>
	</div>
</template>

<style scoped>
@import '../../styles/settings-console.css';

.ai-providers {
	position: relative;
	display: grid;
	grid-template-columns: 290px minmax(0, 1fr);
	width: 100%;
	height: 100%;
	min-height: 0;
	overflow: hidden;
	color: var(--sc-text);
	font-family: var(--sc-sans);
	font-size: var(--fs-14);
	line-height: 1.5;
}

.ai-providers * {
	box-sizing: border-box;
}

.scroll {
	scrollbar-width: thin;
	scrollbar-color: var(--sc-line-2) transparent;
}

.scroll::-webkit-scrollbar {
	width: 7px;
	height: 7px;
}

.scroll::-webkit-scrollbar-thumb {
	background: var(--sc-line-2);
	border-radius: 99px;
}

/* ── left index ─────────────────────────────────────────────── */
.provider-list {
	display: flex;
	min-height: 0;
	flex-direction: column;
	border-right: 1px solid var(--sc-line);
	background: color-mix(in srgb, var(--sc-panel) 82%, var(--sc-bg));
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
	font-size: var(--fs-10);
	font-weight: 650;
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
	padding: 8px 10px 8px 32px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-125);
	outline: none;
	transition: border-color 0.16s;
}

.search input:focus {
	border-color: var(--sc-acid);
}

.icon-btn {
	display: grid;
	width: 36px;
	height: 36px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-soft);
	cursor: pointer;
	transition: all 0.16s var(--sc-ease-spring);
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
	font-size: var(--fs-11);
	font-weight: 600;
	letter-spacing: 0.05em;
}

.grp-en {
	margin-left: auto;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 500;
	letter-spacing: 0.2em;
}

.grp-dot {
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: var(--sc-faint);
}

.grp-dot.live {
	background: var(--sc-ok);
}

.prov {
	position: relative;
	display: flex;
	align-items: center;
	width: 100%;
	gap: 10px;
	padding: 10px 12px;
	margin-bottom: 3px;
	border: 1px solid transparent;
	border-radius: 10px;
	background: transparent;
	color: inherit;
	text-align: left;
	cursor: pointer;
	user-select: none;
	transition: all 0.16s var(--sc-ease-out);
}

.prov:hover {
	background: var(--sc-hover);
}

.prov.active {
	border-color: var(--sc-line-2);
	background: var(--sc-panel);
}

/* Clean solid indicator without colored neon blur */
.prov.active::before {
	position: absolute;
	top: 50%;
	left: 0;
	width: 3px;
	height: 22px;
	transform: translateY(-50%);
	border-radius: 0 2px 2px 0;
	background: var(--sc-acid);
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
	font-size: var(--fs-10);
	letter-spacing: 0.06em;
	transition: color 0.15s;
}

.prov.active .p-index {
	color: var(--sc-acid);
	font-weight: 600;
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
	background: var(--sc-raise);
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
	font-size: var(--fs-135);
	font-weight: 600;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.p-sub {
	margin-top: 1px;
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-105);
	letter-spacing: 0.02em;
}

.dot {
	width: 6px;
	height: 6px;
	flex: 0 0 auto;
	border-radius: 50%;
	background: var(--sc-faint);
	transition: all 0.18s;
}

.prov.on .dot {
	background: var(--sc-ok);
}

.provider-empty {
	padding: 30px 10px;
	color: var(--sc-mute);
	font-size: var(--fs-13);
	text-align: center;
}

.detail-empty {
	display: grid;
	place-items: center;
	height: 100%;
	color: var(--sc-mute);
	font-size: var(--fs-13);
}

/* ── detail ─────────────────────────────────────────────────── */
.detail {
	position: relative;
	display: flex;
	min-width: 0;
	min-height: 0;
	flex: 1;
	flex-direction: column;
	overflow: hidden;
	background: var(--sc-bg);
}

.detail-head {
	position: relative;
	z-index: 1;
	display: flex;
	align-items: center;
	gap: 18px;
	padding: 24px 34px 20px;
	border-bottom: 1px solid var(--sc-line);
	background: linear-gradient(to bottom, var(--sc-raise), transparent);
}

.dh-logo {
	display: grid;
	width: 54px;
	height: 54px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line-2);
	border-radius: 14px;
	background: var(--sc-panel);
}

.dh-logo :deep(.brand-logo) {
	width: 32px;
	height: 32px;
	object-fit: contain;
}

.dh-meta {
	min-width: 0;
	flex: 1;
}

.dh-kicker-row {
	display: flex;
	align-items: center;
	gap: 10px;
	margin-bottom: 4px;
}

.dh-kicker {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-weight: 650;
	letter-spacing: 0.2em;
}

.status-pill {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 2px 8px;
	border-radius: 99px;
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.1em;
}

.status-pill.online {
	border: 1px solid color-mix(in srgb, var(--sc-ok) 35%, transparent);
	background: var(--sc-ok-soft);
	color: var(--sc-ok);
}

.status-pill.offline {
	border: 1px solid var(--sc-line-2);
	background: var(--sc-raise);
	color: var(--sc-mute);
}

.status-led {
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: currentColor;
}

.dh-meta h2 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: var(--fs-25, 25px);
	font-weight: 650;
	letter-spacing: 0.01em;
	line-height: 1.15;
}

.dh-desc {
	margin: 4px 0 0;
	color: var(--sc-mute);
	font-size: var(--fs-125);
}

.dh-actions {
	display: flex;
	align-items: center;
	gap: 14px;
}

.detail-body {
	position: relative;
	z-index: 1;
	min-height: 0;
	max-width: 960px;
	flex: 1;
	overflow-y: auto;
	padding: 24px 34px 72px;
	display: flex;
	flex-direction: column;
	gap: 20px;
}

/* Deck Card */
.deck-card {
	padding: 18px 22px;
	border: 1px solid var(--sc-line);
	border-radius: 12px;
	background: var(--sc-panel);
}

.deck-card:hover {
	border-color: var(--sc-line-2);
}

.card-head {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
	margin-bottom: 14px;
	padding-bottom: 10px;
	border-bottom: 1px solid var(--sc-line);
}

.card-kicker {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.18em;
}

.card-title {
	margin: 0;
	color: var(--sc-soft);
	font-size: var(--fs-13);
	font-weight: 600;
}

.deck-fields {
	display: flex;
	flex-direction: column;
	gap: 14px;
}

.field-item {
	display: flex;
	flex-direction: column;
	gap: 6px;
}

.field-topline {
	display: flex;
	align-items: center;
	justify-content: space-between;
}

.fl {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	color: var(--sc-text);
	font-size: var(--fs-125);
	font-weight: 600;
}

.fl-ico {
	color: var(--sc-faint);
}

.help-link-chip {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	padding: 3px 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 99px;
	background: var(--sc-raise);
	color: var(--sc-soft);
	font-family: var(--sc-sans);
	font-size: var(--fs-11);
	font-weight: 600;
	cursor: pointer;
	transition: all 0.15s;
}

.help-link-chip:hover:not(:disabled) {
	border-color: var(--sc-acid);
	color: var(--sc-acid);
	background: var(--sc-panel);
}

.help-link-chip:disabled {
	opacity: 0.4;
	cursor: default;
}

.input-wrap {
	position: relative;
	display: flex;
	align-items: center;
}

.input {
	width: 100%;
	height: 38px;
	padding: 0 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-13);
	outline: none;
	transition: border-color 0.16s;
}

.input.mono {
	font-family: var(--sc-mono);
	font-size: var(--fs-12);
}

.input:focus {
	border-color: var(--sc-acid);
}

.input-wrap .input {
	padding-right: 40px;
}

.reveal {
	position: absolute;
	right: 6px;
	display: grid;
	width: 28px;
	height: 28px;
	place-items: center;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--sc-mute);
	cursor: pointer;
	transition: all 0.15s;
}

.reveal:hover {
	background: var(--sc-hover);
	color: var(--sc-text);
}

/* Connectivity Check */
.check-box {
	display: flex;
	flex-direction: column;
	gap: 12px;
}

.check-row {
	display: flex;
	align-items: center;
	gap: 10px;
	width: 100%;
}

.select-wrap {
	position: relative;
	flex: 1;
	min-width: 0;
	display: flex;
	align-items: center;
}

.select-wrap select {
	width: 100%;
	height: 38px;
	padding: 0 34px 0 12px;
	appearance: none;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	cursor: pointer;
	font: inherit;
	font-size: var(--fs-13);
	outline: none;
	transition: border-color 0.16s;
}

.select-wrap select:focus {
	border-color: var(--sc-acid);
}

.select-wrap option {
	background: var(--sc-panel);
	color: var(--sc-text);
}

.select-wrap .chev {
	position: absolute;
	right: 12px;
	color: var(--sc-faint);
	pointer-events: none;
}

.btn-check {
	flex: 0 0 auto;
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 6px;
	height: 38px;
	padding: 0 16px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-raise);
	color: var(--sc-text);
	cursor: pointer;
	font-family: inherit;
	font-size: var(--fs-125);
	font-weight: 600;
	white-space: nowrap;
	transition: all 0.16s var(--sc-ease-spring);
}

.btn-check:hover:not(:disabled) {
	border-color: var(--sc-acid);
	color: var(--sc-acid);
	background: var(--sc-hover);
	transform: translateY(-1px);
}

.btn-check:disabled {
	opacity: 0.45;
	cursor: not-allowed;
	transform: none;
}

.check-status {
	display: inline-flex;
	align-items: center;
	gap: 8px;
	padding: 8px 12px;
	border-radius: 8px;
	font-family: var(--sc-mono);
	font-size: var(--fs-12);
	font-weight: 550;
	animation: sc-pop 0.2s var(--sc-ease-spring);
}

.check-status.loading {
	border: 1px solid var(--sc-line-2);
	background: var(--sc-raise);
	color: var(--sc-mute);
}

.check-status.ok {
	border: 1px solid color-mix(in srgb, var(--sc-ok) 35%, transparent);
	background: var(--sc-ok-soft);
	color: var(--sc-ok);
}

.check-status.error {
	border: 1px solid color-mix(in srgb, var(--sc-err) 35%, transparent);
	background: var(--sc-err-soft);
	color: var(--sc-err);
}

.check-status .spin {
	width: 14px;
	height: 14px;
	border: 2px solid var(--sc-line-2);
	border-top-color: var(--sc-acid);
	border-radius: 50%;
	animation: sc-spin 0.7s linear infinite;
}

.err-dot {
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: var(--sc-err);
}

.m-icon {
	display: grid;
	width: 34px;
	height: 34px;
	place-items: center;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-faint);
	cursor: pointer;
	transition: all 0.16s var(--sc-ease-spring);
}

.m-icon:hover {
	border-color: var(--sc-line-2);
	background: var(--sc-hover);
	color: var(--sc-soft);
	transform: translateY(-1px);
}

.provider-delete:hover {
	border-color: color-mix(in srgb, var(--sc-err) 40%, transparent);
	background: var(--sc-err-soft);
	color: var(--sc-err);
}

.m-icon:disabled {
	opacity: 0.4;
	cursor: default;
	transform: none;
}

/* ── switches (clean, no neon halo) ─────────────────────────── */
.switch {
	position: relative;
	width: 44px;
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
	transition: background 0.22s, border-color 0.22s;
}

.knob {
	position: absolute;
	top: 3px;
	left: 3px;
	width: 18px;
	height: 18px;
	border-radius: 50%;
	background: var(--sc-panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.15);
	transition: transform 0.22s var(--sc-ease-spring), background 0.2s;
}

.switch input:checked + .track {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
}

.switch input:checked + .track + .knob {
	transform: translateX(18px);
	background: var(--sc-acid-ink);
}

.switch.big {
	width: 48px;
	height: 26px;
}

.switch.big .knob {
	width: 20px;
	height: 20px;
	top: 2px;
	left: 2px;
}

.switch.big input:checked + .track + .knob {
	transform: translateX(22px);
}

@media (max-width: 980px) {
	.ai-providers {
		grid-template-columns: 260px minmax(0, 1fr);
	}

	.detail-body {
		padding: 20px 20px 56px;
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
}
</style>
