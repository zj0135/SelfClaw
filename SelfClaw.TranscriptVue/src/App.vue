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
	selectedProfileId: null,
	selectedProfileModel: null,
	workspaceRoots: [],
	selectedWorkspaceRootId: null,
	toolPermissionModes: [],
	selectedToolPermissionModeId: 'requireApproval',
	isPlanningModeEnabled: false,
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
const conversationSearch = ref('');
const planPanelCollapsed = ref(false);
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

let pendingStatePayload = null;
let renderFrameHandle = 0;
let profileModelFetchRequestSeed = 0;

const post = (message) => window.chrome?.webview?.postMessage(message);

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
const visibleTeamMembers = computed(() => state.teamMembers || []);
const isProgrammingMode = computed(() => state.selectedConversationModeId === 'programming');
const isTeamMode = computed(() => state.selectedConversationModeId === 'team');
const isChannelMode = computed(() => state.selectedConversationModeId === 'channel');
const showPlanningToggle = computed(() => isProgrammingMode.value);
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
const currentModelLabel = computed(() => state.selectedProfileModel || selectedProfile.value?.label || '未选择模型');
const currentWorkspaceLabel = computed(() => selectedWorkspace.value?.label || '未绑定工作区');
const composerPlaceholder = computed(() => (isChannelMode.value ? '频道会话由外部消息自动驱动' : 'Ask for follow-up changes'));
const conversationSectionTitle = computed(() =>
	isChannelMode.value ? '频道会话' : state.conversations.some((item) => item.parentId) ? '会话树' : '最近会话'
);
const totalStepCount = computed(() => (isTeamMode.value ? visibleTeamMembers.value.length + (state.agentActivities?.length || 0) : state.agentActivities?.length || 0));
const sendButtonDisabled = computed(() => isChannelMode.value || ((!composerValue.value.trim() && !state.isBusy) || !state.selectedProfileId));
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
const stepsHeaderHtml = computed(() => renderStepsHeader({ isTeamMode: isTeamMode.value, totalCount: totalStepCount.value }));
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
	state.toolPermissionModes = state.toolPermissionModes || [];
	state.selectedToolPermissionModeId = state.selectedToolPermissionModeId || 'requireApproval';
	state.isPlanningModeEnabled = Boolean(state.isPlanningModeEnabled);
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
	const prompt = composerValue.value.trim();
	if (!prompt) {
		return;
	}

	post({ type: 'send-prompt', prompt });
	composerValue.value = '';
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
				? createProfileDraft(selectedProfile.value, state.selectedProfileModel)
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

function handleProfileModelsFetched(payload) {
	if (!editorState.open || editorState.kind !== 'profile' || !editorState.draft) {
		return;
	}

	if (Number(payload.requestId || 0) !== Number(editorState.draft.fetchModelsRequestId || 0)) {
		return;
	}

	editorState.draft.isFetchingModels = false;
	editorState.draft.fetchModelsRequestId = 0;

	const modelOptions = Array.isArray(payload.models)
		? [...new Set(payload.models.filter((item) => typeof item === 'string').map((item) => item.trim()).filter(Boolean))]
		: [];

	editorState.draft.modelOptions = modelOptions;
	editorState.feedback = payload.errorMessage
		? { level: 'error', message: payload.errorMessage, scope: 'profile' }
		: {
				level: 'success',
				message: modelOptions.length ? `已加载 ${modelOptions.length} 个模型。` : '没有获取到任何模型。',
				scope: 'profile',
			};

	if (!payload.errorMessage) {
		editorState.feedback = null;
	}

	if (!payload.errorMessage && !editorState.draft.model.trim() && modelOptions.length > 0) {
		editorState.draft.model = modelOptions[0];
	}
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

function onPlanningModeChange(enabled) {
	post({ type: 'set-plan-mode', enabled: Boolean(enabled) });
}

function togglePlanPanelCollapse() {
	planPanelCollapsed.value = !planPanelCollapsed.value;
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

	const requestId = ++profileModelFetchRequestSeed;
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

onMounted(() => {
	normalizeState();
	window.chrome?.webview?.addEventListener('message', handleWebViewMessage);
	document.addEventListener('keydown', onDocumentKeydown);
	requestAnimationFrame(syncConversationMenuPlacement);
});

onUnmounted(() => {
	window.chrome?.webview?.removeEventListener?.('message', handleWebViewMessage);
	document.removeEventListener('keydown', onDocumentKeydown);
});
</script>

<template>
	<div class="transcript-vue-app" :class="{ busy: state.isBusy }" @click="handleDelegatedClick"
		@wheel.capture.passive="onRootWheel" @pointerdown.capture="onRootPointerDown">
		<div class="app-shell">
			<ConversationSidebar ref="sidebarRef" :fallback-status-text="fallbackStatusText"
				:is-channel-mode="isChannelMode" :conversation-search="conversationSearch"
				:conversation-section-title="conversationSectionTitle" :conversation-list-html="conversationListHtml"
				@new-conversation="newConversation" @open-settings="openSettings"
				@search-change="onConversationSearchChange" @search-input="onConversationSearchInput" />

			<main class="main-column">
				<MainTopbar :conversation-modes="state.conversationModes"
					:selected-conversation-mode-id="state.selectedConversationModeId"
					:current-model-label="currentModelLabel" :current-workspace-label="currentWorkspaceLabel"
					@select-conversation-mode="selectConversationMode" @open-settings="openSettings" />

				<TranscriptPanel ref="transcriptPanelRef" :messages-html="messagesHtml" :show-plan-panel="showPlanPanel"
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
					:show-planning-toggle="showPlanningToggle" :is-busy="state.isBusy"
					:is-planning-mode-enabled="state.isPlanningModeEnabled" :send-button-disabled="sendButtonDisabled"
					@composer-input="onComposerInput" @composer-keydown="onComposerKeydown"
					@apply-mention="applyMentionSelection" @select-profile="onProfileSelectChange"
					@select-team-round="onTeamRoundChange" @select-team-output="onTeamOutputChange"
					@select-permission="onPermissionChange" @toggle-planning-mode="onPlanningModeChange"
					@toggle-plan-panel-collapse="togglePlanPanelCollapse" @send-click="onSendClick" />
			</main>

			<StepsPanel ref="stepsPanelRef" :steps-header-html="stepsHeaderHtml" :steps-panel-html="stepsPanelHtml" />
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
