import { computed, onMounted, reactive, ref, watch } from 'vue';
import { useHostBridge } from './hostBridge.js';
import { describeSizes, validateCustomSize, DEFAULT_MAX_PROMPT_LENGTH } from '../renderers/imageStudio.js';

// 生图是单次长请求，不是流式回合：默认 30 秒的请求超时对 1536×1024 的渲染根本不够，
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

// 能力数组的首项即该模型的默认值；能力缺失时返回空串，宿主会按模型默认值补齐。
function defaultOf(list) {
	return Array.isArray(list) && list.length ? list[0] : '';
}

export function useImageStudio() {
	const { request, post } = useHostBridge();

	const models = ref([]);
	const loadingModels = ref(false);
	const selectedModelProfileId = ref('');
	const prompt = ref('');
	const selectedSize = ref('');
	const customWidth = ref(1024);
	const customHeight = ref(1024);
	const customSizeError = ref('');
	const count = ref(1);
	const quality = ref('');
	const style = ref('');
	const background = ref('');
	const outputFormat = ref('');
	const outputCompression = ref(80);
	const moderation = ref('');
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
	const backgroundOptions = computed(() => capabilities.value?.backgrounds || []);
	const outputFormatOptions = computed(() => capabilities.value?.outputFormats || []);
	const moderationOptions = computed(() => capabilities.value?.moderations || []);
	const supportsOutputCompression = computed(() => Boolean(capabilities.value?.supportsOutputCompression));
	const customSizeRules = computed(() => capabilities.value?.customSize || null);
	const maxCount = computed(() => capabilities.value?.maxCount || 1);
	const maxPromptLength = computed(() => capabilities.value?.maxPromptLength || DEFAULT_MAX_PROMPT_LENGTH);

	const currentSize = computed(() => {
		const found = sizeOptions.value.find((option) => option.value === selectedSize.value);
		return found || sizeOptions.value[0] || null;
	});

	const currentQuality = computed(() => {
		const found = qualityOptions.value.find((value) => value === quality.value);
		return found || defaultOf(qualityOptions.value);
	});

	const currentStyle = computed(() => {
		const found = styleOptions.value.find((value) => value === style.value);
		return found || defaultOf(styleOptions.value);
	});

	const currentBackground = computed(() => {
		const found = backgroundOptions.value.find((value) => value === background.value);
		return found || defaultOf(backgroundOptions.value);
	});

	const currentOutputFormat = computed(() => {
		const found = outputFormatOptions.value.find((value) => value === outputFormat.value);
		return found || defaultOf(outputFormatOptions.value);
	});

	const currentModeration = computed(() => {
		const found = moderationOptions.value.find((value) => value === moderation.value);
		return found || defaultOf(moderationOptions.value);
	});

	// 换模型会让原来的尺寸/画质/风格/数量变成非法组合（例如切到不带 style 的模型），
	// 所以模型一变就地把选择收敛回新模型的能力范围，避免下一次点击必然 400。
	watch(currentModel, () => {
		selectedSize.value = defaultOf(sizeOptions.value.map((option) => option.value));
		quality.value = defaultOf(qualityOptions.value);
		style.value = defaultOf(styleOptions.value);
		background.value = defaultOf(backgroundOptions.value);
		outputFormat.value = defaultOf(outputFormatOptions.value);
		moderation.value = defaultOf(moderationOptions.value);
		count.value = Math.min(count.value, maxCount.value);
		customSizeError.value = '';
		// 上限可能变小，超出部分直接裁掉，不能让宿主在提交时才报错。
		if (prompt.value.length > maxPromptLength.value) {
			prompt.value = prompt.value.slice(0, maxPromptLength.value);
		}
	});

	// JPEG 没有 alpha 通道：背景切到透明时把输出格式收敛回 png，
	// 与 ImageAdvancedOptions 里摘掉 jpeg 选项、以及宿主的二次校验保持一致。
	watch(background, (value) => {
		if (value === 'transparent' && outputFormat.value === 'jpeg') {
			outputFormat.value = 'png';
		}
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

	function resetParams() {
		prompt.value = '';
		selectedSize.value = defaultOf(sizeOptions.value.map((option) => option.value));
		customWidth.value = 1024;
		customHeight.value = 1024;
		customSizeError.value = '';
		count.value = 1;
		quality.value = defaultOf(qualityOptions.value);
		style.value = defaultOf(styleOptions.value);
		background.value = defaultOf(backgroundOptions.value);
		outputFormat.value = defaultOf(outputFormatOptions.value);
		outputCompression.value = 80;
		moderation.value = defaultOf(moderationOptions.value);
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

	async function urlToBase64(url) {
		if (!url || typeof fetch !== 'function') return '';
		if (url.startsWith('data:')) return url;
		try {
			const res = await fetch(url);
			if (!res.ok) return '';
			const blob = await res.blob();
			return await new Promise((resolve, reject) => {
				const reader = new FileReader();
				reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : '');
				reader.onerror = reject;
				reader.readAsDataURL(blob);
			});
		} catch (error) {
			console.warn('[ImageStudio] Failed to load image as base64:', error);
			return '';
		}
	}

	async function setReferenceFromImage(image) {
		if (!image?.url) return;
		const name = image.name || (image.prompt ? `${image.prompt.slice(0, 30).trim()}.png` : '生成图.png');
		const directBase64 = image.base64 || (image.url.startsWith('data:') ? image.url : '');
		referenceImage.value = {
			name,
			url: image.url,
			base64: directBase64,
		};
		if (!prompt.value.trim() && image.prompt) {
			prompt.value = image.prompt;
		}
		statusMessage.value = '已将图片设为参考图';
		errorMessage.value = '';

		if (!directBase64) {
			const loaded = await urlToBase64(image.url);
			if (referenceImage.value?.url === image.url && loaded) {
				referenceImage.value.base64 = loaded;
			}
		}
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

		if (promptText.length > maxPromptLength.value) {
			errorMessage.value = `提示词不能超过 ${maxPromptLength.value} 字符`;
			return;
		}

		// 自定义尺寸由前端先校验一遍，宿主仍会按同一规则再校验（不信任渲染层）。
		if (customSizeRules.value && selectedSize.value === 'custom') {
			const invalid = validateCustomSize(customWidth.value, customHeight.value, customSizeRules.value);
			if (invalid) {
				customSizeError.value = invalid;
				return;
			}
		}
		customSizeError.value = '';

		errorMessage.value = '';
		statusMessage.value = '正在生成图片...';
		isGenerating.value = true;
		cancelling.value = false;
		activeJobId.value = `image-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

		try {
			if (referenceImage.value && !referenceImage.value.base64) {
				const converted = await urlToBase64(referenceImage.value.url);
				if (converted) {
					referenceImage.value.base64 = converted;
				} else {
					errorMessage.value = '参考图加载失败，请重新选择参考图';
					return;
				}
			}

			const resolvedSize = customSizeRules.value && selectedSize.value === 'custom'
				? `${customWidth.value}x${customHeight.value}`
				: currentSize.value?.value;
			const payload = {
				jobId: activeJobId.value,
				prompt: promptText,
				modelProfileId: selectedModelProfileId.value || undefined,
				count: Math.min(count.value, maxCount.value),
				size: resolvedSize,
				quality: currentQuality.value || undefined,
				style: currentStyle.value || undefined,
				background: currentBackground.value || undefined,
				outputFormat: currentOutputFormat.value || undefined,
				outputCompression: supportsOutputCompression.value
					&& currentOutputFormat.value
					&& currentOutputFormat.value !== 'png'
					? outputCompression.value
					: undefined,
				moderation: currentModeration.value || undefined,
			};

			const response = referenceImage.value?.base64
				? await request(
					'image-generation/edit',
					{ ...payload, imageBase64: referenceImage.value.base64, style: undefined, moderation: undefined },
					{ timeout: IMAGE_REQUEST_TIMEOUT_MS },
				)
				: await request(
					'image-generation/generate',
					payload,
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
		backgroundOptions,
		outputFormatOptions,
		moderationOptions,
		supportsOutputCompression,
		customSizeRules,
		maxCount,
		maxPromptLength,
		prompt,
		selectedSize,
		customWidth,
		customHeight,
		customSizeError,
		count,
		quality,
		style,
		background,
		outputFormat,
		outputCompression,
		moderation,
		referenceImage,
		isGenerating,
		cancelling,
		zoomLevel,
		gallery,
		statusMessage,
		errorMessage,
		loadModels,
		resetParams,
		removeImage,
		clearGallery,
		setZoom,
		setReferenceImageFile,
		setReferenceFromImage,
		clearReferenceImage,
		generate,
		cancel,
	};
}
