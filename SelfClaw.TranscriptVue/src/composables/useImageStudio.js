import { computed, onMounted, reactive, ref } from 'vue';
import { useHostBridge } from './hostBridge.js';

// 初始样例图片，与设计稿完全一致（支持真实生成后追加）
const INITIAL_DEMO_IMAGES = [
	{
		id: 'demo-1',
		index: 1,
		url: 'https://images.unsplash.com/photo-1464822759023-fed622ff2c3b?auto=format&fit=crop&w=1200&q=80',
		prompt: '雪山日出，云海缭绕，宏伟壮丽的自然风光',
		createdAt: 'Just now',
	},
	{
		id: 'demo-2',
		index: 2,
		url: 'https://images.unsplash.com/photo-1513519245088-0e12902e5a38?auto=format&fit=crop&w=1200&q=80',
		prompt: '阳光透过百叶窗照在花瓶中的鲜花和书本上，温暖静谧的室内',
		createdAt: 'Just now',
	},
	{
		id: 'demo-3',
		index: 3,
		url: 'https://images.unsplash.com/photo-1543852786-1cf6624b9987?auto=format&fit=crop&w=1200&q=80',
		prompt: '一只仰头凝视的可爱毛茸茸长毛猫咪，唯美梦幻光影',
		createdAt: 'Just now',
	},
];

export const PRESET_SIZES = [
	{ key: '1:1', label: '1:1', desc: '1024 × 1024', sizeStr: '1024x1024' },
	{ key: '16:9', label: '16:9', desc: '1920 × 1080', sizeStr: '1792x1024' },
	{ key: '9:16', label: '9:16', desc: '1080 × 1920', sizeStr: '1024x1792' },
	{ key: 'custom', label: '自定义', desc: '自由设定', sizeStr: '1024x1024' },
];

export const COMMON_TAGS = [
	'写实', '插画', '二次元', '摄影', '科幻',
	'产品设计', '风景', '人物', '建筑',
];

export const EXTENDED_TAGS = [
	'赛博朋克', '水彩画', '国风水墨', '油画风格', '3D渲染', '极简主义', '电影质感', '像素艺术',
];

