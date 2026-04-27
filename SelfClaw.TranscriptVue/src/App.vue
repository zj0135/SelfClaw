<script setup>
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import ComposerPanel from './components/ComposerPanel.vue';
import ConversationSidebar from './components/ConversationSidebar.vue';
import EditorModal from './components/EditorModal.vue';
import MainTopbar from './components/MainTopbar.vue';
import SettingsModal from './components/SettingsModal.vue';
import StepsPanel from './components/StepsPanel.vue';
import TranscriptPanel from './components/TranscriptPanel.vue';
import { renderConversationList, renderMessages, renderStepsHeader, renderStepsPanelContent } from './renderers';
import {
	createChannelDraft,
	createProfileDraft,
	createWorkspaceDraft,
	emptyChannel,
	emptyProfile,
	emptyWorkspace,
	formatSamplingValue,
	normalizeSamplingValue,
	validateEditorDraft,
} from './utils/editor';

const state = reactive({
	items: [],
	conversations: [],
	selectedConversationId: null,
	theme: 'dark',
	conversationModes: [],
	selectedConversationModeId: 'programming',
	profiles: [],
	profileModels: [],
	selectedProfileId: null,
	selectedProfileModel: null,
	workspaceRoots: [],
	selectedWorkspaceRootId: null,
	toolPermissionModes: [],
	selectedToolPermissionModeId: 'requireApproval',
	isPlanningModeEnabled: false,
	isReasoningEnabled: false,
	planPanel: null,
	teamRoundModes: [],
	selectedTeamRoundModeId: '2',
	teamOutputModes: [],
	selectedTeamOutputModeId: 'autoDocument',
	themeOptions: [],
	selectedThemeId: 'system',
	channels: [],
	teamMembers: [],
	agentActivities: [],
	statusText: '',
	isBusy: false,
});

const composerValue = ref('');
const composerAttachments = ref([]);
const conversationSearch = ref('');
const leftPaneCollapsed = ref(false);
const planPanelCollapsed = ref(false);
const visualizationEnabled = ref(false);
const rightPanelMode = ref('tools');
const settingsOpen = ref(false);
const activeSettingsSection = ref('profile');
const settingsFeedback = ref(null);
const settingsPanelScrollTop = ref(0);
const openConversationMenuId = ref(null);
const openConversationBranches = ref(new Map());
const openStepSections = ref(
	new Map([
		['team-members', false],
		['team-events', true],
	])
);
const openTeamMembers = ref(new Map());

const editorState = reactive({
	open: false,
	kind: null,
	mode: 'create',
	draft: null,
	feedback: null,
});

const mentionState = reactive({
	open: false,
	query: '',
	start: -1,
	end: -1,
	activeIndex: 0,
});

const sidebarRef = ref(null);
const transcriptPanelRef = ref(null);
const composerPanelRef = ref(null);
const stepsPanelRef = ref(null);
const settingsModalRef = ref(null);

const openActivities = new Set();
const openThoughts = new Set();
const openToolSegments = new Set();
const openToolGroups = new Set();
const pointerHandledActions = new Map();
const scrollFollowState = {
	transcript: true,
	transcriptPausedUntil: 0,
	stepsPausedUntil: 0,
};
const pointerActionSuppressDurationMs = 700;
const desktopPaneCollapseBreakpoint = 1180;

let pendingStatePayload = null;
let renderFrameHandle = 0;
let profileModelFetchRequestSeed = 0;
let topbarProfileModelsFetchRequestId = 0;
let topbarProfileModelsFetchCacheKey = '';
let composerAttachmentSeed = 0;
const profileModelOptionsCache = new Map();
const workspaceTreeState = reactive({
	rootId: null,
	rootLabel: '',
	rootPath: '',
	entries: [],
	isLoading: false,
	isLoaded: false,
	error: '',
});

const post = (message) => window.chrome?.webview?.postMessage(message);

function normalizeWorkspaceRelativePath(relativePath) {
	return String(relativePath || '').replace(/^[/\\]+|[/\\]+$/g, '');
}

function getWorkspaceNodeName(relativePath) {
	const normalized = normalizeWorkspaceRelativePath(relativePath);
	if (!normalized) {
		return '';
	}

	const segments = normalized.split(/[/\\]+/);
	return segments[segments.length - 1] || normalized;
}

function createWorkspaceTreeNode(entry) {
	const relativePath = normalizeWorkspaceRelativePath(entry?.relativePath || '');
	return {
		path: relativePath,
		name: getWorkspaceNodeName(relativePath),
		isDirectory: Boolean(entry?.isDirectory),
		sizeBytes: Number(entry?.sizeBytes || 0),
		children: [],
		isExpanded: false,
		isLoaded: !entry?.isDirectory,
		isLoading: false,
		loadError: '',
	};
}

function createWorkspaceTreeNodes(entries) {
	return (Array.isArray(entries) ? entries : []).map(createWorkspaceTreeNode);
}

function resetWorkspaceTreeState() {
	workspaceTreeState.rootId = state.selectedWorkspaceRootId || null;
	workspaceTreeState.rootLabel = selectedWorkspace.value?.label || '';
	workspaceTreeState.rootPath = selectedWorkspace.value?.description || '';
	workspaceTreeState.entries = [];
	workspaceTreeState.isLoading = false;
	workspaceTreeState.isLoaded = false;
	workspaceTreeState.error = '';
}

function findWorkspaceTreeNode(relativePath, nodes = workspaceTreeState.entries) {
	const normalized = normalizeWorkspaceRelativePath(relativePath);
	for (const node of nodes) {
		if (node.path === normalized) {
			return node;
		}

		if (node.isDirectory && node.children.length > 0) {
			const nestedNode = findWorkspaceTreeNode(normalized, node.children);
			if (nestedNode) {
				return nestedNode;
			}
		}
	}

	return null;
}

function requestWorkspaceDirectory(relativePath = '') {
	if (!state.selectedWorkspaceRootId) {
		resetWorkspaceTreeState();
		return;
	}

	const normalizedPath = normalizeWorkspaceRelativePath(relativePath);
	if (normalizedPath) {
		const targetNode = findWorkspaceTreeNode(normalizedPath);
		if (!targetNode || !targetNode.isDirectory || targetNode.isLoading) {
			return;
		}

		targetNode.isLoading = true;
		targetNode.loadError = '';
	} else {
		workspaceTreeState.rootId = state.selectedWorkspaceRootId || null;
		workspaceTreeState.rootLabel = selectedWorkspace.value?.label || '';
		workspaceTreeState.rootPath = selectedWorkspace.value?.description || '';
		workspaceTreeState.isLoading = true;
		workspaceTreeState.error = '';
	}

	post({
		type: 'load-workspace-directory',
		workspaceRootId: state.selectedWorkspaceRootId,
		relativePath: normalizedPath || null,
	});
}

function ensureWorkspaceTreeLoaded(force = false) {
	if (!state.selectedWorkspaceRootId) {
		resetWorkspaceTreeState();
		return;
	}

	if (force) {
		resetWorkspaceTreeState();
	}

	if (workspaceTreeState.isLoading) {
		return;
	}

	if (!workspaceTreeState.isLoaded || workspaceTreeState.rootId !== state.selectedWorkspaceRootId) {
		requestWorkspaceDirectory('');
	}
}

function setRightPanelMode(mode) {
	if (mode !== 'tools' && mode !== 'workspace') {
		return;
	}

	rightPanelMode.value = mode;
	if (mode === 'workspace') {
		ensureWorkspaceTreeLoaded();
	}
}

function toggleWorkspaceDirectory(relativePath) {
	const targetNode = findWorkspaceTreeNode(relativePath);
	if (!targetNode || !targetNode.isDirectory) {
		return;
	}

	targetNode.isExpanded = !targetNode.isExpanded;
	if (targetNode.isExpanded && !targetNode.isLoaded) {
		requestWorkspaceDirectory(targetNode.path);
	}
}

function openWorkspaceFile(relativePath) {
	const normalizedPath = normalizeWorkspaceRelativePath(relativePath);
	if (!normalizedPath) {
		return;
	}

	post({
		type: 'open-workspace-file',
		relativePath: normalizedPath,
	});
}

