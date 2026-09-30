import { ref } from 'vue';

// 悬浮视图的指针模型。跨源 iframe 的 pointer-events: none 无法被其内部文档覆盖，反过来整层
// 可交互又会吃掉宿主的点击，所以模型只能是「默认全透明 + 插件声明交互矩形 + 命中时按需武装」：
//
//   1. 未武装：外壳的窗口 pointermove 跑命中测试，命中某帧的矩形就把它设为 pointer-events: auto
//      （武装）。同一时刻只有一个帧被武装。
//   2. 已武装：事件进入插件文档。实测（Playwright/Chromium，见 §13.1）指针位于子 iframe 上时父文档
//      收不到任何 pointermove，所以「事件目标不是被武装的帧就解除武装」只能拦住指针跑到宿主自己的
//      上层元素（对话框、启动器、标题栏）上的情形；帧内的离开由插件侧的 release 负责。
//   3. 插件侧在指针离开自己的矩形时会发 hit-regions-release；那是主要路径，不是优化。
//
// 未声明任何矩形的悬浮视图是纯视觉的：它不可能拦截任何输入，这是刻意的安全默认。
//
// 这条「未武装时靠窗口 pointermove」也是悬浮层必须只覆盖主对话区的原因：命中测试永远跑在宿主自己的
// 元素上，一旦悬浮层与右栏的插件帧重叠，那一帧就永远不会被唤醒。
//
// 解除武装必须通知插件（onReleased → hit-released 事件）：武装是外壳单方面翻转
// pointer-events，解除后插件文档就收不到任何指针事件了，它自己维护的 hover 态（hover 类名、
// 展开的 popover）不会自己复位。不依赖子文档是否收到配对的 pointerout。
export function useFloatingPointer({ isSuspended, order, frameFor, onReleased }) {
	const armedKey = ref('');
	const regions = new Map();
	let ordered = [];

	// 武装状态只有这一个写入口：任何转移都恰好产生一次解除通知。
	function setArmed(next) {
		if (armedKey.value === next) return;
		const previous = armedKey.value;
		armedKey.value = next;
		if (previous) onReleased?.(previous);
	}

	function setOrder(keys) {
		ordered = keys;
		if (armedKey.value && !ordered.includes(armedKey.value)) setArmed('');
	}

	function setRegions(key, rects) {
		if (rects.length === 0) regions.delete(key);
		else regions.set(key, rects);
	}

	function forget(key) {
		regions.delete(key);
		release(key);
	}

	function release(key) {
		if (!key || armedKey.value === key) setArmed('');
	}

	function releaseAll() {
		setArmed('');
	}

	// 矩形按插件自己的视口坐标存储（插件报的就是这个），命中测试时把指针换到同一个坐标系里。
	// 不反过来换算矩形：帧的原点会随布局变（侧栏折叠、右栏开合），而插件不会为此重新上报矩形。
	function hitTest(view, x, y) {
		const frame = frameFor?.(view);
		if (!frame) return false;
		const origin = frame.getBoundingClientRect();
		const localX = x - origin.x;
		const localY = y - origin.y;
		return Boolean(regions.get(view)?.some((rect) =>
			localX >= rect.x && localX < rect.x + rect.width && localY >= rect.y && localY < rect.y + rect.height));
	}

	function onPointerMove(event) {
		if (isSuspended?.()) {
			setArmed('');
			return;
		}

		if (armedKey.value) {
			if (event.target !== frameFor?.(armedKey.value)) setArmed('');
			return;
		}

		if (ordered.length === 0) return;
		// 取 DOM 顺序最上面的一个：后渲染者在上。
		for (let index = ordered.length - 1; index >= 0; index -= 1) {
			if (hitTest(ordered[index], event.clientX, event.clientY)) {
				setArmed(ordered[index]);
				return;
			}
		}
	}

	function attach(target = window) {
		target.addEventListener('pointermove', onPointerMove, { capture: true, passive: true });
		target.addEventListener('blur', releaseAll);
		document.addEventListener('visibilitychange', releaseAll);
		return () => {
			target.removeEventListener('pointermove', onPointerMove, { capture: true });
			target.removeEventListener('blur', releaseAll);
			document.removeEventListener('visibilitychange', releaseAll);
		};
	}

	return { armedKey, setOrder, setRegions, forget, release, releaseAll, attach };
}
