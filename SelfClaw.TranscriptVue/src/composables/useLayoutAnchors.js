import { onMounted, onUnmounted, ref } from 'vue';

// 插件可以看到的宿主地标。名字是封闭契约：插件只依赖这五个，加第六个是应用侧的增量改动，
// 不会破坏已发布的插件（多出来的键被忽略）。
export const layoutAnchorNames = ['titlebar', 'sidebar', 'stage', 'composer', 'dock'];

// 只测量几何事实，不下发颜色或样式——与 appearance 同一个设计语言。所有矩形都是窗口 CSS 像素，
// 因为悬浮层与地标元素处在同一个视口里（见 PluginFloatingLayer.vue 的坐标不变式）。
export function useLayoutAnchors() {
	const observed = new Map();
	let observer = null;
	let mutations = null;
	let frame = 0;
	const anchors = ref(measureAnchors());

	function refresh() {
		if (frame) return;
		frame = window.requestAnimationFrame(() => {
			frame = 0;
			const next = measureAnchors();
			if (!sameAnchors(next, anchors.value)) anchors.value = next;
		});
	}

		onMounted(() => {
		if (typeof ResizeObserver === 'function') {
			observer = new ResizeObserver(refresh);
			// 每次新建 observer 都要重新绑：observed 记录的是「已被当前 observer 观察」的元素。
			observed.clear();
			bindObservedElements();
		}

		// 地标元素随主视图切换生灭（输入区与转录舞台只在对话视图里），所以插入/移除也要触发重测。
		if (typeof MutationObserver === 'function') {
			mutations = new MutationObserver(refresh);
			mutations.observe(document.body, { childList: true, subtree: true });
		}

		window.addEventListener('resize', refresh);
		refresh();
	});

	onUnmounted(() => {
		if (frame) window.cancelAnimationFrame(frame);
		frame = 0;
		observer?.disconnect();
		mutations?.disconnect();
		observed.clear();
		window.removeEventListener('resize', refresh);
	});

	function bindObservedElements() {
		const present = new Set();
		for (const name of layoutAnchorNames) {
			const element = document.querySelector(`[data-anchor="${name}"]`);
			if (!element) continue;
			present.add(element);
			if (!observed.has(element)) {
				observed.set(element, name);
				observer?.observe(element);
			}
		}

		for (const element of [...observed.keys()]) {
			if (present.has(element)) continue;
			observer?.unobserve(element);
			observed.delete(element);
		}
	}

	function measureAnchors() {
		bindObservedElements();
		const result = {};
		for (const name of layoutAnchorNames) {
			const rect = document.querySelector(`[data-anchor="${name}"]`)?.getBoundingClientRect();
			// v-show 隐藏的地标（收起的停靠栏）尺寸为 0，语义上就是「当前不存在」。
			result[name] = rect && (rect.width > 0 || rect.height > 0) ? {
				x: Math.round(rect.x),
				y: Math.round(rect.y),
				width: Math.round(rect.width),
				height: Math.round(rect.height),
			} : null;
		}

		return result;
	}

	return { anchors, refresh };
}

function sameAnchors(left, right) {
	return layoutAnchorNames.every((name) => sameRect(left[name], right[name]));
}

function sameRect(left, right) {
	if (left === right) return true;
	if (!left || !right) return false;
	return left.x === right.x && left.y === right.y && left.width === right.width && left.height === right.height;
}
