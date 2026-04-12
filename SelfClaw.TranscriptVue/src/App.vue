<script setup>
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import { renderConversationList, renderMessages, renderStepsHeader, renderStepsPanelContent } from './utils/renderers';

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

const composerRef = ref(null);
const conversationListRef = ref(null);
const transcriptScrollRef = ref(null);
const stepsScrollRef = ref(null);
const settingsPanelRef = ref(null);

const openActivities = new Set();
const openThoughts = new Set();
const openToolSegments = new Set();
const pointerHandledActions = new Map();
const scrollFollowState = {
	transcript: true,
	transcriptPausedUntil: 0,
	stepsPausedUntil: 0,
};
const pointerActionSuppressDurationMs = 700;

let pendingStatePayload = null;
let renderFrameHandle = 0;

const emptyProfile = () => ({
	profileId: null,
	name: '',
	endpoint: '',
	model: '',
	temperatureEnabled: false,
	temperature: 0.7,
	topPEnabled: false,
	topP: 0.7,
	apiKey: '',
});
const emptyWorkspace = () => ({ workspaceRootId: null, name: '', rootPath: '' });
const emptyChannel = () => ({
	channelId: 'feishu',
	displayName: '',
	profileId: '',
	fields: [],
});

const post = (message) => window.chrome?.webview?.postMessage(message);

const setTheme = (theme) => {
	document.documentElement.dataset.theme = theme === 'light' ? 'light' : 'dark';
};

const selectedProfile = computed(() => state.profiles.find((item) => item.id === state.selectedProfileId) || null);
const selectedWorkspace = computed(() => state.workspaceRoots.find((item) => item.id === state.selectedWorkspaceRootId) || null);
const visibleTeamMembers = computed(() => state.teamMembers || []);
const isTeamMode = computed(() => state.selectedConversationModeId === 'team');
const isChannelMode = computed(() => state.selectedConversationModeId === 'channel');
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
			? `${selectedProfile.value.label}${state.selectedProfileModel ? ` 路 ${state.selectedProfileModel}` : ''}`
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

const messagesHtml = computed(() => renderMessages(state.items, openThoughts, openToolSegments));
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

watch(settingsOpen, async (isOpen) => {
	if (!isOpen) {
		return;
	}

	await nextTick();
	if (settingsPanelRef.value) {
		settingsPanelRef.value.scrollTop = settingsPanelScrollTop.value;
	}
});