function openWorkspaceEntryLocation(payload) {
	const normalizedPath = normalizeWorkspaceRelativePath(payload?.path);
	if (!normalizedPath) {
		return;
	}

	post({
		type: 'open-workspace-entry-location',
		relativePath: normalizedPath,
		isDirectory: Boolean(payload?.isDirectory),
	});
}

function handleWorkspaceDirectoryLoaded(payload) {
	const payloadRootId = payload.workspaceRootId || null;
	const activeRootId = state.selectedWorkspaceRootId || null;
	if (payloadRootId && payloadRootId !== activeRootId) {
		return;
	}

	const relativePath = normalizeWorkspaceRelativePath(payload.relativePath);
	const errorMessage = payload.errorMessage || '';
	const nextNodes = createWorkspaceTreeNodes(payload.entries);

	if (!relativePath) {
		workspaceTreeState.rootId = payloadRootId || state.selectedWorkspaceRootId || null;
		workspaceTreeState.rootLabel = payload.workspaceName || selectedWorkspace.value?.label || '';
		workspaceTreeState.rootPath = payload.workspaceRootPath || selectedWorkspace.value?.description || '';
		workspaceTreeState.entries = errorMessage ? [] : nextNodes;
		workspaceTreeState.isLoading = false;
		workspaceTreeState.isLoaded = !errorMessage;
		workspaceTreeState.error = errorMessage;
		return;
	}

	const targetNode = findWorkspaceTreeNode(relativePath);
	if (!targetNode) {
		return;
	}

	targetNode.isLoading = false;
	targetNode.isLoaded = !errorMessage;
	targetNode.loadError = errorMessage;
	if (!errorMessage) {
		targetNode.children = nextNodes;
	}
}

function normalizeProfileModelsEndpoint(endpoint) {
	const normalized = (endpoint || '').trim();
	if (!normalized) {
		return '';
	}

	const withScheme = /^https?:\/\//i.test(normalized) ? normalized : `https://${normalized}`;
	return withScheme.replace(/\/+$/, '');
}

function buildProfileModelsCacheKey(profileDraft) {
	if (!profileDraft) {
		return '';
	}

	const endpoint = normalizeProfileModelsEndpoint(profileDraft.endpoint);
	if (!endpoint) {
		return '';
	}

	return JSON.stringify({
		profileId: profileDraft.profileId || null,
		endpoint,
		apiKey: (profileDraft.apiKey || '').trim(),
	});
}

function applyProfileModelOptions(profileDraft, cacheKey, modelOptions) {
	const normalizedOptions = [...new Set((Array.isArray(modelOptions) ? modelOptions : [])
		.filter((item) => typeof item === 'string')
		.map((item) => item.trim())
		.filter(Boolean))];
	const currentModel = profileDraft.model?.trim() || '';

	profileDraft.modelOptions = currentModel
		? [...new Set([...normalizedOptions, currentModel])]
		: normalizedOptions;
	profileDraft.hasFetchedModelOptions = true;
	profileDraft.modelOptionsCacheKey = cacheKey;
	profileDraft.isFetchingModels = false;
	profileDraft.fetchModelsRequestId = 0;

	if (!currentModel && normalizedOptions.length > 0) {
		profileDraft.model = normalizedOptions[0];
	}
}

function resetProfileModelOptions(profileDraft) {
	if (!profileDraft) {
		return;
	}

	const currentModel = profileDraft.model?.trim() || '';
	profileDraft.modelOptions = currentModel ? [currentModel] : [];
	profileDraft.hasFetchedModelOptions = false;
	profileDraft.modelOptionsCacheKey = '';
	profileDraft.isFetchingModels = false;
	profileDraft.fetchModelsRequestId = 0;
}

const setTheme = (theme) => {
	document.documentElement.dataset.theme = theme === 'light' ? 'light' : 'dark';
};

function getConversationListElement() {
	return sidebarRef.value?.getConversationListEl?.() ?? null;
}

function getTranscriptScrollElement() {
	return transcriptPanelRef.value?.getScrollEl?.() ?? null;
}

function getComposerElement() {
	return composerPanelRef.value?.getComposerEl?.() ?? null;
}

function getStepsScrollElement() {
	return stepsPanelRef.value?.getScrollEl?.() ?? null;
}

function getSettingsPanelElement() {
	return settingsModalRef.value?.getPanelEl?.() ?? null;
}

const selectedProfile = computed(() => state.profiles.find((item) => item.id === state.selectedProfileId) || null);
const selectedWorkspace = computed(() => state.workspaceRoots.find((item) => item.id === state.selectedWorkspaceRootId) || null);

function normalizeProfileModelList(models, selectedModel = '') {
	const normalized = [...new Set((Array.isArray(models) ? models : [])
		.filter((item) => typeof item === 'string')
		.map((item) => item.trim())
		.filter(Boolean))];
	const current = (selectedModel || '').trim();
	if (current && !normalized.includes(current)) {
		normalized.push(current);
	}

	return normalized;
}

function applyTopbarProfileModels(models) {
	const normalized = normalizeProfileModelList(models, state.selectedProfileModel || '');
	state.profileModels = normalized.map((model) => ({ id: model, label: model }));
}

function buildTopbarProfileModelsCacheDescriptor() {
	if (!selectedProfile.value) {
		return null;
	}

	const endpoint = normalizeProfileModelsEndpoint(selectedProfile.value.description || '');
	if (!endpoint) {
		return null;
	}

	const profileId = selectedProfile.value.id || null;
	return {
		profileId,
		endpoint,
		cacheKey: JSON.stringify({
			profileId,
			endpoint,
			apiKey: '',
		}),
	};
}

function applyCachedTopbarProfileModels() {
	const descriptor = buildTopbarProfileModelsCacheDescriptor();
	if (!descriptor || !profileModelOptionsCache.has(descriptor.cacheKey)) {
		return;
	}

	applyTopbarProfileModels(profileModelOptionsCache.get(descriptor.cacheKey));
}

