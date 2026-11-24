import { onMounted, ref } from 'vue';
import { isSuperseded, useHostBridge } from './hostBridge.js';

const DEFAULT_ABSOLUTE_THRESHOLD = 64_000;
const STATE_TYPE = 'direct-compression/state';
const GET_TYPE = 'direct-compression/get';

const absoluteThreshold = ref(DEFAULT_ABSOLUTE_THRESHOLD);
// 用量环上的压缩刻度跟着这个开关走：关掉自动压缩后，上一回合留下的触发点不再有意义。
const enabled = ref(true);
let initialized = false;

function applyPolicy(policy) {
	const tokens = policy?.absoluteThresholdTokens;
	if (Number.isFinite(tokens) && tokens > 0) {
		absoluteThreshold.value = tokens;
	}
	if (typeof policy?.enabled === 'boolean') {
		enabled.value = policy.enabled;
	}
}

export function useCompressionThreshold() {
	const { requestLatest, on } = useHostBridge();

	async function loadThreshold() {
		try {
			const res = await requestLatest('compression-threshold-reader', GET_TYPE);
			applyPolicy(res?.policy);
		} catch (e) {
			if (!isSuperseded(e)) {
				// 保持默认阈值兜底
			}
		}
	}

	on(STATE_TYPE, (payload) => applyPolicy(payload?.policy));

	onMounted(() => {
		if (!initialized) {
			initialized = true;
			void loadThreshold();
		}
	});

	return {
		absoluteThreshold,
		enabled,
		loadThreshold,
	};
}
