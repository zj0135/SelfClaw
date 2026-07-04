import { escapeHtml, toolStatusLabel } from './shared';

export const getMessageSegments = (item) => {
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

export const thinkingBlockId = (messageId, ordinal) => `${messageId}:thinking:${ordinal}`;
export const toolSegmentId = (messageId, segment, index) => segment.segmentId || `${messageId}:tool:${index}`;
export const toolGroupId = (messageId, startIndex, endIndex) => `${messageId}:tool-group:${startIndex}:${endIndex}`;

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

function renderPreparingIndicator(activityText) {
	const label = String(activityText || '').trim() || '准备中...';
	return `
      <div class="preparing-indicator" role="status">
        <span class="thinking-dot live"></span>
        <span class="shimmer-text">${escapeHtml(label)}</span>
      </div>
    `;
}

export function renderThinkingContent(segment) {
	const contentHtml = segment.html || '<p class="thinking-placeholder">Thinking content is streaming.</p>';
	return `<div class="thinking-markdown">${contentHtml}</div>`;
}

function renderThinkingSegment(item, segment, thinkingOrdinal, index, totalSegments, openThoughts) {
	const isPending = Boolean(segment.isPending);
	const isLive = isPending && item.isThinking;
	const hasContent = Boolean(segment.html);
	if (!hasContent && !isLive) {
		return '';
	}

	const isLast = index === totalSegments - 1;
	const label = isLive ? '思考中...' : '思考';
	const labelHtml = `
          <span class="thinking-label">
            <span class="thinking-dot ${isLive ? 'live' : ''}"></span>
            <span class="${isLive ? 'shimmer-text' : ''}">${label}</span>
          </span>`;

	if (!hasContent) {
		return `
      <section class="thinking-block pending ${isLast ? 'last' : ''}">
        <div class="thinking-summary passive">${labelHtml}</div>
      </section>
    `;
	}

	const id = thinkingBlockId(item.id, thinkingOrdinal);
	const isOpen = openThoughts.has(id);
	return `
      <section class="thinking-block ${isOpen ? 'open' : ''} ${isPending ? 'pending' : ''} ${isLast ? 'last' : ''}" data-thinking-id="${escapeHtml(id)}">
        <button class="thinking-summary" type="button" data-action="toggle-thinking" data-thinking-id="${escapeHtml(id)}" aria-expanded="${isOpen ? 'true' : 'false'}">
          ${labelHtml}
          <span class="thinking-chevron">&rsaquo;</span>
        </button>
        <div class="thinking-content">
          ${isOpen ? renderThinkingContent(segment) : ''}
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
        ${isOpen ? renderToolDetails(status, detailTitle, detailText) : ''}
      </section>
    `;
}

export function renderToolDetails(status, detailTitle, detailText) {
	return `
        <div class="tool-details">
          <div class="tool-details-header">${escapeHtml(detailTitle)}</div>
          <div class="tool-details-body">
            <pre class="tool-details-pre"><code>${escapeHtml(detailText)}</code></pre>
          </div>
          <div class="tool-details-footer">
            <span class="tool-details-status ${escapeHtml(status)}">${escapeHtml(toolStatusLabel(status))}</span>
          </div>
        </div>
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

export function renderToolGroupDetails(item, toolSegments, startIndex, openToolSegments) {
	return toolSegments
		.map((toolSegment, offset) => renderToolCard(item, toolSegment, startIndex + offset, openToolSegments, { nested: true }))
		.join('');
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
            ${isOpen ? renderToolGroupDetails(item, toolSegments, startIndex, openToolSegments) : ''}
          </div>
        </section>
      </div>
    `;
}

const skillTokenPattern = /\[\/([^\]\r\n]{1,80})\]/g;
const skillTokenSkipTags = new Set(['A', 'CODE', 'KBD', 'PRE', 'SAMP', 'SCRIPT', 'STYLE']);

function renderSkillChipHtml(name) {
	const safeName = escapeHtml(name);
	return `<span class="composer-inline-skill message-skill-chip" role="text"><span class="composer-inline-skill-icon" aria-hidden="true"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"><path d="M8 1.8 13 4.6v6.8L8 14.2l-5-2.8V4.6L8 1.8Z"></path><path d="M3.2 4.8 8 7.5l4.8-2.7"></path><path d="M8 7.5v6.2"></path></svg></span><span class="composer-inline-skill-name">${safeName}</span></span>`;
}

function renderSkillTokensInText(text) {
	let html = '';
	let lastIndex = 0;
	let match;
	skillTokenPattern.lastIndex = 0;
	while ((match = skillTokenPattern.exec(text || '')) !== null) {
		html += escapeHtml(text.slice(lastIndex, match.index));
		html += renderSkillChipHtml(match[1]);
		lastIndex = match.index + match[0].length;
	}

	return html + escapeHtml(String(text || '').slice(lastIndex));
}