export function useImageStudio() {
	const { request } = useHostBridge();

	const models = ref([]);
	const loadingModels = ref(false);
	const selectedModelProfileId = ref('');
	const prompt = ref('');
	const negativePrompt = ref('');
	const selectedSizeKey = ref('16:9');
	const count = ref(1);
	const steps = ref(30);
	const cfgScale = ref(7.5);
	const quality = ref('standard');
	const style = ref('vivid');
	const referenceImage = ref(null); // { base64, name, url }
	const isGenerating = ref(false);
	const zoomLevel = ref(100);
	const statusMessage = ref('');
	const errorMessage = ref('');

	const gallery = reactive([...INITIAL_DEMO_IMAGES]);

	// 当前选中的模型对象
	const currentModel = computed(() => {
		return models.value.find((m) => m.profileId === selectedModelProfileId.value) || models.value[0] || null;
	});

	// 当前选中的尺寸
	const currentSize = computed(() => {
		return PRESET_SIZES.find((s) => s.key === selectedSizeKey.value) || PRESET_SIZES[1];
	});

	// 加载模型列表，严格只保留 apiFormat === 3 (OpenAIImages) 的生图模型
	async function loadModels() {
		loadingModels.value = true;
		errorMessage.value = '';
		try {
			// 先尝试专用生图通道
			let imageModels = [];
			try {
				const res = await request('image-generation/list-models', {});
				if (Array.isArray(res?.models) && res.models.length > 0) {
					imageModels = res.models;
				}
			} catch {
				// fallback 到 ai-providers/get-state
			}

			if (imageModels.length === 0) {
				const stateRes = await request('ai-providers/get-state', {});
				const providers = Array.isArray(stateRes?.state?.providers) ? stateRes.state.providers : [];
				for (const provider of providers) {
					if (!provider.enabled) continue;
					const pModels = Array.isArray(provider.models) ? provider.models : [];
					for (const m of pModels) {
						// 严格过滤：只允许生图协议模型 (apiFormat === 3)
						if (m.enabled && Number(m.apiFormat) === 3) {
							imageModels.push({
								profileId: m.modelProfileId || m.id,
								providerName: provider.name,
								name: m.name || m.model,
								model: m.model,
								apiFormat: 3,
								description: m.configuration?.description || `${provider.name} 图像生成模型`,
							});
						}
					}
				}
			}

			models.value = imageModels;
			if (imageModels.length > 0 && !selectedModelProfileId.value) {
				selectedModelProfileId.value = imageModels[0].profileId;
			}
		} catch (err) {
			console.warn('[ImageStudio] Failed to load image models:', err);
		} finally {
			loadingModels.value = false;
		}
	}

	function appendTag(tag) {
		if (!tag) return;
		const current = prompt.value.trim();
		if (!current) {
			prompt.value = tag;
		} else if (!current.includes(tag)) {
			prompt.value = `${current}, ${tag}`;
		}
	}

	function resetParams() {
		prompt.value = '';
		negativePrompt.value = '';
		selectedSizeKey.value = '16:9';
		count.value = 1;
		steps.value = 30;
		cfgScale.value = 7.5;
		quality.value = 'standard';
		style.value = 'vivid';
		referenceImage.value = null;
		errorMessage.value = '';
		statusMessage.value = '';
	}

	function removeImage(id) {
		const index = gallery.findIndex((img) => img.id === id);
		if (index >= 0) {
			gallery.splice(index, 1);
		}
	}

	function setZoom(delta) {
		const next = zoomLevel.value + delta;
		if (next >= 50 && next <= 200) {
			zoomLevel.value = next;
		}
	}

	async function setReferenceImageFile(file) {
		if (!file) return;
		const reader = new FileReader();
		reader.onload = (e) => {
			referenceImage.value = {
				name: file.name,
				url: e.target.result,
				base64: e.target.result,
			};
		};
		reader.readAsDataURL(file);
	}

	function clearReferenceImage() {
		referenceImage.value = null;
	}

	async function generate() {
		if (isGenerating.value) return;
		const promptText = prompt.value.trim();
		if (!promptText) {
			errorMessage.value = '请输入你想生成的图片描述';
			return;
		}

		errorMessage.value = '';
		statusMessage.value = '正在生成图片...';
		isGenerating.value = true;

		try {
			const sizeStr = currentSize.value.sizeStr;
			let res;

			if (referenceImage.value?.base64) {
				// 图生图 / 编辑
				res = await request('image-generation/edit', {
					prompt: promptText,
					imageBase64: referenceImage.value.base64,
					modelProfileId: selectedModelProfileId.value || undefined,
					model: currentModel.value?.model || undefined,
					count: count.value,
					size: sizeStr,
				});
			} else {
				// 文生图
				res = await request('image-generation/generate', {
					prompt: promptText,
					modelProfileId: selectedModelProfileId.value || undefined,
					model: currentModel.value?.model || undefined,
					count: count.value,
					size: sizeStr,
					quality: quality.value,
					style: style.value,
				});
			}

			if (res?.ok && Array.isArray(res.images)) {
				for (const item of res.images) {
					gallery.unshift({
						id: item.id || `gen-${Date.now()}-${Math.random()}`,
						index: gallery.length + 1,
						url: item.url,
						prompt: item.revisedPrompt || promptText,
						localPath: item.localPath,
						createdAt: '刚刚',
					});
				}
				statusMessage.value = `生成完成！耗时 ${((res.durationMs || 0) / 1000).toFixed(1)} 秒`;
			} else {
				throw new Error(res?.error || '生成失败，请检查模型与网络');
			}
		} catch (err) {
			errorMessage.value = err?.message || '生成图片失败，请检查生图模型配置';
			console.error('[ImageStudio] Generate failed:', err);
		} finally {
			isGenerating.value = false;
		}
	}

	onMounted(() => {
		loadModels();
	});

	return {
		models,
		loadingModels,
		selectedModelProfileId,
		currentModel,
		prompt,
		negativePrompt,
		selectedSizeKey,
		currentSize,
		count,
		steps,
		cfgScale,
		quality,
		style,
		referenceImage,
		isGenerating,
		zoomLevel,
		gallery,
		statusMessage,
		errorMessage,
		loadModels,
		appendTag,
		resetParams,
		removeImage,
		setZoom,
		setReferenceImageFile,
		clearReferenceImage,
		generate,
	};
}
