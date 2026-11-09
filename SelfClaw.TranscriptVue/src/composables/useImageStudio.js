import { computed, onMounted, reactive, ref, watch } from 'vue';
import { useHostBridge } from './hostBridge.js';
import { describeSizes } from '../renderers/imageStudio.js';

// 生图是单次长请求，不是流式回合：默认 30 秒的请求超时对 1792×1024 的渲染根本不够，
// 超时只会让前端先放弃、后端继续烧钱。这里给足分钟级预算，并把取消按钮的 jobId 一起送下去。
const IMAGE_REQUEST_TIMEOUT_MS = 6 * 60 * 1000;
const GALLERY_STORAGE_KEY = 'selfclaw:image-studio:gallery';
const GALLERY_LIMIT = 60;

function readStoredGallery() {
	try {
		const raw = localStorage.getItem(GALLERY_STORAGE_KEY);
		const parsed = raw ? JSON.parse(raw) : [];
		return Array.isArray(parsed)
			? parsed.filter((item) => item && typeof item.url === 'string' && item.url)
			: [];
	} catch (_) {
		return [];
	}
}

function storeGallery(items) {
	try {
		localStorage.setItem(GALLERY_STORAGE_KEY, JSON.stringify(items.slice(0, GALLERY_LIMIT)));
	} catch (_) {
		// 隐私模式或超额配额：丢历史可以接受，不能影响当前这次生成。
	}
}

