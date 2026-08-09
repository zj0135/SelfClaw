import { onMounted, reactive, ref } from 'vue';
import { useHostBridge, isSuperseded } from './hostBridge.js';

export function formatTokens(value) {
	if (typeof value === 'string' && /^(?:\d+(?:\.\d+)?)(?:K|M)$/.test(value)) return value;
	const number = Number(value);
	if (!Number.isFinite(number) || number <= 0) return '—';
	if (number >= 1_000_000) return `${trimNumber(number / 1_000_000)}M`;
	if (number >= 1_000) return `${trimNumber(number / 1_000)}K`;
	return String(number);
}

export function formatPrice(value) {
	if (value === null || value === undefined || value === '') return '—';
	if (typeof value === 'string' && /^\$\d/.test(value)) return value;
	const number = Number(value);
	if (!Number.isFinite(number) || number < 0) return '—';
	return `$${trimNumber(number, 4)}`;
}

export function useAiProviderHost(options) {
	const {
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
	} = options;

	const { request, requestLatest, post } = useHostBridge();

	const apiKeyInput = ref('');
	const apiKeyDirty = ref(false);
	const loadingState = ref(false);
	const mutating = ref(false);
	const providerDialogOpen = ref(false);
	const modelDialogOpen = ref(false);
	const pendingModelIds = reactive(new Set());
	const modelDraft = reactive({ name: '', model: '', apiFormat: 0 });
	// Custom providers are added by picking a wire protocol, not a built-in catalog entry.
	const customProtocols = reactive([]);
	const providerDraft = reactive({ name: '', protocolId: '', base: '' });

	async function loadState(preferredId = null) {
		loadingState.value = true;
		try {
			// requestLatest：连续触发的加载（如新增服务商后重载）只认最新一次的回包。
			const payload = await requestLatest('ai-providers/get-state', 'ai-providers/get-state');
			applyState(payload.state, preferredId);
		} catch (error) {
			if (isSuperseded(error)) return;
			showToast(errorMessage(error, '加载 AI 服务商失败'));
		} finally {
			loadingState.value = false;
		}
	}

	function applyState(state, preferredId = null) {
		const rawProviders = Array.isArray(state?.providers) ? state.providers : [];
		const previous = activeProvider.value;
		const nextProviders = rawProviders.map(normalizeProvider);
		providers.splice(0, providers.length, ...nextProviders);
		customProtocols.splice(
			0,
			customProtocols.length,
			...(Array.isArray(state?.customProtocols) ? state.customProtocols.map(normalizeProtocol) : []));

		const selected = (preferredId && nextProviders.find((provider) => provider.id === preferredId))
			|| findMatchingProvider(previous, nextProviders)
			|| nextProviders.find((provider) => provider.isConfigured)
			|| nextProviders[0];
		activeId.value = selected?.id || '';
		syncActiveProviderInputs();
	}

	function selectProvider(id) {
		activeId.value = id;
		apiKeyVisible.value = false;
		resetCheckStatus();
		syncActiveProviderInputs();
	}

	function syncActiveProviderInputs() {
		const provider = activeProvider.value;
		// The input is a fresh draft, never the mask: an empty box means "leave the stored key
		// unchanged", so the mask can never be edited back into the request as a new key.
		apiKeyInput.value = '';
		apiKeyDirty.value = false;
		selectedCheckModel.value = provider?.models?.[0]?.profileId || '';
	}

	function markApiKeyDirty() {
		apiKeyDirty.value = true;
	}

	async function saveApiKey() {
		const provider = activeProvider.value;
		if (!provider || !apiKeyDirty.value) return;

		await runMutation(async () => {
			await saveProvider(provider, apiKeyInput.value);
			const cleared = !apiKeyInput.value;
			apiKeyInput.value = '';
			apiKeyDirty.value = false;
			showToast(cleared ? 'API Key 已清除' : 'API Key 已保存');
		}, '保存 API Key 失败');
	}

	async function saveApiBase() {
		const provider = activeProvider.value;
		if (!provider) return;
		if (!isAbsoluteUrl(provider.base)) {
			showToast('请输入完整的 API 地址');
			return;
		}

		await runMutation(async () => {
			await saveProvider(provider, null);
			showToast('代理地址已保存');
		}, '保存代理地址失败');
	}

	async function setProviderEnabled(enabled) {
		const provider = activeProvider.value;
		if (!provider) return;

		await runMutation(async () => {
			if (!provider.connectionId) {
				if (!enabled) return;
				await saveProvider(provider, null);
			} else {
				await request('ai-providers/set-provider-enabled', {
					providerId: provider.connectionId,
					enabled,
				});
				provider.enabled = enabled;
			}
			showToast(enabled ? `已启用 ${provider.name}` : `已禁用 ${provider.name}`);
		}, enabled ? '启用服务商失败' : '禁用服务商失败');
	}

	async function deleteProvider() {
		const provider = activeProvider.value;
		if (!provider?.connectionId) return;

		await runMutation(async () => {
			await request('ai-providers/delete-provider', { providerId: provider.connectionId });
			await loadState();
			showToast(`${provider.name} 已删除`);
		}, '删除服务商失败');
	}

	async function setAllModelsEnabled(enabled) {
		const provider = activeProvider.value;
		if (!provider?.connectionId || provider.models.length === 0) return;

		await runMutation(async () => {
			await request('ai-providers/set-all-models-enabled', {
				providerId: provider.connectionId,
				enabled,
			});
			provider.models.forEach((model) => { model.on = enabled; });
			showToast(enabled ? '已启用全部模型' : '已禁用全部模型');
		}, '更新模型状态失败');
	}

	async function setModelEnabled(model, enabled) {
		if (!model?.profileId || pendingModelIds.has(model.profileId)) return;

		pendingModelIds.add(model.profileId);
		try {
			await request('ai-providers/set-model-enabled', {
				modelProfileId: model.profileId,
				enabled,
			});
			model.on = enabled;
		} catch (error) {
			showToast(errorMessage(error, '更新模型状态失败'));
		} finally {
			pendingModelIds.delete(model.profileId);
		}
	}

	async function deleteModel(model) {
		const provider = activeProvider.value;
		if (!provider?.connectionId || !model?.profileId || !window.confirm(`删除模型 ${model.name}？`)) return;

		pendingModelIds.add(model.profileId);
		try {
			await request('ai-providers/delete-model', { modelProfileId: model.profileId });
			provider.models = provider.models.filter((item) => item.profileId !== model.profileId);
			provider.total = provider.models.length;
			if (selectedCheckModel.value === model.profileId) {
				selectedCheckModel.value = provider.models[0]?.profileId || '';
			}
			showToast('模型已删除');
		} catch (error) {
			showToast(errorMessage(error, '删除模型失败'));
		} finally {
			pendingModelIds.delete(model.profileId);
		}
	}

	async function fetchModelList() {
		const provider = activeProvider.value;
		if (!provider?.connectionId || !provider.supportsModelListing || fetchingModels.value) return;

		fetchingModels.value = true;
		try {
			const payload = await request('ai-providers/fetch-models', { providerId: provider.connectionId });
			provider.models = Array.isArray(payload.models) ? payload.models.map(normalizeModel) : [];
			provider.total = provider.models.length;
			selectedCheckModel.value = provider.models[0]?.profileId || '';
			showToast('模型列表已更新');
		} catch (error) {
			showToast(errorMessage(error, '获取模型列表失败'));
		} finally {
			fetchingModels.value = false;
		}
	}

	async function checkConnectivity() {
		const provider = activeProvider.value;
		if (!provider?.connectionId || !selectedCheckModel.value || checking.value) return;

		checking.value = true;
		checkStatus.visible = true;
		checkStatus.state = 'loading';
		checkStatus.text = '正在检查连通性…';
		try {
			const payload = await request('ai-providers/check', {
				providerId: provider.connectionId,
				modelProfileId: selectedCheckModel.value,
			});
			checkStatus.state = payload.ok ? 'ok' : 'error';
			checkStatus.text = payload.ok
				? `连接正常 · 延迟 ${payload.latencyMs}ms`
				: (payload.error || '连接检查失败');
		} catch (error) {
			checkStatus.state = 'error';
			checkStatus.text = errorMessage(error, '连接检查失败');
		} finally {
			checking.value = false;
		}
	}

	function openProviderConsole() {
		const href = activeProvider.value?.getApiKeyUrl;
		if (!href) {
			showToast('该服务商没有配置控制台地址');
			return;
		}

		post({ type: 'open-link', href });
	}

	function openProviderDialog() {
		providerDraft.name = '';
		providerDraft.protocolId = customProtocols[0]?.id || '';
		providerDraft.base = '';
		providerDialogOpen.value = true;
	}

	async function createCustomProvider() {
		const protocol = customProtocols.find((entry) => entry.id === providerDraft.protocolId);
		const name = providerDraft.name.trim();
		const base = providerDraft.base.trim();
		if (!protocol || !name) {
			showToast('请填写服务商名称并选择协议类型');
			return;
		}
		if (!isAbsoluteUrl(base)) {
			showToast('请输入完整的 Base URL');
			return;
		}

		await runMutation(async () => {
			const payload = await request('ai-providers/save-provider', {
				catalogId: 'custom',
				name,
				base,
				apiKey: null,
				providerKind: protocol.providerKind,
				apiFormat: protocol.defaultApiFormat,
				connectionOptions: {},
				// New connections start disabled; the user enables them after adding a key/models.
				enabled: false,
			});
			providerDialogOpen.value = false;
			await loadState(payload.provider?.connectionId || null);
			showToast(`已添加 ${name}`);
		}, '添加服务商失败');
	}

	function openModelDialog() {
		const provider = activeProvider.value;
		if (!provider?.connectionId) {
			showToast('请先创建服务商连接');
			return;
		}
		modelDraft.name = '';
		modelDraft.model = '';
		modelDraft.apiFormat = provider.defaultApiFormat;
		modelDialogOpen.value = true;
	}

	async function createModel() {
		const provider = activeProvider.value;
		if (!provider?.connectionId || !modelDraft.name.trim() || !modelDraft.model.trim()) return;

		await runMutation(async () => {
			const payload = await request('ai-providers/upsert-model', {
				providerConnectionId: provider.connectionId,
				name: modelDraft.name.trim(),
				model: modelDraft.model.trim(),
				apiFormat: modelDraft.apiFormat,
				enabled: true,
				modelOptions: {},
			});
			const created = normalizeModel(payload.model);
			provider.models.push(created);
			provider.total = provider.models.length;
			if (!selectedCheckModel.value) selectedCheckModel.value = created.profileId;
			modelDialogOpen.value = false;
			showToast('模型已添加');
		}, '添加模型失败');
	}

	async function saveProvider(provider, apiKey) {
		const payload = await request('ai-providers/save-provider', {
			id: provider.connectionId,
			catalogId: provider.catalogId,
			name: provider.name,
			base: provider.base,
			apiKey,
			connectionOptions: provider.connectionOptions || {},
		});
		return replaceProvider(provider, payload.provider);
	}

	function replaceProvider(previous, rawProvider) {
		const normalized = normalizeProvider(rawProvider);
		const index = providers.findIndex((provider) => provider.id === previous.id);
		if (index >= 0) providers.splice(index, 1, normalized);
		else providers.push(normalized);
		activeId.value = normalized.id;
		return normalized;
	}

	async function runMutation(action, fallbackMessage) {
		if (mutating.value) return;
		mutating.value = true;
		try {
			await action();
		} catch (error) {
			showToast(errorMessage(error, fallbackMessage));
		} finally {
			mutating.value = false;
		}
	}

	onMounted(loadState);

	return {
		apiKeyInput,
		apiKeyDirty,
		loadingState,
		mutating,
		providerDialogOpen,
		modelDialogOpen,
		modelDraft,
		customProtocols,
		providerDraft,
		pendingModelIds,
		selectProvider,
		markApiKeyDirty,
		saveApiKey,
		saveApiBase,
		setProviderEnabled,
		deleteProvider,
		setAllModelsEnabled,
		setModelEnabled,
		deleteModel,
		fetchModelList,
		checkConnectivity,
		openProviderConsole,
		openProviderDialog,
		createCustomProvider,
		openModelDialog,
		createModel,
	};
}

