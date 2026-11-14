import { ref } from 'vue';

// transcript 折叠状态的单一载体：按稳定 id 记住用户点过的块，未被点过的块跟随
// 调用方给出的自动默认值（进行中展开 / 完成后折叠，其余默认折叠）。顶层持有，跨流式重建存活。
export function useTranscriptCollapse() {
	const overrides = ref(new Map());

	// automatic 是「未手动干预时」的开合值：思考 / 工具组进行中 true、完成后 false；
	// 单个工具、组内成员、任务计划卡恒 false。
	function resolve(id, automatic) {
		if (!id) {
			return false;
		}

		return overrides.value.has(id) ? overrides.value.get(id) : Boolean(automatic);
	}

	function toggle(id, automatic) {
		if (!id) {
			return;
		}

		const next = new Map(overrides.value);
		next.set(id, !resolve(id, automatic));
		overrides.value = next;
	}

	return {
		isThinkingOpen: (id, automatic) => resolve(id, automatic),
		isToolOpen: (id, automatic) => resolve(id, automatic),
		isToolGroupOpen: (id, automatic) => resolve(id, automatic),
		isTodoOpen: (id, automatic) => resolve(id, automatic),
		toggleThinking: (id, automatic) => toggle(id, automatic),
		toggleTool: (id, automatic) => toggle(id, automatic),
		toggleToolGroup: (id, automatic) => toggle(id, automatic),
		toggleTodo: (id, automatic) => toggle(id, automatic),
	};
}
