export const emptyProfile = () => ({
	profileId: null,
	name: '',
	endpoint: '',
	model: '',
	modelOptions: [],
	isFetchingModels: false,
	fetchModelsRequestId: 0,
	temperatureEnabled: false,
	temperature: 0.7,
	topPEnabled: false,
	topP: 0.7,
	apiKey: '',
});

export const emptyWorkspace = () => ({
	workspaceRootId: null,
	name: '',
	rootPath: '',
});

export const emptyChannel = () => ({
	channelId: 'feishu',
	displayName: '',
	profileId: '',
	fields: [],
});

export function normalizeSamplingValue(value, fallback, max) {
	const numeric = Number(value);
	if (Number.isNaN(numeric) || !Number.isFinite(numeric)) {
		return fallback;
	}

	return Math.max(0, Math.min(max, Number(numeric.toFixed(2))));
}

export function formatSamplingValue(value, max) {
	return normalizeSamplingValue(value, 0.7, max).toFixed(2);
}

export function createProfileDraft(profile, selectedProfileModel) {
	const model = selectedProfileModel || '';
	return {
		profileId: profile?.id || null,
		name: profile?.label || '',
		endpoint: profile?.description || '',
		model,
		modelOptions: model ? [model] : [],
		isFetchingModels: false,
		fetchModelsRequestId: 0,
		temperatureEnabled: Boolean(profile?.temperatureEnabled),
		temperature: normalizeSamplingValue(profile?.temperature, 0.7, 2),
		topPEnabled: Boolean(profile?.topPEnabled),
		topP: normalizeSamplingValue(profile?.topP, 0.7, 1),
		apiKey: '',
	};
}

export function createWorkspaceDraft(workspace) {
	return {
		workspaceRootId: workspace?.id || null,
		name: workspace?.label || '',
		rootPath: workspace?.description || '',
	};
}

export function createChannelDraft(channel) {
	return {
		channelId: channel?.id || 'feishu',
		displayName: channel?.displayName || '',
		profileId: channel?.profileId || '',
		fields: (channel?.fields || []).map((field) => ({
			...field,
			value: field.kind === 'secret' ? '' : field.value || '',
		})),
	};
}

export function validateEditorDraft(editor) {
	if (!editor?.open || !editor?.draft) {
		return '没有可保存的表单内容。';
	}

	if (editor.kind === 'profile') {
		if (!editor.draft.name.trim() || !editor.draft.endpoint.trim() || !editor.draft.model.trim()) {
			return '请完整填写配置名称、Endpoint 和模型。';
		}

		if (editor.mode === 'create' && !editor.draft.apiKey.trim()) {
			return '新增配置时必须提供 API Key。';
		}

		return null;
	}

	if (editor.kind === 'channel') {
		if (!editor.draft.displayName.trim()) {
			return '请填写频道名称。';
		}

		if (!editor.draft.profileId) {
			return '请先为频道绑定模型。';
		}

		for (const field of editor.draft.fields || []) {
			const hasText = Boolean((field.value || '').trim());
			if (field.required && field.kind === 'secret' && !field.hasValue && !hasText) {
				return `请填写${field.label}。`;
			}

			if (field.required && field.kind !== 'secret' && !hasText) {
				return `请填写${field.label}。`;
			}
		}

		return null;
	}

	if (!editor.draft.rootPath.trim()) {
		return '请先选择工作区位置。';
	}

	return null;
}