function normalizeProvider(raw) {
	const catalogId = raw?.catalogId || 'custom';
	const connectionId = raw?.connectionId || null;
	const models = Array.isArray(raw?.models) ? raw.models.map(normalizeModel) : [];
	return {
		...raw,
		id: connectionId || `catalog:${catalogId}`,
		connectionId,
		catalogId,
		kind: catalogId,
		name: raw?.name || catalogId,
		sub: raw?.sub || '',
		color: raw?.color || '#6B7280',
		enabled: Boolean(raw?.enabled),
		isConfigured: Boolean(raw?.isConfigured),
		keyMask: raw?.keyMask || '',
		base: raw?.base || '',
		authKind: Number(raw?.authKind ?? 0),
		providerKind: Number(raw?.providerKind ?? 0),
		getApiKeyUrl: raw?.getApiKeyUrl || '',
		supportsModelListing: Boolean(raw?.supportsModelListing),
		defaultApiFormat: Number(raw?.defaultApiFormat ?? 0),
		supportedFormats: Array.isArray(raw?.supportedFormats) ? raw.supportedFormats.map(Number) : [0],
		connectionOptions: raw?.connectionOptions || {},
		models,
		total: Number(raw?.total ?? models.length),
	};
}

function normalizeProtocol(raw) {
	return {
		id: raw?.id || '',
		label: raw?.label || raw?.id || '',
		providerKind: Number(raw?.providerKind ?? 0),
		defaultApiFormat: Number(raw?.defaultApiFormat ?? 0),
		authKind: Number(raw?.authKind ?? 0),
		supportsModelListing: Boolean(raw?.supportsModelListing),
	};
}