const hasSelectedWorkspace = computed(() => Boolean(state.selectedWorkspaceRootId && selectedWorkspace.value));
const visibleTeamMembers = computed(() => state.teamMembers || []);
const isProgrammingMode = computed(() => state.selectedConversationModeId === 'programming');
const isTeamMode = computed(() => state.selectedConversationModeId === 'team');
const isChannelMode = computed(() => state.selectedConversationModeId === 'channel');
const showPlanningToggle = computed(() => isProgrammingMode.value);
const showVisualizationToggle = computed(() => !isChannelMode.value);
const activeVisualizationEnabled = computed(() => visualizationEnabled.value && !isChannelMode.value);
const planPanel = computed(() => (isProgrammingMode.value ? state.planPanel : null));
const showPlanPanel = computed(() => Boolean(planPanel.value?.isVisible));
const planSteps = computed(() => planPanel.value?.steps || []);
const currentPlanStepMeta = computed(() => {
	const steps = planSteps.value;
	if (!steps.length) {
		return { index: 0, total: 0, status: planPanel.value?.state || 'planning' };
	}

	const runningIndex = steps.findIndex((step) => step.status === 'running');
	if (runningIndex >= 0) {
		return { index: runningIndex + 1, total: steps.length, status: 'running' };
	}

	const failedIndex = steps.findIndex((step) => step.status === 'failed');
	if (failedIndex >= 0) {
		return { index: failedIndex + 1, total: steps.length, status: 'failed' };
	}

	const cancelledIndex = steps.findIndex((step) => step.status === 'cancelled');
	if (cancelledIndex >= 0) {
		return { index: cancelledIndex + 1, total: steps.length, status: 'cancelled' };
	}

	const completedCount = steps.filter((step) => step.status === 'completed').length;
	return {
		index: Math.min(Math.max(completedCount, 1), steps.length),
		total: steps.length,
		status: completedCount === steps.length ? 'completed' : 'pending',
	};
});
const collapsedPlanText = computed(() => {
	const meta = currentPlanStepMeta.value;
	if (!meta.total) {
		return planPanel.value?.state === 'planning' ? '正在梳理计划' : '准备执行';
	}

	switch (meta.status) {
		case 'running':
			return `执行到第 ${meta.index} / ${meta.total} 个任务`;
		case 'completed':
			return `已完成 ${meta.total} / ${meta.total} 个任务`;
		case 'failed':
			return `第 ${meta.index} / ${meta.total} 个任务失败`;
		case 'cancelled':
			return `已停止在第 ${meta.index} / ${meta.total} 个任务`;
		default:
			return `准备执行第 ${meta.index} / ${meta.total} 个任务`;
	}
});
const firstChannel = computed(() => (state.channels && state.channels.length > 0 ? state.channels[0] : null));
const fallbackStatusText = computed(() => state.statusText || (state.isBusy ? '处理中' : '就绪'));
const selectedThemeLabel = computed(
	() =>
		state.themeOptions.find((item) => item.id === state.selectedThemeId)?.label ||
		{
			system: '跟随系统',
			light: '浅色',
			dark: '深色',
		}[state.selectedThemeId || 'system'] ||
		'跟随系统'
);
const currentModelLabel = computed(() => state.selectedProfileModel || '未选择模型');
const currentWorkspaceLabel = computed(() => selectedWorkspace.value?.label || '未绑定工作区');
const composerPlaceholder = computed(() => (isChannelMode.value ? '频道会话由外部消息自动驱动' : 'Ask for follow-up changes'));
const conversationSectionTitle = computed(() =>
	isChannelMode.value ? '频道会话' : state.conversations.some((item) => item.parentId) ? '会话树' : '最近会话'
);
const totalStepCount = computed(() => (isTeamMode.value ? visibleTeamMembers.value.length + (state.agentActivities?.length || 0) : state.agentActivities?.length || 0));
const sendButtonDisabled = computed(
	() => isChannelMode.value || (!state.isBusy && !composerValue.value.trim() && composerAttachments.value.length === 0) || !state.selectedProfileId
);
const settingsSections = computed(() => [
	{
		id: 'profile',
		eyebrow: '模型配置',
		title: '模型配置',
		description: selectedProfile.value
			? `${selectedProfile.value.label}${state.selectedProfileModel ? `  ${state.selectedProfileModel}` : ''}`
			: '选择默认模型，并管理 Endpoint 与 API Key。',
		badge: selectedProfile.value ? '已选择' : '未选择',
	},
	{
		id: 'workspace',
		eyebrow: '工作区',
		title: '工作区',
		description: selectedWorkspace.value?.label || '绑定本地目录，作为工具读取和搜索范围。',
		badge: selectedWorkspace.value ? '已绑定' : '未绑定',
	},
	{
		id: 'channels',
		eyebrow: '我的频道',
		title: '我的频道',
		description: firstChannel.value
			? `${firstChannel.value.name} · ${firstChannel.value.statusLabel}`
			: '管理外部频道连接与自动收消息。',
		badge: state.channels.filter((item) => item.isEnabled).length > 0 ? '已启用' : '未启用',
	},
	{
		id: 'theme',
		eyebrow: '界面主题',
		title: '界面主题',
		description: state.selectedThemeId === 'system' ? '当前跟随系统外观' : `当前为${selectedThemeLabel.value}`,
		badge: selectedThemeLabel.value,
	},
]);
const activeSettingsMeta = computed(() => settingsSections.value.find((item) => item.id === activeSettingsSection.value) || settingsSections.value[0] || null);
const visibleSettingsFeedback = computed(() => {
	if (!settingsFeedback.value) {
		return null;
	}

	return !settingsFeedback.value.scope || settingsFeedback.value.scope === activeSettingsSection.value ? settingsFeedback.value : null;
});

const profileSummaryCards = computed(() => [
	{ label: '名称', value: selectedProfile.value?.label || '未选择配置' },
	{ label: '模型', value: state.selectedProfileModel || '未选择配置' },
	{ label: 'Endpoint', value: selectedProfile.value?.description || '未设置 Endpoint' },
	{
		label: 'Temperature',
		value: selectedProfile.value?.temperatureEnabled
			? `${formatSamplingValue(selectedProfile.value?.temperature ?? 0.7, 2)} · 已启用`
			: `${formatSamplingValue(selectedProfile.value?.temperature ?? 0.7, 2)} · 未启用`,
	},
	{
		label: 'Top-P',
		value: selectedProfile.value?.topPEnabled
			? `${formatSamplingValue(selectedProfile.value?.topP ?? 0.7, 1)} · 已启用`
			: `${formatSamplingValue(selectedProfile.value?.topP ?? 0.7, 1)} · 未启用`,
	},
]);

const workspaceSummaryCards = computed(() => [
	{ label: '名称', value: selectedWorkspace.value?.label || '未绑定工作区' },
	{ label: '路径', value: selectedWorkspace.value?.description || '未设置工作区路径' },
]);

const workspaceTreeLabel = computed(() => workspaceTreeState.rootLabel || selectedWorkspace.value?.label || '');
const workspaceTreePath = computed(() => workspaceTreeState.rootPath || selectedWorkspace.value?.description || '');

const filteredConversations = computed(() => {
	const query = conversationSearch.value.trim().toLowerCase();
	if (!query) {
		return state.conversations;
	}

	const conversationsById = new Map(state.conversations.map((item) => [item.id, item]));
	const includedIds = new Set();

	state.conversations.forEach((item) => {
		const haystack = `${item.title || ''} ${item.badge || ''} ${item.subtitle || ''}`.toLowerCase();
		if (!haystack.includes(query)) {
			return;
		}

		let current = item;
		while (current) {
			includedIds.add(current.id);
			current = current.parentId ? conversationsById.get(current.parentId) || null : null;
		}
	});

	return state.conversations.filter((item) => includedIds.has(item.id));
});

const conversationListHtml = computed(() =>
	renderConversationList({
		conversations: filteredConversations.value,
		conversationSearch: conversationSearch.value,
		selectedConversationId: state.selectedConversationId,
		openConversationBranches: openConversationBranches.value,
		openConversationMenuId: openConversationMenuId.value,
	})
);

const messagesHtml = computed(() => renderMessages(state.items, openThoughts, openToolSegments, openToolGroups));
const stepsHeaderHtml = computed(() => renderStepsHeader({ isTeamMode: isTeamMode.value }));
const stepsPanelHtml = computed(() =>
	renderStepsPanelContent({
		isTeamMode: isTeamMode.value,
		teamMembers: visibleTeamMembers.value,
		agentActivities: state.agentActivities,
		openStepSections: openStepSections.value,
		openActivities,
		openTeamMembers: openTeamMembers.value,
	})
);

const mentionAgents = computed(() => visibleTeamMembers.value.map((item) => ({ id: item.id, name: item.title, role: item.summary })));
const mentionCandidates = computed(() => {
	if (!isTeamMode.value) {
		return [];
	}

	const normalizedQuery = mentionState.query.trim().toLowerCase();
	return mentionAgents.value.filter((item) => {
		if (!normalizedQuery) {
			return true;
		}

		return item.name.toLowerCase().includes(normalizedQuery) || item.role.toLowerCase().includes(normalizedQuery);
	});
});

watch(
	() => state.theme,
	(theme) => {
		setTheme(theme);
	},
	{ immediate: true }
);

watch(
	() => isTeamMode.value,
	(teamMode) => {
		if (!teamMode) {
			closeMentionPicker();
		}
	}
);

watch(
	() => showPlanPanel.value,
	(isVisible) => {
		if (!isVisible) {
			planPanelCollapsed.value = false;
		}
	}
);

watch(
	() => planPanel.value?.state,
	(stateValue, previousStateValue) => {
		if (stateValue === 'planning' && previousStateValue !== 'planning') {
			planPanelCollapsed.value = false;
		}
	}
);

watch(isChannelMode, (channelMode) => {
	if (channelMode) {
		visualizationEnabled.value = false;
		composerAttachments.value = [];
	}
});

watch(
	() => state.selectedWorkspaceRootId,
	() => {
		resetWorkspaceTreeState();
		if (rightPanelMode.value === 'workspace') {
			ensureWorkspaceTreeLoaded();
		}
	}
);

watch(
	() => [selectedWorkspace.value?.label, selectedWorkspace.value?.description],
	([label, description]) => {
		if (!workspaceTreeState.rootId || workspaceTreeState.rootId === state.selectedWorkspaceRootId) {
			workspaceTreeState.rootLabel = label || '';
			workspaceTreeState.rootPath = description || '';
		}
	}
);

