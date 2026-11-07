import { onMounted, onUnmounted, ref } from 'vue';
import { hostBridge } from './hostBridge.js';

// 图片预览浮层的唯一状态源（转录缩略图、消息内图片、输入框附件共用）。
//
// Escape 在窗口层（MainWindow.HandlePreviewKeyDown）就被接走了，网页里根本等不到那次按键，
// 宿主于是无从判断这一下该「写终端 / 中断正在进行的回合」还是「关掉浮层」。所以浮层开合必须
// 回写宿主：宿主趁它开着时把 Escape 判给浮层，只推一条 dismiss，既不写终端也不中断回合。
//
// 页内仍保留一个 Escape 监听做兜底（`npm run dev` 没有宿主时也能关掉）；两条路径都走 close()，
// 重复触发是空操作，不会给宿主多推消息。

// 宿主推回来的「这次 Escape 归你」信号：宿主已经判过归属，浮层只管关掉自己。
const DISMISS_PUSH = 'image-preview/dismiss';

export function useImagePreview() {
	const preview = ref(null);

	function close() {
		if (preview.value === null) {
			return;
		}

		preview.value = null;
		hostBridge.post({ type: 'image-preview/close' });
	}

	function open(next) {
		preview.value = next;
		hostBridge.post({ type: 'image-preview/open' });
	}

	function onKeydown(event) {
		if (event.key === 'Escape') {
			close();
		}
	}

	const unsubscribe = hostBridge.on(DISMISS_PUSH, close);

	onMounted(() => document.addEventListener('keydown', onKeydown));
	onUnmounted(() => {
		document.removeEventListener('keydown', onKeydown);
		unsubscribe();
	});

	return { preview, open, close };
}
