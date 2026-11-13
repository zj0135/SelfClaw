import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { defineComponent, h } from 'vue';
import {
	describeSize,
	describeSizes,
	validateCustomSize,
	formatGeneratedAt,
	DEFAULT_MAX_PROMPT_LENGTH,
} from '../src/renderers/imageStudio.js';

const posted = [];
let deliver;
let wrapper;

const GPT_SIZES = [
	'auto', '1024x1024', '1536x1024', '1024x1536',
	'1536x864', '864x1536', '1024x768', '768x1024', '1536x768', '768x1536',
];

const CUSTOM_SIZE_RULES = {
	multipleOf: 16,
	minAspectRatio: 1,
	maxAspectRatio: 3,
	maxWidth: 3840,
	maxHeight: 2160,
};

// 主路径：gpt-image 家族的增强能力集（整个家族统一，不按 2.5/2.6 细分）。
const MODEL = {
	profileId: 'profile-1',
	providerName: 'OpenAI',
	name: 'GPT Image 2.6 Sunburst',
	model: 'gpt-image-2.6-sunburst',
	description: 'GPT Image 家族生图模型',
	capabilities: {
		sizes: GPT_SIZES,
		qualities: ['auto', 'low', 'medium', 'high', 'xhigh', 'max'],
		styles: [],
		maxCount: 10,
		maxPromptLength: 32000,
		backgrounds: ['auto', 'transparent', 'opaque'],
		outputFormats: ['png', 'jpeg', 'webp'],
		moderations: ['auto', 'low'],
		supportsOutputCompression: true,
		customSize: CUSTOM_SIZE_RULES,
	},
};

// 网关兜底：未识别的模型 id 由宿主下发保守的旧参数集，prompt 上限默认 1000。
const GATEWAY_MODEL = {
	profileId: 'profile-2',
	providerName: 'Gateway',
	name: 'Legacy Image Gateway',
	model: 'acme/legacy-image',
	description: '第三方生图网关',
	capabilities: {
		sizes: ['1024x1024', '1024x1536', '1536x1024', '1024x1792', '1792x1024', '512x512', '256x256'],
		qualities: ['auto', 'low', 'medium', 'high', 'standard', 'hd'],
		styles: ['vivid', 'natural'],
		maxCount: 10,
		maxPromptLength: DEFAULT_MAX_PROMPT_LENGTH,
		backgrounds: [],
		outputFormats: [],
		moderations: [],
		supportsOutputCompression: false,
		customSize: null,
	},
};

beforeEach(() => {
	posted.length = 0;
	localStorage.clear();
	vi.resetModules();

	const listeners = new Set();
	window.chrome = {
		webview: {
			addEventListener(type, listener) {
				if (type === 'message') listeners.add(listener);
			},
			postMessage(message) {
				posted.push(message);
			},
		},
	};
	deliver = (payload) => {
		for (const listener of listeners) listener({ data: payload });
	};
});

afterEach(() => {
	wrapper?.unmount();
	wrapper = null;
	delete window.chrome;
});

function lastRequest(type) {
	return [...posted].reverse().find((message) => message.type === type);
}

function settle() {
	return new Promise((resolve) => setTimeout(resolve, 0));
}

async function respond(request, payload) {
	deliver({ type: request.type, requestId: request.requestId, ...payload });
	await settle();
}

async function mountStudio(models = [MODEL]) {
	const { useImageStudio } = await import('../src/composables/useImageStudio.js');
	let api;
	const Harness = defineComponent({
		setup() {
			api = useImageStudio();
			return () => h('div');
		},
	});
	wrapper = mount(Harness);

	const request = lastRequest('image-generation/list-models');
	expect(request).toBeTruthy();
	await respond(request, { ok: true, models });
	return api;
}

