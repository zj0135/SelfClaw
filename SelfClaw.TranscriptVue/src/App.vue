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
	teamMembers: [],
	agentActivities: [],
	statusText: '',
	isBusy: false,
});

const composerValue = ref('');
const conversationSearch = ref('');
const settingsOpen = ref(false);
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
const scrollFollowState = {
	transcript: true,
	transcriptPausedUntil: 0,
	stepsPausedUntil: 0,
};

let pendingScrollToBottom = false;
let pendingStatePayload = null;
let renderFrameHandle = 0;

const emptyProfile = () => ({ profileId: null, name: '', endpoint: '', model: '', apiKey: '' });
const emptyWorkspace = () => ({ workspaceRootId: null, name: '', rootPath: '' });

const post = (message) => window.chrome?.webview?.postMessage(message);

const setTheme = (theme) => {
	document.documentElement.dataset.theme = theme === 'light' ? 'light' : 'dark';
};

const selectedProfile = computed(() => state.profiles.find((item) => item.id === state.selectedProfileId) || null);
const selectedWorkspace = computed(() => state.workspaceRoots.find((item) => item.id === state.selectedWorkspaceRootId) || null);
const visibleTeamMembers = computed(() => state.teamMembers || []);
const isTeamMode = computed(() => state.selectedConversationModeId === 'team');
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
const conversationSectionTitle = computed(() => (state.conversations.some((item) => item.parentId) ? '会话树' : '最近会话'));
const totalStepCount = computed(() => (isTeamMode.value ? visibleTeamMembers.value.length + (state.agentActivities?.length || 0) : state.agentActivities?.length || 0));
const sendButtonDisabled = computed(() => ((!composerValue.value.trim() && !state.isBusy) || !state.selectedProfileId));

