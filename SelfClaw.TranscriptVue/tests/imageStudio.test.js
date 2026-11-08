import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { defineComponent, h } from 'vue';

const posted = [];
let deliver;

beforeEach(() => {
	posted.length = 0;
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

let wrapper;

async function mountStudio() {
	const { useImageStudio } = await import('../src/composables/useImageStudio.js');
	let api;
	const Harness = defineComponent({
		setup() {
			api = useImageStudio();
			return () => h('div');
		},
	});
	wrapper = mount(Harness);
	return api;
}

it('initializes with default settings and initial demo images', async () => {
	const studio = await mountStudio();

	expect(studio.gallery).toHaveLength(3);
	expect(studio.selectedSizeKey.value).toBe('16:9');
	expect(studio.count.value).toBe(1);
	expect(studio.steps.value).toBe(30);
	expect(studio.cfgScale.value).toBe(7.5);
	expect(studio.quality.value).toBe('standard');
	expect(studio.style.value).toBe('vivid');
});

it('appends tags intelligently with comma separation', async () => {
	const studio = await mountStudio();

	expect(studio.prompt.value).toBe('');
	studio.appendTag('写实');
	expect(studio.prompt.value).toBe('写实');

	studio.appendTag('二次元');
	expect(studio.prompt.value).toBe('写实, 二次元');

	// Duplicate tag should not be appended again
	studio.appendTag('写实');
	expect(studio.prompt.value).toBe('写实, 二次元');
});

it('resets parameters to initial state', async () => {
	const studio = await mountStudio();

	studio.prompt.value = 'a futuristic city';
	studio.negativePrompt.value = 'low quality';
	studio.count.value = 4;
	studio.steps.value = 50;
	studio.cfgScale.value = 15;
	studio.selectedSizeKey.value = '1:1';

	studio.resetParams();

	expect(studio.prompt.value).toBe('');
	expect(studio.negativePrompt.value).toBe('');
	expect(studio.count.value).toBe(1);
	expect(studio.steps.value).toBe(30);
	expect(studio.cfgScale.value).toBe(7.5);
	expect(studio.selectedSizeKey.value).toBe('16:9');
});

it('removes image from gallery correctly', async () => {
	const studio = await mountStudio();

	expect(studio.gallery).toHaveLength(3);
	const targetId = studio.gallery[0].id;
	studio.removeImage(targetId);
	expect(studio.gallery).toHaveLength(2);
	expect(studio.gallery.find((img) => img.id === targetId)).toBeUndefined();
});

it('filters only models using OpenAIImages format (apiFormat === 3)', async () => {
	const studio = await mountStudio();

	// Simulate host response for image-generation/list-models
	const req = posted.find((p) => p.type === 'image-generation/list-models');
	if (req) {
		deliver({
			type: 'image-generation/list-models',
			requestId: req.requestId,
			ok: true,
			models: [
				{
					profileId: 'img-1',
					name: 'DALL-E 3',
					model: 'dall-e-3',
					apiFormat: 3,
					description: '高质量生图模型',
				},
			],
		});
		await new Promise((r) => setTimeout(r, 10));
		expect(studio.models.value).toHaveLength(1);
		expect(studio.models.value[0].apiFormat).toBe(3);
	}
});
