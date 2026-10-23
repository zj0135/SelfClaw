import { onMounted, onUnmounted, watch } from 'vue';
import { patchStreamingHtml } from '../renderers/streamingPatch.js';

/**
 * 专为转录正文与思考块打造的平滑流式渲染调度 composable。
 * 结合 requestAnimationFrame 增量补丁已有 DOM 结构，消除卡顿与闪烁，并触发物理淡入动效。
 *
 * @param {import('vue').Ref<HTMLElement | null>} containerRef 容器元素引用
 * @param {import('vue').Ref<string>} sourceHtml 响应式 HTML 字符串
 * @param {import('vue').Ref<boolean>} isStreaming 是否处于流式传输中
 */
export function useStreamingContent(containerRef, sourceHtml, isStreaming) {
	let animFrame = null;

	function cancelScheduled() {
		if (animFrame !== null) {
			window.cancelAnimationFrame(animFrame);
			animFrame = null;
		}
	}

	function applyPatch(streamingMode) {
		const el = containerRef.value;
		if (!el) {
			return;
		}
		patchStreamingHtml(el, sourceHtml.value, { isStreaming: streamingMode });
	}

	function schedulePatch() {
		if (animFrame !== null) {
			return;
		}

		animFrame = window.requestAnimationFrame(() => {
			animFrame = null;
			applyPatch(Boolean(isStreaming.value));
		});
	}

	// 监听 HTML 内容与流式状态变化
	watch(
		[sourceHtml, isStreaming],
		([nextHtml, streaming], [prevHtml, prevStreaming] = []) => {
			if (!streaming) {
				cancelScheduled();
				applyPatch(false);
				return;
			}

			// 流式中：如果 HTML 没有变化且流式状态也没变，无需重新调度
			if (nextHtml === prevHtml && streaming === prevStreaming) {
				return;
			}

			schedulePatch();
		},
		{ flush: 'post' }
	);

	// 监听容器元素挂载（例如 ThinkingBlock 展开从 v-if: false 变为 true）
	watch(
		containerRef,
		(newEl) => {
			if (newEl) {
				cancelScheduled();
				applyPatch(Boolean(isStreaming.value));
			}
		},
		{ flush: 'post' }
	);

	onMounted(() => {
		applyPatch(Boolean(isStreaming.value));
	});

	onUnmounted(cancelScheduled);
}