function normalizeModel(raw) {
	return {
		...raw,
		profileId: raw?.modelProfileId || raw?.profileId || raw?.id,
		name: raw?.name || raw?.model || 'Model',
		id: raw?.model || raw?.id || '',
		apiFormat: Number(raw?.apiFormat ?? 0),
		ctx: formatTokens(raw?.contextLength ?? raw?.ctx),
		out: formatTokens(raw?.maxOutputTokens ?? raw?.out),
		inp: formatPrice(raw?.priceInPerMTok ?? raw?.inp),
		outp: formatPrice(raw?.priceOutPerMTok ?? raw?.outp),
		cacheW: nullablePrice(raw?.priceCacheWritePerMTok ?? raw?.cacheW),
		cacheR: nullablePrice(raw?.priceCacheReadPerMTok ?? raw?.cacheR),
		on: Boolean(raw?.enabled ?? raw?.on),
	};
}

function findMatchingProvider(previous, candidates) {
	if (!previous) return null;
	return candidates.find((provider) => previous.connectionId && provider.connectionId === previous.connectionId)
		|| candidates.find((provider) => provider.catalogId === previous.catalogId);
}

function nullablePrice(value) {
	if (value === null || value === undefined || value === '' || value === '—') return null;
	return formatPrice(value);
}

function trimNumber(value, maximumFractionDigits = 2) {
	return Number(value).toLocaleString('en-US', {
		useGrouping: false,
		maximumFractionDigits,
	});
}

function isAbsoluteUrl(value) {
	try {
		return Boolean(new URL(value));
	} catch {
		return false;
	}
}

function errorMessage(error, fallback) {
	return error?.message ? `${fallback}：${error.message}` : fallback;
}
