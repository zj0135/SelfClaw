import { ref } from 'vue';

// transcript 折叠状态的单一载体：按稳定 id 记忆哪些块是展开的。
// 顶层持有（跨消息、跨流式重建存活），组件通过 isOpen 读、toggle 写。
// 为「全部折叠」等未来操作预留了 collapseAll。
export function useTranscriptCollapse() {
	// 三类块各一个 Set：thinking / tool 单卡 / tool group。
	const openThoughts = ref(new Set());
	const openToolSegments = ref(new Set());
	const openToolGroups = ref(new Set());

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
		openThoughts.value = new Set();
		openToolSegments.value = new Set();
		openToolGroups.value = new Set();
	}

	return {
		isThinkingOpen: (id) => openThoughts.value.has(id),
		isToolOpen: (id) => openToolSegments.value.has(id),
		isToolGroupOpen: (id) => openToolGroups.value.has(id),
		toggleThinking: (id) => toggle(openThoughts, id),
		toggleTool: (id) => toggle(openToolSegments, id),
		toggleToolGroup: (id) => toggle(openToolGroups, id),
		collapseAll,
	};
}