watch(settingsOpen, async (isOpen) => {
	if (!isOpen) {
		return;
	}

	await nextTick();
	const panel = getSettingsPanelElement();
	if (panel) {
		panel.scrollTop = settingsPanelScrollTop.value;
	}
});

function normalizeState() {
	state.conversationModes = state.conversationModes || [];
	state.selectedConversationModeId = state.selectedConversationModeId || 'programming';
	state.profileModels = state.profileModels || [];
	state.toolPermissionModes = state.toolPermissionModes || [];
	state.selectedToolPermissionModeId = state.selectedToolPermissionModeId || 'requireApproval';
	state.isPlanningModeEnabled = Boolean(state.isPlanningModeEnabled);
	state.isReasoningEnabled = Boolean(state.isReasoningEnabled);
	state.planPanel = state.planPanel || null;
	state.teamRoundModes = state.teamRoundModes || [];
	state.selectedTeamRoundModeId = state.selectedTeamRoundModeId || '2';
	state.teamOutputModes = state.teamOutputModes || [];
	state.selectedTeamOutputModeId = state.selectedTeamOutputModeId || 'autoDocument';
	state.teamMembers = state.teamMembers || [];
	state.agentActivities = state.agentActivities || [];
	state.themeOptions = state.themeOptions || [];
	state.channels = state.channels || [];
}

function closeMentionPicker() {
	mentionState.open = false;
	mentionState.query = '';
	mentionState.start = -1;
	mentionState.end = -1;
	mentionState.activeIndex = 0;
}

function syncMentionState(target) {
	if (!(target instanceof HTMLTextAreaElement) || !isTeamMode.value) {
		closeMentionPicker();
		return;
	}

	const selectionStart = target.selectionStart ?? composerValue.value.length;
	const beforeCaret = composerValue.value.slice(0, selectionStart);
	const tokenStart = Math.max(beforeCaret.lastIndexOf(' '), beforeCaret.lastIndexOf('\n'), beforeCaret.lastIndexOf('\t')) + 1;
	const token = beforeCaret.slice(tokenStart);
	if (!token.startsWith('@') || token.startsWith('@{') || token.includes('}') || /\s/.test(token.slice(1))) {
		closeMentionPicker();
		return;
	}

	mentionState.query = token.slice(1);
	mentionState.start = tokenStart;
	mentionState.end = selectionStart;
	if (!mentionCandidates.value.length) {
		closeMentionPicker();
		return;
	}

	mentionState.open = true;
	mentionState.activeIndex = Math.min(mentionState.activeIndex, mentionCandidates.value.length - 1);
}

function applyMentionSelection(agent) {
	const target = getComposerElement();
	if (!(target instanceof HTMLTextAreaElement) || !agent || mentionState.start < 0 || mentionState.end < mentionState.start) {
		return;
	}

	const nextValue = `${composerValue.value.slice(0, mentionState.start)}@{${agent.name}} ${composerValue.value.slice(mentionState.end)}`;
	const nextCaret = mentionState.start + agent.name.length + 4;
	composerValue.value = nextValue;
	closeMentionPicker();

	nextTick(() => {
		target.focus();
		target.setSelectionRange(nextCaret, nextCaret);
	});
}

function submitComposer() {
	if (state.isBusy || isChannelMode.value) {
		return;
	}

	const prompt = composerValue.value.trim();
	if (!prompt && composerAttachments.value.length === 0) {
		return;
	}

	post({
		type: 'send-prompt',
		prompt,
		enableReasoning: Boolean(state.isReasoningEnabled),
		profileModel: state.selectedProfileModel || null,
		attachments: composerAttachments.value.map((attachment) => ({
			sourcePath: attachment.sourcePath,
			fileName: attachment.fileName,
			mediaType: attachment.mediaType,
			byteLength: attachment.byteLength,
		})),
	});
	composerValue.value = '';
	composerAttachments.value = [];
	closeMentionPicker();
}

function clearFeedback(scope) {
	if (settingsFeedback.value && (!scope || !settingsFeedback.value.scope || settingsFeedback.value.scope === scope)) {
		settingsFeedback.value = null;
	}
}

function editorScope() {
	if (editorState.kind === 'profile' || editorState.kind === 'workspace') {
		return editorState.kind;
	}

	return editorState.kind === 'channel' ? 'channels' : null;
}

function openEditor(kind, mode, payload = null) {
	const scope = kind === 'channel' ? 'channels' : kind;
	if (kind === 'profile' || kind === 'workspace') {
		activeSettingsSection.value = kind;
	}
	if (kind === 'channel') {
		activeSettingsSection.value = 'channels';
	}

	editorState.open = true;
	editorState.kind = kind;
	editorState.mode = mode;
	editorState.draft =
		kind === 'profile'
			? mode === 'edit' && state.selectedProfileId
				? createProfileDraft(selectedProfile.value, state.selectedProfileModel || selectedProfile.value?.model)
				: emptyProfile()
			: kind === 'workspace'
				? mode === 'edit' && state.selectedWorkspaceRootId
					? createWorkspaceDraft(selectedWorkspace.value)
					: emptyWorkspace()
				: payload
					? createChannelDraft(payload)
					: emptyChannel();
	editorState.feedback = null;
	clearFeedback(scope);
}

function closeEditor() {
	editorState.open = false;
	editorState.kind = null;
	editorState.mode = 'create';
	editorState.draft = null;
	editorState.feedback = null;
}

watch(
	() => (editorState.open && editorState.kind === 'profile' && editorState.draft
		? buildProfileModelsCacheKey(editorState.draft)
		: null),
	(nextKey, previousKey) => {
		if (!editorState.open || editorState.kind !== 'profile' || !editorState.draft || nextKey === previousKey) {
			return;
		}

		if (!nextKey) {
			resetProfileModelOptions(editorState.draft);
			return;
		}

		if (profileModelOptionsCache.has(nextKey)) {
			applyProfileModelOptions(editorState.draft, nextKey, profileModelOptionsCache.get(nextKey));
			return;
		}

		if (editorState.draft.modelOptionsCacheKey !== nextKey || editorState.draft.fetchModelsRequestId) {
			resetProfileModelOptions(editorState.draft);
		}
	}
);

function handleProfileModelsFetched(payload) {
	const requestId = Number(payload.requestId || 0);
	const modelOptions = normalizeProfileModelList(payload.models);

	if (requestId === Number(topbarProfileModelsFetchRequestId || 0)) {
		const cacheKey = topbarProfileModelsFetchCacheKey;
		topbarProfileModelsFetchRequestId = 0;
		topbarProfileModelsFetchCacheKey = '';
		if (!payload.errorMessage && cacheKey) {
			profileModelOptionsCache.set(cacheKey, modelOptions);
			applyTopbarProfileModels(modelOptions);
		}
	}

	if (!editorState.open || editorState.kind !== 'profile' || !editorState.draft) {
		return;
	}

	if (requestId !== Number(editorState.draft.fetchModelsRequestId || 0)) {
		return;
	}

	editorState.draft.isFetchingModels = false;
	editorState.draft.fetchModelsRequestId = 0;

	if (payload.errorMessage) {
		editorState.draft.hasFetchedModelOptions = false;
		editorState.draft.modelOptionsCacheKey = '';
		editorState.feedback = { level: 'error', message: payload.errorMessage, scope: 'profile' };
		return;
	}

	const cacheKey = buildProfileModelsCacheKey(editorState.draft);
	profileModelOptionsCache.set(cacheKey, modelOptions);
	applyProfileModelOptions(editorState.draft, cacheKey, modelOptions);
	editorState.feedback = null;
}

function normalizeComposerAttachment(rawAttachment) {
	if (!rawAttachment || !rawAttachment.sourcePath) {
		return null;
	}

	return {
		id: rawAttachment.id || `composer-image-${++composerAttachmentSeed}`,
		sourcePath: rawAttachment.sourcePath,
		fileName: rawAttachment.fileName || 'image',
		mediaType: rawAttachment.mediaType || 'image/png',
		byteLength: Number(rawAttachment.byteLength || 0),
		dataUrl: rawAttachment.dataUrl || '',
	};
}

