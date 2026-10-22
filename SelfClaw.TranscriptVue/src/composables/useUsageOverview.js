import { ref, watch } from 'vue';
import { isSuperseded, useHostBridge } from './hostBridge.js';

// 关于页的用量数据层。三条约束来自设计：
//   1. 用 requestLatest —— 快速来回切 7d/30d 时，慢的那次必须作废，否则会盖掉新结果；
//   2. 模块级 30 秒缓存 —— About 是设置页默认页，反复开关设置会反复扫库；
//   3. 用 watch(immediate) 而不是 onMounted —— 组件是 defineAsyncComponent，卸载/重建很频繁。

const CACHE_TTL_MS = 30_000;
const REQUEST_KEY = 'usage-overview';
const REQUEST_TYPE = 'usage/get-overview';
const DEFAULT_RANGE = '7d';
const FALLBACK_ERROR = '加载用量统计失败';

// key = range|timeZone：用户改了时区就不该复用旧分桶。
const cache = new Map();

export function useUsageOverview() {
	const { requestLatest } = useHostBridge();
	const range = ref(DEFAULT_RANGE);
	const overview = ref(null);
	const loading = ref(false);
	const error = ref('');

	// 作废请求的 finally 不能把 loading 关掉，否则新请求还在飞时加载态会闪断。
	let ticket = 0;

	async function load(nextRange, { force = false } = {}) {
		const timeZone = resolveTimeZone();
		const key = `${nextRange}|${timeZone}`;
		// 每次选择都要作废更早的回包，命中缓存的那次也不例外：先发出的慢请求晚点回来时，
		// 用户可能已经切回缓存里的范围，不能让它把界面盖成另一个范围。
		const current = ++ticket;
		if (!force) {
			const cached = readCache(key);
			if (cached) {
				overview.value = cached;
				error.value = '';
				loading.value = false;
				return;
			}
		}

		loading.value = true;
		error.value = '';
		try {
			const payload = await requestLatest(REQUEST_KEY, REQUEST_TYPE, {
				range: nextRange,
				...(timeZone ? { timeZone } : {}),
			});
			if (current !== ticket) return;
			overview.value = payload;
			cache.set(key, { at: Date.now(), overview: payload });
		} catch (failure) {
			if (isSuperseded(failure) || current !== ticket) return;
			error.value = errorMessage(failure);
		} finally {
			if (current === ticket) loading.value = false;
		}
	}

	watch(range, (next) => void load(next), { immediate: true });

	return {
		range,
		overview,
		loading,
		error,
		refresh: () => load(range.value, { force: true }),
	};
}

function readCache(key) {
	const entry = cache.get(key);
	if (!entry) return null;
	if (Date.now() - entry.at > CACHE_TTL_MS) {
		cache.delete(key);
		return null;
	}

	return entry.overview;
}

function resolveTimeZone() {
	try {
		return Intl.DateTimeFormat().resolvedOptions().timeZone || '';
	} catch {
		return '';
	}
}

function errorMessage(failure) {
	const text = typeof failure?.message === 'string' ? failure.message.trim() : '';
	return text || FALLBACK_ERROR;
}
