import { escapeHtml, toolStatusLabel } from './shared';

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
const toolGroupId = (messageId, startIndex, endIndex) => `${messageId}:tool-group:${startIndex}:${endIndex}`;

const toolActionDescriptors = {
	run: { verb: 'ran', singular: 'command', plural: 'commands' },
	edit: { verb: 'edited', singular: 'file', plural: 'files' },
	read: { verb: 'read', singular: 'file', plural: 'files' },
	search: { verb: 'searched', singular: 'query', plural: 'queries' },
	list: { verb: 'listed', singular: 'directory', plural: 'directories' },
	export: { verb: 'exported', singular: 'document', plural: 'documents' },
	tool: { verb: 'used', singular: 'tool', plural: 'tools' },
};

function capitalize(value) {
	if (!value) {
		return '';
	}

	return value.charAt(0).toUpperCase() + value.slice(1);
}

function resolveToolName(segment) {
	const explicitToolName = String(segment.toolName || '')
		.trim()
		.toLowerCase();
	if (explicitToolName) {
		return explicitToolName;
	}

	const detailTitle = String(segment.detailTitle || '')
		.trim()
		.toLowerCase();
	switch (detailTitle) {
		case 'shell':
			return 'run_shell_command';
		case 'write file':
			return 'write_workspace_file';
		case 'read file':
			return 'read_workspace_file';
		case 'search results':
			return 'search_workspace_text';
		case 'workspace entries':
			return 'list_workspace_files';
		case 'export team document':
			return 'export_team_document';
		default:
			break;
	}

	const summaryText = String(segment.text || '')
		.trim()
		.toLowerCase();
	if (summaryText.startsWith('run ')) {
		return 'run_shell_command';
	}

	if (summaryText.startsWith('write ') || summaryText.startsWith('create ')) {
		return 'write_workspace_file';
	}

	if (summaryText.startsWith('read ')) {
		return 'read_workspace_file';
	}

	if (summaryText.startsWith('search ')) {
		return 'search_workspace_text';
	}

	if (summaryText.startsWith('list ')) {
		return 'list_workspace_files';
	}

	return '';
}

function resolveToolAction(segment) {
	const toolName = resolveToolName(segment);
	switch (toolName) {
		case 'run_shell_command':
			return 'run';
		case 'write_workspace_file':
			return 'edit';
		case 'read_workspace_file':
			return 'read';
		case 'search_workspace_text':
			return 'search';
		case 'list_workspace_files':
			return 'list';
		case 'export_team_document':
			return 'export';
		default:
			return 'tool';
	}
}

function buildToolActionSummary(segments) {
	const groups = new Map();
	for (const segment of segments) {
		const action = resolveToolAction(segment);
		const existing = groups.get(action) || { action, count: 0 };
		existing.count += 1;
		groups.set(action, existing);
	}

	const fragments = Array.from(groups.values()).map((group, index) => {
		const descriptor = toolActionDescriptors[group.action] || toolActionDescriptors.tool;
		const verb = index === 0 ? capitalize(descriptor.verb) : descriptor.verb;
		const noun = group.count === 1 ? descriptor.singular : descriptor.plural;
		return `${verb} ${group.count} ${noun}`;
	});

	return fragments.join(', ');
}