function handleComposerImagesPicked(payload) {
	const picked = Array.isArray(payload.attachments)
		? payload.attachments.map(normalizeComposerAttachment).filter(Boolean)
		: [];
	if (!picked.length) {
		return;
	}

	const existingSourcePaths = new Set(composerAttachments.value.map((item) => item.sourcePath));
	const nextAttachments = [...composerAttachments.value];
	for (const attachment of picked) {
		if (existingSourcePaths.has(attachment.sourcePath)) {
			continue;
		}

		existingSourcePaths.add(attachment.sourcePath);
		nextAttachments.push(attachment);
	}

	composerAttachments.value = nextAttachments.slice(0, 6);
}

function handleComposerImagesPickedEvent(event) {
	handleComposerImagesPicked(event?.detail || {});
}

function captureScroll(element) {
	return element ? { top: element.scrollTop, nearBottom: element.scrollHeight - element.scrollTop - element.clientHeight < 40 } : null;
}

function restoreScroll(element, snapshot, toBottom = false) {
	if (!element) {
		return;
	}

	if (toBottom) {
		element.scrollTop = element.scrollHeight;
		return;
	}

	if (snapshot) {
		element.scrollTop = snapshot.top;
	}
}

function pauseTranscriptAutoFollow(durationMs = 1200) {
	scrollFollowState.transcript = false;
	scrollFollowState.transcriptPausedUntil = Date.now() + durationMs;
}

function resumeTranscriptAutoFollow() {
	scrollFollowState.transcript = true;
	scrollFollowState.transcriptPausedUntil = 0;
}

function pauseStepsScrollRestore(durationMs = 1200) {
	scrollFollowState.stepsPausedUntil = Date.now() + durationMs;
}

function canRestoreStepsScroll() {
	return Date.now() >= scrollFollowState.stepsPausedUntil;
}

function canAutoFollowTranscript(snapshot) {
	if (!snapshot?.nearBottom) {
		return false;
	}

	if (Date.now() < scrollFollowState.transcriptPausedUntil) {
		return false;
	}

	return scrollFollowState.transcript;
}

function syncConversationMenuPlacement() {
	if (!openConversationMenuId.value) {
		return;
	}

	const conversationList = getConversationListElement();
	const menu = conversationList?.querySelector('.conversation-menu');
	const menuShell = menu?.parentElement;
	if (!conversationList || !menu || !(menuShell instanceof HTMLElement)) {
		return;
	}

	menu.classList.remove('upward');

	const listRect = conversationList.getBoundingClientRect();
	const shellRect = menuShell.getBoundingClientRect();
	const menuHeight = menu.offsetHeight;
	const spaceBelow = listRect.bottom - (shellRect.top + 42);
	const spaceAbove = shellRect.bottom - 38 - listRect.top;

	if (menuHeight > spaceBelow && spaceAbove > spaceBelow) {
		menu.classList.add('upward');
	}
}

async function preserveConversationList(mutator) {
	const snapshot = captureScroll(getConversationListElement());
	mutator();
	await nextTick();
	restoreScroll(getConversationListElement(), snapshot);
	requestAnimationFrame(syncConversationMenuPlacement);
}

async function preserveStepsPanel(mutator) {
	const snapshot = captureScroll(getStepsScrollElement());
	mutator();
	await nextTick();
	if (canRestoreStepsScroll()) {
		restoreScroll(getStepsScrollElement(), snapshot);
	}
}

async function applyStatePayload(payload) {
	const transcriptState = captureScroll(getTranscriptScrollElement());
	const conversationState = captureScroll(getConversationListElement());
	const stepsState = captureScroll(getStepsScrollElement());
	const { type: _type, ...nextState } = payload;

	Object.assign(state, nextState);
	normalizeState();
	applyCachedTopbarProfileModels();

	await nextTick();
	restoreScroll(getConversationListElement(), conversationState);
	if (canRestoreStepsScroll()) {
		restoreScroll(getStepsScrollElement(), stepsState);
	}

	restoreScroll(getTranscriptScrollElement(), transcriptState, canAutoFollowTranscript(transcriptState));
	requestAnimationFrame(syncConversationMenuPlacement);
}

async function flushPendingStatePayload() {
	renderFrameHandle = 0;
	if (!pendingStatePayload) {
		return;
	}

	const payload = pendingStatePayload;
	pendingStatePayload = null;
	await applyStatePayload(payload);
	if (pendingStatePayload) {
		scheduleStatePayload(pendingStatePayload);
	}
}

function scheduleStatePayload(payload) {
	pendingStatePayload = payload;
	if (renderFrameHandle) {
		return;
	}

	renderFrameHandle = requestAnimationFrame(() => {
		void flushPendingStatePayload();
	});
}

function handleSettingsFeedback(payload) {
	const nextFeedback = payload.message ? { level: payload.level || 'success', message: payload.message, scope: payload.scope || null } : null;
	if (payload.scope === 'profile' || payload.scope === 'workspace' || payload.scope === 'channels' || payload.scope === 'theme') {
		activeSettingsSection.value = payload.scope;
	}

	if (editorState.open && payload.scope === editorScope()) {
		if (payload.level === 'success') {
			settingsFeedback.value = nextFeedback;
			closeEditor();
		} else {
			settingsFeedback.value = null;
			editorState.feedback = nextFeedback;
		}
	} else {
		settingsFeedback.value = nextFeedback;
	}
}

function handleWebViewMessage(event) {
	const payload = event.data || {};

	if (payload.type === 'replaceState') {
		scheduleStatePayload(payload);
		return;
	}

	if (payload.type === 'workspace-directory-loaded') {
		handleWorkspaceDirectoryLoaded(payload);
		return;
	}

	if (payload.type === 'workspace-path-picked') {
		if (editorState.open && editorState.kind === 'workspace' && editorState.draft) {
			editorState.draft.rootPath = payload.rootPath || '';
			editorState.feedback = null;
		}
		return;
	}

	if (payload.type === 'profile-models-fetched') {
		handleProfileModelsFetched(payload);
		return;
	}

	if (payload.type === 'composer-images-picked') {
		handleComposerImagesPicked(payload);
		return;
	}

	if (payload.type === 'settings-feedback') {
		handleSettingsFeedback(payload);
	}
}

function onDocumentKeydown(event) {
	if (event.key === 'Escape' && editorState.open) {
		event.preventDefault();
		closeEditor();
		return;
	}

	if (event.key === 'Escape' && settingsOpen.value) {
		event.preventDefault();
		closeSettings();
		return;
	}

	if (event.key === 'Escape' && state.isBusy) {
		post({ type: 'stop-generation' });
	}
}

function onComposerInput(event) {
	composerValue.value = event.target.value;
	syncMentionState(event.target);
}

function onComposerKeydown(event) {
	if (mentionState.open && mentionCandidates.value.length > 0) {
		if (event.key === 'ArrowDown') {
			event.preventDefault();
			mentionState.activeIndex = (mentionState.activeIndex + 1) % mentionCandidates.value.length;
			return;
		}

		if (event.key === 'ArrowUp') {
			event.preventDefault();
			mentionState.activeIndex = (mentionState.activeIndex - 1 + mentionCandidates.value.length) % mentionCandidates.value.length;
			return;
		}

		if (event.key === 'Enter' || event.key === 'Tab') {
			event.preventDefault();
			applyMentionSelection(mentionCandidates.value[mentionState.activeIndex] || mentionCandidates.value[0]);
			return;
		}

		if (event.key === 'Escape') {
			event.preventDefault();
			closeMentionPicker();
			return;
		}
	}

	if (event.key === 'Enter' && !event.shiftKey) {
		event.preventDefault();
		submitComposer();
	}
}

function getActionSuppressKey(action, actionElement) {
	switch (action) {
		case 'toggle-thinking': {
			const id = actionElement.getAttribute('data-thinking-id');
			return id ? `${action}:${id}` : null;
		}
		case 'toggle-tool-segment': {
			const id = actionElement.getAttribute('data-tool-segment-id');
			return id ? `${action}:${id}` : null;
		}
		case 'toggle-tool-group': {
			const id = actionElement.getAttribute('data-tool-group-id');
			return id ? `${action}:${id}` : null;
		}
		default:
			return null;
	}
}

function pruneSuppressedActions() {
	const now = Date.now();
	for (const [key, timestamp] of pointerHandledActions) {
		if (now - timestamp > pointerActionSuppressDurationMs * 3) {
			pointerHandledActions.delete(key);
		}
	}
}

