import { reactive, ref } from 'vue';
import { useHostBridge } from './hostBridge.js';

export function useModelConfigurationEditor(model, onSaved, provider = null) {
	const { request } = useHostBridge();
	const source = model.configuration ?? model;
	const options = model.modelOptions ?? {};
	const profileId = model.profileId || model.modelProfileId || null;
	const providerConnectionId = model.providerConnectionId || provider?.connectionId || null;
	const hasProfile = Boolean(profileId && providerConnectionId);

	const draft = reactive({
		model: model.model ?? model.id,
		name: source.name ?? model.name,
		apiFormat: model.apiFormat != null ? Number(model.apiFormat) : Number(provider?.defaultApiFormat ?? 0),
		hasProfile,
		sampling: { temperatureEnabled: false, temperature: 0.7, topPEnabled: false, topP: 0.7, ...source.sampling },
		isMultimodal: source.isMultimodal ?? null,
		reasoningEffort: model.configuration
			? (source.reasoningEffort ?? '')
			: (source.reasoningEffort ?? options['reasoning.effort'] ?? options.reasoning_effort ?? ''),
		contextLength: source.contextLength ?? model.contextLength ?? '',
		maxOutputTokens: source.maxOutputTokens ?? model.maxOutputTokens ?? '',
		priceInPerMTok: source.priceInPerMTok ?? model.priceInPerMTok ?? '',
		priceOutPerMTok: source.priceOutPerMTok ?? model.priceOutPerMTok ?? '',
		priceCacheReadPerMTok: source.priceCacheReadPerMTok ?? model.priceCacheReadPerMTok ?? '',
		priceCacheWritePerMTok: source.priceCacheWritePerMTok ?? model.priceCacheWritePerMTok ?? '',
		description: source.description ?? '',
	});
	const busy = ref(false);
	const error = ref('');

	async function save() {
		if (busy.value) return;
		error.value = '';
		const configuration = { ...draft, sampling: { ...draft.sampling } };
		for (const key of ['contextLength', 'maxOutputTokens', 'priceInPerMTok', 'priceOutPerMTok', 'priceCacheReadPerMTok', 'priceCacheWritePerMTok']) {
			configuration[key] = draft[key] === '' || draft[key] == null ? null : Number(draft[key]);
		}
		configuration.reasoningEffort ||= null;
		if (configuration.contextLength && configuration.maxOutputTokens >= configuration.contextLength) {
			error.value = '最大输出 token 数必须小于上下文长度。';
			return;
		}
		busy.value = true;
		try {
			if (hasProfile) {
				await request('ai-providers/upsert-model', {
					id: profileId,
					providerConnectionId,
					name: draft.name,
					model: draft.model,
					apiFormat: Number(draft.apiFormat),
				});
				model.apiFormat = Number(draft.apiFormat);
			}
			const payload = await request('ai-providers/save-model-configuration', { configuration });
			onSaved(payload.configuration);
		} catch (failure) {
			error.value = failure.message || '模型参数保存失败';
		} finally {
			busy.value = false;
		}
	}

	return { draft, busy, error, save };
}
