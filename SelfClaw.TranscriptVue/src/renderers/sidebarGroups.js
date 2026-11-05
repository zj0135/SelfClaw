// 侧栏分组：把宿主推下来的会话列表切成「项目」「对话」「归档」三块互斥的分支。
// 归档只看时间：最后一次对话距今超过 7 天即移走；再发一条消息就会自己回来，
// 所以没有「取消归档」这类反向操作。
export const ARCHIVE_AFTER_MS = 7 * 24 * 60 * 60 * 1000;

// 归档里「没有绑定工作区」的会话没有文件夹可挂，统一收进这个伪文件夹。
export const ARCHIVE_UNBOUND_FOLDER_ID = 'archive-workspace-unbound';

function timestampOf(conversation) {
	const time = Date.parse(conversation?.updatedAtUtc ?? '');
	return Number.isFinite(time) ? time : null;
}

// 时间缺失或不可解析时宁可留在原位：按错时间把会话藏进归档，用户找不到也删不掉。
export function isArchived(conversation, now) {
	const time = timestampOf(conversation);
	return time !== null && now - time > ARCHIVE_AFTER_MS;
}

function hasWorkspace(conversation) {
	return Boolean(conversation?.workspaceRootId || conversation?.workspaceRootPath || conversation?.workspaceRootName);
}

function toConversationNode(conversation, active) {
	const isSelected = conversation.id === active.selectedConversationId;
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
		isRunning: Boolean(conversation.isRunning || (isSelected && active.selectedIsBusy)),
	};
}

// 同一个工作区可能被 Git 仓库名合并成一格，所以键按优先级取，与既有侧栏口径一致。
function workspaceKeyOf(conversation) {
	return (
		conversation.gitRepositoryId ||
		conversation.workspaceRootId ||
		conversation.workspaceRootPath ||
		conversation.workspaceRootName ||
		'workspace'
	);
}

function createFolder(conversation, idPrefix) {
	return {
		id: `${idPrefix}-${workspaceKeyOf(conversation)}`,
		label: conversation.gitRepositoryName || conversation.workspaceRootName || conversation.workspaceRootPath || '工作区',
		workspaceRootId: conversation.workspaceRootId || null,
		workspaceRootName: conversation.workspaceRootName || '',
		workspaceRootPath: conversation.workspaceRootPath || '',
		gitRepositoryId: conversation.gitRepositoryId || null,
		type: 'folder',
		children: [],
	};
}

function newestTimestamp(folder) {
	let newest = null;
	for (const conversation of folder.children) {
		const time = timestampOf(conversation);
		if (time !== null && (newest === null || time > newest)) {
			newest = time;
		}
	}

	return newest ?? 0;
}

function compareByUpdatedAtDesc(left, right) {
	return (timestampOf(right) ?? 0) - (timestampOf(left) ?? 0);
}

function buildFolders(conversations, idPrefix, active) {
	const folders = new Map();
	for (const conversation of conversations) {
		const key = workspaceKeyOf(conversation);
		if (!folders.has(key)) {
			folders.set(key, createFolder(conversation, idPrefix));
		}

		folders.get(key).children.push(conversation);
	}

	// 文件夹按组内最新会话时间降序，文件夹内会话按时间降序：宿主推送顺序只在那一刻成立，
	// 归档里攒了几个月之后不能指望它还对。
	const ordered = Array.from(folders.values())
		.map((folder) => ({ ...folder, children: folder.children.slice().sort(compareByUpdatedAtDesc) }))
		.sort((left, right) => newestTimestamp(right) - newestTimestamp(left));

	return ordered.map((folder) => ({ ...folder, children: folder.children.map((conversation) => toConversationNode(conversation, active)) }));
}

function buildUnboundFolder(conversations, active) {
	const children = conversations
		.slice()
		.sort(compareByUpdatedAtDesc)
		.map((conversation) => toConversationNode(conversation, active));
	return {
		id: ARCHIVE_UNBOUND_FOLDER_ID,
		label: '未绑定工作区',
		workspaceRootId: null,
		workspaceRootName: '',
		workspaceRootPath: '',
		gitRepositoryId: null,
		archive: true,
		type: 'folder',
		children,
	};
}

export function buildSidebarGroups({
	conversations = [],
	selectedConversationId = null,
	selectedIsBusy = false,
	now = Date.now(),
} = {}) {
	const active = { selectedConversationId, selectedIsBusy };
	const archived = [];
	const withWorkspace = [];
	const withoutWorkspace = [];
	for (const conversation of conversations) {
		if (isArchived(conversation, now)) {
			archived.push(conversation);
		} else if (hasWorkspace(conversation)) {
			withWorkspace.push(conversation);
		} else {
			withoutWorkspace.push(conversation);
		}
	}

	// 归档里没有工作区的会话单独成组，排在所有工作区文件夹之后。
	const archivedWorkspaces = archived.filter(hasWorkspace);
	const archivedUnbound = archived.filter((conversation) => !hasWorkspace(conversation));
	const archivedFolders = buildFolders(archivedWorkspaces, 'archive-workspace', active).map((folder) => ({ ...folder, archive: true }));
	if (archivedUnbound.length > 0) {
		archivedFolders.push(buildUnboundFolder(archivedUnbound, active));
	}

	return {
		projects: buildFolders(withWorkspace, 'workspace', active),
		conversations: withoutWorkspace.map((conversation) => toConversationNode(conversation, active)),
		archived: archivedFolders,
	};
}
