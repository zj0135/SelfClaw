export const escapeHtml = (value) =>
	String(value ?? '')
		.replaceAll('&', '&amp;')
		.replaceAll('<', '&lt;')
		.replaceAll('>', '&gt;')
		.replaceAll('"', '&quot;')
		.replaceAll("'", '&#39;');

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
const toolSegmentId = (messageId, segment, index) => segment.segmentId || `${messageId}:tool:${index}`;

function toolStatusLabel(status) {
	switch (status) {
		case 'running':
			return '执行中';
		case 'awaitingapproval':
			return '等待确认';
		case 'failed':
			return '失败';
		case 'cancelled':
			return '已取消';
		default:
			return '成功';
	}
}

function renderPendingThinking(item, thinkingOrdinal, isLast, openThoughts) {
	return renderThinkingSegment(item, { html: '', isPending: true }, thinkingOrdinal, isLast ? 0 : -1, 1, openThoughts);
}

function renderThinkingSegment(item, segment, thinkingOrdinal, index, totalSegments, openThoughts) {
	const isPending = Boolean(segment.isPending);
	const isLast = index === totalSegments - 1;
	const label = isPending && item.isThinking ? '思考中...' : '思考';
	const id = thinkingBlockId(item.id, thinkingOrdinal);
	const isOpen = openThoughts.has(id);
	const contentHtml = segment.html || '<p class="thinking-placeholder">思考内容流式接收中，展开后会继续实时更新。</p>';
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

function renderToolSegment(item, segment, index, totalSegments, openToolSegments) {
	const label = segment.text || '工具调用';
	const status = segment.status || 'completed';
	const id = toolSegmentId(item.id, segment, index);
	const detailTitle = segment.detailTitle || 'Tool';
	const detailText = segment.detailText || '暂无可展示的执行结果。';
	const durationText = segment.durationText || '';
	const isOpen = openToolSegments.has(id);
	const classes = ['tool-segment', status];
	if (index === 0) {
		classes.push('first');
	}

	if (index === totalSegments - 1) {
		classes.push('last');
	}

	return `
      <div class="${classes.join(' ')}">
        <section class="tool-block ${escapeHtml(status)} ${isOpen ? 'open' : ''}" data-tool-segment-id="${escapeHtml(id)}">
          <button class="tool-summary" type="button" data-action="toggle-tool-segment" data-tool-segment-id="${escapeHtml(id)}" aria-expanded="${isOpen ? 'true' : 'false'}">
            <span class="tool-summary-main">
              <span class="inline-tool-dot"></span>
              <span class="inline-tool-label">${escapeHtml(label)}</span>
            </span>
            <span class="tool-summary-side">
              ${durationText ? `<span class="tool-summary-duration">${escapeHtml(durationText)}</span>` : ''}
              <span class="tool-summary-chevron">&rsaquo;</span>
            </span>
          </button>
          <div class="tool-details">
            <div class="tool-details-header">${escapeHtml(detailTitle)}</div>
            <div class="tool-details-body">
              <pre class="tool-details-pre"><code>${escapeHtml(detailText)}</code></pre>
            </div>
            <div class="tool-details-footer">
              <span class="tool-details-status ${escapeHtml(status)}">${escapeHtml(toolStatusLabel(status))}</span>
            </div>
          </div>
        </section>
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

function renderMessageContent(item, openThoughts, openToolSegments) {
	const segments = getMessageSegments(item);
	if (!segments.length) {
		return item.role === 'assistant' && item.isThinking
			? `<div class="message-flow">${renderPendingThinking(item, 0, true, openThoughts)}</div>`
			: '';
	}

	let thinkingOrdinal = 0;

	return `
      <div class="message-flow">
        ${segments
					.map((segment, index) =>
						segment.kind === 'thinking'
							? renderThinkingSegment(item, segment, thinkingOrdinal++, index, segments.length, openThoughts)
							: segment.kind === 'tool'
								? renderToolSegment(item, segment, index, segments.length, openToolSegments)
								: renderBodySegment(segment, index, segments.length)
					)
					.join('')}
      </div>
    `;
}

export function renderMessages(items, openThoughts, openToolSegments) {
	if (!items?.length) {
		return `
      <div class="empty">
        <strong>鍑嗗寮€濮?/strong>
        鎻忚堪浣犳兂鏋勫缓鐨勫唴瀹广€佷慨澶?Bug锛屾垨璁?SelfClaw 甯綘鍒嗘瀽宸ヤ綔鍖恒€?      </div>
    `;
	}

	return items
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
            ${renderMessageContent(item, openThoughts, openToolSegments)}
          </article>
        </div>
      </div>
    `;
		})
		.join('');
}