function markPointerHandledAction(action, actionElement) {
	const key = getActionSuppressKey(action, actionElement);
	if (!key) {
		return;
	}

	pruneSuppressedActions();
	pointerHandledActions.set(key, Date.now());
}

function shouldSuppressClickAction(action, actionElement) {
	const key = getActionSuppressKey(action, actionElement);
	if (!key) {
		return false;
	}

	const timestamp = pointerHandledActions.get(key);
	if (typeof timestamp !== 'number') {
		return false;
	}

	const isFresh = Date.now() - timestamp <= pointerActionSuppressDurationMs;
	pointerHandledActions.delete(key);
	return isFresh;
}

function toggleThinking(actionElement) {
	const id = actionElement.getAttribute('data-thinking-id');
	const block = actionElement.closest('.thinking-block');
	if (!id || !block) {
		return false;
	}

	const isOpen = openThoughts.has(id);
	if (isOpen) {
		openThoughts.delete(id);
		block.classList.remove('open');
	} else {
		openThoughts.add(id);
		block.classList.add('open');
	}

	actionElement.setAttribute('aria-expanded', isOpen ? 'false' : 'true');
	return true;
}

function toggleToolSegment(actionElement) {
	const id = actionElement.getAttribute('data-tool-segment-id');
	const block = actionElement.closest('.tool-block');
	if (!id || !block) {
		return false;
	}

	const isOpen = openToolSegments.has(id);
	if (isOpen) {
		openToolSegments.delete(id);
		block.classList.remove('open');
	} else {
		openToolSegments.add(id);
		block.classList.add('open');
	}

	actionElement.setAttribute('aria-expanded', isOpen ? 'false' : 'true');
	return true;
}

function toggleToolGroup(actionElement) {
	const id = actionElement.getAttribute('data-tool-group-id');
	const block = actionElement.closest('.tool-group-block');
	if (!id || !block) {
		return false;
	}

	const isOpen = openToolGroups.has(id);
	if (isOpen) {
		openToolGroups.delete(id);
		block.classList.remove('open');
	} else {
		openToolGroups.add(id);
		block.classList.add('open');
	}

	actionElement.setAttribute('aria-expanded', isOpen ? 'false' : 'true');
	return true;
}

async function handleDelegatedClick(event) {
	const target = event.target instanceof Element ? event.target : null;
	if (!target) {
		return;
	}

	const actionElement = target.closest('[data-action]');
	if (actionElement) {
		const action = actionElement.getAttribute('data-action');
		if (action && shouldSuppressClickAction(action, actionElement)) {
			return;
		}

		switch (action) {
			case 'select-conversation':
				openConversationMenuId.value = null;
				post({ type: 'select-conversation', conversationId: actionElement.getAttribute('data-conversation-id') });
				return;
			case 'toggle-conversation-branch': {
				const conversationId = actionElement.getAttribute('data-conversation-id');
				if (!conversationId) {
					return;
				}

				await preserveConversationList(() => {
					openConversationMenuId.value = null;
					const next = new Map(openConversationBranches.value);
					next.set(conversationId, openConversationBranches.value.get(conversationId) === false);
					openConversationBranches.value = next;
				});
				return;
			}
			case 'toggle-conversation-menu': {
				const conversationId = actionElement.getAttribute('data-conversation-id');
				await preserveConversationList(() => {
					openConversationMenuId.value = openConversationMenuId.value === conversationId ? null : conversationId;
				});
				return;
			}
			case 'delete-conversation':
				openConversationMenuId.value = null;
				post({ type: 'delete-conversation', conversationId: actionElement.getAttribute('data-conversation-id') });
				return;
			case 'toggle-thinking':
				toggleThinking(actionElement);
				return;
			case 'toggle-tool-segment':
				toggleToolSegment(actionElement);
				return;
			case 'toggle-tool-group':
				toggleToolGroup(actionElement);
				return;
			case 'toggle-team-member': {
				const memberId = actionElement.getAttribute('data-member-id');
				if (!memberId) {
					return;
				}

				await preserveStepsPanel(() => {
					const next = new Map(openTeamMembers.value);
					next.set(memberId, !(next.has(memberId) ? Boolean(next.get(memberId)) : false));
					openTeamMembers.value = next;
				});
				return;
			}
			case 'toggle-activity': {
				const id = actionElement.getAttribute('data-activity-id');
				const card = actionElement.closest('.activity-card');
				if (!id || !card) {
					return;
				}

				const isOpen = openActivities.has(id);
				if (isOpen) {
					openActivities.delete(id);
					card.classList.remove('open');
				} else {
					openActivities.add(id);
					card.classList.add('open');
				}

				const toggle = card.querySelector('.activity-toggle');
				if (toggle) {
					toggle.textContent = isOpen ? '详情' : '收起';
				}
				return;
			}
			case 'toggle-steps-section': {
				const sectionId = actionElement.getAttribute('data-section-id');
				if (!sectionId) {
					return;
				}

				await preserveStepsPanel(() => {
					const next = new Map(openStepSections.value);
					const current = next.has(sectionId) ? Boolean(next.get(sectionId)) : true;
					next.set(sectionId, !current);
					openStepSections.value = next;
				});
				return;
			}
			case 'approve-tool-execution':
				post({ type: 'approve-tool-execution', toolExecutionId: actionElement.getAttribute('data-tool-execution-id') });
				return;
			case 'reject-tool-execution':
				post({ type: 'reject-tool-execution', toolExecutionId: actionElement.getAttribute('data-tool-execution-id') });
				return;
		}
	}

	const link = target.closest('a[href]');
	if (link) {
		event.preventDefault();
		post({ type: 'open-link', href: link.getAttribute('href') });
		return;
	}

	if (openConversationMenuId.value) {
		await preserveConversationList(() => {
			openConversationMenuId.value = null;
		});
		return;
	}

	if (mentionState.open) {
		closeMentionPicker();
	}
}

function onTranscriptScroll(event) {
	const target = event.target instanceof HTMLElement ? event.target : null;
	if (!target) {
		return;
	}

	const nearBottom = target.scrollHeight - target.scrollTop - target.clientHeight < 40;
	if (nearBottom) {
		resumeTranscriptAutoFollow();
		return;
	}

	scrollFollowState.transcript = false;
}

function onRootWheel(event) {
	const transcriptTarget = event.target instanceof Element ? event.target.closest('#transcript-scroll') : null;
	if (transcriptTarget && event.deltaY < 0) {
		pauseTranscriptAutoFollow(1600);
		return;
	}

	const stepsTarget = event.target instanceof Element ? event.target.closest('#steps-scroll') : null;
	if (stepsTarget) {
		pauseStepsScrollRestore(1600);
	}
}

function onRootPointerDown(event) {
	const actionElement = event.target instanceof Element ? event.target.closest('[data-action]') : null;
	if (state.isBusy && actionElement) {
		const action = actionElement.getAttribute('data-action');
		const handled =
			action === 'toggle-thinking'
				? toggleThinking(actionElement)
				: action === 'toggle-tool-segment'
					? toggleToolSegment(actionElement)
					: action === 'toggle-tool-group'
						? toggleToolGroup(actionElement)
						: false;
		if (handled && action) {
			markPointerHandledAction(action, actionElement);
			pauseTranscriptAutoFollow(1600);
			event.preventDefault();
			return;
		}
	}

	const transcriptTarget = event.target instanceof Element ? event.target.closest('#transcript-scroll') : null;
	if (transcriptTarget && state.isBusy) {
		pauseTranscriptAutoFollow(900);
		return;
	}

	const stepsTarget = event.target instanceof Element ? event.target.closest('#steps-scroll') : null;
	if (stepsTarget) {
		pauseStepsScrollRestore(900);
	}
}

function newConversation() {
	openConversationMenuId.value = null;
	if (isChannelMode.value) {
		return;
	}

	post({ type: 'new-conversation' });
}

function selectConversationMode(modeId) {
	openConversationMenuId.value = null;
	post({ type: 'select-conversation-mode', modeId });
}

async function selectSettingsSection(sectionId) {
	if (!sectionId || activeSettingsSection.value === sectionId) {
		return;
	}

	activeSettingsSection.value = sectionId;
	settingsPanelScrollTop.value = 0;
	await nextTick();
	const panel = getSettingsPanelElement();
	if (panel) {
		panel.scrollTop = 0;
	}
}