function shouldSkipSkillTokenRendering(node) {
	let current = node.parentElement;
	while (current) {
		if (skillTokenSkipTags.has(current.tagName)) {
			return true;
		}

		current = current.parentElement;
	}

	return false;
}

// 用户消息的 HTML 在会话内不变，缓存 token 替换结果避免每次重渲染都做 DOM 解析。
const skillTokenHtmlCache = new Map();
const skillTokenHtmlCacheLimit = 200;

function renderSkillTokensInUserHtml(html) {
	if (!html || !html.includes('[/') || typeof document === 'undefined') {
		return html;
	}

	const cached = skillTokenHtmlCache.get(html);
	if (cached !== undefined) {
		return cached;
	}

	const template = document.createElement('template');
	template.innerHTML = html;
	const walker = document.createTreeWalker(template.content, NodeFilter.SHOW_TEXT);
	const textNodes = [];
	let node = walker.nextNode();
	while (node) {
		const text = node.nodeValue || '';
		if (text.includes('[/') && !shouldSkipSkillTokenRendering(node)) {
			skillTokenPattern.lastIndex = 0;
			if (skillTokenPattern.test(text)) {
				textNodes.push(node);
			}
		}

		node = walker.nextNode();
	}

	for (const textNode of textNodes) {
		const wrapper = document.createElement('span');
		wrapper.innerHTML = renderSkillTokensInText(textNode.nodeValue || '');
		textNode.replaceWith(...wrapper.childNodes);
	}

	const result = template.innerHTML;
	if (skillTokenHtmlCache.size >= skillTokenHtmlCacheLimit) {
		skillTokenHtmlCache.clear();
	}

	skillTokenHtmlCache.set(html, result);
	return result;
}

function renderBodySegment(item, segment, index, totalSegments) {
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

	const html = item.role === 'user' ? renderSkillTokensInUserHtml(segment.html) : segment.html;
	return `<div class="${classes.join(' ')}">${html}</div>`;
}

function formatAttachmentSize(byteLength) {
	const size = Number(byteLength || 0);
	if (size >= 1024 * 1024) {
		return `${(size / (1024 * 1024)).toFixed(size >= 10 * 1024 * 1024 ? 0 : 1)} MB`;
	}

	if (size >= 1024) {
		return `${Math.max(1, Math.round(size / 1024))} KB`;
	}

	return `${Math.max(0, size)} B`;
}

function renderMessageAttachments(item) {
	if (!Array.isArray(item.attachments) || item.attachments.length === 0) {
		return '';
	}

	const attachments = item.attachments
		.filter((attachment) => attachment && String(attachment.mediaType || '').startsWith('image/'))
		.map((attachment) => {
			const fileName = escapeHtml(attachment.fileName || 'image');
			const size = escapeHtml(formatAttachmentSize(attachment.byteLength));
			const sourceUrl = attachment.sourceUrl || attachment.dataUrl || '';
			const image = sourceUrl
				? `<img class="message-attachment-image" src="${escapeHtml(sourceUrl)}" alt="${fileName}" loading="lazy" />`
				: `<div class="message-attachment-image missing" aria-hidden="true"></div>`;
			return `
        <figure class="message-attachment">
          ${image}
          <figcaption>
            <span class="message-attachment-name">${fileName}</span>
            <span class="message-attachment-size">${size}</span>
          </figcaption>
        </figure>
      `;
		})
		.join('');

	return attachments ? `<div class="message-attachments">${attachments}</div>` : '';
}

function renderMessageContent(item, openThoughts, openToolSegments, openToolGroups, activityText) {
	const segments = getMessageSegments(item);
	const attachmentsHtml = renderMessageAttachments(item);
	if (!segments.length) {
		return item.role === 'assistant' && item.isThinking
			? `<div class="message-flow">${renderPreparingIndicator(activityText)}</div>`
			: attachmentsHtml
				? `<div class="message-flow">${attachmentsHtml}</div>`
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

		parts.push(renderBodySegment(item, segment, index, segments.length));
	}

	return `
      <div class="message-flow">
        ${attachmentsHtml}
        ${parts.join('')}
      </div>
    `;
}

export function renderMessageBody(item, openThoughts, openToolSegments, openToolGroups, activityText) {
	const headerClass = item.role === 'user' ? 'header user-time-header' : 'header assistant-time-header';
	return `
        <div class="message-main">
          <article class="item ${escapeHtml(item.kind)} ${escapeHtml(item.role)} ${escapeHtml(item.status)}">
            <div class="${headerClass}">
              <span class="message-time">${escapeHtml(item.timestamp)}</span>
            </div>
            ${renderMessageContent(item, openThoughts, openToolSegments, openToolGroups, activityText)}
          </article>
        </div>
    `;
}
