import { ref } from 'vue';
import { isSuperseded, useHostBridge } from './hostBridge.js';

// 当前 Agent 的有效技能（extensions/list-effective-skills）。
// 模块级缓存按 `${agentId}:${capabilityRevision}` 命中：输入框 `$` 弹层与「+」菜单共用一份结果，
// 并发请求只发一次；capabilityRevision 由宿主在扩展状态变化时递增，旧缓存自然失效。
const cache = new Map();
const inFlight = new Map();

export function useEffectiveSkills(getAgentId, getCapabilityRevision) {
	const { requestLatest } = useHostBridge();
	const skills = ref([]);
	const loading = ref(false);
	const error = ref('');
	let sequence = 0;

	function cacheKey() {
		return `${getAgentId() || ''}:${getCapabilityRevision() || 0}`;
	}

	async function load() {
		const key = cacheKey();
		const cached = cache.get(key);
		if (cached) {
			skills.value = cached;
			error.value = '';
			return;
		}

		const token = ++sequence;
		loading.value = true;
		error.value = '';
		try {
			let request = inFlight.get(key);
			if (!request) {
				request = requestLatest('effective-skills', 'extensions/list-effective-skills', { agentId: getAgentId() || null })
					.then((response) => (Array.isArray(response?.skills) ? response.skills : []))
					.finally(() => inFlight.delete(key));
				inFlight.set(key, request);
			}

			const next = await request;
			cache.set(key, next);
			// 等待期间 Agent 可能已切换并发起了新的 load：只让最后一次写回视图。
			if (token === sequence) skills.value = next;
		} catch (cause) {
			if (token === sequence && !isSuperseded(cause)) error.value = cause?.message || '无法加载技能。';
		} finally {
			if (token === sequence) loading.value = false;
		}
	}

	return { skills, loading, error, load };
}