const profileSummaryCards = computed(() => [
	{ label: '名称', value: selectedProfile.value?.label || '未选择配置' },
	{ label: '模型', value: state.selectedProfileModel || '未选择配置' },
	{ label: 'Endpoint', value: selectedProfile.value?.description || '未设置 Endpoint' },
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

const messagesHtml = computed(() => renderMessages(state.items, openThoughts));
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

	pendingScrollToBottom = true;
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

function clearFeedback(scope) {
	if (settingsFeedback.value && (!scope || !settingsFeedback.value.scope || settingsFeedback.value.scope === scope)) {
		settingsFeedback.value = null;
	}
}

function editorScope() {
	return editorState.kind === 'profile' || editorState.kind === 'workspace' ? editorState.kind : null;
}

function openEditor(kind, mode) {
	editorState.open = true;
	editorState.kind = kind;
	editorState.mode = mode;
	editorState.draft =
		kind === 'profile'
			? mode === 'edit' && state.selectedProfileId
				? profileDraft()
				: emptyProfile()
			: mode === 'edit' && state.selectedWorkspaceRootId
				? workspaceDraft()
				: emptyWorkspace();
	editorState.feedback = null;
	clearFeedback(kind);
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

function canAutoFollowTranscript(snapshot, explicit = false) {
	if (explicit) {
		return true;
	}

	if (Date.now() < scrollFollowState.transcriptPausedUntil) {
		return false;
	}

	return scrollFollowState.transcript && Boolean(snapshot?.nearBottom);
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
		canAutoFollowTranscript(transcriptState, pendingScrollToBottom || Boolean(payload.autoScroll))
	);
	requestAnimationFrame(syncConversationMenuPlacement);
	pendingScrollToBottom = false;
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

async function handleDelegatedClick(event) {
	const target = event.target instanceof Element ? event.target : null;
	if (!target) {
		return;
	}

	const actionElement = target.closest('[data-action]');
	if (actionElement) {
		const action = actionElement.getAttribute('data-action');
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
				const id = actionElement.getAttribute('data-thinking-id');
				const block = actionElement.closest('.thinking-block');
				if (!id || !block) {
					return;
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
	if (nearBottom && Date.now() >= scrollFollowState.transcriptPausedUntil) {
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
	post({ type: 'new-conversation' });
}

function selectConversationMode(modeId) {
	openConversationMenuId.value = null;
	post({ type: 'select-conversation-mode', modeId });
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
			apiKey: editorState.draft.apiKey,
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
	<div class="transcript-vue-app" @click="handleDelegatedClick" @wheel.capture.passive="onRootWheel"
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
				<button class="sidebar-primary" type="button" @click="newConversation">+ 新建对话</button>
				<input id="conversation-search" v-model="conversationSearch" class="search-box" type="text"
					placeholder="搜索会话..." @input="onConversationSearchInput" />
				<div class="section-title">{{ conversationSectionTitle }}</div>
				<div id="conversation-list" ref="conversationListRef" class="conversation-list"
					v-html="conversationListHtml"></div>
				<button class="sidebar-footer" type="button" @click="openSettings">
					<div class="avatar">SC</div>
					<div class="sidebar-footer-copy">
						<div class="sidebar-footer-title">系统设置</div>
						<div class="sidebar-footer-subtitle">模型、工作区、主题</div>
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
						<button class="mode-chip" type="button" disabled>协作</button>
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
									placeholder="Ask for follow-up changes" @input="onComposerInput"
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
			<div v-if="settingsOpen" ref="settingsPanelRef" class="settings-panel" role="dialog" aria-modal="true"
				aria-label="系统设置" @scroll="onSettingsPanelScroll">
				<div class="settings-header">
					<div>
						<div class="settings-title">系统设置</div>
					</div>
					<button class="close-btn" type="button" aria-label="关闭" @click="closeSettings">&times;</button>
				</div>

				<div v-if="settingsFeedback" class="settings-feedback"
					:class="settingsFeedback.level === 'error' ? 'error' : 'success'">
					{{ settingsFeedback.message }}
				</div>

				<section class="settings-section">
					<div class="settings-section-header">
						<div class="settings-section-copy">
							<div class="field-label">模型配置</div>
							<div class="settings-section-title">模型配置</div>
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
							<button class="ghost-btn compact-btn danger-btn" type="button" :disabled="!selectedProfile"
								@click="deleteProfile">删除</button>
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

				<section class="settings-section">
					<div class="settings-section-header">
						<div class="settings-section-copy">
							<div class="field-label">工作区</div>
							<div class="settings-section-title">工作区</div>
						</div>
						<div class="settings-badge">{{ selectedWorkspace ? '已绑定' : '未绑定' }}</div>
					</div>
					<div class="field-group">
						<div class="field-label">当前工作区</div>
						<div class="settings-select-row">
							<select id="workspace-select" class="field-select"
								:value="state.selectedWorkspaceRootId || ''" @change="onWorkspaceChange">
								<option value="">未绑定工作区</option>
								<option v-for="option in state.workspaceRoots" :key="option.id" :value="option.id">{{
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
					<div class="field-help">设置页负责展示与切换当前工作区，新增和编辑会在中间弹窗里完成。</div>
				</section>

				<section class="settings-section">
					<div class="settings-section-header">
						<div class="settings-section-copy">
							<div class="field-label">界面主题</div>
							<div class="settings-section-title">主题</div>
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

				<div class="settings-footer">
					<button class="ghost-btn" type="button" @click="closeSettings">完成</button>
				</div>
			</div>
		</div>

		<div id="editor-overlay" class="editor-overlay" :class="{ open: editorState.open }" @click.self="closeEditor">
			<div v-if="editorState.open && editorState.draft" class="editor-panel" role="dialog" aria-modal="true"
				:aria-label="editorState.kind === 'profile' ? (editorState.mode === 'create' ? '新增模型配置' : '编辑模型配置') : editorState.mode === 'create' ? '新增工作区' : '编辑工作区'">
				<div class="editor-header">
					<div>
						<div class="editor-title">
							{{ editorState.kind === 'profile' ? (editorState.mode === 'create' ? '新增模型配置' : '编辑模型配置') :
								editorState.mode === 'create' ? '新增工作区' : '编辑工作区' }}
						</div>
						<div class="settings-hint">
							{{
								editorState.kind === 'profile'
									? editorState.mode === 'create'
										? '填写名称、Endpoint、模型和 API Key 后保存，新配置会自动加入下拉列表并切换到当前选择。'
										: '你可以更新当前模型配置；如果不需要替换密钥，API Key 留空即可。'
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
						<div>
							<div class="field-label">API Key</div>
							<input id="editor-profile-api-key" v-model="editorState.draft.apiKey" class="field-input"
								type="password"
								:placeholder="editorState.mode === 'create' ? '新增配置时必填' : '留空则保留现有密钥'" />
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