describe('useImageStudio', () => {
	it('starts from a real empty workbench: no sample images, no fabricated model', async () => {
		const studio = await mountStudio([]);

		expect(studio.gallery).toHaveLength(0);
		expect(studio.models.value).toHaveLength(0);
		expect(studio.currentModel.value).toBeNull();
		expect(studio.sizeOptions.value).toHaveLength(0);
		expect(studio.maxCount.value).toBe(1);
	});

	it('only asks the host for image models, never for the chat model catalogue', async () => {
		await mountStudio();

		expect(posted.some((message) => message.type === 'ai-providers/get-state')).toBe(false);
	});

	it('derives size, quality and style controls from the selected model capabilities', async () => {
		const studio = await mountStudio([MODEL]);

		expect(studio.sizeOptions.value.map((option) => option.value))
			.toEqual(GPT_SIZES);
		expect(studio.qualityOptions.value).toEqual(['auto', 'low', 'medium', 'high', 'xhigh', 'max']);
		expect(studio.styleOptions.value).toEqual([]);
		expect(studio.backgroundOptions.value).toEqual(['auto', 'transparent', 'opaque']);
		expect(studio.outputFormatOptions.value).toEqual(['png', 'jpeg', 'webp']);
		expect(studio.moderationOptions.value).toEqual(['auto', 'low']);
		expect(studio.supportsOutputCompression.value).toBe(true);
		expect(studio.customSizeRules.value).toEqual(CUSTOM_SIZE_RULES);
		expect(studio.maxPromptLength.value).toBe(32000);
		expect(studio.maxCount.value).toBe(10);
		expect(studio.selectedSize.value).toBe('auto');
		expect(studio.quality.value).toBe('auto');
		expect(studio.style.value).toBe('');
		expect(studio.background.value).toBe('auto');
		expect(studio.outputFormat.value).toBe('png');
		expect(studio.moderation.value).toBe('auto');
	});

	it('re-clamps the knobs when switching to a model with different capabilities', async () => {
		const studio = await mountStudio([MODEL, GATEWAY_MODEL]);

		studio.selectedSize.value = '1536x1024';
		studio.quality.value = 'high';
		studio.count.value = 4;

		studio.selectedModelProfileId.value = GATEWAY_MODEL.profileId;
		await settle();

		// 收敛回新模型能力集的首项，且 style 从无到有也要拿到默认值。
		expect(studio.selectedSize.value).toBe('1024x1024');
		expect(studio.quality.value).toBe('auto');
		expect(studio.style.value).toBe('vivid');
		expect(studio.styleOptions.value).toEqual(['vivid', 'natural']);
		expect(studio.maxCount.value).toBe(10);

		studio.selectedSize.value = '1792x1024';
		studio.quality.value = 'hd';
		studio.style.value = 'natural';

		studio.selectedModelProfileId.value = MODEL.profileId;
		await settle();

		// 切回 gpt-image：1792x1024 / hd / natural 全部失效，风格控件整体消失，
		// 且新维度重新拿到默认值。
		expect(studio.selectedSize.value).toBe('auto');
		expect(studio.quality.value).toBe('auto');
		expect(studio.style.value).toBe('');
		expect(studio.background.value).toBe('auto');
		expect(studio.outputFormat.value).toBe('png');
		expect(studio.supportsOutputCompression.value).toBe(true);
		expect(studio.customSizeRules.value).toEqual(CUSTOM_SIZE_RULES);
		expect(studio.maxPromptLength.value).toBe(32000);
		expect(studio.sizeOptions.value.map((option) => option.value))
			.toEqual(GPT_SIZES);
	});

	it('resets parameters back to the model defaults', async () => {
		const studio = await mountStudio([MODEL]);

		studio.prompt.value = 'a futuristic city';
		studio.selectedSize.value = '1536x1024';
		studio.quality.value = 'high';
		studio.setZoom(50);

		studio.resetParams();

		expect(studio.prompt.value).toBe('');
		expect(studio.selectedSize.value).toBe('auto');
		expect(studio.quality.value).toBe('auto');
		expect(studio.zoomLevel.value).toBe(100);
	});

	it('sends a long-running generate request with a job id and no model override', async () => {
		const studio = await mountStudio([MODEL]);
		studio.prompt.value = 'a cat';
		studio.count.value = 1;

		const generating = studio.generate();
		await settle();

		const request = lastRequest('image-generation/generate');
		expect(request).toBeTruthy();
		expect(request.prompt).toBe('a cat');
		expect(request.size).toBe('auto');
		expect(request.quality).toBe('auto');
		expect(request.background).toBe('auto');
		expect(request.outputFormat).toBe('png');
		expect(request.moderation).toBe('auto');
		// png 不产生 output_compression；能力里 styles 为空时 style 也不应出现在 payload 中。
		expect(request.outputCompression).toBeUndefined();
		expect(request.style).toBeUndefined();
		expect(request.modelProfileId).toBe(MODEL.profileId);
		expect(request.jobId).toMatch(/^image-/);
		// 模型 id 由宿主按 profile 决定，渲染层不再二次指定。
		expect(request).not.toHaveProperty('model');

		await respond(request, {
			ok: true,
			durationMs: 1200,
			images: [
				{ id: 'img-a', url: 'https://generated-images.selfclaw.local/a.png', revisedPrompt: 'cat A' },
				{ id: 'img-b', url: 'https://generated-images.selfclaw.local/b.png', revisedPrompt: null },
			],
		});
		await generating;

		expect(studio.gallery.map((item) => item.id)).toEqual(['img-a', 'img-b']);
		expect(studio.gallery[0].prompt).toBe('cat A');
		expect(studio.gallery[1].prompt).toBe('a cat');
		expect(studio.statusMessage.value).toContain('1.2 秒');
		expect(studio.isGenerating.value).toBe(false);
	});

	it('persists generated results so switching views does not lose them', async () => {
		const studio = await mountStudio([MODEL]);
		studio.prompt.value = 'a cat';

		const generating = studio.generate();
		await settle();
		await respond(lastRequest('image-generation/generate'), {
			ok: true,
			images: [{ id: 'img-a', url: 'https://generated-images.selfclaw.local/a.png' }],
		});
		await generating;

		expect(JSON.parse(localStorage.getItem('selfclaw:image-studio:gallery'))).toHaveLength(1);

		// 重新挂载相当于切走视图再切回来。
		wrapper.unmount();
		const remounted = await mountStudio([MODEL]);
		expect(remounted.gallery).toHaveLength(1);
		expect(remounted.gallery[0].url).toBe('https://generated-images.selfclaw.local/a.png');
	});

	it('cancels an in-flight job through the host', async () => {
		const studio = await mountStudio([MODEL]);
		studio.prompt.value = 'a cat';

		const generating = studio.generate();
		await settle();

		const generateRequest = lastRequest('image-generation/generate');
		studio.cancel();
		await settle();

		const cancelRequest = lastRequest('image-generation/cancel');
		expect(cancelRequest.jobId).toBe(generateRequest.jobId);
		expect(studio.cancelling.value).toBe(true);

		await respond(generateRequest, { ok: false, cancelled: true });
		await generating;

		expect(studio.statusMessage.value).toBe('已取消本次生成');
		expect(studio.gallery).toHaveLength(0);
		expect(studio.isGenerating.value).toBe(false);
	});

	it('surfaces host failures as a readable error without adding gallery entries', async () => {
		const studio = await mountStudio([MODEL]);
		studio.prompt.value = 'a cat';

		const generating = studio.generate();
		await settle();
		await respond(lastRequest('image-generation/generate'), {
			ok: false,
			error: '生图配置有误：模型已禁用',
		});
		await generating;

		expect(studio.errorMessage.value).toBe('生图配置有误：模型已禁用');
		expect(studio.gallery).toHaveLength(0);
	});

	it('refuses to generate without a model instead of sending a doomed request', async () => {
		const studio = await mountStudio([]);
		studio.prompt.value = 'a cat';

		await studio.generate();

		expect(lastRequest('image-generation/generate')).toBeUndefined();
		expect(studio.errorMessage.value).toContain('尚未配置生图模型');
	});

	it('takes the prompt limit from capabilities and falls back to 1000 for gateways', async () => {
		const studio = await mountStudio([MODEL, GATEWAY_MODEL]);
		expect(studio.maxPromptLength.value).toBe(32000);

		studio.selectedModelProfileId.value = GATEWAY_MODEL.profileId;
		await settle();
		expect(studio.maxPromptLength.value).toBe(DEFAULT_MAX_PROMPT_LENGTH);
	});

	it('truncates a long prompt when switching to a model with a smaller limit', async () => {
		const studio = await mountStudio([MODEL, GATEWAY_MODEL]);
		studio.prompt.value = 'a'.repeat(2000);

		studio.selectedModelProfileId.value = GATEWAY_MODEL.profileId;
		await settle();

		expect(studio.prompt.value).toHaveLength(1000);
	});

	it('blocks generation before dispatch when the prompt exceeds the current limit', async () => {
		const studio = await mountStudio([GATEWAY_MODEL]);
		studio.prompt.value = 'a'.repeat(1001);

		await studio.generate();

		expect(lastRequest('image-generation/generate')).toBeUndefined();
		expect(studio.errorMessage.value).toContain('1000');
	});

	it('sends the enhanced knobs and only compresses non-png outputs', async () => {
		const studio = await mountStudio([MODEL]);
		studio.prompt.value = 'a cat';
		studio.background.value = 'transparent';
		studio.outputFormat.value = 'webp';
		studio.outputCompression.value = 42;
		studio.moderation.value = 'low';
		studio.quality.value = 'xhigh';

		const generating = studio.generate();
		await settle();
		const request = lastRequest('image-generation/generate');
		expect(request.background).toBe('transparent');
		expect(request.outputFormat).toBe('webp');
		expect(request.outputCompression).toBe(42);
		expect(request.moderation).toBe('low');
		expect(request.quality).toBe('xhigh');

		await respond(request, { ok: true, images: [] });
		await generating;
	});

	it('dispatches to image-generation/edit when a reference image is set', async () => {
		const studio = await mountStudio([MODEL]);
		studio.prompt.value = 'turn this cat into a dog';
		studio.referenceImage.value = {
			name: 'cat.png',
			url: 'data:image/png;base64,AAAA',
			base64: 'data:image/png;base64,AAAA',
		};

		const generating = studio.generate();
		await settle();
		const editRequest = lastRequest('image-generation/edit');
		expect(editRequest).toBeDefined();
		expect(editRequest.imageBase64).toBe('data:image/png;base64,AAAA');
		expect(editRequest.prompt).toBe('turn this cat into a dog');
		expect(editRequest.style).toBeUndefined();
		expect(editRequest.moderation).toBeUndefined();

		await respond(editRequest, {
			ok: true,
			images: [{ id: 'img-edit', url: 'https://generated-images.selfclaw.local/edit.png' }],
		});
		await generating;

		expect(studio.gallery).toHaveLength(1);
		expect(studio.gallery[0].id).toBe('img-edit');
	});

	it('setReferenceFromImage loads a gallery image into referenceImage and seeds empty prompt', async () => {
		const studio = await mountStudio([MODEL]);
		expect(studio.referenceImage.value).toBeNull();
		expect(studio.prompt.value).toBe('');

		await studio.setReferenceFromImage({
			id: 'sample-1',
			url: 'data:image/png;base64,ZZZZ',
			prompt: 'a futuristic city skyline',
		});

		expect(studio.referenceImage.value).toMatchObject({
			url: 'data:image/png;base64,ZZZZ',
			base64: 'data:image/png;base64,ZZZZ',
		});
		expect(studio.referenceImage.value.name).toContain('futuristic');
		expect(studio.prompt.value).toBe('a futuristic city skyline');
		expect(studio.statusMessage.value).toBe('已将图片设为参考图');
	});

	it('encodes a validated custom size in the request', async () => {
		const studio = await mountStudio([MODEL]);
		studio.prompt.value = 'a cat';
		studio.selectedSize.value = 'custom';
		studio.customWidth.value = 3840;
		studio.customHeight.value = 2160;

		const generating = studio.generate();
		await settle();
		const request = lastRequest('image-generation/generate');
		expect(request.size).toBe('3840x2160');

		await respond(request, { ok: true, images: [] });
		await generating;
	});

	it('refuses an invalid custom size before dispatching', async () => {
		const studio = await mountStudio([MODEL]);
		studio.prompt.value = 'a cat';
		studio.selectedSize.value = 'custom';
		studio.customWidth.value = 1000; // not a multiple of 16
		studio.customHeight.value = 1024;

		await studio.generate();

		expect(lastRequest('image-generation/generate')).toBeUndefined();
		expect(studio.customSizeError.value).toContain('16 的倍数');
	});

	it('switches jpeg back to png when the background becomes transparent', async () => {
		const studio = await mountStudio([MODEL]);
		studio.outputFormat.value = 'jpeg';
		studio.background.value = 'transparent';
		await settle();

		expect(studio.outputFormat.value).toBe('png');
	});
});