export function renderActivities(agentActivities, { isTeamMode, openActivities }) {
	if (!agentActivities?.length) {
		return isTeamMode
			? '<div class="muted-placeholder">杩欓噷浼氭樉绀烘瘡浣嶆垚鍛樼殑鏈€鏂扮姸鎬併€佸伐鍏疯皟鐢ㄤ互鍙婃寜闇€瑙﹀彂鐨勬枃妗ｅ鍑烘祦绋嬨€?/div>'
			: '<div class="muted-placeholder">杩欓噷浼氭樉绀哄伐鍏疯皟鐢ㄣ€佹墽琛岀粨鏋滃拰鍚庣画杩愯姝ラ銆?/div>';
	}

	return agentActivities
		.map((item) => {
			const isOpen = openActivities.has(item.id) || item.status === 'awaitingapproval';
			const actionButtons =
				item.status === 'awaitingapproval'
					? `
        <div class="activity-actions">
          <button class="activity-action-btn primary" type="button" data-action="approve-tool-execution" data-tool-execution-id="${escapeHtml(item.id)}">纭</button>
          <button class="activity-action-btn secondary" type="button" data-action="reject-tool-execution" data-tool-execution-id="${escapeHtml(item.id)}">鍙栨秷</button>
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
              <div class="activity-meta-label">绫诲瀷</div>
              <div class="activity-meta-value">${escapeHtml(item.kindLabel)}</div>
            </div>
            <div class="activity-meta-item">
              <div class="activity-meta-label">鐘舵€?/div>
              <div class="activity-meta-value">${escapeHtml(item.statusLabel)}</div>
            </div>
            <div class="activity-meta-item">
              <div class="activity-meta-label">鏃堕棿</div>
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

function isTeamMemberOpen(openTeamMembers, memberId) {
	return openTeamMembers.has(memberId) ? Boolean(openTeamMembers.get(memberId)) : false;
}

export function renderTeamMembers(teamMembers, openTeamMembers) {
	if (!teamMembers?.length) {
		return '<div class="muted-placeholder">杩欐浼氳瘽鐨勫洟闃熸垚鍛樹細鍦ㄤ富 Agent 瀹屾垚瑙勫垝鍚庡嚭鐜板湪杩欓噷銆?/div>';
	}

	return teamMembers
		.map((member) => {
			const isOpen = isTeamMemberOpen(openTeamMembers, member.id);
			const prompt = member.details.find((detail) => detail.label === 'Prompt')?.value || '';
			return `
      <article class="team-member-card ${escapeHtml(member.status)} ${isOpen ? 'open' : 'collapsed'}">
        <button class="team-member-toggle" type="button" data-action="toggle-team-member" data-member-id="${escapeHtml(member.id)}" aria-expanded="${isOpen ? 'true' : 'false'}">
          <div class="team-member-top">
            <div>
              <div class="team-member-name">${escapeHtml(member.title)}</div>
              <div class="team-member-role">${escapeHtml(member.summary)}</div>
            </div>
            <div class="team-member-meta">
              <span class="team-member-status ${escapeHtml(member.status)}">${escapeHtml(member.statusLabel)}</span>
								<span class="team-member-chevron">${isOpen ? '▾' : '▸'}</span>
            </div>
          </div>
          <div class="team-member-time">${escapeHtml(member.timestamp)}</div>
        </button>
        <div class="team-member-body">
				<div class="team-member-note">${escapeHtml(prompt || '该成员暂时无额外说明。')}</div>
        </div>
      </article>
    `;
		})
		.join('');
}

function isStepSectionOpen(openStepSections, sectionId, defaultOpen = true) {
	return openStepSections.has(sectionId) ? Boolean(openStepSections.get(sectionId)) : defaultOpen;
}

export function renderStepsHeader({ isTeamMode, totalCount }) {
	return `
    <div>
		<div class="steps-title">${isTeamMode ? '团队动态' : '工具'}</div>
		<div class="steps-subtitle">${isTeamMode ? '团队成员与团队事件状态' : '运行步骤与工具状态'}</div>
    </div>
    <div class="steps-count">${totalCount}</div>
  `;
}

export function renderStepsPanelContent({ isTeamMode, teamMembers, agentActivities, openStepSections, openActivities, openTeamMembers }) {
	const memberCount = teamMembers?.length || 0;
	const eventCount = agentActivities?.length || 0;
	const membersOpen = isStepSectionOpen(openStepSections, 'team-members', false);
	const eventsOpen = isStepSectionOpen(openStepSections, 'team-events', true);

	return `
    ${
			isTeamMode
				? `
          <section class="steps-section-block ${membersOpen ? 'open' : 'collapsed'}">
            <button class="steps-section-head steps-section-toggle" type="button" data-action="toggle-steps-section" data-section-id="team-members" aria-expanded="${membersOpen ? 'true' : 'false'}">
              <div class="steps-section-heading">
				<div class="steps-section-title">团队成员</div>
                <div class="steps-section-count">${memberCount}</div>
              </div>
				<span class="steps-section-chevron">${membersOpen ? '▾' : '▸'}</span>
            </button>
            <div class="steps-section-body">
              <div class="team-member-list">${renderTeamMembers(teamMembers, openTeamMembers)}</div>
            </div>
          </section>
          <section class="steps-section-block ${eventsOpen ? 'open' : 'collapsed'}">
            <button class="steps-section-head steps-section-toggle" type="button" data-action="toggle-steps-section" data-section-id="team-events" aria-expanded="${eventsOpen ? 'true' : 'false'}">
              <div class="steps-section-heading">
				<div class="steps-section-title">团队事件</div>
                <div class="steps-section-count">${eventCount}</div>
              </div>
				<span class="steps-section-chevron">${eventsOpen ? '▾' : '▸'}</span>
            </button>
            <div class="steps-section-body">
              <div class="activity-list">${renderActivities(agentActivities, { isTeamMode, openActivities })}</div>
            </div>
          </section>
        `
				: `
          <section class="steps-section-block">
            <div class="steps-section-head">
              <div class="steps-section-title">杩愯姝ラ</div>
              <div class="steps-section-count">${eventCount}</div>
            </div>
            <div class="activity-list">${renderActivities(agentActivities, { isTeamMode, openActivities })}</div>
          </section>
        `
		}
  `;
}

export function renderConversationList({
	conversations,
	conversationSearch,
	selectedConversationId,
	openConversationBranches,
	openConversationMenuId,
}) {
	if (!conversations.length) {
		return '<div class="muted-placeholder">杩樻病鏈変細璇濓紝鐐瑰嚮鈥滄柊寤哄璇濃€濆紑濮嬨€?/div>';
	}

	const itemsById = new Map(conversations.map((item) => [item.id, item]));
	const childrenByParent = new Map();
	for (const item of conversations) {
		if (!item.parentId) {
			continue;
		}

		const siblings = childrenByParent.get(item.parentId) || [];
		siblings.push(item);
		childrenByParent.set(item.parentId, siblings);
	}

	const forceExpand = Boolean(conversationSearch.trim());
	const effectiveBranches = new Map(openConversationBranches);
	let selectedItem = selectedConversationId ? itemsById.get(selectedConversationId) || null : null;
	while (selectedItem?.parentId) {
		effectiveBranches.set(selectedItem.parentId, true);
		selectedItem = itemsById.get(selectedItem.parentId) || null;
	}

	const isBranchExpanded = (conversationId) => (forceExpand ? true : effectiveBranches.get(conversationId) !== false);
	const renderConversationNode = (item) => {
		const menuOpen = openConversationMenuId === item.id;
		const depth = Number.isFinite(item.depth) ? Number(item.depth) : 0;
		const children = childrenByParent.get(item.id) || [];
		const hasChildren = children.length > 0;
		const isExpanded = !hasChildren || isBranchExpanded(item.id);
		const conversationClasses = ['conversation-card'];
		if (item.id === selectedConversationId) {
			conversationClasses.push('selected');
		}

		if (item.isAgentConversation) {
			conversationClasses.push('agent-conversation');
		}

		if (hasChildren) {
			conversationClasses.push('with-children');
		}

		const rowClasses = ['conversation-row', item.isAgentConversation ? 'branch' : 'root'];
		if (hasChildren) {
			rowClasses.push('has-children');
		}

		if (hasChildren && isExpanded) {
			rowClasses.push('expanded');
		}

		if (hasChildren && !isExpanded) {
			rowClasses.push('collapsed');
		}

		const titleText = item.title || '未命名会话';
		return `
      <div class="${rowClasses.join(' ')}" style="--conversation-depth:${depth};">
        ${
					hasChildren
						? `<button class="conversation-branch-toggle" data-action="toggle-conversation-branch" data-conversation-id="${escapeHtml(item.id)}" type="button" aria-label="${isExpanded ? '折叠子会话' : '展开子会话'}" aria-expanded="${isExpanded ? 'true' : 'false'}" title="${isExpanded ? '折叠子会话' : '展开子会话'}">${isExpanded ? '▾' : '▸'}</button>`
						: ''
				}
        <button class="${conversationClasses.join(' ')}" data-action="select-conversation" data-conversation-id="${escapeHtml(item.id)}" type="button" title="${escapeHtml(titleText)}">
          <div class="conversation-title-row">
            ${item.badge ? `<span class="conversation-badge">@${escapeHtml(item.badge)}</span>` : ''}
            <div class="conversation-title" title="${escapeHtml(titleText)}">${escapeHtml(titleText)}</div>
          </div>
          ${item.subtitle ? `<div class="conversation-subtitle">${escapeHtml(item.subtitle)}</div>` : ''}
          <div class="conversation-time">${escapeHtml(item.timestamp)}</div>
        </button>
        <div class="conversation-menu-shell">
          <button class="conversation-menu-btn" data-action="toggle-conversation-menu" data-conversation-id="${escapeHtml(item.id)}" type="button" aria-label="浼氳瘽鑿滃崟">鈰?/button>
          ${menuOpen ? `<div class="conversation-menu"><button class="conversation-menu-item danger" data-action="delete-conversation" data-conversation-id="${escapeHtml(item.id)}" type="button">鍒犻櫎浼氳瘽</button></div>` : ''}
        </div>
      </div>
      ${hasChildren && isExpanded ? children.map((child) => renderConversationNode(child)).join('') : ''}
    `;
	};

	const renderedIds = new Set();
	const fragments = [];
	for (const item of conversations) {
		if (item.parentId || renderedIds.has(item.id)) {
			continue;
		}

		const html = renderConversationNode(item);
		fragments.push(html);
		const markRendered = (node) => {
			renderedIds.add(node.id);
			for (const child of childrenByParent.get(node.id) || []) {
				markRendered(child);
			}
		};
		markRendered(item);
	}

	for (const item of conversations) {
		if (!renderedIds.has(item.id)) {
			fragments.push(renderConversationNode(item));
		}
	}

	return fragments.join('');
}