function openSettings() {
	openConversationMenuId.value = null;
	clearFeedback();
	settingsOpen.value = true;
}

function closeSettings() {
	openConversationMenuId.value = null;
	settingsOpen.value = false;
	closeEditor();
}

function onConversationSearchChange(value) {
	conversationSearch.value = value;
}

function onConversationSearchInput() {
	if (openConversationMenuId.value) {
		openConversationMenuId.value = null;
	}
}

function onProfileSelectChange(profileId) {
	post({ type: 'select-profile', profileId });
}

function requestTopbarProfileModels() {
	const descriptor = buildTopbarProfileModelsCacheDescriptor();
	if (!descriptor) {
		return;
	}

	if (profileModelOptionsCache.has(descriptor.cacheKey)) {
		applyTopbarProfileModels(profileModelOptionsCache.get(descriptor.cacheKey));
		return;
	}

	if (topbarProfileModelsFetchRequestId && topbarProfileModelsFetchCacheKey === descriptor.cacheKey) {
		return;
	}

	const requestId = ++profileModelFetchRequestSeed;
	topbarProfileModelsFetchRequestId = requestId;
	topbarProfileModelsFetchCacheKey = descriptor.cacheKey;
	post({
		type: 'fetch-profile-models',
		requestId,
		profileId: descriptor.profileId,
		endpoint: descriptor.endpoint,
		apiKey: '',
	});
}

function onProfileModelChange(profileModel) {
	post({ type: 'select-profile-model', profileModel: profileModel || null });
}

function onSettingsProfileChange(profileId) {
	clearFeedback('profile');
	post({ type: 'select-profile', profileId });
}

function onWorkspaceChange(workspaceRootId) {
	clearFeedback('workspace');
	post({ type: 'select-workspace', workspaceRootId: workspaceRootId || null });
}

function onPermissionChange(permissionModeId) {
	post({ type: 'select-tool-permission', permissionModeId });
}

function pickComposerImages() {
	if (state.isBusy || isChannelMode.value) {
		return;
	}

	post({ type: 'pick-composer-images' });
}

function captureComposerScreenshot() {
	if (state.isBusy || isChannelMode.value) {
		return;
	}

	post({ type: 'capture-composer-screenshot' });
}

function removeComposerAttachment(attachmentId) {
	composerAttachments.value = composerAttachments.value.filter((item) => item.id !== attachmentId);
}

function onPlanningModeChange(enabled) {
	post({ type: 'set-plan-mode', enabled: Boolean(enabled) });
}

function onReasoningModeChange(enabled) {
	state.isReasoningEnabled = Boolean(enabled);
}

function togglePlanPanelCollapse() {
	planPanelCollapsed.value = !planPanelCollapsed.value;
}

function toggleLeftPaneCollapse() {
	openConversationMenuId.value = null;
	leftPaneCollapsed.value = !leftPaneCollapsed.value;
}

function onTeamRoundChange(roundsId) {
	post({ type: 'select-team-max-rounds', roundsId });
}

function onTeamOutputChange(outputModeId) {
	post({ type: 'select-team-output-mode', outputModeId });
}

function onThemeChange(themeId) {
	post({ type: 'select-theme', themeId });
}

function toggleChannelEnabled({ channel, enabled }) {
	clearFeedback('channels');
	post({
		type: 'toggle-channel',
		channelId: channel.id,
		enabled: Boolean(enabled),
	});
}

function saveEditor() {
	const error = validateEditorDraft(editorState);
	if (error) {
		editorState.feedback = { level: 'error', message: error, scope: editorScope() };
		return;
	}

	if (editorState.kind === 'profile') {
		post({
			type: 'save-profile',
			profileId: editorState.mode === 'edit' ? state.selectedProfileId || editorState.draft.profileId : null,
			name: editorState.draft.name.trim(),
			endpoint: editorState.draft.endpoint.trim(),
			model: editorState.draft.model.trim(),
			temperatureEnabled: Boolean(editorState.draft.temperatureEnabled),
			temperature: normalizeSamplingValue(editorState.draft.temperature, 0.7, 2),
			topPEnabled: Boolean(editorState.draft.topPEnabled),
			topP: normalizeSamplingValue(editorState.draft.topP, 0.7, 1),
			apiKey: editorState.draft.apiKey,
		});
		return;
	}

	if (editorState.kind === 'channel') {
		const fieldValues = Object.fromEntries((editorState.draft.fields || []).map((field) => [field.key, field.value || '']));
		post({
			type: 'save-channel',
			channelId: editorState.draft.channelId,
			displayName: editorState.draft.displayName.trim(),
			profileId: editorState.draft.profileId || null,
			fieldValues,
		});
		return;
	}

	post({
		type: 'save-workspace',
		workspaceRootId: editorState.mode === 'edit' ? state.selectedWorkspaceRootId || editorState.draft.workspaceRootId : null,
		name: editorState.draft.name.trim(),
		rootPath: editorState.draft.rootPath.trim(),
	});
}

function pickWorkspacePath() {
	post({ type: 'pick-workspace-path' });
}

function fetchProfileModels() {
	if (!editorState.open || editorState.kind !== 'profile' || !editorState.draft) {
		return;
	}

	const endpoint = editorState.draft.endpoint.trim();
	if (!endpoint) {
		editorState.feedback = { level: 'error', message: '请先填写 Endpoint。', scope: 'profile' };
		return;
	}

	const cacheKey = buildProfileModelsCacheKey(editorState.draft);
	if (editorState.draft.hasFetchedModelOptions && editorState.draft.modelOptionsCacheKey === cacheKey) {
		return;
	}

	if (profileModelOptionsCache.has(cacheKey)) {
		applyProfileModelOptions(editorState.draft, cacheKey, profileModelOptionsCache.get(cacheKey));
		editorState.feedback = null;
		return;
	}

	const requestId = ++profileModelFetchRequestSeed;
	editorState.draft.hasFetchedModelOptions = false;
	editorState.draft.modelOptionsCacheKey = cacheKey;
	editorState.draft.isFetchingModels = true;
	editorState.draft.fetchModelsRequestId = requestId;
	editorState.feedback = null;

	post({
		type: 'fetch-profile-models',
		requestId,
		profileId: editorState.mode === 'edit' ? state.selectedProfileId || editorState.draft.profileId : editorState.draft.profileId || null,
		endpoint,
		apiKey: editorState.draft.apiKey || '',
	});
}

function deleteProfile() {
	if (!state.selectedProfileId) {
		return;
	}

	clearFeedback('profile');
	post({ type: 'delete-profile', profileId: state.selectedProfileId });
}

function deleteWorkspace() {
	if (!state.selectedWorkspaceRootId) {
		return;
	}

	clearFeedback('workspace');
	post({ type: 'delete-workspace', workspaceRootId: state.selectedWorkspaceRootId });
}

function onSendClick() {
	if (state.isBusy) {
		post({ type: 'stop-generation' });
		return;
	}

	if (isChannelMode.value) {
		return;
	}

	submitComposer();
}

function onSettingsPanelScroll(scrollTop) {
	settingsPanelScrollTop.value = scrollTop;
}

function onVisualizationModeChange(enabled) {
	visualizationEnabled.value = Boolean(enabled);
}

function syncPaneCollapseForViewport() {
	if (window.innerWidth > desktopPaneCollapseBreakpoint) {
		return;
	}

	if (leftPaneCollapsed.value) {
		openConversationMenuId.value = null;
	}

	leftPaneCollapsed.value = false;
}

onMounted(() => {
	normalizeState();
	resetWorkspaceTreeState();
	window.selfClawComposerImagesPicked = handleComposerImagesPicked;
	window.addEventListener('selfclaw-composer-images-picked', handleComposerImagesPickedEvent);
	window.chrome?.webview?.addEventListener('message', handleWebViewMessage);
	document.addEventListener('keydown', onDocumentKeydown);
	window.addEventListener('resize', syncPaneCollapseForViewport);
	syncPaneCollapseForViewport();
	requestAnimationFrame(syncConversationMenuPlacement);
});

