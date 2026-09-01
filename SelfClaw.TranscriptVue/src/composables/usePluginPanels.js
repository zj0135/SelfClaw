import { computed, onMounted, onUnmounted, ref, toRaw, watch } from 'vue';
import { hostBridge, useHostBridge } from './hostBridge';
import { useAppearance } from './useAppearance.js';

// 面板与宿主之间的中转。插件永远不直接跟宿主说话：它 postMessage 给外壳，外壳凭
// event.origin + event.source 认出是哪个面板，再用自己的 hostBridge 转发。
//
// 身份只来自这两样东西。payload 里出现的任何 pluginId 都不作数——那正是插件唯一能
// 伪造的部分。
const SHELL_ORIGIN = 'https://appassets.selfclaw.local';
const MAX_TABS = 8;
const SAVE_DEBOUNCE_MS = 400;

// 面板是跨源 iframe，CSS 变量继承不进去。外壳只告诉它「现在是什么外观」，
// 不下发一整套解析后的实色：那等于把外壳的 token 表变成插件 API，往后每次
// 增删颜色都成了破坏性变更。面板自己订阅 appearance-changed 决定怎么画。
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

export function usePluginPanels() {
	const { request, on } = useHostBridge();
	const appearance = useAppearance();
	const available = ref([]);
	const tabs = ref([]);
	const activeKey = ref('');
	const error = ref('');
	const loading = ref(false);
	const frames = new Map();
	const onInsertPrompt = ref(null);
	let saveTimer = null;
	let latestContext = null;
	let latestTranscript = null;

	const activeTab = computed(() => tabs.value.find((tab) => tab.key === activeKey.value) || null);
	const isOpen = computed(() => tabs.value.length > 0);

	function registerFrame(key, element) {
		if (element) frames.set(key, element);
		else frames.delete(key);
	}

	function findTabBySource(event) {
		for (const tab of tabs.value) {
			const frame = frames.get(tab.key);
			if (frame && frame.contentWindow === event.source && event.origin === tab.panel.origin) {
				return tab;
			}
		}

		return null;
	}

	// postMessage 走的是结构化克隆，而克隆不接受 Proxy。tabs 是个 ref，所以 tabs.value[i]
	// 及其嵌套字段读出来都是响应式代理，直接塞进消息会抛 DataCloneError（握手里的
	// permissions 数组就是这么炸的）。toRaw 只脱一层，嵌套的代理还在，因此这里按整棵树脱。
	// 出站消息只有这一个出口，把它挡在这里就不用在每个调用点各记一次。
	function toPlain(value) {
		if (Array.isArray(value)) return value.map(toPlain);
		if (value === null || typeof value !== 'object') return value;
		const raw = toRaw(value);
		// Date/Map/Set 这类内置类型克隆本来就支持，拆成普通对象反而会丢掉语义。
		if (raw instanceof Date || raw instanceof Map || raw instanceof Set) return raw;
		return Object.fromEntries(Object.entries(raw).map(([key, item]) => [key, toPlain(item)]));
	}

	function sendToFrame(tab, message) {
		const frame = frames.get(tab.key);
		frame?.contentWindow?.postMessage(toPlain({ __selfclaw: 1, ...message }), tab.panel.origin);
	}

	function grants(tab, permission) {
		return (tab.panel.permissions || []).includes(permission);
	}

	function broadcast(type, payload, permission) {
		for (const tab of tabs.value) {
			if (!permission || grants(tab, permission)) {
				sendToFrame(tab, { kind: 'event', type, payload });
			}
		}
	}

	async function handleFrameRequest(tab, message) {
		const respond = (ok, body) => sendToFrame(tab, { kind: 'response', id: message.id, ok, ...body });
		try {
			// composer.insert 不需要往返宿主：外壳自己就持有输入框。
			if (message.op === 'composer.insert') {
				if (!grants(tab, 'host.composer.write')) {
					throw new Error('This panel does not declare the "host.composer.write" permission.');
				}

				onInsertPrompt.value?.(String(message.args?.text ?? ''));
				respond(true, { result: null });
				return;
			}

			const response = await request('plugin-host/api', {
				panelKey: tab.key,
				op: message.op,
				args: message.args || {},
			});
			if (response?.ok === false) throw new Error(response.error || '宿主拒绝了该调用。');
			respond(true, { result: response?.result ?? null });
		} catch (cause) {
			respond(false, { error: cause?.message || '面板调用失败。' });
		}
	}

	function onWindowMessage(event) {
		if (!event.data || event.data.__selfclaw !== 1) return;
		const tab = findTabBySource(event);
		if (!tab) return;

		if (event.data.kind === 'hello') {
			// 外观放在 handshake 里，不做权限门：它是「你被嵌在什么样的外壳里」这个
			// 事实，跟会话内容无关，任何面板都该能画得跟外壳一致。
			sendToFrame(tab, {
				kind: 'event',
				type: 'handshake',
				payload: {
					panelKey: tab.key,
					permissions: tab.panel.permissions || [],
					appearance: readAppearanceFacts(appearance),
				},
			});
			// 一个刚打开的面板没有历史可听。把最近一次状态补给它，否则在空闲会话里
			// 它要一直等到下一次变化才能画出第一屏。
			if (latestContext && grants(tab, 'host.context.read')) {
				sendToFrame(tab, { kind: 'event', type: 'context-changed', payload: latestContext });
			}

			if (latestTranscript && grants(tab, 'host.transcript.read')) {
				sendToFrame(tab, { kind: 'event', type: 'transcript', payload: latestTranscript });
			}

			return;
		}

		if (event.data.kind === 'ready') {
			tab.ready = true;
			return;
		}

		if (event.data.kind === 'request') handleFrameRequest(tab, event.data);
	}

	function scheduleSave() {
		if (saveTimer) window.clearTimeout(saveTimer);
		saveTimer = window.setTimeout(() => {
			saveTimer = null;
			hostBridge.post({
				type: 'plugin-host/save-tabs',
				tabs: tabs.value.map((tab) => tab.key),
				activeKey: activeKey.value || null,
			});
		}, SAVE_DEBOUNCE_MS);
	}

	async function load() {
		loading.value = true;
		try {
			const response = await request('plugin-host/get-panels');
			available.value = response?.panels || [];
			error.value = '';
			return response?.tabs || [];
		} catch (cause) {
			error.value = cause?.message || '无法加载插件面板。';
			return [];
		} finally {
			loading.value = false;
		}
	}

	async function open(key) {
		const existing = tabs.value.find((tab) => tab.key === key);
		if (existing) {
			activeKey.value = key;
			scheduleSave();
			return existing;
		}

		if (tabs.value.length >= MAX_TABS) {
			error.value = `最多同时打开 ${MAX_TABS} 个面板。`;
			return null;
		}

		try {
			const response = await request('plugin-host/open', { panelKey: key });
			const tab = { key, panel: response.panel, url: response.url, ready: false };
			tabs.value = [...tabs.value, tab];
			activeKey.value = key;
			error.value = '';
			scheduleSave();
			return tab;
		} catch (cause) {
			error.value = cause?.message || '无法打开该面板。';
			return null;
		}
	}

	function close(key) {
		const index = tabs.value.findIndex((tab) => tab.key === key);
		if (index < 0) return;

		frames.delete(key);
		tabs.value = tabs.value.filter((tab) => tab.key !== key);
		if (activeKey.value === key) {
			activeKey.value = tabs.value[Math.min(index, tabs.value.length - 1)]?.key || '';
		}

		hostBridge.post({ type: 'plugin-host/close', panelKey: key });
		scheduleSave();
	}

	// 宿主在禁用或删除插件时推送：面板的源已经停止解析，标签留在界面上只会显示一个死框。
	function evict(pluginId) {
		for (const tab of tabs.value.filter((candidate) => candidate.panel.pluginId === pluginId)) {
			frames.delete(tab.key);
		}

		const remaining = tabs.value.filter((tab) => tab.panel.pluginId !== pluginId);
		if (remaining.length === tabs.value.length) return;

		tabs.value = remaining;
		if (!remaining.some((tab) => tab.key === activeKey.value)) {
			activeKey.value = remaining[0]?.key || '';
		}

		scheduleSave();
	}

	// 上下文只有宿主一个生产者。外壳不再从 transcript 负载里自己拼一份——那样拼出来的
	// 字段集和 getContext() 拿到的并不一致，工作区根还可能与 workspace.* 实际解析的根不同。
	function publishContext(context) {
		if (!context) return;
		latestContext = context;
		broadcast('context-changed', context, 'host.context.read');
	}

	function publishTranscript(payload) {
		latestTranscript = payload;
		broadcast('transcript', payload, 'host.transcript.read');
	}

	// useHostBridge 的 on() 已在 onUnmounted 自动退订，这里不需要再自己收集 disposer。
	on('plugin-host/context', (payload) => publishContext(payload.context));
	on('plugin-host/evict', (payload) => evict(payload.pluginId));
	on('extensions/state-changed', async () => {
		await load();
		const keys = new Set(available.value.map((panel) => panel.key));
		for (const tab of tabs.value.filter((candidate) => !keys.has(candidate.key))) {
			close(tab.key);
		}
	});

	watch(activeKey, scheduleSave);

	// revision 覆盖了外观的每一项改动，包括「跟随系统」时系统自己翻明暗。
	// 不带权限：与 handshake 里同一份事实，同一个理由。
	watch(appearance.revision, () => {
		broadcast('appearance-changed', readAppearanceFacts(appearance));
	});

	onMounted(async () => {
		window.addEventListener('message', onWindowMessage);
		const persisted = await load();
		const keys = new Set(available.value.map((panel) => panel.key));
		for (const key of persisted.filter((candidate) => keys.has(candidate))) {
			await open(key);
		}
	});

	onUnmounted(() => {
		window.removeEventListener('message', onWindowMessage);
		if (saveTimer) window.clearTimeout(saveTimer);
	});

	return {
		available,
		tabs,
		activeKey,
		activeTab,
		isOpen,
		error,
		loading,
		open,
		close,
		load,
		registerFrame,
		publishTranscript,
		onInsertPrompt,
	};
}
