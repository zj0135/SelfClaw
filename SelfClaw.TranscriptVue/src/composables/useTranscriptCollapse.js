import { ref } from 'vue';

// transcript 折叠状态的单一载体：按稳定 id 记忆哪些块是展开/折叠的。
// 顶层持有（跨消息、跨流式重建存活），组件通过 isOpen 读、toggle 写。
// 为「全部折叠」等未来操作预留了 collapseAll。
export function useTranscriptCollapse() {
	// 思考块默认展开，用 closedThoughts 记录主动折叠的 id；
	// 工具单卡 / 工具组 / 任务计划卡默认折叠，用 open* 记录展开的 id。
	const closedThoughts = ref(new Set());
	const openToolSegments = ref(new Set());
	const openToolGroups = ref(new Set());
	const openTodoSegments = ref(new Set());

	function toggle(source, id) {
		if (!id) {
			return;
		}

		const next = new Set(source.value);
		if (next.has(id)) {
			next.delete(id);
		} else {
			next.add(id);
		}

		source.value = next;
	}

	function collapseAll() {
		closedThoughts.value = new Set();
		openToolSegments.value = new Set();
		openToolGroups.value = new Set();
		openTodoSegments.value = new Set();
	}

	return {
		isThinkingOpen: (id) => Boolean(id) && !closedThoughts.value.has(id),
		isToolOpen: (id) => openToolSegments.value.has(id),
		isToolGroupOpen: (id) => openToolGroups.value.has(id),
		isTodoOpen: (id) => openTodoSegments.value.has(id),
		toggleThinking: (id) => toggle(closedThoughts, id),
		toggleTool: (id) => toggle(openToolSegments, id),
		toggleToolGroup: (id) => toggle(openToolGroups, id),
		toggleTodo: (id) => toggle(openTodoSegments, id),
		collapseAll,
	};
}
