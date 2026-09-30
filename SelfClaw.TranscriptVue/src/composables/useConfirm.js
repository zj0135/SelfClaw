import { reactive } from 'vue';

// 全局单例确认框：与 useToast 同构，模块级状态驱动同一个 <AppConfirmDialog />。
// 区别在于它要等一个用户决定，因此返回 Promise<boolean>：
// 确认为 true；取消 / Esc / 点击遮罩为 false。
//
// 同一时间只允许一个确认框未决：已有一个时再调用，旧的按取消收尾再开新的，
// 这样调用方的 await 不会永远挂着。
const confirmState = reactive({
	open: false,
	title: '',
	message: '',
	confirmText: '',
	cancelText: '',
	danger: false,
});

const DEFAULTS = {
	title: '确认操作',
	message: '',
	confirmText: '确认',
	cancelText: '取消',
	danger: false,
};

let settle = null;

function finish(approved) {
	const resolve = settle;
	settle = null;
	confirmState.open = false;
	resolve?.(approved);
}

// 兼容 window.confirm 的字符串调用形式，便于后续逐处替换。
function normalize(options) {
	if (typeof options === 'string') {
		return { ...DEFAULTS, message: options };
	}

	const next = { ...DEFAULTS, ...(options ?? {}) };
	return {
		title: String(next.title ?? DEFAULTS.title),
		message: String(next.message ?? ''),
		confirmText: String(next.confirmText ?? DEFAULTS.confirmText),
		cancelText: String(next.cancelText ?? DEFAULTS.cancelText),
		danger: next.danger === true,
	};
}

export function useConfirm() {
	/**
	 * 打开确认框并等待决定。options 可以是 window.confirm 风格的字符串，
	 * 也可以是 { title, message, confirmText, cancelText, danger }。
	 */
	function confirm(options) {
		finish(false);
		Object.assign(confirmState, normalize(options), { open: true });
		return new Promise((resolve) => {
			settle = resolve;
		});
	}

	/** 由 <AppConfirmDialog /> 调用；关闭动画期间重复触发会被忽略。 */
	function resolveConfirm(approved) {
		if (!confirmState.open) {
			return;
		}

		finish(approved === true);
	}

	return { confirmState, confirm, resolveConfirm };
}