describe('imageStudio renderers', () => {
	it('describes auto as a readable card', () => {
		expect(describeSize('auto')).toMatchObject({ ratio: '自动', desc: '自动', isAuto: true });
	});

	it('labels the new aspect-ratio presets', () => {
		expect(describeSize('1536x864')).toMatchObject({ ratio: '16:9', orientation: 'landscape' });
		expect(describeSize('864x1536')).toMatchObject({ ratio: '9:16', orientation: 'portrait' });
		expect(describeSize('1024x768')).toMatchObject({ ratio: '4:3', orientation: 'landscape' });
		expect(describeSize('768x1024')).toMatchObject({ ratio: '3:4', orientation: 'portrait' });
		expect(describeSize('1536x768')).toMatchObject({ ratio: '2:1' });
		expect(describeSize('768x1536')).toMatchObject({ ratio: '1:2' });
	});

	it('validates custom sizes with the same rules as the host', () => {
		expect(validateCustomSize(3840, 2160, CUSTOM_SIZE_RULES)).toBe('');
		expect(validateCustomSize(1000, 1024, CUSTOM_SIZE_RULES)).toContain('16 的倍数');
		expect(validateCustomSize(4096, 1024, CUSTOM_SIZE_RULES)).toContain('上限');
		expect(validateCustomSize(512, 2048, CUSTOM_SIZE_RULES)).toContain('宽高比');
		expect(validateCustomSize(1024, 1024, null)).toContain('不支持自定义尺寸');
	});

	it('turns a wire size into a readable ratio card', () => {
		expect(describeSize('1024x1536')).toMatchObject({ ratio: '2:3', orientation: 'portrait', desc: '1024 × 1536' });
		expect(describeSize('1792x1024')).toMatchObject({ ratio: '7:4', orientation: 'landscape' });
		expect(describeSize('1024x1024')).toMatchObject({ ratio: '1:1', orientation: 'square' });
		expect(describeSize('')).toMatchObject({ ratio: '', orientation: 'square' });
	});

	it('maps every capability entry without dropping the raw value', () => {
		expect(describeSizes(['512x512'])).toEqual([
			{ value: '512x512', ratio: '1:1', orientation: 'square', desc: '512 × 512' },
		]);
		expect(describeSizes(undefined)).toEqual([]);
	});

	it('renders an empty label for an unparseable timestamp', () => {
		expect(formatGeneratedAt('not-a-date')).toBe('');
		expect(formatGeneratedAt(new Date().toISOString())).toContain('今天');
	});
});
