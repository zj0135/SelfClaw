const app = document.getElementById('app');
const settingsOverlay = document.getElementById('settings-overlay');
const editorOverlay = document.getElementById('editor-overlay');

const state = {
	items: [],
	autoScroll: false,
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
	themeOptions: [],
	selectedThemeId: 'system',
	agentActivities: [],
	statusText: '',
	isBusy: false,
};

let composerValue = '';
let conversationSearch = '';
let settingsOpen = false;
let pendingScrollToBottom = false;
let settingsFeedback = null;
let settingsPanelScrollTop = 0;
let editorState = { open: false, kind: null, mode: 'create', draft: null, feedback: null };
let openConversationMenuId = null;

const openActivities = new Set();
const openThoughts = new Set();

const emptyProfile = () => ({ profileId: null, name: '', endpoint: '', model: '', apiKey: '' });
const emptyWorkspace = () => ({ workspaceRootId: null, name: '', rootPath: '' });
const closeEditorState = () => ({ open: false, kind: null, mode: 'create', draft: null, feedback: null });

const escapeHtml = (value) =>
	String(value ?? '')
		.replaceAll('&', '&amp;')
		.replaceAll('<', '&lt;')
		.replaceAll('>', '&gt;')
		.replaceAll('"', '&quot;')
		.replaceAll("'", '&#39;');

const post = (message) => window.chrome?.webview?.postMessage(message);
const setTheme = (theme) => {
	document.documentElement.dataset.theme = theme === 'light' ? 'light' : 'dark';
};

const filteredConversations = () => {
	const query = conversationSearch.trim().toLowerCase();
	return query ? state.conversations.filter((item) => item.title.toLowerCase().includes(query)) : state.conversations;
};

const renderOptions = (options, selectedId, placeholder) => {
	const placeholderOption = placeholder ? `<option value="" ${!selectedId ? 'selected' : ''}>${escapeHtml(placeholder)}</option>` : '';

	return (
		placeholderOption +
		options
			.map((option) => `<option value="${escapeHtml(option.id)}" ${option.id === selectedId ? 'selected' : ''}>${escapeHtml(option.label)}</option>`)
			.join('')
	);
};

const selectedProfile = () => state.profiles.find((item) => item.id === state.selectedProfileId) || null;
const selectedWorkspace = () => state.workspaceRoots.find((item) => item.id === state.selectedWorkspaceRootId) || null;
const selectedPermissionMode = () => state.toolPermissionModes.find((item) => item.id === state.selectedToolPermissionModeId) || null;
const isTeamMode = () => state.selectedConversationModeId === 'team';
const fallbackStatusText = () => state.statusText || (state.isBusy ? '处理中' : '就绪');
const selectedThemeLabel = () =>
	state.themeOptions.find((item) => item.id === state.selectedThemeId)?.label ||
	{
		system: '跟随系统',
		light: '浅色',
		dark: '深色',
	}[state.selectedThemeId || 'system'] ||
	'跟随系统';
const currentModelLabel = () => state.selectedProfileModel || selectedProfile()?.label || '未选择模型';
const currentWorkspaceLabel = () => selectedWorkspace()?.label || '未绑定工作区';

const profileDraft = () => {
	const profile = selectedProfile();
	return {
		profileId: profile?.id || null,
		name: profile?.label || '',
		endpoint: profile?.description || '',
		model: state.selectedProfileModel || '',
		apiKey: '',
	};
};

const workspaceDraft = () => {
	const workspace = selectedWorkspace();
	return {
		workspaceRootId: workspace?.id || null,
		name: workspace?.label || '',
		rootPath: workspace?.description || '',
	};
};

const clearFeedback = (scope) => {
	if (settingsFeedback && (!scope || !settingsFeedback.scope || settingsFeedback.scope === scope)) {
		settingsFeedback = null;
	}
};

const editorScope = () => (editorState.kind === 'profile' || editorState.kind === 'workspace' ? editorState.kind : null);

function openEditor(kind, mode) {
	editorState = {
		open: true,
		kind,
		mode,
		draft:
			kind === 'profile'
				? mode === 'edit' && state.selectedProfileId
					? profileDraft()
					: emptyProfile()
				: mode === 'edit' && state.selectedWorkspaceRootId
					? workspaceDraft()
					: emptyWorkspace(),
		feedback: null,
	};

	clearFeedback(kind);
	renderEditor();
}

function closeEditor() {
	editorState = closeEditorState();
	renderEditor();
}

