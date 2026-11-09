import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { defineComponent, h } from 'vue';
import { describeSize, describeSizes, formatGeneratedAt } from '../src/renderers/imageStudio.js';

const posted = [];
let deliver;
let wrapper;

const MODEL = {
	profileId: 'profile-1',
	providerName: 'OpenAI',
	name: 'DALL·E 3',
	model: 'dall-e-3',
	description: '高质量生图模型',
	capabilities: {
		sizes: ['1024x1024', '1024x1792', '1792x1024'],
		qualities: ['standard', 'hd'],
		styles: ['vivid', 'natural'],
		maxCount: 1,
	},
};

const GPT_MODEL = {
	profileId: 'profile-2',
	providerName: 'OpenAI',
	name: 'GPT Image 1',
	model: 'gpt-image-1',
	description: '新一代生图模型',
	capabilities: {
		sizes: ['1024x1024', '1024x1536', '1536x1024'],
		qualities: ['auto', 'low', 'medium', 'high'],
		styles: [],
		maxCount: 10,
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
			.toEqual(['1024x1024', '1024x1792', '1792x1024']);
		expect(studio.qualityOptions.value).toEqual(['standard', 'hd']);
		expect(studio.styleOptions.value).toEqual(['vivid', 'natural']);
		expect(studio.maxCount.value).toBe(1);
		expect(studio.selectedSize.value).toBe('1024x1024');
		expect(studio.quality.value).toBe('standard');
		expect(studio.style.value).toBe('vivid');
	});

	it('re-clamps the knobs when switching to a model with different capabilities', async () => {
		const studio = await mountStudio([MODEL, GPT_MODEL]);

		studio.selectedSize.value = '1792x1024';
		studio.quality.value = 'hd';
		studio.style.value = 'natural';
		studio.count.value = 1;

		studio.selectedModelProfileId.value = GPT_MODEL.profileId;
		await settle();

		expect(studio.selectedSize.value).toBe('1024x1024');
		expect(studio.quality.value).toBe('auto');
		expect(studio.style.value).toBe('');
		expect(studio.maxCount.value).toBe(10);
	});

	it('appends tags with comma separation and ignores duplicates', async () => {
		const studio = await mountStudio();

		studio.appendTag('写实');
		expect(studio.prompt.value).toBe('写实');

		studio.appendTag('二次元');
		expect(studio.prompt.value).toBe('写实, 二次元');

		studio.appendTag('写实');
		expect(studio.prompt.value).toBe('写实, 二次元');
	});

	it('resets parameters back to the model defaults', async () => {
		const studio = await mountStudio([MODEL]);

		studio.prompt.value = 'a futuristic city';
		studio.selectedSize.value = '1792x1024';
		studio.quality.value = 'hd';
		studio.setZoom(50);

		studio.resetParams();

		expect(studio.prompt.value).toBe('');
		expect(studio.selectedSize.value).toBe('1024x1024');
		expect(studio.quality.value).toBe('standard');
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
		expect(request.size).toBe('1024x1024');
		expect(request.quality).toBe('standard');
		expect(request.style).toBe('vivid');
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
});

describe('imageStudio renderers', () => {
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
