import { escapeHtml } from './shared';

export function renderConversationList({
	conversations,
	selectedConversationId,
	openConversationBranches,
	openConversationMenuId,
}) {
	if (!conversations.length) {
		return '<div class="muted-placeholder">还没有会话，点击"新建对话"开始。</div>';
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

	const effectiveBranches = new Map(openConversationBranches);
	let selectedItem = selectedConversationId ? itemsById.get(selectedConversationId) || null : null;
	while (selectedItem?.parentId) {
		if (!effectiveBranches.has(selectedItem.parentId)) {
			effectiveBranches.set(selectedItem.parentId, true);
		}

		selectedItem = itemsById.get(selectedItem.parentId) || null;
	}

	const isBranchExpanded = (conversationId) => effectiveBranches.get(conversationId) !== false;
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
			conversationClasses.push('collapsible');
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
		const hoverTitle = item.timestamp ? `${titleText} ${item.timestamp}` : titleText;
		return `
      <div class="${rowClasses.join(' ')}" style="--conversation-depth:${depth};">
        <button class="${conversationClasses.join(' ')}" data-action="select-conversation" data-conversation-id="${escapeHtml(item.id)}" data-has-children="${hasChildren ? 'true' : 'false'}" type="button" title="${escapeHtml(hoverTitle)}"${hasChildren ? ` aria-expanded="${isExpanded ? 'true' : 'false'}"` : ''}>
          <div class="conversation-title-row">
            ${item.badge ? `<span class="conversation-badge">@${escapeHtml(item.badge)}</span>` : ''}
            <div class="conversation-title" title="${escapeHtml(hoverTitle)}">${escapeHtml(titleText)}</div>
          </div>
          ${item.subtitle ? `<div class="conversation-subtitle">${escapeHtml(item.subtitle)}</div>` : ''}
        </button>
        <div class="conversation-menu-shell">
          <button class="conversation-menu-btn" data-action="toggle-conversation-menu" data-conversation-id="${escapeHtml(item.id)}" type="button" aria-label="会话菜单">⋯</button>
          ${menuOpen ? `<div class="conversation-menu"><button class="conversation-menu-item danger" data-action="delete-conversation" data-conversation-id="${escapeHtml(item.id)}" type="button">删除会话</button></div>` : ''}
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
