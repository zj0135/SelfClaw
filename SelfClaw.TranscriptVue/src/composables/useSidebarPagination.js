import { ref } from 'vue';

// 一个分支（「对话」分组或某个工作区文件夹）默认只放出前 10 条会话。
export const CONVERSATION_PAGE_SIZE = 10;

// 侧栏会话分页：每个分支各自记一份已放出的条数，点一次多一页。
// 只管「渲染多少条」，与过滤（useSidebarSearch）互不影响。
export function useSidebarPagination(pageSize = CONVERSATION_PAGE_SIZE) {
	// containerId -> 已放出的条数；没有记录的分支按一页算。
	const limits = ref(new Map());

	function childrenOf(container) {
		return Array.isArray(container?.children) ? container.children : [];
	}

	function limitFor(containerId) {
		return limits.value.get(containerId) ?? pageSize;
	}

	function visibleChildren(container, showAll = false) {
		const children = childrenOf(container);
		return showAll ? children : children.slice(0, limitFor(container?.id));
	}

	function hiddenCount(container, showAll = false) {
		return showAll ? 0 : childrenOf(container).length - visibleChildren(container).length;
	}

	function expand(containerId) {
		limits.value.set(containerId, limitFor(containerId) + pageSize);
	}

	// 选中的会话排在第一批之外时，把它那一页一起放出：否则选中项在侧栏里是隐形的。
	function revealNode(container, nodeId) {
		const index = childrenOf(container).findIndex((child) => child?.id === nodeId);
		if (index < limitFor(container?.id)) {
			return;
		}

		limits.value.set(container.id, Math.ceil((index + 1) / pageSize) * pageSize);
	}

	return { visibleChildren, hiddenCount, expand, revealNode };
}
