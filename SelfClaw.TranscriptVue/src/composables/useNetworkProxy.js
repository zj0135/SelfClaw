import { computed, onMounted, ref } from 'vue';
import { isSuperseded, useHostBridge } from './hostBridge.js';

// 网络出口设置链路：读 → 本地编辑 → 保存 → 以宿主回显为真值。
//
// 归一化只发生在宿主（NetworkProxySettingsService），前端不夹紧取值；唯一的前端判断是
// 「自定义端点还没填完就不保存」—— 宿主会把不完整端点当系统模式处理，边打字边保存会让
// 用户刚选的模式被悄悄弹回去，所以不完整时只给行内提示，等填完再提交。

const STATE_TYPE = 'network-proxy/state';
const GET_TYPE = 'network-proxy/get';
const SAVE_TYPE = 'network-proxy/save';

export const PROXY_MODES = [
	{ id: 'Direct', label: '本地直连', hint: '直连，忽略系统代理' },
	{ id: 'System', label: '系统网络', hint: '跟随 Windows 代理设置' },
	{ id: 'Custom', label: '自定义', hint: '指定代理地址与端口' },
];

const DEFAULT_MODE = 'System';
const MIN_PORT = 1;
const MAX_PORT = 65535;

export function parseEndpoint(endpointText) {
	const raw = String(endpointText || '').trim();
	if (!raw) {
		return { host: '', port: 0 };
	}

	let clean = raw;
	if (clean.startsWith('http://')) clean = clean.slice(7).trim();
	else if (clean.startsWith('https://')) clean = clean.slice(8).trim();
	clean = clean.replace(/\/+$/, '');

	const lastColon = clean.lastIndexOf(':');
	if (lastColon <= 0 || lastColon === clean.length - 1) {
		return { host: clean, port: 0 };
	}

	const hostPart = clean.slice(0, lastColon).trim();
	const portPart = clean.slice(lastColon + 1).trim();
	const portNum = Number(portPart);

	return {
		host: hostPart,
		port: Number.isInteger(portNum) && portNum >= MIN_PORT && portNum <= MAX_PORT ? portNum : 0,
	};
}

export function formatEndpoint(state) {
	if (!state?.host) return '';
	const port = Number(state.port);
	return port >= MIN_PORT && port <= MAX_PORT ? `${state.host}:${port}` : state.host;
}

function defaultState() {
	return { mode: DEFAULT_MODE, host: '', port: 0 };
}

function stateFrom(raw) {
	const source = raw && typeof raw === 'object' ? raw : {};
	return {
		mode: PROXY_MODES.some((item) => item.id === source.mode) ? source.mode : DEFAULT_MODE,
		host: typeof source.host === 'string' ? source.host : '',
		port: Number.isFinite(source.port) ? source.port : 0,
	};
}

export function isCustomEndpointUsable(state) {
	const port = Number(state?.port);
	return typeof state?.host === 'string' && state.host.trim() !== '' &&
		Number.isInteger(port) && port >= MIN_PORT && port <= MAX_PORT;
}

function errorMessage(failure, fallback) {
	const text = typeof failure?.message === 'string' ? failure.message.trim() : '';
	return text || fallback;
}

export function useNetworkProxy() {
	const { requestLatest, on } = useHostBridge();
	const state = ref(defaultState());
	const loading = ref(true);
	const saving = ref(false);
	const error = ref('');

	// 编辑票据：宿主推送可能来自别的消费者，不能盖掉尚未保存的本地编辑。
	let editTicket = 0;
	let confirmedTicket = 0;

	const customIncomplete = computed(
		() => state.value.mode === 'Custom' && !isCustomEndpointUsable(state.value),
	);

	function applyState(raw) {
		state.value = stateFrom(raw);
	}

	async function load() {
		loading.value = true;
		try {
			applyState(await requestLatest('network-proxy', GET_TYPE));
			error.value = '';
		} catch (failure) {
			if (!isSuperseded(failure)) error.value = errorMessage(failure, '加载网络出口设置失败');
		} finally {
			loading.value = false;
		}
	}

	async function commit() {
		if (editTicket <= confirmedTicket) return;
		if (state.value.mode === 'Custom' && !isCustomEndpointUsable(state.value)) return;

		const ticket = editTicket;
		const snapshot = { ...state.value };
		saving.value = true;
		try {
			const payload = await requestLatest(SAVE_TYPE, SAVE_TYPE, snapshot);
			confirmedTicket = Math.max(confirmedTicket, ticket);
			error.value = '';
			// 保存往返期间又有编辑时保留用户输入，交给下一次保存收敛。
			if (ticket === editTicket) applyState(payload);
		} catch (failure) {
			if (!isSuperseded(failure)) error.value = errorMessage(failure, '保存网络出口设置失败');
		} finally {
			saving.value = false;
		}
	}

	function setMode(mode) {
		if (!PROXY_MODES.some((item) => item.id === mode) || mode === state.value.mode) return;
		state.value = { ...state.value, mode };
		editTicket += 1;
		void commit();
	}

	function setHost(host) {
		state.value = { ...state.value, host: String(host).trim() };
		editTicket += 1;
		void commit();
	}

	function setPort(port) {
		const value = Number(String(port).trim());
		state.value = { ...state.value, port: Number.isFinite(value) ? value : 0 };
		editTicket += 1;
		void commit();
	}

	function setEndpoint(endpointText) {
		const parsed = parseEndpoint(endpointText);
		state.value = { ...state.value, host: parsed.host, port: parsed.port };
		editTicket += 1;
		void commit();
	}

	on(STATE_TYPE, (payload) => {
		if (editTicket > confirmedTicket) return;
		applyState(payload);
	});

	onMounted(() => {
		void load();
	});

	return { state, loading, saving, error, customIncomplete, setMode, setHost, setPort, setEndpoint, load };
}
