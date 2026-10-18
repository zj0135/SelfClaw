import { onMounted, ref } from 'vue';
import { isSuperseded, useHostBridge } from './hostBridge.js';

const DEFAULT_ABSOLUTE_THRESHOLD = 64_000;
const STATE_TYPE = 'direct-compression/state';
const GET_TYPE = 'direct-compression/get';

const absoluteThreshold = ref(DEFAULT_ABSOLUTE_THRESHOLD);
let initialized = false;

export function useCompressionThreshold() {
	const { requestLatest, on } = useHostBridge();

	async function loadThreshold() {
		try {
			const res = await requestLatest('compression-threshold-reader', GET_TYPE);
			const tokens = res?.policy?.absoluteThresholdTokens;
			if (Number.isFinite(tokens) && tokens > 0) {
				absoluteThreshold.value = tokens;
			}
		} catch (e) {
			if (!isSuperseded(e)) {
				// 保持默认阈值兜底
			}
		}
	}

	on(STATE_TYPE, (payload) => {
		const tokens = payload?.policy?.absoluteThresholdTokens;
		if (Number.isFinite(tokens) && tokens > 0) {
			absoluteThreshold.value = tokens;
		}
	});

	onMounted(() => {
		if (!initialized) {
			initialized = true;
			void loadThreshold();
		}
	});

	return {
		absoluteThreshold,
		loadThreshold,
	};
}
