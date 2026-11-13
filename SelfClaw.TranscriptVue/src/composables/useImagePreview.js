import { computed, onMounted, onUnmounted, ref } from 'vue';
import { hostBridge } from './hostBridge.js';

// 图片预览浮层的唯一状态源（转录缩略图、消息内图片、输入框附件、生图画廊共用）。
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
	const items = ref([]);
	const currentIndex = ref(0);

	const totalCount = computed(() => items.value.length);
	const hasPrev = computed(() => currentIndex.value > 0);
	const hasNext = computed(() => currentIndex.value < items.value.length - 1);

	function syncPreview() {
		if (items.value.length === 0) {
			preview.value = null;
			return;
		}
		const current = items.value[currentIndex.value];
		preview.value = {
			...current,
			index: currentIndex.value,
			total: items.value.length,
		};
	}

	function close() {
		if (preview.value === null) {
			return;
		}

		preview.value = null;
		items.value = [];
		currentIndex.value = 0;
		hostBridge.post({ type: 'image-preview/close' });
	}

	function open(next) {
		if (!next) return;

		const list = Array.isArray(next.images) && next.images.length > 0
			? next.images
			: [next];

		items.value = list.map((item) => ({
			src: item?.src || item?.url || '',
			alt: item?.alt || item?.prompt || '',
		})).filter((item) => Boolean(item.src));

		if (items.value.length === 0 && next.src) {
			items.value = [{ src: next.src, alt: next.alt || '' }];
		}

		let initialIndex = typeof next.index === 'number' ? next.index : 0;
		if (typeof next.index !== 'number' && next.src) {
			const found = items.value.findIndex((item) => item.src === next.src);
			if (found >= 0) initialIndex = found;
		}

		currentIndex.value = Math.max(0, Math.min(initialIndex, Math.max(0, items.value.length - 1)));
		syncPreview();
		hostBridge.post({ type: 'image-preview/open' });
	}

	function prev() {
		if (currentIndex.value > 0) {
			currentIndex.value -= 1;
			syncPreview();
		}
	}

	function next() {
		if (currentIndex.value < items.value.length - 1) {
			currentIndex.value += 1;
			syncPreview();
		}
	}

	function onKeydown(event) {
		if (preview.value === null) return;

		if (event.key === 'Escape') {
			close();
		} else if (event.key === 'ArrowLeft' || event.key === 'Left') {
			event.preventDefault();
			prev();
		} else if (event.key === 'ArrowRight' || event.key === 'Right') {
			event.preventDefault();
			next();
		}
	}

	const unsubscribe = hostBridge.on(DISMISS_PUSH, close);

	onMounted(() => document.addEventListener('keydown', onKeydown));
	onUnmounted(() => {
		document.removeEventListener('keydown', onKeydown);
		unsubscribe();
	});

	return {
		preview,
		currentIndex,
		totalCount,
		hasPrev,
		hasNext,
		open,
		close,
		prev,
		next,
	};
}
