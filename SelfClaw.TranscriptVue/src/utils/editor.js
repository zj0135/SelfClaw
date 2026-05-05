export const emptyProfile = () => ({
	profileId: null,
	name: '',
	endpoint: '',
	model: '',
	modelOptions: [],
	hasFetchedModelOptions: false,
	modelOptionsCacheKey: '',
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

export const emptyMcpServer = () => ({
	serverId: '',
	displayName: '',
	enabled: true,
	command: '',
	argsText: '',
	envText: '',
});

export const emptyAgent = () => ({
	originalAgentId: null,
	agentId: '',
	name: '',
	description: '',
	mode: 'direct',
	toolPolicy: 'system',
	skillsText: '',
	mcpServersText: '',
	instructions: '',
	isBuiltIn: false,
	warnings: [],
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
		hasFetchedModelOptions: false,
		modelOptionsCacheKey: '',
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

export function formatMcpArgs(args) {
	return (Array.isArray(args) ? args : [])
		.filter((item) => typeof item === 'string' && item.trim())
		.join('\n');
}

export function formatMcpEnv(env) {
	return Object.entries(env || {})
		.filter(([key]) => key && key.trim())
		.map(([key, value]) => `${key.trim()}=${String(value ?? '').trim()}`)
		.join('\n');
}

export function createMcpServerDraft(server) {
	return {
		serverId: server?.id || '',
		displayName: server?.displayName || '',
		enabled: server?.enabled !== false,
		command: server?.command || '',
		argsText: formatMcpArgs(server?.args || []),
		envText: formatMcpEnv(server?.env || {}),
	};
}

export function formatLineList(values) {
	return (Array.isArray(values) ? values : [])
		.filter((item) => typeof item === 'string' && item.trim())
		.join('\n');
}

export function createAgentDraft(agent) {
	return {
		originalAgentId: agent?.id || null,
		agentId: agent?.id || '',
		name: agent?.name || '',
		description: agent?.description || '',
		mode: agent?.mode || 'direct',
		toolPolicy: agent?.toolPolicy || 'system',
		skillsText: formatLineList(agent?.skills || []),
		mcpServersText: formatLineList(agent?.mcpServers || []),
		instructions: agent?.instructions || '',
		isBuiltIn: Boolean(agent?.isBuiltIn),
		warnings: Array.isArray(agent?.warnings) ? [...agent.warnings] : [],
	};
}

export function parseMcpArgsText(value) {
	return String(value || '')
		.split(/\r?\n/)
		.map((item) => item.trim())
		.filter(Boolean);
}

export function parseLineListText(value) {
	return [...new Set(
		String(value || '')
			.split(/\r?\n/)
			.map((item) => item.trim())
			.filter(Boolean)
	)];
}

export function parseMcpEnvText(value) {
	const entries = {};
	const lines = String(value || '')
		.split(/\r?\n/)
		.map((item) => item.trim())
		.filter(Boolean);

	for (const line of lines) {
		const separatorIndex = line.indexOf('=');
		if (separatorIndex <= 0) {
			throw new Error('环境变量格式不正确，请使用 KEY=VALUE。');
		}

		const key = line.slice(0, separatorIndex).trim();
		if (!key) {
			throw new Error('环境变量名称不能为空。');
		}

		entries[key] = line.slice(separatorIndex + 1).trim();
	}

	return entries;
}

export function validateEditorDraft(editor) {
	if (!editor?.open || !editor?.draft) {
		return '当前没有可保存的编辑内容。';
	}

	if (editor.kind === 'profile') {
		if (!editor.draft.name.trim() || !editor.draft.endpoint.trim() || !editor.draft.model.trim()) {
			return '请填写名称、Endpoint 和模型。';
		}

		if (editor.mode === 'create' && !editor.draft.apiKey.trim()) {
			return '新建模型配置时必须填写 API Key。';
		}

		return null;
	}

	if (editor.kind === 'channel') {
		if (!editor.draft.displayName.trim()) {
			return '请填写频道显示名称。';
		}

		if (!editor.draft.profileId) {
			return '请为频道选择模型配置。';
		}

		for (const field of editor.draft.fields || []) {
			const hasText = Boolean((field.value || '').trim());
			if (field.required && field.kind === 'secret' && !field.hasValue && !hasText) {
				return `请填写 ${field.label}。`;
			}

			if (field.required && field.kind !== 'secret' && !hasText) {
				return `请填写 ${field.label}。`;
			}
		}

		return null;
	}

	if (editor.kind === 'mcp') {
		if (!editor.draft.serverId.trim()) {
			return '请填写 MCP 服务 ID。';
		}

		if (!/^[A-Za-z0-9_.-]+$/.test(editor.draft.serverId.trim())) {
			return 'MCP 服务 ID 只能包含字母、数字、点、下划线和短横线。';
		}

		if (!editor.draft.command.trim()) {
			return '请填写 MCP 服务命令。';
		}

		try {
			parseMcpEnvText(editor.draft.envText);
		} catch (error) {
			return error?.message || '环境变量格式不正确。';
		}

		return null;
	}

	if (editor.kind === 'agent') {
		if (!editor.draft.agentId.trim()) {
			return '请填写智能体 ID。';
		}

		if (!/^[A-Za-z0-9_-]+$/.test(editor.draft.agentId.trim())) {
			return '智能体 ID 只能包含字母、数字、下划线和短横线。';
		}

		if (!editor.draft.name.trim()) {
			return '请填写智能体名称。';
		}

		if (!editor.draft.instructions.trim()) {
			return '请填写智能体正文提示词。';
		}

		return null;
	}

	if (!editor.draft.rootPath.trim()) {
		return '请选择工作区路径。';
	}

	return null;
}
