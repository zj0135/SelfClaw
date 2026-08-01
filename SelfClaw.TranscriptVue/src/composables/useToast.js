import { reactive } from 'vue';

// 全局单例 toast：模块级状态，任意页面调用 showToast 都驱动同一个 <AppToast />。
const toastState = reactive({ visible: false, text: '' });
let toastTimer = null;

export function useToast() {
	function showToast(text) {
		toastState.text = text;
		toastState.visible = true;
		window.clearTimeout(toastTimer);
		toastTimer = window.setTimeout(() => {
			toastState.visible = false;
		}, 1900);
	}

	return { toastState, showToast };
}
