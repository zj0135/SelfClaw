import { onMounted, onUnmounted, toRaw } from 'vue';
import { createPluginTranscriptProjector } from '../renderers/pluginTranscript.js';

// 帧注册表与消息中转。dock 面板与悬浮视图是同一批帧的两种摆放方式，所以身份判定、握手、
// 四类推送与插件通知的分发都只在这里实现一次。
//
// 插件永远不直接跟宿主说话：它 postMessage 给外壳，外壳凭 event.origin + event.source 认出是
// 哪个视图，再用自己的 hostBridge 转发。身份只来自这两样东西——payload 里出现的任何 pluginId
// 都不作数，那正是插件唯一能伪造的部分。
const MAXIMUM_REGION_RECTS = 64;
const TRANSCRIPT_DEBOUNCE_MS = 500;

export function usePluginFrames({ views, request, appearanceFacts, anchors, onNotice, localOps }) {
	const frames = new Map();
	const projectTranscript = createPluginTranscriptProjector();
	const lastAnchors = new Map();
	let latestContext = null;
	let latestTranscriptInput = null;
	let latestTranscript = null;
	let transcriptTimer = null;

	function registerFrame(key, element) {
		if (element) frames.set(key, element);
		else {
			frames.delete(key);
			lastAnchors.delete(key);
		}
	}

	function frameFor(key) {
		return frames.get(key) ?? null;
	}

	// 地标是宿主在窗口坐标里量的，但插件只认得自己视口里的数。把地标换算到每个帧自己的坐标系里，
	// 插件作者就不用关心原点差异；浮层被摆到哪里、侧栏与右栏怎么变，都不影响这条规则。
	function localizeAnchors(anchors, key) {
		if (!anchors) return null;
		const frame = frames.get(key);
		if (!frame) return anchors;
		const origin = frame.getBoundingClientRect();
		return Object.fromEntries(Object.entries(anchors).map(([name, rect]) => [name, rect
			? { x: rect.x - origin.x, y: rect.y - origin.y, width: rect.width, height: rect.height }
			: null]));
	}

	// postMessage 走的是结构化克隆，而克隆不接受 Proxy。views 里读出来的字段都是响应式代理，
	// 直接塞进消息会抛 DataCloneError。toRaw 只脱一层，嵌套的代理还在，因此这里按整棵树脱。
	// 出站消息只有这一个出口，把它挡在这里就不用在每个调用点各记一次。
	function toPlain(value) {
		if (Array.isArray(value)) return value.map(toPlain);
		if (value === null || typeof value !== 'object') return value;
		const raw = toRaw(value);
		// Date/Map/Set 这类内置类型克隆本来就支持，拆成普通对象反而会丢掉语义。
		if (raw instanceof Date || raw instanceof Map || raw instanceof Set) return raw;
		return Object.fromEntries(Object.entries(raw).map(([key, item]) => [key, toPlain(item)]));
	}

	function sendTo(view, message, prepared = false) {
		const frame = frames.get(view.key);
		frame?.contentWindow?.postMessage(
			prepared ? { __selfclaw: 1, ...message } : toPlain({ __selfclaw: 1, ...message }),
			view.origin);
	}

	function grants(view, permission) {
		return (view.permissions || []).includes(permission);
	}

	function broadcast(type, payload, permission) {
		for (const view of views()) {
			if (!permission || grants(view, permission)) sendTo(view, { kind: 'event', type, payload });
		}
	}

	// 武装被解除时告知插件，让它清理自己的 hover 态。不依赖子文档是否收到 pointerout。
	function release(view) {
		sendTo(view, { kind: 'event', type: 'hit-released' });
	}

	function findViewBySource(event) {
		for (const view of views()) {
			const frame = frames.get(view.key);
			if (frame && frame.contentWindow === event.source && event.origin === view.origin) {
				return view;
			}
		}

		return null;
	}

	async function handleRequest(view, message) {
		const respond = (ok, body) => sendTo(view, { kind: 'response', id: message.id, ok, ...body });
		try {
			const op = localOps[message.op];
			if (op) {
				if (op.permission && !grants(view, op.permission)) {
					throw new Error(`This view does not declare the "${op.permission}" permission.`);
				}

				await op.run(view, message.args || {});
				respond(true, { result: null });
				return;
			}

			const response = await request('plugin-host/api', {
				viewKey: view.key,
				op: message.op,
				args: message.args || {},
			});
			if (response?.ok === false) throw new Error(response.error || '宿主拒绝了该调用。');
			respond(true, { result: response?.result ?? null });
		} catch (cause) {
			respond(false, { error: cause?.message || '插件调用失败。' });
		}
	}

	function handleNotice(view, message) {
		const type = message.type;
		if (type === 'hit-regions') {
			onNotice?.(view, type, { rects: normalizeRects(message.payload?.rects) });
			return;
		}

		if (type === 'hit-regions-release') onNotice?.(view, type, null);
	}

	function onWindowMessage(event) {
		if (!event.data || event.data.__selfclaw !== 1) return;
		const view = findViewBySource(event);
		if (!view) return;

		if (event.data.kind === 'hello') {
			handshake(view);
			return;
		}

		if (event.data.kind === 'ready') {
			view.ready = true;
			return;
		}

		if (event.data.kind === 'notice') {
			handleNotice(view, event.data);
			return;
		}

		if (event.data.kind === 'request') handleRequest(view, event.data);
	}

	// 一个刚打开的帧没有历史可听，所以握手要把最近一次状态一次补齐；空闲会话里这是它画出第一屏的
	// 唯一来源。外观与地标不做权限门：它们是「你被嵌在什么样的外壳里」这个事实，与会话内容无关。
	function handshake(view) {
		sendTo(view, {
			kind: 'event',
			type: 'handshake',
			payload: {
				viewKey: view.key,
				slot: view.slot,
				permissions: view.permissions || [],
				appearance: appearanceFacts(),
				anchors: localizeAnchors(anchors(), view.key),
			},
		});

		if (latestContext && grants(view, 'host.context.read')) {
			sendTo(view, { kind: 'event', type: 'context-changed', payload: latestContext });
		}

		if (latestTranscriptInput && grants(view, 'host.transcript.read')) {
			latestTranscript = projectTranscript(latestTranscriptInput);
			sendTo(view, { kind: 'event', type: 'transcript', payload: latestTranscript }, true);
		}
	}

	function publishContext(context) {
		if (!context) return;
		latestContext = context;
		broadcast('context-changed', context, 'host.context.read');
	}

	function publishAnchors(next) {
		if (!next) return;
		// 换算后的值也要去重：流式 transcript 期间每 120ms 一次布局信号不该变成每 120ms 一次推送。
		for (const view of views()) {
			const localized = localizeAnchors(next, view.key);
			const serialized = JSON.stringify(localized);
			if (serialized === lastAnchors.get(view.key)) continue;
			lastAnchors.set(view.key, serialized);
			sendTo(view, { kind: 'event', type: 'anchors-changed', payload: localized });
		}
	}

	function publishAppearance(facts) {
		broadcast('appearance-changed', facts);
	}

	function publishTranscript(payload) {
		latestTranscriptInput = payload;
		if (transcriptTimer || !views().some((view) => grants(view, 'host.transcript.read'))) return;
		transcriptTimer = window.setTimeout(() => {
			transcriptTimer = null;
			latestTranscript = projectTranscript(latestTranscriptInput);
			for (const view of views()) {
				if (grants(view, 'host.transcript.read')) {
					sendTo(view, { kind: 'event', type: 'transcript', payload: latestTranscript }, true);
				}
			}
		}, TRANSCRIPT_DEBOUNCE_MS);
	}

	onMounted(() => window.addEventListener('message', onWindowMessage));
	onUnmounted(() => {
		if (transcriptTimer) window.clearTimeout(transcriptTimer);
		window.removeEventListener('message', onWindowMessage);
	});

	return {
		registerFrame,
		frameFor,
		publishContext,
		publishAnchors,
		publishAppearance,
		publishTranscript,
		release,
	};
}

// 矩形来自第三方页面，必须校验：数量、有限数、非负，并夹到窗口范围内。
function normalizeRects(rects) {
	if (!Array.isArray(rects)) return [];
	const width = window.innerWidth;
	const height = window.innerHeight;
	const results = [];
	for (const rect of rects.slice(0, MAXIMUM_REGION_RECTS)) {
		const x = Number(rect?.x);
		const y = Number(rect?.y);
		const w = Number(rect?.width);
		const h = Number(rect?.height);
		if (![x, y, w, h].every(Number.isFinite) || w <= 0 || h <= 0) continue;
		// 向外取整：插件按自己的布局取整，宿主再往里收会把边缘像素丢掉。
		const left = Math.max(0, Math.floor(x));
		const top = Math.max(0, Math.floor(y));
		const right = Math.min(width, Math.ceil(x + w));
		const bottom = Math.min(height, Math.ceil(y + h));
		if (right <= left || bottom <= top) continue;
		results.push({ x: left, y: top, width: right - left, height: bottom - top });
	}

	return results;
}
