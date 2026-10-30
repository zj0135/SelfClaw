import { computed, reactive, ref, watch } from 'vue';

// 「Direct ask_user」的答题草稿模型。
//
// 宿主给的选项里没有"其它"——它由这里追加为每题的最后一项，选中后展开自由文本。
// 前端不做业务判定（选项合法性、去重、长度上限都在宿主的 AskUserRules 里），
// 只回答一个 UI 事实：这一题算不算"已答"——有选中项，或"其它"里有非空文本。
// 提交按钮的可用条件因此是"至少一题已答"，允许部分作答（design §12.1）。
const EMPTY_DRAFT = Object.freeze({ selected: Object.freeze([]), otherActive: false, other: '' });

export function useAskUserAnswers(questionRef) {
	const prompts = computed(() => normalizePrompts(questionRef.value?.prompts));
	const drafts = reactive({});
	const activeIndex = ref(0);

	// 批次换了（收起后再来一批，requestId 随之变化）就清空草稿：
	// 上一批的选择绝不能当成这一批的答案提交出去。
	watch(() => questionRef.value?.requestId ?? '', () => {
		for (const key of Object.keys(drafts)) delete drafts[key];
		activeIndex.value = 0;
	});

	const multiple = computed(() => prompts.value.length > 1);
	const activePrompt = computed(() => prompts.value[activeIndex.value] ?? null);
	const activePromptId = computed(() => activePrompt.value?.promptId ?? '');
	const answers = computed(() => prompts.value
		.filter((prompt) => isAnswered(prompt.promptId))
		.map(toAnswer));
	const answeredCount = computed(() => answers.value.length);
	const canSubmit = computed(() => answers.value.length > 0);

	function draftOf(promptId) {
		return drafts[promptId] || EMPTY_DRAFT;
	}

	// 只在真正改答案时落库草稿；读取一律走 draftOf，模板求值不会写状态。
	function editableDraft(promptId) {
		drafts[promptId] ??= { selected: [], otherActive: false, other: '' };
		return drafts[promptId];
	}

	function isAnswered(promptId) {
		const draft = draftOf(promptId);
		return draft.selected.length > 0 || (draft.otherActive && draft.other.trim().length > 0);
	}

	function toAnswer(prompt) {
		const draft = draftOf(prompt.promptId);
		return {
			promptId: prompt.promptId,
			// 多选按点击顺序累积，但答案必须回到选项的展示顺序：C、B、D 点选得到 A、B、C、D 的子集
			// 时应答 B、C、D 而不是 C、B、D，否则模型读到的顺序与界面读到的顺序不一致。
			selected: orderByOption(prompt, draft.selected),
			other: draft.otherActive ? draft.other.trim() : '',
		};
	}

	function orderByOption(prompt, labels) {
		if (labels.length < 2) {
			return [...labels];
		}

		const rank = (label) => {
			const index = prompt.options.findIndex((option) => option.label === label);
			return index === -1 ? Number.MAX_SAFE_INTEGER : index;
		};

		return [...labels].sort((left, right) => rank(left) - rank(right));
	}

	function isSelected(promptId, label) {
		return draftOf(promptId).selected.includes(label);
	}

	function isOtherActive(promptId) {
		return draftOf(promptId).otherActive;
	}

	function otherText(promptId) {
		return draftOf(promptId).other;
	}

	function setOtherText(promptId, text) {
		editableDraft(promptId).other = text;
	}

	// 单选换选即覆盖，且与"其它"互斥；多选取并集，"其它"可与选项共存。
	function selectOption(promptId, label, multiSelect) {
		const draft = editableDraft(promptId);
		if (multiSelect) {
			draft.selected = draft.selected.includes(label)
				? draft.selected.filter((item) => item !== label)
				: [...draft.selected, label];
			return;
		}

		draft.selected = [label];
		draft.otherActive = false;
	}

	function chooseOther(promptId, multiSelect) {
		const draft = editableDraft(promptId);
		draft.otherActive = multiSelect ? !draft.otherActive : true;
		if (!multiSelect) {
			draft.selected = [];
		}
	}

	function setActive(index) {
		const count = prompts.value.length;
		if (index < 0 || index >= count) {
			return;
		}

		activeIndex.value = index;
	}

	function moveTab(delta) {
		const count = prompts.value.length;
		if (count < 2) {
			return;
		}

		activeIndex.value = (activeIndex.value + delta + count) % count;
	}

	// 数字键 1..N 命中当前 tab 的第 N 个模型选项（"其它"不占数字，见 design §6.2）。
	function selectByIndex(index) {
		const prompt = activePrompt.value;
		const option = prompt?.options[index];
		if (!option) {
			return;
		}

		selectOption(prompt.promptId, option.label, prompt.multiSelect);
	}

	return {
		prompts,
		multiple,
		activeIndex,
		activePrompt,
		activePromptId,
		answeredCount,
		canSubmit,
		isAnswered,
		isSelected,
		isOtherActive,
		otherText,
		setOtherText,
		selectOption,
		chooseOther,
		setActive,
		moveTab,
		selectByIndex,
		buildAnswers: () => [...answers.value],
	};
}

function normalizePrompts(rawPrompts) {
	if (!Array.isArray(rawPrompts)) {
		return [];
	}

	return rawPrompts
		.filter((prompt) => prompt && prompt.promptId)
		.map((prompt) => ({
			promptId: String(prompt.promptId),
			header: String(prompt.header ?? '').trim(),
			question: String(prompt.question ?? ''),
			multiSelect: Boolean(prompt.multiSelect),
			options: normalizeOptions(prompt.options),
		}));
}

function normalizeOptions(rawOptions) {
	if (!Array.isArray(rawOptions)) {
		return [];
	}

	return rawOptions
		.filter((option) => option && option.label)
		.map((option) => ({
			label: String(option.label),
			description: String(option.description ?? ''),
		}));
}
