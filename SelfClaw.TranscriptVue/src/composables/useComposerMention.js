import { ref } from 'vue';

// 输入框快捷弹层的打开状态机：只管「哪种触发、query 是什么、锚在哪个文本节点」。
// 列表数据与键盘导航属于 MentionPopover；chip 的插入属于 ComposerEditor。
export function useComposerMention({ isKindEnabled }) {
	const open = ref(false);
	const kind = ref('');
	const query = ref('');
	// 触发符所在的文本节点与偏移：选中时用它替换 `@query` 这段文本。DOM 节点不需要响应式。
	let anchor = null;
	let start = 0;
	// Esc 关闭后，同一触发位置继续打字不得重开；光标离开触发区间（trigger 为 null）后清除。
	let dismissed = null;

	function notify(payload) {
		if (!payload) {
			dismissed = null;
			close();
			return;
		}

		if (!isKindEnabled(payload.kind)) {
			close();
			return;
		}

		if (dismissed && dismissed.anchor === payload.anchor && dismissed.start === payload.start) {
			return;
		}

		dismissed = null;
		anchor = payload.anchor;
		start = payload.start;
		kind.value = payload.kind;
		query.value = payload.query ?? '';
		open.value = true;
	}

	function close() {
		open.value = false;
		kind.value = '';
		query.value = '';
		anchor = null;
		start = 0;
	}

	function dismiss() {
		if (open.value) {
			dismissed = { anchor, start };
		}

		close();
	}

	// 当前触发区间，交给编辑器做替换；关闭后为 null。
	function trigger() {
		return anchor ? { anchor, start } : null;
	}

	return { open, kind, query, notify, close, dismiss, trigger };
}