function setDraft(field, value) {
	if (!editorState.open || !editorState.draft) {
		return;
	}

	editorState.draft[field] = value;
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

const summaryCard = (label, value, emptyText) => `
  <div class="selected-summary-card">
    <div class="selected-summary-label">${escapeHtml(label)}</div>
    <div class="selected-summary-value">${escapeHtml(value && String(value).trim() ? value : emptyText)}</div>
  </div>
`;

const captureScroll = (id) => {
	const element = document.getElementById(id);
	return element ? { top: element.scrollTop, nearBottom: element.scrollHeight - element.scrollTop - element.clientHeight < 40 } : null;
};

const restoreScroll = (id, snapshot, toBottom = false) => {
	const element = document.getElementById(id);
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
};

function updateComposer() {
	const button = document.getElementById('send-button');
	if (!button) {
		return;
	}

	button.disabled = (!composerValue.trim() && !state.isBusy) || !state.selectedProfileId;
	button.classList.toggle('loading', state.isBusy);
	button.classList.toggle('idle', !state.isBusy);
	button.setAttribute('aria-label', state.isBusy ? '停止生成' : '发送消息');
	button.setAttribute('title', state.isBusy ? '停止生成' : '发送消息');
	button.innerHTML = renderSendButtonInner(state.isBusy);
}

function renderSendButtonInner(isBusy) {
	return isBusy
		? `
      <span class="send-btn-spinner" aria-hidden="true">
        <span class="send-btn-spinner-ring"></span>
        <span class="send-btn-spinner-core"></span>
      </span>
    `
		: `
      <span class="send-btn-arrow" aria-hidden="true">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
          <path d="M5 12h11"></path>
          <path d="m12 5 7 7-7 7"></path>
        </svg>
      </span>
    `;
}

const getMessageSegments = (item) => {
	if (Array.isArray(item.segments) && item.segments.length > 0) {
		return item.segments;
	}

	const legacySegments = [];
	if (item.thinkingHtml) {
		legacySegments.push({ kind: 'thinking', html: item.thinkingHtml, isPending: false });
	}

	if (item.html) {
		legacySegments.push({ kind: 'content', html: item.html, isPending: false });
	}

	return legacySegments;
};

const thinkingBlockId = (messageId, ordinal) => `${messageId}:thinking:${ordinal}`;

function renderPendingThinking(item, thinkingOrdinal, isLast = false) {
	return renderThinkingSegment(item, { html: '', isPending: true }, thinkingOrdinal, isLast ? 0 : -1, 1);
}

function renderThinkingSegment(item, segment, thinkingOrdinal, index, totalSegments) {
	const isPending = Boolean(segment.isPending);
	const isLast = index === totalSegments - 1;
	const label = isPending && item.isThinking ? '思考中...' : '思考';
	const id = thinkingBlockId(item.id, thinkingOrdinal);
	const isOpen = openThoughts.has(id);
	const contentHtml =
		segment.html ||
		'<p class="thinking-placeholder">思考内容流式接收中，展开后会继续实时更新。</p>';
	return `
      <section class="thinking-block ${isOpen ? 'open' : ''} ${isPending ? 'pending' : ''} ${isLast ? 'last' : ''}" data-thinking-id="${escapeHtml(id)}">
        <button class="thinking-summary" type="button" data-action="toggle-thinking" data-thinking-id="${escapeHtml(id)}" aria-expanded="${isOpen ? 'true' : 'false'}">
          <span class="thinking-label">
            <span class="thinking-dot ${isPending && item.isThinking ? 'live' : ''}"></span>
            <span>${label}</span>
          </span>
          <span class="thinking-chevron">&rsaquo;</span>
        </button>
        <div class="thinking-content">
          <div class="thinking-markdown">${contentHtml}</div>
        </div>
      </section>
    `;
}

function renderToolSegment(segment, index, totalSegments) {
	const label = segment.text || '工具调用';
	const status = segment.status || 'completed';
	const classes = ['tool-segment', status];
	if (index === 0) {
		classes.push('first');
	}

	if (index === totalSegments - 1) {
		classes.push('last');
	}

	return `
      <div class="${classes.join(' ')}">
        <div class="inline-tool ${escapeHtml(status)}">
          <span class="inline-tool-dot"></span>
          <span class="inline-tool-label">${escapeHtml(label)}</span>
        </div>
      </div>
    `;
}

function renderBodySegment(segment, index, totalSegments) {
	if (!segment.html) {
		return '';
	}

	const classes = ['body', 'body-segment'];
	if (index === 0) {
		classes.push('first');
	}

	if (index === totalSegments - 1) {
		classes.push('last');
	}

	return `<div class="${classes.join(' ')}">${segment.html}</div>`;
}

function renderMessageContent(item) {
	const segments = getMessageSegments(item);
	if (!segments.length) {
		return item.role === 'assistant' && item.isThinking ? `<div class="message-flow">${renderPendingThinking(item, 0, true)}</div>` : '';
	}

	let thinkingOrdinal = 0;

	return `
      <div class="message-flow">
        ${segments
					.map((segment, index) =>
						segment.kind === 'thinking'
							? renderThinkingSegment(item, segment, thinkingOrdinal++, index, segments.length)
							: segment.kind === 'tool'
								? renderToolSegment(segment, index, segments.length)
								: renderBodySegment(segment, index, segments.length)
					)
					.join('')}
      </div>
    `;
}

function renderMessages() {
	if (!state.items?.length) {
		return `
      <div class="empty">
        <strong>准备开始</strong>
        描述你想构建的内容、修复 Bug，或让 SelfClaw 帮你分析工作区。
      </div>
    `;
	}

	return state.items
		.map((item) => {
			const avatar = item.avatarLabel || (item.role === 'user' ? '你' : item.role === 'assistant' ? 'SC' : 'SYS');
			const headerClass = item.title ? 'header' : 'header no-title';
			const headerTitle = item.title ? `<span>${escapeHtml(item.title)}</span>` : '';
			const headerSubtitle = item.subtitle ? `<span class="message-subtitle">${escapeHtml(item.subtitle)}</span>` : '';
			return `
      <div class="message-row ${escapeHtml(item.role)} ${escapeHtml(item.status)}">
        <div class="message-avatar">${escapeHtml(avatar)}</div>
        <div class="message-main">
          <article class="item ${escapeHtml(item.kind)} ${escapeHtml(item.role)} ${escapeHtml(item.status)}">
            <div class="${headerClass}">
              <span class="message-heading">${headerTitle}${headerSubtitle}</span>
              <span>${escapeHtml(item.timestamp)}</span>
            </div>
            ${renderMessageContent(item)}
          </article>
        </div>
      </div>
    `;
		})
		.join('');
}

function renderActivities() {
	if (!state.agentActivities?.length) {
		return isTeamMode()
			? '<div class="muted-placeholder">这里会显示团队成员状态、工具读取情况和 Markdown 导出进度。</div>'
			: '<div class="muted-placeholder">这里会显示工具调用、执行结果和后续运行步骤。</div>';
	}

	return state.agentActivities
		.map((item) => {
			const isOpen = openActivities.has(item.id) || item.status === 'awaitingapproval';
			const actionButtons =
				item.status === 'awaitingapproval'
					? `
        <div class="activity-actions">
          <button class="activity-action-btn primary" type="button" data-action="approve-tool-execution" data-tool-execution-id="${escapeHtml(item.id)}">确认</button>
          <button class="activity-action-btn secondary" type="button" data-action="reject-tool-execution" data-tool-execution-id="${escapeHtml(item.id)}">取消</button>
        </div>
      `
					: '';

			return `
      <div class="activity-card ${escapeHtml(item.status)} ${isOpen ? 'open' : ''}" data-activity-id="${escapeHtml(item.id)}">
        <div class="activity-summary" data-action="toggle-activity" data-activity-id="${escapeHtml(item.id)}">
          <div class="activity-top">
            <div class="activity-title">${escapeHtml(item.title)}</div>
            <button class="activity-toggle" type="button" tabindex="-1">${isOpen ? '收起' : '详情'}</button>
          </div>
          <div class="activity-meta">
            <div class="activity-meta-item">
              <div class="activity-meta-label">类型</div>
              <div class="activity-meta-value">${escapeHtml(item.kindLabel)}</div>
            </div>
            <div class="activity-meta-item">
              <div class="activity-meta-label">状态</div>
              <div class="activity-meta-value">${escapeHtml(item.statusLabel)}</div>
            </div>
            <div class="activity-meta-item">
              <div class="activity-meta-label">时间</div>
              <div class="activity-meta-value">${escapeHtml(item.timestamp)}</div>
            </div>
          </div>
          <div class="activity-text">${escapeHtml(item.summary)}</div>
        </div>
        <div class="activity-details">
          ${item.details
						.map(
							(detail) => `
            <div class="detail-block">
              <div class="detail-label">${escapeHtml(detail.label)}</div>
              <div class="detail-value ${detail.isCode ? 'code' : ''}">${escapeHtml(detail.value)}</div>
            </div>
          `
						)
						.join('')}
          ${actionButtons}
        </div>
      </div>
    `;
		})
		.join('');
}

function renderSettings() {
	const panel = settingsOverlay.querySelector('.settings-panel');
	if (panel) {
		settingsPanelScrollTop = panel.scrollTop;
	}

	settingsOverlay.className = settingsOpen ? 'settings-overlay open' : 'settings-overlay';
	if (!settingsOpen) {
		settingsOverlay.innerHTML = '';
		return;
	}

	const feedback = settingsFeedback
		? `<div class="settings-feedback ${settingsFeedback.level === 'error' ? 'error' : 'success'}">${escapeHtml(settingsFeedback.message)}</div>`
		: '';
	const profile = selectedProfile();
	const workspace = selectedWorkspace();

	settingsOverlay.innerHTML = `
    <div class="settings-panel" role="dialog" aria-modal="true" aria-label="系统设置">
      <div class="settings-header">
        <div>
          <div class="settings-title">系统设置</div>
          <div class="settings-hint">这里负责展示和切换当前配置，编辑与新增会在中间弹窗中完成。</div>
        </div>
        <button class="close-btn" data-action="close-settings" type="button" aria-label="关闭">&times;</button>
      </div>
      ${feedback}
      <section class="settings-section">
        <div class="settings-section-header">
          <div class="settings-section-copy">
            <div class="field-label">模型配置</div>
            <div class="settings-section-title">模型配置</div>
          </div>
          <div class="settings-badge">${profile ? '已选择' : '未选择'}</div>
        </div>
        <div class="field-group">
          <div class="field-label">当前配置</div>
          <div class="settings-select-row">
            <select id="profile-select" class="field-select">${renderOptions(state.profiles, state.selectedProfileId, '未选择配置')}</select>
            <button class="ghost-btn compact-btn" data-action="open-edit-profile" type="button" ${profile ? '' : 'disabled'}>编辑</button>
            <button class="icon-add-btn" data-action="open-create-profile" type="button" aria-label="新增模型配置">+</button>
          </div>
        </div>
        <div class="selected-summary-grid">
          ${summaryCard('名称', profile?.label, '未选择配置')}
          ${summaryCard('模型', state.selectedProfileModel, '未选择配置')}
          ${summaryCard('Endpoint', profile?.description, '未设置 Endpoint')}
        </div>
        <div class="field-help">通过下拉切换当前模型配置；点击“编辑”修改当前项，点击加号创建新配置。</div>
      </section>
      <section class="settings-section">
        <div class="settings-section-header">
          <div class="settings-section-copy">
            <div class="field-label">工作区</div>
            <div class="settings-section-title">工作区</div>
          </div>
          <div class="settings-badge">${workspace ? '已绑定' : '未绑定'}</div>
        </div>
        <div class="field-group">
          <div class="field-label">当前工作区</div>
          <div class="settings-select-row">
            <select id="workspace-select" class="field-select">${renderOptions(state.workspaceRoots, state.selectedWorkspaceRootId, '未绑定工作区')}</select>
            <button class="ghost-btn compact-btn" data-action="open-edit-workspace" type="button" ${workspace ? '' : 'disabled'}>编辑</button>
            <button class="icon-add-btn" data-action="open-create-workspace" type="button" aria-label="新增工作区">+</button>
          </div>
        </div>
        <div class="selected-summary-grid">
          ${summaryCard('名称', workspace?.label, '未绑定工作区')}
          ${summaryCard('路径', workspace?.description, '未设置工作区路径')}
        </div>
        <div class="field-help">设置页负责展示与切换当前工作区，新增和编辑会在中间弹窗里完成。</div>
      </section>
      <section class="settings-section">
        <div class="settings-section-header">
          <div class="settings-section-copy">
            <div class="field-label">界面主题</div>
            <div class="settings-section-title">主题</div>
          </div>
          <div class="settings-badge">${escapeHtml(selectedThemeLabel())}</div>
        </div>
        <div class="field-group">
          <div class="field-label">界面主题</div>
          <select id="theme-select" class="field-select">${renderOptions(state.themeOptions, state.selectedThemeId)}</select>
        </div>
        <div class="field-help">跟随系统时，聊天区和外层窗口会同步使用当前 Windows 主题。</div>
      </section>
      <div class="settings-footer">
        <button class="ghost-btn" data-action="close-settings" type="button">完成</button>
      </div>
    </div>
  `;

	const nextPanel = settingsOverlay.querySelector('.settings-panel');
	if (nextPanel) {
		nextPanel.scrollTop = settingsPanelScrollTop;
	}
}

function renderEditor() {
	editorOverlay.className = editorState.open ? 'editor-overlay open' : 'editor-overlay';
	if (!editorState.open || !editorState.draft) {
		editorOverlay.innerHTML = '';
		return;
	}

	const isProfileEditor = editorState.kind === 'profile';
	const title = isProfileEditor
		? editorState.mode === 'create'
			? '新增模型配置'
			: '编辑模型配置'
		: editorState.mode === 'create'
			? '新增工作区'
			: '编辑工作区';
	const hint = isProfileEditor
		? editorState.mode === 'create'
			? '填写名称、Endpoint、模型和 API Key 后保存，新配置会自动加入下拉列表并切换到当前选择。'
			: '你可以更新当前模型配置；如果不需要替换密钥，API Key 留空即可。'
		: editorState.mode === 'create'
			? '填写名称并选择本机目录后保存，工作区会自动加入下拉列表并设为当前选择。'
			: '在这里调整当前工作区的显示名称或重新选择目录，然后保存变更。';
	const feedback = editorState.feedback
		? `<div class="settings-feedback ${editorState.feedback.level === 'error' ? 'error' : 'success'}">${escapeHtml(editorState.feedback.message)}</div>`
		: '';

	editorOverlay.innerHTML = `
    <div class="editor-panel" role="dialog" aria-modal="true" aria-label="${escapeHtml(title)}">
      <div class="editor-header">
        <div>
          <div class="editor-title">${escapeHtml(title)}</div>
          <div class="settings-hint">${escapeHtml(hint)}</div>
        </div>
        <button class="close-btn" data-action="close-editor" type="button" aria-label="关闭">&times;</button>
      </div>
      ${feedback}
      <div class="editor-body">
        ${
					isProfileEditor
						? `
          <div class="field-inline">
            <div>
              <div class="field-label">配置名称</div>
              <input id="editor-profile-name" class="field-input" type="text" placeholder="例如：OpenAI / 本地代理" value="${escapeHtml(editorState.draft.name)}" />
            </div>
            <div>
              <div class="field-label">模型</div>
              <input id="editor-profile-model" class="field-input" type="text" placeholder="例如：gpt-4.1-mini" value="${escapeHtml(editorState.draft.model)}" />
            </div>
          </div>
          <div>
            <div class="field-label">Endpoint</div>
            <input id="editor-profile-endpoint" class="field-input" type="text" placeholder="https://api.openai.com/v1" value="${escapeHtml(editorState.draft.endpoint)}" />
          </div>
          <div>
            <div class="field-label">API Key</div>
            <input id="editor-profile-api-key" class="field-input" type="password" placeholder="${editorState.mode === 'create' ? '新增配置时必填' : '留空则保留现有密钥'}" value="${escapeHtml(editorState.draft.apiKey)}" />
          </div>
        `
						: `
          <div>
            <div class="field-label">显示名称</div>
            <input id="editor-workspace-name" class="field-input" type="text" placeholder="例如：SelfClaw 主工作区" value="${escapeHtml(editorState.draft.name)}" />
          </div>
          <div>
            <div class="field-label">工作区位置</div>
            <div class="field-picker-row">
              <div class="field-readonly">${escapeHtml(editorState.draft.rootPath || '请选择文件夹')}</div>
              <button class="ghost-btn compact-btn" data-action="pick-workspace-path" type="button">选择</button>
            </div>
          </div>
        `
				}
      </div>
      <div class="editor-footer">
        <button class="ghost-btn" data-action="close-editor" type="button">取消</button>
        <button class="primary-btn" data-action="save-editor" type="button">保存</button>
      </div>
    </div>
  `;
}

function render() {
	const transcriptState = captureScroll('transcript-scroll');
	const conversationState = captureScroll('conversation-list');
	const activityState = captureScroll('activity-list');
	setTheme(state.theme);

	const conversations = filteredConversations();
	const permissionMode = selectedPermissionMode();
	const permissionTitle = permissionMode?.description || '控制写文件和命令执行是否需要人工确认';
	const statusText = fallbackStatusText();
	const modelLabel = currentModelLabel();
	const workspaceLabel = currentWorkspaceLabel();

	app.innerHTML = `
    <div class="app-shell">
      <aside class="panel sidebar">
        <div class="brand">
          <div class="brand-badge">SC</div>
          <div>
            <div class="brand-name">SelfClaw</div>
            <div class="status-row">
              <span class="status-dot"></span>
              <span>${escapeHtml(statusText)}</span>
            </div>
          </div>
        </div>
        <button class="sidebar-primary" data-action="new-conversation" type="button">+ 新建对话</button>
        <input id="conversation-search" class="search-box" type="text" placeholder="搜索会话..." value="${escapeHtml(conversationSearch)}" />
        <div class="section-title">最近会话</div>
        <div id="conversation-list" class="conversation-list">
          ${
						conversations.length === 0
							? '<div class="muted-placeholder">还没有会话，点击“新建对话”开始。</div>'
							: conversations
									.map((item) => {
										const menuOpen = openConversationMenuId === item.id;
										return `
                <div class="conversation-row">
                  <button class="conversation-card ${item.id === state.selectedConversationId ? 'selected' : ''}" data-action="select-conversation" data-conversation-id="${escapeHtml(item.id)}" type="button">
                    <div class="conversation-title">${escapeHtml(item.title)}</div>
                    <div class="conversation-time">${escapeHtml(item.timestamp)}</div>
                  </button>
                  <div class="conversation-menu-shell">
                    <button class="conversation-menu-btn" data-action="toggle-conversation-menu" data-conversation-id="${escapeHtml(item.id)}" type="button" aria-label="会话菜单">⋯</button>
                    ${menuOpen ? `<div class="conversation-menu"><button class="conversation-menu-item danger" data-action="delete-conversation" data-conversation-id="${escapeHtml(item.id)}" type="button">删除会话</button></div>` : ''}
                  </div>
                </div>
              `;
									})
									.join('')
					}
        </div>
        <button class="sidebar-footer" data-action="toggle-settings" type="button">
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
          <div class="chip-row">
            ${state.conversationModes
							.map(
								(mode) => `
              <button class="mode-chip ${mode.id === state.selectedConversationModeId ? 'active' : ''}" type="button" data-action="select-mode" data-mode-id="${escapeHtml(mode.id)}">${escapeHtml(mode.label)}</button>
            `
							)
							.join('')}
            <button class="mode-chip" type="button" disabled>协作</button>
          </div>
          <div class="topbar-right">
            <div class="context-pill" title="${escapeHtml(modelLabel)}">
              <span class="context-label">模型</span>
              <span class="context-value">${escapeHtml(modelLabel)}</span>
            </div>
            <div class="context-pill" title="${escapeHtml(workspaceLabel)}">
              <span class="context-label">工作区</span>
              <span class="context-value">${escapeHtml(workspaceLabel)}</span>
            </div>
            <button class="icon-btn" data-action="toggle-settings" type="button" aria-label="打开系统设置">设置</button>
          </div>
        </div>
        <section class="panel transcript-panel">
          <div id="transcript-scroll" class="transcript-scroll">${renderMessages()}</div>
        </section>
        <section class="panel composer-panel">
          <div class="composer-toolbar">
            <div class="composer-meta">
              <span class="meta-pill">Enter 发送</span>
              <span class="meta-pill">Shift+Enter 换行</span>
              <span class="meta-pill">Esc 停止</span>
              <span class="meta-pill status-pill">${escapeHtml(statusText)}</span>
            </div>
            <div class="permission-control" title="${escapeHtml(permissionTitle)}">
              <select id="permission-select" class="permission-select" aria-label="工具权限模式" ${isTeamMode() ? 'disabled' : ''}>
                ${renderOptions(state.toolPermissionModes, state.selectedToolPermissionModeId)}
              </select>
            </div>
          </div>
          <div class="composer-grid">
            <textarea id="composer" class="composer-box" placeholder="描述你想构建的内容，例如修复 Bug、写脚本，或使用 /commit 提交仓库...">${escapeHtml(composerValue)}</textarea>
            <button id="send-button" class="send-btn ${state.isBusy ? 'loading' : 'idle'}" type="button" aria-label="${state.isBusy ? '停止生成' : '发送消息'}" title="${state.isBusy ? '停止生成' : '发送消息'}">${renderSendButtonInner(state.isBusy)}</button>
          </div>
        </section>
      </main>
      <aside class="panel steps-panel">
        <div class="steps-header">
          <div>
            <div class="steps-title">${isTeamMode() ? '团队动态' : '工具'}</div>
            <div class="steps-subtitle">${isTeamMode() ? '团队成员状态与导出进度' : '运行步骤与工具状态'}</div>
          </div>
          <div class="steps-count">${state.agentActivities.length}</div>
        </div>
        <div id="activity-list" class="activity-list">${renderActivities()}</div>
      </aside>
    </div>
  `;

	renderSettings();
	renderEditor();
	updateComposer();
	restoreScroll('conversation-list', conversationState);
	restoreScroll('activity-list', activityState);
	restoreScroll('transcript-scroll', transcriptState, state.autoScroll || pendingScrollToBottom || transcriptState?.nearBottom);
	requestAnimationFrame(syncConversationMenuPlacement);
	pendingScrollToBottom = false;
}

function syncConversationMenuPlacement() {
	if (!openConversationMenuId) {
		return;
	}

	const conversationList = document.getElementById('conversation-list');
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

window.chrome?.webview?.addEventListener('message', (event) => {
	const payload = event.data || {};

	if (payload.type === 'replaceState') {
		Object.assign(state, payload);
		state.conversationModes = state.conversationModes || [];
		state.selectedConversationModeId = state.selectedConversationModeId || 'programming';
		state.toolPermissionModes = state.toolPermissionModes || [];
		state.selectedToolPermissionModeId = state.selectedToolPermissionModeId || 'requireApproval';
		render();
		return;
	}

	if (payload.type === 'workspace-path-picked') {
		if (editorState.open && editorState.kind === 'workspace' && editorState.draft) {
			editorState.draft.rootPath = payload.rootPath || '';
			editorState.feedback = null;
			renderEditor();
		}
		return;
	}

	if (payload.type === 'settings-feedback') {
		const nextFeedback = payload.message ? { level: payload.level || 'success', message: payload.message, scope: payload.scope || null } : null;

		if (editorState.open && payload.scope === editorScope()) {
			if (payload.level === 'success') {
				settingsFeedback = nextFeedback;
				closeEditor();
			} else {
				settingsFeedback = null;
				editorState.feedback = nextFeedback;
				renderEditor();
			}
		} else {
			settingsFeedback = nextFeedback;
		}

		renderSettings();
	}
});

document.addEventListener('input', (event) => {
	if (event.target.id === 'composer') {
		composerValue = event.target.value;
		updateComposer();
		return;
	}

	if (event.target.id === 'conversation-search') {
		conversationSearch = event.target.value;
		render();
		return;
	}

	if (event.target.id === 'editor-profile-name') {
		setDraft('name', event.target.value);
		return;
	}

	if (event.target.id === 'editor-profile-endpoint') {
		setDraft('endpoint', event.target.value);
		return;
	}

	if (event.target.id === 'editor-profile-model') {
		setDraft('model', event.target.value);
		return;
	}

	if (event.target.id === 'editor-profile-api-key') {
		setDraft('apiKey', event.target.value);
		return;
	}

	if (event.target.id === 'editor-workspace-name') {
		setDraft('name', event.target.value);
	}
});

document.addEventListener('change', (event) => {
	if (event.target.id === 'profile-select') {
		clearFeedback('profile');
		post({ type: 'select-profile', profileId: event.target.value });
		return;
	}

	if (event.target.id === 'workspace-select') {
		clearFeedback('workspace');
		post({ type: 'select-workspace', workspaceRootId: event.target.value || null });
		return;
	}

	if (event.target.id === 'permission-select') {
		post({ type: 'select-tool-permission', permissionModeId: event.target.value });
		return;
	}

	if (event.target.id === 'conversation-mode-select') {
		post({ type: 'select-conversation-mode', modeId: event.target.value });
		return;
	}

	if (event.target.id === 'theme-select') {
		post({ type: 'select-theme', themeId: event.target.value });
	}
});

document.addEventListener('keydown', (event) => {
	if (event.key === 'Escape' && editorState.open) {
		event.preventDefault();
		closeEditor();
		return;
	}

	if (event.target.id === 'composer' && event.key === 'Enter' && !event.shiftKey) {
		event.preventDefault();
		const prompt = composerValue.trim();
		if (!prompt) {
			return;
		}

		pendingScrollToBottom = true;
		post({ type: 'send-prompt', prompt });
		composerValue = '';
		event.target.value = '';
		updateComposer();
		return;
	}

	if (event.key === 'Escape' && state.isBusy) {
		post({ type: 'stop-generation' });
	}
});

document.addEventListener('click', (event) => {
	const target = event.target instanceof Element ? event.target : null;
	if (!target) {
		return;
	}

	const actionElement = target.closest('[data-action]');
	if (actionElement) {
		const action = actionElement.getAttribute('data-action');
		switch (action) {
			case 'new-conversation':
				openConversationMenuId = null;
				post({ type: 'new-conversation' });
				break;
			case 'select-mode':
				openConversationMenuId = null;
				post({ type: 'select-conversation-mode', modeId: actionElement.getAttribute('data-mode-id') });
				break;
			case 'select-conversation':
				openConversationMenuId = null;
				post({ type: 'select-conversation', conversationId: actionElement.getAttribute('data-conversation-id') });
				break;
			case 'toggle-conversation-menu': {
				const conversationId = actionElement.getAttribute('data-conversation-id');
				openConversationMenuId = openConversationMenuId === conversationId ? null : conversationId;
				render();
				break;
			}
			case 'delete-conversation':
				openConversationMenuId = null;
				post({ type: 'delete-conversation', conversationId: actionElement.getAttribute('data-conversation-id') });
				break;
			case 'toggle-settings':
				openConversationMenuId = null;
				clearFeedback();
				settingsOpen = true;
				renderSettings();
				break;
			case 'close-settings':
				openConversationMenuId = null;
				settingsOpen = false;
				closeEditor();
				renderSettings();
				break;
			case 'open-edit-profile':
				openConversationMenuId = null;
				if (state.selectedProfileId) {
					openEditor('profile', 'edit');
				}
				break;
			case 'open-create-profile':
				openConversationMenuId = null;
				openEditor('profile', 'create');
				break;
			case 'open-edit-workspace':
				openConversationMenuId = null;
				if (state.selectedWorkspaceRootId) {
					openEditor('workspace', 'edit');
				}
				break;
			case 'open-create-workspace':
				openConversationMenuId = null;
				openEditor('workspace', 'create');
				break;
			case 'close-editor':
				openConversationMenuId = null;
				closeEditor();
				break;
			case 'pick-workspace-path':
				openConversationMenuId = null;
				post({ type: 'pick-workspace-path' });
				break;
			case 'save-editor': {
				const error = validateDraft();
				if (error) {
					editorState.feedback = { level: 'error', message: error, scope: editorScope() };
					renderEditor();
					break;
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
				} else {
					post({
						type: 'save-workspace',
						workspaceRootId: editorState.mode === 'edit' ? state.selectedWorkspaceRootId || editorState.draft.workspaceRootId : null,
						name: editorState.draft.name.trim(),
						rootPath: editorState.draft.rootPath.trim(),
					});
				}
				break;
			}
			case 'toggle-thinking': {
				const id = actionElement.getAttribute('data-thinking-id');
				const block = actionElement.closest('.thinking-block');
				if (!id || !block) {
					break;
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
				break;
			}
			case 'toggle-activity': {
				const id = actionElement.getAttribute('data-activity-id');
				const card = actionElement.closest('.activity-card');
				if (!id || !card) {
					break;
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
				break;
			}
			case 'approve-tool-execution':
				post({ type: 'approve-tool-execution', toolExecutionId: actionElement.getAttribute('data-tool-execution-id') });
				break;
			case 'reject-tool-execution':
				post({ type: 'reject-tool-execution', toolExecutionId: actionElement.getAttribute('data-tool-execution-id') });
				break;
		}

		return;
	}

	const sendButton = target.closest('#send-button');
	if (sendButton) {
		if (state.isBusy) {
			post({ type: 'stop-generation' });
			return;
		}

		const prompt = composerValue.trim();
		if (!prompt) {
			return;
		}

		pendingScrollToBottom = true;
		post({ type: 'send-prompt', prompt });
		composerValue = '';
		const composer = document.getElementById('composer');
		if (composer) {
			composer.value = '';
		}
		updateComposer();
		return;
	}

	const link = target.closest('a[href]');
	if (link) {
		event.preventDefault();
		post({ type: 'open-link', href: link.getAttribute('href') });
		return;
	}

	if (target === editorOverlay) {
		closeEditor();
		return;
	}

	if (target === settingsOverlay) {
		openConversationMenuId = null;
		settingsOpen = false;
		closeEditor();
		renderSettings();
		return;
	}

	if (openConversationMenuId) {
		openConversationMenuId = null;
		render();
	}
});

render();