function resolveToolGroupStatus(segments) {
	if (segments.some((segment) => segment.status === 'awaitingapproval')) {
		return 'awaitingapproval';
	}

	if (segments.some((segment) => segment.status === 'running')) {
		return 'running';
	}

	if (segments.some((segment) => segment.status === 'failed')) {
		return 'failed';
	}

	if (segments.some((segment) => segment.status === 'cancelled')) {
		return 'cancelled';
	}

	return 'completed';
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

function renderToolCard(item, segment, index, openToolSegments, options = {}) {
	const summaryLabel = options.summaryLabel || segment.text || '工具调用';
	const status = segment.status || 'completed';
	const id = toolSegmentId(item.id, segment, index);
	const detailTitle = segment.detailTitle || 'Tool';
	const detailText = segment.detailText || '暂无可展示的执行结果。';
	const durationText = segment.durationText || '';
	const isOpen = openToolSegments.has(id);

	return `
      <section class="tool-block ${escapeHtml(status)} ${isOpen ? 'open' : ''} ${options.nested ? 'nested' : ''}" data-tool-segment-id="${escapeHtml(id)}">
        <button class="tool-summary ${options.nested ? 'nested' : ''}" type="button" data-action="toggle-tool-segment" data-tool-segment-id="${escapeHtml(id)}" aria-expanded="${isOpen ? 'true' : 'false'}">
          <span class="tool-summary-main">
            <span class="inline-tool-label">${escapeHtml(summaryLabel)}</span>
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
    `;
}

function renderToolSegment(item, segment, index, totalSegments, openToolSegments) {
	const status = segment.status || 'completed';
	const summaryLabel = buildToolActionSummary([segment]);
	const classes = ['tool-segment', status];
	if (index === 0) {
		classes.push('first');
	}

	if (index === totalSegments - 1) {
		classes.push('last');
	}

	return `
      <div class="${classes.join(' ')}">
        ${renderToolCard(item, segment, index, openToolSegments, { summaryLabel })}
      </div>
    `;
}

function renderToolGroup(item, toolSegments, startIndex, endIndex, totalSegments, openToolSegments, openToolGroups) {
	const status = resolveToolGroupStatus(toolSegments);
	const summaryLabel = buildToolActionSummary(toolSegments);
	const id = toolGroupId(item.id, startIndex, endIndex);
	const isOpen = openToolGroups.has(id);
	const classes = ['tool-segment', 'tool-group-segment', status];
	if (startIndex === 0) {
		classes.push('first');
	}

	if (endIndex === totalSegments - 1) {
		classes.push('last');
	}

	return `
      <div class="${classes.join(' ')}">
        <section class="tool-group-block ${escapeHtml(status)} ${isOpen ? 'open' : ''}" data-tool-group-id="${escapeHtml(id)}">
          <button class="tool-group-summary" type="button" data-action="toggle-tool-group" data-tool-group-id="${escapeHtml(id)}" aria-expanded="${isOpen ? 'true' : 'false'}">
            <span class="tool-group-summary-main">
              <span class="tool-group-label">${escapeHtml(summaryLabel)}</span>
            </span>
            <span class="tool-group-summary-side">
              <span class="tool-group-chevron">&rsaquo;</span>
            </span>
          </button>
          <div class="tool-group-details">
            ${toolSegments
							.map((toolSegment, offset) =>
								renderToolCard(item, toolSegment, startIndex + offset, openToolSegments, { nested: true })
							)
							.join('')}
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

function renderMessageContent(item, openThoughts, openToolSegments, openToolGroups) {
	const segments = getMessageSegments(item);
	if (!segments.length) {
		return item.role === 'assistant' && item.isThinking
			? `<div class="message-flow">${renderPendingThinking(item, 0, true, openThoughts)}</div>`
			: '';
	}

	let thinkingOrdinal = 0;
	const parts = [];

	for (let index = 0; index < segments.length; index += 1) {
		const segment = segments[index];
		if (segment.kind === 'thinking') {
			parts.push(renderThinkingSegment(item, segment, thinkingOrdinal++, index, segments.length, openThoughts));
			continue;
		}

		if (segment.kind === 'tool') {
			let endIndex = index;
			while (endIndex + 1 < segments.length && segments[endIndex + 1].kind === 'tool') {
				endIndex += 1;
			}

			const toolSegments = segments.slice(index, endIndex + 1);
			if (toolSegments.length > 1) {
				parts.push(renderToolGroup(item, toolSegments, index, endIndex, segments.length, openToolSegments, openToolGroups));
			} else {
				parts.push(renderToolSegment(item, segment, index, segments.length, openToolSegments));
			}

			index = endIndex;
			continue;
		}

		parts.push(renderBodySegment(segment, index, segments.length));
	}

	return `
      <div class="message-flow">
        ${parts.join('')}
      </div>
    `;
}

export function renderMessages(items, openThoughts, openToolSegments, openToolGroups) {
	if (!items?.length) {
		return `
      <div class="empty">
        <strong>准备开始</strong>
        描述你想构建的内容、修复 Bug，或让 SelfClaw 帮你分析工作区。
      </div>
    `;
	}

	return items
		.map((item) => {
			const headerClass = item.title ? 'header' : 'header no-title';
			const headerTitle = item.title ? `<span>${escapeHtml(item.title)}</span>` : '';
			const headerSubtitle = item.subtitle ? `<span class="message-subtitle">${escapeHtml(item.subtitle)}</span>` : '';
			return `
      <div class="message-row ${escapeHtml(item.role)} ${escapeHtml(item.status)}">
        <div class="message-main">
          <article class="item ${escapeHtml(item.kind)} ${escapeHtml(item.role)} ${escapeHtml(item.status)}">
            <div class="${headerClass}">
              <span class="message-heading">${headerTitle}${headerSubtitle}</span>
              <span>${escapeHtml(item.timestamp)}</span>
            </div>
            ${renderMessageContent(item, openThoughts, openToolSegments, openToolGroups)}
          </article>
        </div>
      </div>
    `;
		})
		.join('');
}
