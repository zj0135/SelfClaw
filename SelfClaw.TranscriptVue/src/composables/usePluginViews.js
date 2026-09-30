import { nextTick, onMounted, onUnmounted, ref, watch, watchEffect } from 'vue';
import { useHostBridge } from './hostBridge.js';
import { useAppearance } from './useAppearance.js';
import { useFloatingPointer } from './useFloatingPointer.js';
import { useLayoutAnchors } from './useLayoutAnchors.js';
import { usePluginFrames } from './usePluginFrames.js';
import { usePluginViewHost } from './usePluginViewHost.js';

// 插件视图（右侧停靠面板 + 窗口悬浮层）的外壳入口。App.vue 只依赖这一个对象，内部按关注点分成：
//   usePluginViewHost   —— 与宿主谈判：可用视图、打开/关闭、持久化、evict
//   usePluginFrames     —— 帧注册表、身份判定、握手与四类推送、插件通知
//   useLayoutAnchors    —— 宿主地标矩形（几何事实）
//   useFloatingPointer  —— 悬浮层的交互矩形与武装状态
//
// 面板与悬浮视图共用同一条帧管道，两种摆放方式只在组件层分开。
//
// 面板是跨源 iframe，CSS 变量继承不进去。外壳只告诉它「现在是什么外观」，不下发一整套解析后的
// 实色：那等于把外壳的 token 表变成插件 API，往后每次增删颜色都成了破坏性变更。
function readAppearanceFacts(appearance) {
	return {
		theme: appearance.resolvedTheme.value,
		mode: appearance.state.mode,
		uiFontFamily: appearance.state.uiFontFamily,
		uiFontScale: appearance.state.uiFontScale,
		codeFontFamily: appearance.state.codeFontFamily,
		codeFontScale: appearance.state.codeFontScale,
	};
}

export function usePluginViews({ isSuspended } = {}) {
	const { request, on } = useHostBridge();
	const appearance = useAppearance();
	const anchors = useLayoutAnchors();
	const onInsertPrompt = ref(null);
	let detachPointer = null;

	const host = usePluginViewHost({
		request,
		onClosed: (view) => pointer.forget(view.key),
		onEvicted: (view) => pointer.forget(view.key),
	});

	const frames = usePluginFrames({
		views: () => host.openViews.value,
		request,
		appearanceFacts: () => readAppearanceFacts(appearance),
		anchors: () => anchors.anchors.value,
		onNotice: (view, type, payload) => {
			if (view.slot !== 'floating') return;
			if (type === 'hit-regions') pointer.setRegions(view.key, payload.rects);
			else pointer.release(view.key);
		},
		localOps: {
			// 输入框由外壳自己持有，不需要往返宿主。
			'composer.insert': {
				permission: 'host.composer.write',
				run: (view, args) => onInsertPrompt.value?.(String(args.text ?? '')),
			},
			// 视图关掉自己：状态在外壳，不新增宿主消息类型，也不需要权限。
			'view.close': { run: (view) => host.close(view.key) },
		},
	});

	const pointer = useFloatingPointer({
		isSuspended,
		order: () => host.floatingViews.value.map((view) => view.key),
		frameFor: (key) => frames.frameFor(key),
		// 解除武装时告知插件（它自己的 hover 态不会自己复位）。插件主动要求解除时也发一次：
		// 规则简单——每次解除恰好一条通知，处理器写成幂等即可。
		onReleased: (key) => {
			const view = host.openViews.value.find((candidate) => candidate.key === key);
			if (view) frames.release(view);
		},
	});

	// 悬浮视图按 viewKey 稳定排序渲染，命中测试取最上面的一个（DOM 顺序 = 后渲染者在上）。
	watchEffect(() => pointer.setOrder(host.floatingViews.value.map((view) => view.key)));

	// 上下文只有宿主一个生产者：外壳不再从 transcript 负载里自己拼一份——那样推的字段集与
	// getContext() 拉到的并不一致，工作区根还可能与 workspace.* 实际解析的根不同。
	on('plugin-host/context', (payload) => frames.publishContext(payload.context));
	on('plugin-host/evict', (payload) => host.evict(payload.pluginId));
	on('extensions/state-changed', () => host.synchronize());

	// revision 覆盖了外观的每一项改动，包括「跟随系统」时系统自己翻明暗。
	watch(appearance.revision, () => frames.publishAppearance(readAppearanceFacts(appearance)));
	watch(anchors.anchors, (next) => frames.publishAnchors(next));

	onMounted(async () => {
		detachPointer = pointer.attach();
		const persisted = await host.load();
		await host.restore(persisted);
		// 首次测量发生在挂载时，恢复出来的地标可能出现得更晚；补一次让刚打开的悬浮视图立刻有几何。
		await nextTick();
		anchors.refresh();
	});

	onUnmounted(() => {
		detachPointer?.();
		host.dispose();
	});

	return {
		available: host.available,
		openViews: host.openViews,
		dockedViews: host.dockedViews,
		floatingViews: host.floatingViews,
		activeKey: host.activeKey,
		armedKey: pointer.armedKey,
		error: host.error,
		isOpen: host.isOpen,
		open: host.open,
		close: host.close,
		activate: host.activate,
		registerFrame: frames.registerFrame,
		publishTranscript: frames.publishTranscript,
		onInsertPrompt,
	};
}
