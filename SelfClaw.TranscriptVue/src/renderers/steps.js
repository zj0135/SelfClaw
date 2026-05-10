import { escapeHtml } from './shared';

const openActivityDetails = new WeakMap();

function getActivityDetailsHtml(item, actionButtons, detailValueClasses) {
	if (openActivityDetails.has(item)) {
		return openActivityDetails.get(item);
	}

	const detailsHtml = `
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
        `;
	openActivityDetails.set(item, detailsHtml);
	return detailsHtml;
}

function renderActivities(agentActivities, { openActivities }) {
	if (!agentActivities?.length) {
		return '<div class="muted-placeholder">这里会显示工具调用、执行结果和后续运行步骤。</div>';
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
      <div class="activity-card ${escapeHtml(item.status)} ${isOpen ? 'open' : ''}" data-activity-id="${escapeHtml(item.id)}" data-activity-has-details="${item.details?.length || actionButtons ? 'true' : 'false'}">
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
          ${isOpen ? getActivityDetailsHtml(item, actionButtons, detailValueClasses) : ''}
        </div>
      </div>
    `;
		})
		.join('');
}

export function renderStepsHeader() {
	return `
    <div>
      <div class="steps-title">工具</div>
      <div class="steps-subtitle">运行步骤与工具状态</div>
    </div>
  `;
}

export function renderStepsPanelContent({ agentActivities, openStepSections, openActivities }) {
	const eventCount = agentActivities?.length || 0;
	const eventsOpen = openStepSections.has('runtime-steps') ? Boolean(openStepSections.get('runtime-steps')) : true;

	return `
      <section class="steps-section-block ${eventsOpen ? 'open' : 'collapsed'}">
        <button class="steps-section-head steps-section-toggle" type="button" data-action="toggle-steps-section" data-section-id="runtime-steps" aria-expanded="${eventsOpen ? 'true' : 'false'}">
          <div class="steps-section-heading">
            <div class="steps-section-title">运行步骤</div>
            <div class="steps-section-count">${eventCount}</div>
          </div>
          <span class="steps-section-chevron">${eventsOpen ? '▾' : '▸'}</span>
        </button>
        <div class="steps-section-body">
          <div class="activity-list">${renderActivities(agentActivities, { openActivities })}</div>
        </div>
      </section>
  `;
}