onUnmounted(() => {
	window.removeEventListener('selfclaw-composer-images-picked', handleComposerImagesPickedEvent);
	if (window.selfClawComposerImagesPicked === handleComposerImagesPicked) {
		delete window.selfClawComposerImagesPicked;
	}
	window.chrome?.webview?.removeEventListener?.('message', handleWebViewMessage);
	document.removeEventListener('keydown', onDocumentKeydown);
	window.removeEventListener('resize', syncPaneCollapseForViewport);
});
</script>

<template>
	<div class="transcript-vue-app" :class="{ busy: state.isBusy }" @click="handleDelegatedClick"
		@wheel.capture.passive="onRootWheel" @pointerdown.capture="onRootPointerDown">
		<div class="app-shell" :class="{ 'left-pane-collapsed': leftPaneCollapsed }">
			<ConversationSidebar ref="sidebarRef" :fallback-status-text="fallbackStatusText"
				:is-channel-mode="isChannelMode" :conversation-search="conversationSearch"
				:conversation-section-title="conversationSectionTitle" :conversation-list-html="conversationListHtml"
				:collapsed="leftPaneCollapsed" @new-conversation="newConversation" @open-settings="openSettings"
				@search-change="onConversationSearchChange" @search-input="onConversationSearchInput"
				@toggle-collapse="toggleLeftPaneCollapse" />

			<main class="main-column">
				<MainTopbar :conversation-modes="state.conversationModes"
					:selected-conversation-mode-id="state.selectedConversationModeId"
					:current-model-label="currentModelLabel" :current-workspace-label="currentWorkspaceLabel"
					:profile-models="state.profileModels" :selected-profile-model="state.selectedProfileModel || ''"
					:workspace-roots="state.workspaceRoots"
					:selected-workspace-root-id="state.selectedWorkspaceRootId || ''"
					@select-conversation-mode="selectConversationMode" @open-settings="openSettings"
					@request-profile-models="requestTopbarProfileModels"
					@select-profile-model="onProfileModelChange" @select-workspace="onWorkspaceChange" />

				<TranscriptPanel ref="transcriptPanelRef" :messages-html="messagesHtml"
					:visualization-enabled="activeVisualizationEnabled" :items="state.items"
					:conversations="state.conversations" :selected-conversation-id="state.selectedConversationId"
					:selected-conversation-mode-id="state.selectedConversationModeId"
					:selected-profile-model="state.selectedProfileModel || ''" :team-members="state.teamMembers"
					:agent-activities="state.agentActivities" :show-plan-panel="showPlanPanel"
					:plan-panel-collapsed="planPanelCollapsed" @scroll="onTranscriptScroll" />

				<ComposerPanel ref="composerPanelRef" :show-plan-panel="showPlanPanel" :plan-panel="planPanel"
					:plan-steps="planSteps" :plan-panel-collapsed="planPanelCollapsed"
					:collapsed-plan-text="collapsedPlanText" :composer-value="composerValue"
					:composer-placeholder="composerPlaceholder" :is-channel-mode="isChannelMode"
					:mention-state="mentionState" :mention-candidates="mentionCandidates" :profiles="state.profiles"
					:selected-profile-id="state.selectedProfileId || ''" :is-team-mode="isTeamMode"
					:team-round-modes="state.teamRoundModes"
					:selected-team-round-mode-id="state.selectedTeamRoundModeId"
					:team-output-modes="state.teamOutputModes"
					:selected-team-output-mode-id="state.selectedTeamOutputModeId"
					:tool-permission-modes="state.toolPermissionModes"
					:selected-tool-permission-mode-id="state.selectedToolPermissionModeId"
					:show-planning-toggle="showPlanningToggle" :show-visualization-toggle="showVisualizationToggle"
					:is-busy="state.isBusy" :is-planning-mode-enabled="state.isPlanningModeEnabled"
					:is-reasoning-enabled="state.isReasoningEnabled"
					:send-button-disabled="sendButtonDisabled" :visualization-enabled="activeVisualizationEnabled"
					:attachments="composerAttachments" @composer-input="onComposerInput"
					@composer-keydown="onComposerKeydown" @apply-mention="applyMentionSelection"
					@select-profile="onProfileSelectChange" @select-team-round="onTeamRoundChange"
					@select-team-output="onTeamOutputChange" @select-permission="onPermissionChange"
					@toggle-reasoning-mode="onReasoningModeChange"
					@toggle-planning-mode="onPlanningModeChange" @toggle-visualization-mode="onVisualizationModeChange"
					@toggle-plan-panel-collapse="togglePlanPanelCollapse" @pick-images="pickComposerImages"
					@capture-screenshot="captureComposerScreenshot" @remove-attachment="removeComposerAttachment"
					@send-click="onSendClick" />
			</main>

			<StepsPanel ref="stepsPanelRef" :steps-header-html="stepsHeaderHtml" :steps-panel-html="stepsPanelHtml"
				:panel-mode="rightPanelMode" :workspace-label="workspaceTreeLabel" :workspace-path="workspaceTreePath"
				:workspace-tree-entries="workspaceTreeState.entries"
				:workspace-tree-loading="workspaceTreeState.isLoading"
				:workspace-tree-loaded="workspaceTreeState.isLoaded" :workspace-tree-error="workspaceTreeState.error"
				:has-workspace="hasSelectedWorkspace" @set-panel-mode="setRightPanelMode"
				@toggle-workspace-directory="toggleWorkspaceDirectory" @open-workspace-file="openWorkspaceFile"
				@open-workspace-entry-location="openWorkspaceEntryLocation" />

			<button v-if="leftPaneCollapsed"
				class="pane-collapse-toggle pane-collapse-toggle-floating pane-collapse-toggle-floating-left"
				type="button" aria-label="展开左侧会话栏" title="展开左侧会话栏" @click="toggleLeftPaneCollapse">
				<svg class="pane-collapse-toggle-icon pane-collapse-toggle-icon-left collapsed"
					xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" aria-hidden="true">
					<path fill="currentColor"
						d="M452.864 149.312a29.12 29.12 0 0 1 41.728.064L826.24 489.664a32 32 0 0 1 0 44.672L494.592 874.624a29.12 29.12 0 0 1-41.728 0 30.59 30.59 0 0 1 0-42.752L764.736 512 452.864 192a30.59 30.59 0 0 1 0-42.688m-256 0a29.12 29.12 0 0 1 41.728.064L570.24 489.664a32 32 0 0 1 0 44.672L238.592 874.624a29.12 29.12 0 0 1-41.728 0 30.59 30.59 0 0 1 0-42.752L508.736 512 196.864 192a30.59 30.59 0 0 1 0-42.688">
					</path>
				</svg>
			</button>


		</div>

		<SettingsModal ref="settingsModalRef" :open="settingsOpen" :settings-sections="settingsSections"
			:active-section="activeSettingsSection" :active-settings-meta="activeSettingsMeta"
			:visible-feedback="visibleSettingsFeedback" :selected-profile="selectedProfile" :profiles="state.profiles"
			:selected-profile-id="state.selectedProfileId || ''" :profile-summary-cards="profileSummaryCards"
			:selected-workspace="selectedWorkspace" :workspace-roots="state.workspaceRoots"
			:selected-workspace-root-id="state.selectedWorkspaceRootId || ''"
			:workspace-summary-cards="workspaceSummaryCards" :channels="state.channels"
			:selected-theme-label="selectedThemeLabel" :theme-options="state.themeOptions"
			:selected-theme-id="state.selectedThemeId || 'system'" @close="closeSettings"
			@select-section="selectSettingsSection" @panel-scroll="onSettingsPanelScroll"
			@select-profile="onSettingsProfileChange" @edit-profile="openEditor('profile', 'edit')"
			@delete-profile="deleteProfile" @create-profile="openEditor('profile', 'create')"
			@select-workspace="onWorkspaceChange" @edit-workspace="openEditor('workspace', 'edit')"
			@delete-workspace="deleteWorkspace" @create-workspace="openEditor('workspace', 'create')"
			@toggle-channel="toggleChannelEnabled" @edit-channel="openEditor('channel', 'edit', $event)"
			@select-theme="onThemeChange" />

		<EditorModal :open="editorState.open" :editor="editorState" :profiles="state.profiles" @close="closeEditor"
			@pick-workspace-path="pickWorkspacePath" @fetch-models="fetchProfileModels" @save="saveEditor" />
	</div>
</template>
