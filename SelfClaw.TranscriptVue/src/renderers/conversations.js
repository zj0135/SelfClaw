import { escapeHtml } from './shared';

export function renderConversationList({
	conversations,
	selectedConversationId,
	openConversationMenuId,
}) {
	if (!conversations.length) {
		return '<div class="muted-placeholder">暂无会话</div>';
	}

	return conversations
		.map((item) => {
			const menuOpen = openConversationMenuId === item.id;
			const conversationClasses = ['conversation-card'];
			if (item.id === selectedConversationId) {
				conversationClasses.push('selected');
			}

			const titleText = item.title || '未命名会话';
			const hoverTitle = item.timestamp ? `${titleText} ${item.timestamp}` : titleText;
			return `
      <div class="conversation-row root">
        <button class="${conversationClasses.join(' ')}" data-action="select-conversation" data-conversation-id="${escapeHtml(item.id)}" type="button" title="${escapeHtml(hoverTitle)}">
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
    `;
		})
		.join('');
}