export function useImageStudio() {
	const { request, post } = useHostBridge();

	const models = ref([]);
	const loadingModels = ref(false);
	const selectedModelProfileId = ref('');
	const prompt = ref('');
	const selectedSize = ref('');
	const count = ref(1);
	const quality = ref('');
	const style = ref('');
	const referenceImage = ref(null); // { base64, name, url }
	const isGenerating = ref(false);
	const cancelling = ref(false);
	const zoomLevel = ref(100);
	const statusMessage = ref('');
	const errorMessage = ref('');
	const activeJobId = ref('');

	const gallery = reactive(readStoredGallery());

	const currentModel = computed(
		() => models.value.find((m) => m.profileId === selectedModelProfileId.value) || models.value[0] || null,
	);

	const capabilities = computed(() => currentModel.value?.capabilities || null);
	const sizeOptions = computed(() => describeSizes(capabilities.value?.sizes));
	const qualityOptions = computed(() => capabilities.value?.qualities || []);
	const styleOptions = computed(() => capabilities.value?.styles || []);
	const maxCount = computed(() => capabilities.value?.maxCount || 1);

	const currentSize = computed(() => {
		const found = sizeOptions.value.find((option) => option.value === selectedSize.value);
		return found || sizeOptions.value[0] || null;
	});

	const currentQuality = computed(() => {
		const found = qualityOptions.value.find((value) => value === quality.value);
		return found || qualityOptions.value[0] || '';
	});

	const currentStyle = computed(() => {
		const found = styleOptions.value.find((value) => value === style.value);
		return found || styleOptions.value[0] || '';
	});

	// 换模型会让原来的尺寸/画质/风格/数量变成非法组合（dall-e-3 只有 1 张、gpt-image-1 没有 style），
	// 所以模型一变就地把选择收敛回新模型的能力范围，避免下一次点击必然 400。
	watch(currentModel, () => {
		selectedSize.value = sizeOptions.value[0]?.value || '';
		quality.value = qualityOptions.value[0] || '';
		style.value = styleOptions.value[0] || '';
		count.value = Math.min(count.value, maxCount.value);
	});

	function persistGallery() {
		storeGallery(gallery.map((item) => ({
			id: item.id,
			url: item.url,
			prompt: item.prompt,
			createdAt: item.createdAt,
		})));
	}

	async function loadModels() {
		loadingModels.value = true;
		errorMessage.value = '';

		try {
			const response = await request('image-generation/list-models', {});
			const list = Array.isArray(response?.models) ? response.models : [];
			models.value = list;

			if (list.length > 0 && !list.some((model) => model.profileId === selectedModelProfileId.value)) {
				selectedModelProfileId.value = list[0].profileId;
			}
		} catch (error) {
			console.warn('[ImageStudio] Failed to load image models:', error);
			models.value = [];
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
		selectedSize.value = sizeOptions.value[0]?.value || '';
		count.value = 1;
		quality.value = qualityOptions.value[0] || '';
		style.value = styleOptions.value[0] || '';
		referenceImage.value = null;
		zoomLevel.value = 100;
		errorMessage.value = '';
		statusMessage.value = '';
	}

	function removeImage(id) {
		const index = gallery.findIndex((item) => item.id === id);
		if (index >= 0) {
			gallery.splice(index, 1);
			persistGallery();
		}
	}

	function clearGallery() {
		gallery.splice(0, gallery.length);
		persistGallery();
	}

	function setZoom(delta) {
		const next = zoomLevel.value + delta;
		if (next >= 50 && next <= 200) {
			zoomLevel.value = next;
		}
	}

	function setReferenceImageFile(file) {
		if (!file) return;
		const reader = new FileReader();
		reader.onload = (event) => {
			referenceImage.value = {
				name: file.name,
				url: event.target.result,
				base64: event.target.result,
			};
		};
		reader.readAsDataURL(file);
	}

	function clearReferenceImage() {
		referenceImage.value = null;
	}

	function appendImages(images, promptText) {
		const created = images
			.filter((item) => item?.url)
			.map((item) => ({
				id: item.id,
				url: item.url,
				prompt: item.revisedPrompt || promptText,
				createdAt: new Date().toISOString(),
			}));

		// unshift 一个数组保持宿主返回的顺序；逐条 unshift 会把整批结果倒过来。
		gallery.unshift(...created);
		if (gallery.length > GALLERY_LIMIT) {
			gallery.splice(GALLERY_LIMIT);
		}
		persistGallery();
		return created.length;
	}

	async function cancel() {
		if (!isGenerating.value || !activeJobId.value) return;
		cancelling.value = true;
		statusMessage.value = '正在取消…';
		post({ type: 'image-generation/cancel', jobId: activeJobId.value });
	}

	async function generate() {
		if (isGenerating.value) return;

		const promptText = prompt.value.trim();
		if (!promptText) {
			errorMessage.value = '请输入你想生成的图片描述';
			return;
		}

		if (!currentModel.value) {
			errorMessage.value = '尚未配置生图模型，请先在提供商设置中添加 OpenAI Images 协议的模型';
			return;
		}

		errorMessage.value = '';
		statusMessage.value = '正在生成图片...';
		isGenerating.value = true;
		cancelling.value = false;
		activeJobId.value = `image-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

		try {
			const payload = {
				jobId: activeJobId.value,
				prompt: promptText,
				modelProfileId: selectedModelProfileId.value || undefined,
				count: Math.min(count.value, maxCount.value),
				size: currentSize.value?.value,
			};

			const response = referenceImage.value?.base64
				? await request(
					'image-generation/edit',
					{ ...payload, imageBase64: referenceImage.value.base64 },
					{ timeout: IMAGE_REQUEST_TIMEOUT_MS },
				)
				: await request(
					'image-generation/generate',
					{ ...payload, quality: currentQuality.value, style: currentStyle.value || undefined },
					{ timeout: IMAGE_REQUEST_TIMEOUT_MS },
				);

			if (response?.cancelled) {
				statusMessage.value = '已取消本次生成';
				return;
			}

			if (!response?.ok || !Array.isArray(response.images)) {
				throw new Error(response?.error || '生成失败，请检查模型与网络');
			}

			const added = appendImages(response.images, promptText);
			statusMessage.value = added > 0
				? `生成完成！耗时 ${((response.durationMs || 0) / 1000).toFixed(1)} 秒`
				: '模型没有返回任何图片，请稍后重试';
		} catch (error) {
			errorMessage.value = error?.message || '生成图片失败，请检查生图模型配置';
			console.error('[ImageStudio] Generate failed:', error);
		} finally {
			isGenerating.value = false;
			cancelling.value = false;
			activeJobId.value = '';
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
		capabilities,
		sizeOptions,
		qualityOptions,
		styleOptions,
		maxCount,
		prompt,
		selectedSize,
		count,
		quality,
		style,
		referenceImage,
		isGenerating,
		cancelling,
		zoomLevel,
		gallery,
		statusMessage,
		errorMessage,
		loadModels,
		appendTag,
		resetParams,
		removeImage,
		clearGallery,
		setZoom,
		setReferenceImageFile,
		clearReferenceImage,
		generate,
		cancel,
	};
}