function normalizeState() {
	state.conversationModes = state.conversationModes || [];
	state.selectedConversationModeId = state.selectedConversationModeId || 'programming';
	state.toolPermissionModes = state.toolPermissionModes || [];
	state.selectedToolPermissionModeId = state.selectedToolPermissionModeId || 'requireApproval';
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
	const target = composerRef.value;
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

function profileDraft() {
	const profile = selectedProfile.value;
	return {
		profileId: profile?.id || null,
		name: profile?.label || '',
		endpoint: profile?.description || '',
		model: state.selectedProfileModel || '',
		temperatureEnabled: Boolean(profile?.temperatureEnabled),
		temperature: normalizeSamplingValue(profile?.temperature, 0.7, 2),
		topPEnabled: Boolean(profile?.topPEnabled),
		topP: normalizeSamplingValue(profile?.topP, 0.7, 1),
		apiKey: '',
	};
}

function workspaceDraft() {
	const workspace = selectedWorkspace.value;
	return {
		workspaceRootId: workspace?.id || null,
		name: workspace?.label || '',
		rootPath: workspace?.description || '',
	};
}

function channelDraft(channel) {
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

function clearFeedback(scope) {
	if (settingsFeedback.value && (!scope || !settingsFeedback.value.scope || settingsFeedback.value.scope === scope)) {
		settingsFeedback.value = null;
	}
}

function normalizeSamplingValue(value, fallback, max) {
	const numeric = Number(value);
	if (Number.isNaN(numeric) || !Number.isFinite(numeric)) {
		return fallback;
	}

	return Math.max(0, Math.min(max, Number(numeric.toFixed(2))));
}

function formatSamplingValue(value, max) {
	return normalizeSamplingValue(value, 0.7, max).toFixed(2);
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
				? profileDraft()
				: emptyProfile()
			: kind === 'workspace'
				? mode === 'edit' && state.selectedWorkspaceRootId
					? workspaceDraft()
					: emptyWorkspace()
				: payload
					? channelDraft(payload)
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

function validateDraft() {
	if (!editorState.open || !editorState.draft) {
		return '没有可保存的表单内容。';
	}

	if (editorState.kind === 'profile') {
		if (!editorState.draft.name.trim() || !editorState.draft.endpoint.trim() || !editorState.draft.model.trim()) {
			return '请完整填写配置名称、Endpoint 和模型。';
		}

		if (editorState.mode === 'create' && !editorState.draft.apiKey.trim()) {
			return '新增配置时必须提供 API Key。';
		}

		return null;
	}

	if (editorState.kind === 'channel') {
		if (!editorState.draft.displayName.trim()) {
			return '请填写频道名称。';
		}

		if (!editorState.draft.profileId) {
			return '请先为频道绑定模型。';
		}

		for (const field of editorState.draft.fields || []) {
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

	if (!editorState.draft.rootPath.trim()) {
		return '请先选择工作区位置。';
	}

	return null;
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

	const conversationList = conversationListRef.value;
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
	const snapshot = captureScroll(conversationListRef.value);
	mutator();
	await nextTick();
	restoreScroll(conversationListRef.value, snapshot);
	requestAnimationFrame(syncConversationMenuPlacement);
}

async function preserveStepsPanel(mutator) {
	const snapshot = captureScroll(stepsScrollRef.value);
	mutator();
	await nextTick();
	if (canRestoreStepsScroll()) {
		restoreScroll(stepsScrollRef.value, snapshot);
	}
}

async function applyStatePayload(payload) {
	const transcriptState = captureScroll(transcriptScrollRef.value);
	const conversationState = captureScroll(conversationListRef.value);
	const stepsState = captureScroll(stepsScrollRef.value);
	const { type: _type, ...nextState } = payload;

	Object.assign(state, nextState);
	normalizeState();

	await nextTick();
	restoreScroll(conversationListRef.value, conversationState);
	if (canRestoreStepsScroll()) {
		restoreScroll(stepsScrollRef.value, stepsState);
	}

	restoreScroll(
		transcriptScrollRef.value,
		transcriptState,
		canAutoFollowTranscript(transcriptState)
	);
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
			case 'toggle-thinking': {
				toggleThinking(actionElement);
				return;
			}
			case 'toggle-tool-segment': {
				toggleToolSegment(actionElement);
				return;
			}
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

	if (!nearBottom) {
		scrollFollowState.transcript = false;
	}
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
			action === 'toggle-thinking' ? toggleThinking(actionElement) : action === 'toggle-tool-segment' ? toggleToolSegment(actionElement) : false;
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
	if (settingsPanelRef.value) {
		settingsPanelRef.value.scrollTop = 0;
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

function onConversationSearchInput() {
	if (openConversationMenuId.value) {
		openConversationMenuId.value = null;
	}
}

function onProfileSelectChange(event) {
	post({ type: 'select-profile', profileId: event.target.value });
}

function onSettingsProfileChange(event) {
	clearFeedback('profile');
	post({ type: 'select-profile', profileId: event.target.value });
}

function onWorkspaceChange(event) {
	clearFeedback('workspace');
	post({ type: 'select-workspace', workspaceRootId: event.target.value || null });
}

function onPermissionChange(event) {
	post({ type: 'select-tool-permission', permissionModeId: event.target.value });
}

function onTeamRoundChange(event) {
	post({ type: 'select-team-max-rounds', roundsId: event.target.value });
}

function onTeamOutputChange(event) {
	post({ type: 'select-team-output-mode', outputModeId: event.target.value });
}

function onThemeChange(event) {
	post({ type: 'select-theme', themeId: event.target.value });
}

function toggleChannelEnabled(channel, event) {
	clearFeedback('channels');
	post({
		type: 'toggle-channel',
		channelId: channel.id,
		enabled: Boolean(event.target.checked),
	});
}

function saveEditor() {
	const error = validateDraft();
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
		const fieldValues = Object.fromEntries(
			(editorState.draft.fields || []).map((field) => [field.key, field.value || ''])
		);
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

function onSettingsPanelScroll(event) {
	const panel = event.target instanceof HTMLElement ? event.target : null;
	if (panel) {
		settingsPanelScrollTop.value = panel.scrollTop;
	}
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
	<div class="transcript-vue-app" :class="{ busy: state.isBusy }" @click="handleDelegatedClick" @wheel.capture.passive="onRootWheel"
		@pointerdown.capture="onRootPointerDown">
		<div class="app-shell">
			<aside class="panel sidebar">
				<div class="brand">
					<div class="brand-badge">SC</div>
					<div>
						<div class="brand-name">SelfClaw</div>
						<div class="status-row">
							<span class="status-dot"></span>
							<span id="sidebar-status-text">{{ fallbackStatusText }}</span>
						</div>
					</div>
				</div>
				<button class="sidebar-primary" type="button" :disabled="isChannelMode"
					:title="isChannelMode ? '频道会话由外部消息自动创建' : '新建对话'" @click="newConversation">+ 新建对话</button>
				<input id="conversation-search" v-model="conversationSearch" class="search-box" type="text"
					placeholder="搜索会话..." @input="onConversationSearchInput" />
				<div class="section-title">{{ conversationSectionTitle }}</div>
				<div id="conversation-list" ref="conversationListRef" class="conversation-list"
					v-html="conversationListHtml"></div>
				<button class="sidebar-footer" type="button" @click="openSettings">
					<div class="avatar">SC</div>
					<div class="sidebar-footer-copy">
						<div class="sidebar-footer-title">系统设置</div>
						<div class="sidebar-footer-subtitle">模型、工作区、我的频道、主题</div>
					</div>
					<div>&rsaquo;</div>
				</button>
			</aside>

			<main class="main-column">
				<div class="panel topbar">
					<div id="mode-chip-row" class="chip-row">
						<button v-for="mode in state.conversationModes" :key="mode.id" class="mode-chip"
							:class="{ active: mode.id === state.selectedConversationModeId }" type="button"
							@click="selectConversationMode(mode.id)">
							{{ mode.label }}
						</button>
						
					</div>
					<div class="topbar-right">
						<div id="topbar-model-pill" class="context-pill" :title="currentModelLabel">
							<span class="context-label">模型</span>
							<span id="topbar-model-value" class="context-value">{{ currentModelLabel }}</span>
						</div>
						<div id="topbar-workspace-pill" class="context-pill" :title="currentWorkspaceLabel">
							<span class="context-label">工作区</span>
							<span id="topbar-workspace-value" class="context-value">{{ currentWorkspaceLabel }}</span>
						</div>
						<button class="icon-btn" type="button" aria-label="打开系统设置" @click="openSettings">设置</button>
					</div>
				</div>

				<section class="panel transcript-panel">
					<div id="transcript-scroll" ref="transcriptScrollRef" class="transcript-scroll"
						v-html="messagesHtml" @scroll="onTranscriptScroll"></div>
				</section>

				<section class="panel composer-panel">

					<div class="composer-grid">
						<div class="composer-surface">
							<div class="composer-stack">
								<textarea id="composer" ref="composerRef" v-model="composerValue" class="composer-box"
								:disabled="isChannelMode" :placeholder="composerPlaceholder" @input="onComposerInput"
								@keydown="onComposerKeydown"></textarea>
								<div id="mention-picker" class="mention-picker"
									:class="{ open: mentionState.open && mentionCandidates.length > 0 }">
									<button v-for="(item, index) in mentionCandidates" :key="item.id"
										class="mention-option" :class="{ active: index === mentionState.activeIndex }"
										type="button" @click.stop="applyMentionSelection(item)">
										<span class="mention-option-name">@{{ item.name }}</span>
										<span class="mention-option-role">{{ item.role }}</span>
									</button>
								</div>
							</div>
							<div class="composer-footer">
								<div class="composer-controls">
									<select id="composer-profile-select" class="composer-inline-select"
										aria-label="当前模型配置" :value="state.selectedProfileId || ''"
										@change="onProfileSelectChange">
										<option value="">选择模型</option>
										<option v-for="option in state.profiles" :key="option.id" :value="option.id">{{
											option.label }}</option>
									</select>
									<template v-if="isTeamMode">
										<select id="composer-team-round-select" class="composer-inline-select"
											aria-label="团队最大讨论轮次" :value="state.selectedTeamRoundModeId"
											@change="onTeamRoundChange">
											<option v-for="option in state.teamRoundModes" :key="option.id"
												:value="option.id">{{ option.label }}</option>
										</select>
										<select id="composer-team-output-select" class="composer-inline-select"
											aria-label="团队总结输出方式" :value="state.selectedTeamOutputModeId"
											@change="onTeamOutputChange">
											<option v-for="option in state.teamOutputModes" :key="option.id"
												:value="option.id">{{ option.label }}</option>
										</select>
									</template>
									<select v-else id="composer-permission-select" class="composer-inline-select"
										aria-label="工具权限模式" :value="state.selectedToolPermissionModeId"
										@change="onPermissionChange">
										<option v-for="option in state.toolPermissionModes" :key="option.id"
											:value="option.id">{{ option.label }}</option>
									</select>
								</div>
								<button id="send-button" class="send-btn"
									:class="{ loading: state.isBusy, idle: !state.isBusy }" type="button"
									:disabled="sendButtonDisabled" :aria-label="state.isBusy ? '停止生成' : '发送消息'"
									:title="state.isBusy ? '停止生成' : '发送消息'" @click="onSendClick">
									<span v-if="state.isBusy" class="send-btn-spinner" aria-hidden="true">
										<span class="send-btn-spinner-ring"></span>
										<span class="send-btn-spinner-core"></span>
									</span>
									<span v-else class="send-btn-arrow" aria-hidden="true">
										<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2"
											stroke-linecap="round" stroke-linejoin="round">
											<path d="M12 19V7"></path>
											<path d="m6 11 6-6 6 6"></path>
										</svg>
									</span>
								</button>
							</div>
						</div>
					</div>
				</section>
			</main>

			<aside id="steps-panel-shell" class="panel steps-panel">
				<div id="steps-header" class="steps-header" v-html="stepsHeaderHtml"></div>
				<div id="steps-scroll" ref="stepsScrollRef" class="steps-scroll" v-html="stepsPanelHtml"></div>
			</aside>
		</div>

		<div id="settings-overlay" class="settings-overlay" :class="{ open: settingsOpen }" @click.self="closeSettings">
			<div v-if="settingsOpen" class="settings-panel" role="dialog" aria-modal="true" aria-label="系统设置">
				<aside class="settings-nav">
					<div class="settings-nav-header">
						<div class="settings-title">系统设置</div>
						<div class="settings-hint">左侧切换模块，右侧集中完成当前配置。</div>
					</div>

					<div class="settings-nav-list">
						<button v-for="section in settingsSections" :key="section.id" class="settings-nav-item"
							:class="{ active: activeSettingsSection === section.id }" type="button"
							:aria-pressed="activeSettingsSection === section.id"
							@click="selectSettingsSection(section.id)">
							<div class="settings-nav-item-top">
								<div class="settings-nav-item-title">{{ section.title }}</div>
								<div class="settings-nav-item-badge">{{ section.badge }}</div>
							</div>
							<div class="settings-nav-item-description">{{ section.description }}</div>
						</button>
					</div>

					<div class="settings-nav-footer">
						<button class="ghost-btn" type="button" @click="closeSettings">完成</button>
					</div>
				</aside>

				<div ref="settingsPanelRef" class="settings-content" @scroll="onSettingsPanelScroll">
					<div class="settings-header">
						<div>
							<div class="field-label">{{ activeSettingsMeta?.eyebrow }}</div>
							<div class="settings-section-title settings-section-title-hero">{{ activeSettingsMeta?.title
							}}
							</div>
							<div class="settings-hint settings-header-hint">{{ activeSettingsMeta?.description }}</div>
						</div>
						<button class="close-btn" type="button" aria-label="关闭" @click="closeSettings">&times;</button>
					</div>

					<div v-if="visibleSettingsFeedback" class="settings-feedback"
						:class="visibleSettingsFeedback.level === 'error' ? 'error' : 'success'">
						{{ visibleSettingsFeedback.message }}
					</div>

					<section v-if="activeSettingsSection === 'profile'"
						class="settings-section settings-section-active">
						<div class="settings-section-header">
							<div class="settings-section-copy">
								<div class="field-label">当前配置</div>
								<div class="settings-section-title">模型选择与管理</div>
							</div>
							<div class="settings-badge">{{ selectedProfile ? '已选择' : '未选择' }}</div>
						</div>
						<div class="field-group">
							<div class="field-label">当前配置</div>
							<div class="settings-select-row">
								<select id="profile-select" class="field-select" :value="state.selectedProfileId || ''"
									@change="onSettingsProfileChange">
									<option value="">未选择配置</option>
									<option v-for="option in state.profiles" :key="option.id" :value="option.id">{{
										option.label }}
									</option>
								</select>
								<button class="ghost-btn compact-btn" type="button" :disabled="!selectedProfile"
									@click="openEditor('profile', 'edit')">编辑</button>
								<button class="ghost-btn compact-btn danger-btn" type="button"
									:disabled="!selectedProfile" @click="deleteProfile">删除</button>
								<button class="icon-add-btn" type="button" aria-label="新增模型配置"
									@click="openEditor('profile', 'create')">+</button>
							</div>
						</div>
						<div class="selected-summary-grid">
							<div v-for="card in profileSummaryCards" :key="card.label" class="selected-summary-card">
								<div class="selected-summary-label">{{ card.label }}</div>
								<div class="selected-summary-value">{{ card.value }}</div>
							</div>
						</div>
					</section>

					<section v-else-if="activeSettingsSection === 'workspace'"
						class="settings-section settings-section-active">
						<div class="settings-section-header">
							<div class="settings-section-copy">
								<div class="field-label">当前工作区</div>
								<div class="settings-section-title">工作区绑定与切换</div>
							</div>
							<div class="settings-badge">{{ selectedWorkspace ? '已绑定' : '未绑定' }}</div>
						</div>
						<div class="field-group">
							<div class="field-label">当前工作区</div>
							<div class="settings-select-row">
								<select id="workspace-select" class="field-select"
									:value="state.selectedWorkspaceRootId || ''" @change="onWorkspaceChange">
									<option value="">未绑定工作区</option>
									<option v-for="option in state.workspaceRoots" :key="option.id" :value="option.id">
										{{
											option.label }}</option>
								</select>
								<button class="ghost-btn compact-btn" type="button" :disabled="!selectedWorkspace"
									@click="openEditor('workspace', 'edit')">编辑</button>
								<button class="ghost-btn compact-btn danger-btn" type="button"
									:disabled="!selectedWorkspace" @click="deleteWorkspace">删除</button>
								<button class="icon-add-btn" type="button" aria-label="新增工作区"
									@click="openEditor('workspace', 'create')">+</button>
							</div>
						</div>
						<div class="selected-summary-grid">
							<div v-for="card in workspaceSummaryCards" :key="card.label" class="selected-summary-card">
								<div class="selected-summary-label">{{ card.label }}</div>
								<div class="selected-summary-value">{{ card.value }}</div>
							</div>
						</div>
					</section>

					<section v-else-if="activeSettingsSection === 'channels'"
						class="settings-section settings-section-active">
						<div class="settings-section-header">
							<div class="settings-section-copy">
								<div class="field-label">支持的频道</div>
								<div class="settings-section-title">频道接入与监听</div>
							</div>
							<div class="settings-badge">{{ state.channels.filter((item) => item.isEnabled).length }} / {{ state.channels.length }}</div>
						</div>
						<div class="channel-card-list">
							<article v-for="channel in state.channels" :key="channel.id" class="channel-card"
								:class="[{ enabled: channel.isEnabled }, channel.status]">
								<div class="channel-card-top">
									<div class="channel-card-copy">
										<div class="field-label">{{ channel.name }}</div>
										<div class="settings-section-title">{{ channel.displayName || channel.name }}</div>
										<div class="settings-hint">{{ channel.description }}</div>
									</div>
									<label class="toggle-field channel-toggle">
										<input class="toggle-input" type="checkbox" :checked="channel.isEnabled"
											@change="toggleChannelEnabled(channel, $event)" />
										<span class="toggle-switch"></span>
										<span class="toggle-label">{{ channel.isEnabled ? '已开启' : '已关闭' }}</span>
									</label>
								</div>
								<div class="selected-summary-grid channel-summary-grid">
									<div v-for="summary in channel.summaryItems" :key="summary.label" class="selected-summary-card">
										<div class="selected-summary-label">{{ summary.label }}</div>
										<div class="selected-summary-value">{{ summary.value }}</div>
									</div>
								</div>
								<div v-if="channel.statusDetail" class="settings-hint channel-status-detail">{{ channel.statusDetail }}</div>
								<div class="channel-card-actions">
									<div class="settings-badge">{{ channel.statusLabel }}</div>
									<button class="ghost-btn compact-btn" type="button" @click="openEditor('channel', 'edit', channel)">配置</button>
								</div>
							</article>
						</div>
					</section>

					<section v-else class="settings-section settings-section-active">
						<div class="settings-section-header">
							<div class="settings-section-copy">
								<div class="field-label">界面主题</div>
								<div class="settings-section-title">主题与外观</div>
							</div>
							<div class="settings-badge">{{ selectedThemeLabel }}</div>
						</div>
						<div class="field-group">
							<div class="field-label">界面主题</div>
							<select id="theme-select" class="field-select" :value="state.selectedThemeId || 'system'"
								@change="onThemeChange">
								<option v-for="option in state.themeOptions" :key="option.id" :value="option.id">{{
									option.label }}
								</option>
							</select>
						</div>
					</section>
				</div>
			</div>
		</div>

		<div id="editor-overlay" class="editor-overlay" :class="{ open: editorState.open }" @click.self="closeEditor">
			<div v-if="editorState.open && editorState.draft" class="editor-panel" role="dialog" aria-modal="true"
				:aria-label="editorState.kind === 'profile' ? (editorState.mode === 'create' ? '新增模型配置' : '编辑模型配置') : editorState.kind === 'channel' ? '编辑频道配置' : editorState.mode === 'create' ? '新增工作区' : '编辑工作区'">
				<div class="editor-header">
					<div>
						<div class="editor-title">
							{{ editorState.kind === 'profile' ? (editorState.mode === 'create' ? '新增模型配置' : '编辑模型配置') :
								editorState.kind === 'channel' ? '编辑频道配置' : editorState.mode === 'create' ? '新增工作区' : '编辑工作区' }}
						</div>
						<div class="settings-hint">
							{{
								editorState.kind === 'profile'
									? editorState.mode === 'create'
										? '填写名称、Endpoint、模型、采样参数和 API Key 后保存，新配置会自动加入下拉列表并切换到当前选择。'
										: '你可以更新当前模型配置和采样参数；如果不需要替换密钥，API Key 留空即可。'
									: editorState.kind === 'channel'
										? '填写频道名称、绑定模型和当前渠道要求的连接字段后保存；开启开关后就会开始接收该渠道消息。'
								: editorState.mode === 'create'
									? '填写名称并选择本机目录后保存，工作区会自动加入下拉列表并设为当前选择。'
									: '在这里调整当前工作区的显示名称或重新选择目录，然后保存变更。'
							}}
						</div>
					</div>
					<button class="close-btn" type="button" aria-label="关闭" @click="closeEditor">&times;</button>
				</div>

				<div v-if="editorState.feedback" class="settings-feedback"
					:class="editorState.feedback.level === 'error' ? 'error' : 'success'">
					{{ editorState.feedback.message }}
				</div>

				<div class="editor-body">
					<template v-if="editorState.kind === 'profile'">
						<div class="field-inline">
							<div>
								<div class="field-label">配置名称</div>
								<input id="editor-profile-name" v-model="editorState.draft.name" class="field-input"
									type="text" placeholder="例如：OpenAI / 本地代理" />
							</div>
							<div>
								<div class="field-label">模型</div>
								<input id="editor-profile-model" v-model="editorState.draft.model" class="field-input"
									type="text" placeholder="例如：gpt-4.1-mini" />
							</div>
						</div>
						<div>
							<div class="field-label">Endpoint</div>
							<input id="editor-profile-endpoint" v-model="editorState.draft.endpoint" class="field-input"
								type="text" placeholder="https://api.openai.com/v1" />
						</div>
						<div class="field-inline field-inline-ranges">
							<div class="range-field" :class="{ disabled: !editorState.draft.temperatureEnabled }">
								<div class="range-header">
									<div>
										<div class="field-label">Temperature</div>
										<label class="toggle-field">
											<input id="editor-profile-temperature-enabled"
												v-model="editorState.draft.temperatureEnabled" class="toggle-input"
												type="checkbox" />
											<span class="toggle-switch"></span>
											<span class="toggle-label">启用</span>
										</label>
									</div>
									<div class="range-value">{{ formatSamplingValue(editorState.draft.temperature, 2) }}
									</div>
								</div>
								<input id="editor-profile-temperature" v-model.number="editorState.draft.temperature"
									class="field-range" type="range" min="0" max="2" step="0.01"
									:disabled="!editorState.draft.temperatureEnabled" />
							</div>
							<div class="range-field" :class="{ disabled: !editorState.draft.topPEnabled }">
								<div class="range-header">
									<div>
										<div class="field-label">Top-P</div>
										<label class="toggle-field">
											<input id="editor-profile-top-p-enabled"
												v-model="editorState.draft.topPEnabled" class="toggle-input"
												type="checkbox" />
											<span class="toggle-switch"></span>
											<span class="toggle-label">启用</span>
										</label>
									</div>
									<div class="range-value">{{ formatSamplingValue(editorState.draft.topP, 1) }}</div>
								</div>
								<input id="editor-profile-top-p" v-model.number="editorState.draft.topP"
									class="field-range" type="range" min="0" max="1" step="0.01"
									:disabled="!editorState.draft.topPEnabled" />
							</div>
						</div>
						<div>
							<div class="field-label">API Key</div>
							<input id="editor-profile-api-key" v-model="editorState.draft.apiKey" class="field-input"
								type="password"
								:placeholder="editorState.mode === 'create' ? '新增配置时必填' : '留空则保留现有密钥'" />
						</div>
					</template>

					<template v-else-if="editorState.kind === 'channel'">
						<div class="field-inline">
							<div>
								<div class="field-label">频道名称</div>
								<input id="editor-channel-display-name" v-model="editorState.draft.displayName" class="field-input"
									type="text" placeholder="例如：我的飞书" />
							</div>
							<div>
								<div class="field-label">绑定模型</div>
								<select id="editor-channel-profile" v-model="editorState.draft.profileId" class="field-select">
									<option value="">请选择模型</option>
									<option v-for="option in state.profiles" :key="option.id" :value="option.id">{{ option.label }}</option>
								</select>
							</div>
						</div>
						<div v-for="field in editorState.draft.fields" :key="field.key">
							<div class="field-label">{{ field.label }}</div>
							<textarea v-if="field.kind === 'multiline'" v-model="field.value" class="field-input field-textarea"
								:placeholder="field.placeholder || ''"></textarea>
							<input v-else-if="field.kind === 'secret'" v-model="field.value" class="field-input"
								type="password"
								:placeholder="field.hasValue ? '留空则保留现有密钥' : (field.placeholder || '请填写')" />
							<input v-else v-model="field.value" class="field-input" type="text"
								:placeholder="field.placeholder || ''" />
							<div v-if="field.description" class="settings-hint channel-field-hint">{{ field.description }}</div>
						</div>
					</template>

					<template v-else>
						<div>
							<div class="field-label">显示名称</div>
							<input id="editor-workspace-name" v-model="editorState.draft.name" class="field-input"
								type="text" placeholder="例如：SelfClaw 主工作区" />
						</div>
						<div>
							<div class="field-label">工作区位置</div>
							<div class="field-picker-row">
								<div class="field-readonly">{{ editorState.draft.rootPath || '请选择文件夹' }}</div>
								<button class="ghost-btn compact-btn" type="button"
									@click="pickWorkspacePath">选择</button>
							</div>
						</div>
					</template>
				</div>

				<div class="editor-footer">
					<button class="ghost-btn" type="button" @click="closeEditor">取消</button>
					<button class="primary-btn" type="button" @click="saveEditor">保存</button>
				</div>
			</div>
		</div>
	</div>
</template>
