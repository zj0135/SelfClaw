import { onMounted, onUnmounted, ref } from 'vue';
import { isSuperseded, useHostBridge } from './hostBridge.js';

// Direct 上下文压缩策略的设置链路：读 → 本地编辑 → 防抖保存 → 按宿主回显收敛。
// 归一化只发生在宿主（DirectCompressionSettingsService），所以前端从不自己夹紧取值，
// 而是把回显当成唯一真值 —— 非法输入保存后会被悄悄改正，这本身就是提示。

const STATE_TYPE = 'direct-compression/state';
const SAVE_TYPE = 'direct-compression/save';
const MODELS_TYPE = 'ai-providers/list-enabled-models';
const SAVE_DEBOUNCE_MS = 400;

// 与 Core 的 DirectCompressionPolicy 默认值一致；只用于首帧占位，真值一律来自宿主。
function defaultPolicy() {
	return { enabled: true, thresholdRatio: 0.75, absoluteThresholdTokens: 64000, modelProfileId: null };
}

function policyFrom(raw) {
	const source = raw && typeof raw === 'object' ? raw : {};
	const fallback = defaultPolicy();
	return {
		enabled: typeof source.enabled === 'boolean' ? source.enabled : fallback.enabled,
		thresholdRatio: Number.isFinite(source.thresholdRatio) ? source.thresholdRatio : fallback.thresholdRatio,
		absoluteThresholdTokens: Number.isFinite(source.absoluteThresholdTokens)
			? source.absoluteThresholdTokens
			: fallback.absoluteThresholdTokens,
		modelProfileId: typeof source.modelProfileId === 'string' && source.modelProfileId !== '' ? source.modelProfileId : null,
	};
}

function errorMessage(failure, fallback) {
	const text = typeof failure?.message === 'string' ? failure.message.trim() : '';
	return text || fallback;
}

export function useDirectCompression() {
	const { requestLatest, on } = useHostBridge();
	const policy = ref(defaultPolicy());
	const models = ref([]);
	const loading = ref(true);
	const modelsLoading = ref(true);
	const saving = ref(false);
	const error = ref('');
	const modelsError = ref('');

	// 编辑票据：宿主推送可能来自别的消费者，不能盖掉尚未保存的本地编辑；
	// 而保存回显是归一化后的真值，只允许“更晚的编辑”盖过它。
	let editTicket = 0;
	let confirmedTicket = 0;
	let saveTimer = 0;
	let inFlightSaves = 0;

	function applyState(raw) {
		policy.value = policyFrom(raw);
	}

	async function load() {
		loading.value = true;
		try {
			applyState((await requestLatest('direct-compression', 'direct-compression/get'))?.policy);
			error.value = '';
		} catch (failure) {
			if (!isSuperseded(failure)) error.value = errorMessage(failure, '加载上下文压缩设置失败');
		} finally {
			loading.value = false;
		}
	}

	async function loadModels() {
		modelsLoading.value = true;
		try {
			const payload = await requestLatest('direct-compression-models', MODELS_TYPE);
			models.value = (Array.isArray(payload?.models) ? payload.models : []).filter((model) => model?.modelProfileId);
			modelsError.value = '';
		} catch (failure) {
			if (!isSuperseded(failure)) modelsError.value = errorMessage(failure, '加载已启用模型失败');
		} finally {
			modelsLoading.value = false;
		}
	}

	async function commit() {
		if (editTicket <= confirmedTicket) return;

		const ticket = editTicket;
		const snapshot = { ...policy.value };
		inFlightSaves += 1;
		saving.value = true;
		try {
			const payload = await requestLatest(SAVE_TYPE, SAVE_TYPE, { policy: snapshot });
			confirmedTicket = Math.max(confirmedTicket, ticket);
			error.value = '';
			// 保存往返期间又有编辑时保留用户输入，交给下一次保存收敛。
			if (ticket === editTicket) applyState(payload?.policy);
		} catch (failure) {
			// 被更新的保存取代：由那一次收尾，这里既不改真值也不报错。
			if (!isSuperseded(failure)) error.value = errorMessage(failure, '保存上下文压缩设置失败');
		} finally {
			inFlightSaves -= 1;
			saving.value = inFlightSaves > 0;
		}
	}

	function scheduleSave() {
		if (saveTimer) window.clearTimeout(saveTimer);
		saveTimer = window.setTimeout(() => {
			saveTimer = 0;
			void commit();
		}, SAVE_DEBOUNCE_MS);
	}

	function update(patch) {
		policy.value = { ...policy.value, ...patch };
		editTicket += 1;
		scheduleSave();
	}

	function setEnabled(enabled) {
		update({ enabled: Boolean(enabled) });
	}

	function setThresholdRatio(ratio) {
		const value = Number(ratio);
		if (!Number.isFinite(value)) return;
		update({ thresholdRatio: value });
	}

	function setAbsoluteThresholdTokens(tokens) {
		const value = Number(tokens);
		if (!Number.isFinite(value)) return;
		update({ absoluteThresholdTokens: Math.round(value) });
	}

	function setModelProfileId(modelProfileId) {
		update({ modelProfileId: modelProfileId ? String(modelProfileId) : null });
	}

	on(STATE_TYPE, (payload) => {
		if (editTicket > confirmedTicket) return;
		applyState(payload?.policy);
	});

	onMounted(() => {
		void load();
		void loadModels();
	});

	// 页面在防抖窗口内被切走时不能丢编辑：卸载前把待保存的改动发出去。
	onUnmounted(() => {
		if (!saveTimer) return;
		window.clearTimeout(saveTimer);
		saveTimer = 0;
		void commit();
	});

	return {
		policy,
		models,
		loading,
		modelsLoading,
		saving,
		error,
		modelsError,
		load,
		loadModels,
		setEnabled,
		setThresholdRatio,
		setAbsoluteThresholdTokens,
		setModelProfileId,
	};
}
