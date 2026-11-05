import { computed, nextTick, onUnmounted, ref } from 'vue';
import { useHostBridge, isSuperseded } from './hostBridge.js';
import { useTranscriptBridge } from './transcriptBridge.js';
import { useToast } from './useToast.js';
import { useConfirm } from './useConfirm.js';
import { buildSidebarGroups } from '../renderers/sidebarGroups.js';

// 「归档」是按时间推导出来的：挂机过夜、跨过第 7 天边界都得自己迁移，
// 所以不靠宿主推布尔值，而是定期把 now 往前推一步重算分组。
const ARCHIVE_TICK_MS = 15 * 60 * 1000;

export function useConversationNavigation(currentViewId, chatViewRef) {
	const { on, request } = useHostBridge();
	const transcript = useTranscriptBridge();
	const { showToast } = useToast();
	const { confirm } = useConfirm();
	const sidebarConversations = ref([]);
	const selectedConversationId = ref(null);
	const isSelectedBusy = ref(false);
	const now = ref(Date.now());
	transcript.on((payload) => {
		if (sidebarConversations.value !== payload.conversations) sidebarConversations.value = payload.conversations || [];
		selectedConversationId.value = payload.selectedConversationId || null;
		isSelectedBusy.value = Boolean(payload.isBusy);
	});
	on('operation-result', (payload) => {
		// 「shutting-down」是宿主正在关闭，不是操作失败：窗口已经在消失，再弹一条
		// 错误只会让用户以为关闭出了故障。
		if (!payload.requestId && payload.error && payload.errorCode !== 'shutting-down') showToast(payload.error);
	});
	on('conversation-activated', () => {
		currentViewId.value = 'chat';
	});

	const timer = setInterval(() => {
		now.value = Date.now();
	}, ARCHIVE_TICK_MS);
	// 系统休眠后定时器会被一起冻住：回到前台先对一次表，别等下一次 tick。
	const refreshNow = () => {
		now.value = Date.now();
	};
	if (typeof window !== 'undefined') {
		window.addEventListener('focus', refreshNow);
		document.addEventListener('visibilitychange', refreshNow);
	}
	onUnmounted(() => {
		clearInterval(timer);
		if (typeof window !== 'undefined') {
			window.removeEventListener('focus', refreshNow);
			document.removeEventListener('visibilitychange', refreshNow);
		}
	});

	const groups = computed(() =>
		buildSidebarGroups({
			conversations: sidebarConversations.value,
			selectedConversationId: selectedConversationId.value,
			selectedIsBusy: isSelectedBusy.value,
			now: now.value,
		}),
	);

	const navItems = computed(() => [
		{ id: 'new-chat', label: '新建对话', type: 'action' },
		{ id: 'search', label: '搜索', type: 'action' },
		{
			id: 'extensions',
			label: '扩展',
			type: 'action',
			children: [
				{ id: 'image-generation', label: '生图', type: 'view' },
				{ id: 'translation', label: '翻译', type: 'view' },
			],
		},
		{ id: 'automation', label: '自动化', type: 'action' },
		{
			id: 'projects',
			label: '项目',
			type: 'group',
			layout: 'folders',
			canCreate: true,
			emptyText: '暂无项目会话',
			children: groups.value.projects,
		},
		{
			id: 'conversations',
			label: '对话',
			type: 'group',
			layout: 'list',
			canCreate: true,
			emptyText: '暂无会话记录',
			children: groups.value.conversations,
		},
		{
			id: 'archived',
			label: '归档',
			type: 'group',
			layout: 'folders',
			// 归档是时间推导出来的，没有「新建归档」这种操作。
			canCreate: false,
			emptyText: '暂无归档会话',
			children: groups.value.archived,
		},
		{ id: 'settings', label: '设置', type: 'view' },
	]);

	// 独立视图（设置与扩展功能下的占位页）直接以自身 id 高亮；
	// 其余情况侧栏跟着当前选中的会话走。
	const standaloneViewIds = new Set(['settings', 'image-generation', 'translation', 'automation']);
	const sidebarActiveId = computed(() => {
		const viewId = currentViewId.value;
		return standaloneViewIds.has(viewId) ? viewId : selectedConversationId.value;
	});

	async function onSidebarAction(action) {
		try {
			const actionId = typeof action === 'string' ? action : action?.id;
			switch (actionId) {
				case 'new-chat':
					currentViewId.value = 'chat';
					await request('new-chat', { keepWorkspace: true });
					break;
				case 'add-conversations':
					currentViewId.value = 'chat';
					await request('new-chat', {});
					break;
				case 'add-projects':
					currentViewId.value = 'chat';
					nextTick(() => chatViewRef.value?.browseWorkspaceFolder());
					break;
				case 'automation':
					currentViewId.value = 'automation';
					break;
				case 'delete-conversation':
					if (action?.conversationId) {
						let removeManagedWorktree = false;
						if (action.isManagedWorktree) {
							const confirmed = await confirm({
								title: '删除会话',
								message: '确认删除该会话？工作树可以继续保留。',
								confirmText: '删除',
								danger: true,
							});
							if (!confirmed) break;

							removeManagedWorktree = await confirm({
								title: '移除工作树',
								message: '是否同时安全移除工作树？仅已合并且无未提交更改时可移除。',
								confirmText: '移除',
								danger: true,
							});
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
				case 'rename-conversation':
					if (action?.conversationId && typeof action.title === 'string' && action.title.trim()) {
						await request('rename-conversation', { conversationId: action.conversationId, title: action.title.trim() });
					}
					break;
				case 'open-in-explorer':
					if (action?.workspaceRootId) {
						await request('open-in-explorer', { workspaceRootId: action.workspaceRootId });
					}
					break;
				default:
					break;
			}
		} catch (failure) {
			if (!isSuperseded(failure)) showToast(failure?.message || String(failure));
		}
	}

	async function onSidebarSelect(id) {
		try {
			if (id === 'chat' || standaloneViewIds.has(id)) {
				currentViewId.value = id;
				return;
			}

			if (sidebarConversations.value.some((conversation) => conversation.id === id)) {
				currentViewId.value = 'chat';
				await request('select-conversation', { conversationId: id });
			}
		} catch (failure) {
			if (!isSuperseded(failure)) showToast(failure?.message || String(failure));
		}
	}
	return { navItems, sidebarActiveId, onSidebarAction, onSidebarSelect };
}
