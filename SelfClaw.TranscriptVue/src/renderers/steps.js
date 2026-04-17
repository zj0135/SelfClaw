import { escapeHtml } from './shared';

function isTeamMemberOpen(openTeamMembers, memberId) {
	return openTeamMembers.has(memberId) ? Boolean(openTeamMembers.get(memberId)) : false;
}

function renderActivities(agentActivities, { isTeamMode, openActivities }) {
	if (!agentActivities?.length) {
		return isTeamMode
			? '<div class="muted-placeholder">这里会显示每位成员的最新状态、工具调用以及按需触发的文档导出流程。</div>'
			: '<div class="muted-placeholder">这里会显示工具调用、执行结果和后续运行步骤。</div>';
	}

	const detailValueClasses = (detail) => {
		const classes = ['detail-value'];
		if (detail.isCode) {
			classes.push('code');
		}

		const normalizedLabel = String(detail.label || '')
			.trim()
			.toLowerCase();
		if (normalizedLabel === 'arguments' || normalizedLabel === 'argument' || normalizedLabel.includes('参数')) {
			classes.push('arguments');
		}

		return classes.join(' ');
	};

	return agentActivities
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
              <div class="${detailValueClasses(detail)}">${escapeHtml(detail.value)}</div>
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

function renderTeamMembers(teamMembers, openTeamMembers) {
	if (!teamMembers?.length) {
		return '<div class="muted-placeholder">这次会话的团队成员会在主 Agent 完成规划后出现在这里。</div>';
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

export function renderStepsPanelContent({
	isTeamMode,
	teamMembers,
	agentActivities,
	openStepSections,
	openActivities,
	openTeamMembers,
}) {
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
              <div class="steps-section-title">运行步骤</div>
              <div class="steps-section-count">${eventCount}</div>
            </div>
            <div class="activity-list">${renderActivities(agentActivities, { isTeamMode, openActivities })}</div>
          </section>
        `
		}
  `;
}
