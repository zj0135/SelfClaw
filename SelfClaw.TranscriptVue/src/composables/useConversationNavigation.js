import { computed, nextTick, ref } from 'vue';
import { useHostBridge, isSuperseded } from './hostBridge.js';
import { useTranscriptBridge } from './transcriptBridge.js';
import { useToast } from './useToast.js';

export function useConversationNavigation(currentViewId, chatViewRef, openPlugins) {
	const { on, request } = useHostBridge();
	const transcript = useTranscriptBridge();
	const { showToast } = useToast();
	const sidebarConversations = ref([]);
	const selectedConversationId = ref(null);
	transcript.on((payload) => {
		if (sidebarConversations.value !== payload.conversations)
			sidebarConversations.value = payload.conversations || [];
		selectedConversationId.value = payload.selectedConversationId || null;
	});
	on('operation-result', (payload) => { if (!payload.requestId && payload.error) showToast(payload.error); });
	on('conversation-activated', () => { currentViewId.value = 'chat'; });

function toConversationNode(conversation) {
	return {
		id: conversation.id,
		label: conversation.title || '未命名对话',
		time: conversation.timestamp || '',
		type: 'conversation',
		isManagedWorktree: Boolean(conversation.isManagedWorktree),
		workspaceRootId: conversation.workspaceRootId || null,
		// 右键「工作目录」要用工作区根的名字与路径，不是会话标题。
		workspaceRootName: conversation.workspaceRootName || '',
		workspaceRootPath: conversation.workspaceRootPath || '',
	};
}

function hasWorkspace(conversation) {
	return Boolean(conversation?.workspaceRootId || conversation?.workspaceRootPath || conversation?.workspaceRootName);
}

function buildProjectGroups(conversations) {
	const groups = new Map();
	for (const conversation of conversations.filter(hasWorkspace)) {
		const key = conversation.gitRepositoryId || conversation.workspaceRootId || conversation.workspaceRootPath || conversation.workspaceRootName || 'workspace';
		if (!groups.has(key)) {
			groups.set(key, {
				id: `workspace-${key}`,
				label: conversation.gitRepositoryName || conversation.workspaceRootName || conversation.workspaceRootPath || '工作区',
				workspaceRootId: conversation.workspaceRootId || null,
				workspaceRootName: conversation.workspaceRootName || '',
				workspaceRootPath: conversation.workspaceRootPath || '',
				gitRepositoryId: conversation.gitRepositoryId || null,
				type: 'folder',
				children: [],
			});
		}

		groups.get(key).children.push(toConversationNode(conversation));
	}

	return Array.from(groups.values());
}

const navItems = computed(() => [
	{ id: 'new-chat', label: '新建对话', type: 'action' },
	{ id: 'search', label: '搜索', type: 'action' },
	{ id: 'plugins', label: '插件', type: 'action' },
	{ id: 'extensions', label: '扩展功能', type: 'action' },
	{ id: 'automation', label: '自动化', type: 'action' },
	{
		id: 'projects',
		label: '项目',
		type: 'group',
		children: buildProjectGroups(sidebarConversations.value),
	},
	{
		id: 'conversations',
		label: '对话',
		type: 'group',
		children: sidebarConversations.value.filter((conversation) => !hasWorkspace(conversation)).map(toConversationNode),
	},
	{ id: 'settings', label: '设置', type: 'view' },
]);

const sidebarActiveId = computed(() => (currentViewId.value === 'settings' ? 'settings' : selectedConversationId.value));


async function onSidebarAction(action) {
	try {
	const actionId = typeof action === 'string' ? action : action?.id;
	switch (actionId) {
		case 'new-chat':
		case 'add-conversations':
			currentViewId.value = 'chat';
						await request('new-chat', { });
			break;
		case 'add-projects':
			currentViewId.value = 'chat';
			nextTick(() => chatViewRef.value?.browseWorkspaceFolder());
			break;
		case 'plugins':
			openPlugins();
			break;
		case 'delete-conversation':
			if (action?.conversationId) {
				let removeManagedWorktree = false;
				if (action.isManagedWorktree) {
					if (!window.confirm('确认删除该会话？工作树可以继续保留。')) break;
					removeManagedWorktree = window.confirm('是否同时安全移除工作树？仅已合并且无未提交更改时可移除。');
				}

				await request('delete-conversation', {
					conversationId: action.conversationId,
					removeManagedWorktree,
				});
			}
			break;
		case 'clear-conversations':
			if (Array.isArray(action?.conversationIds) && action.conversationIds.length > 0) {
				await request('clear-conversations', { conversationIds: action.conversationIds });
			}
			break;
		case 'delete-workspace-root':
			if (action?.workspaceRootId) {
				await request('delete-workspace-root', { workspaceRootId: action.workspaceRootId });
			}
			break;
		default:
			break;
	}
	} catch (failure) { if (!isSuperseded(failure)) showToast(failure?.message || String(failure)); }
}

async function onSidebarSelect(id) {
	try {
	if (id === 'chat' || id === 'settings') {
		currentViewId.value = id;
		return;
	}

	if (sidebarConversations.value.some((conversation) => conversation.id === id)) {
		currentViewId.value = 'chat';
		await request('select-conversation', { conversationId: id });
	}
	} catch (failure) { if (!isSuperseded(failure)) showToast(failure?.message || String(failure)); }
}
	return { navItems, sidebarActiveId, onSidebarAction, onSidebarSelect };
}
