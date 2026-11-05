import { afterEach, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';

const { bridge } = vi.hoisted(() => ({
	bridge: {
		request: vi.fn(),
		requestLatest: vi.fn(),
		on: vi.fn(() => () => {}),
		post: vi.fn(),
		hasHost: () => true,
	},
}));

vi.mock('../src/composables/hostBridge.js', () => ({
	hostBridge: bridge,
	useHostBridge: () => bridge,
	isSuperseded: () => false,
}));

import { useModelConfigurationEditor } from '../src/composables/useModelConfigurationEditor.js';
import ModelParameterFields from '../src/components/settings/models/ModelParameterFields.vue';

afterEach(() => {
	bridge.request.mockReset();
	bridge.requestLatest.mockReset();
});

it('initializes apiFormat and updates both profile and configuration on save', async () => {
	const model = {
		profileId: 'prof-123',
		providerConnectionId: 'prov-456',
		model: 'gpt-6.1-sol',
		name: 'GPT 6.1 Sol',
		apiFormat: 1, // OpenAIResponses
		configuration: null,
	};
	const onSaved = vi.fn();
	const editor = useModelConfigurationEditor(model, onSaved);

	expect(editor.draft.hasProfile).toBe(true);
	expect(editor.draft.apiFormat).toBe(1);

	// Change format to OpenAI Chat Completions (0)
	editor.draft.apiFormat = 0;

	bridge.request.mockImplementation((type, payload) => {
		if (type === 'ai-providers/upsert-model') {
			return Promise.resolve({ ok: true });
		}
		if (type === 'ai-providers/save-model-configuration') {
			return Promise.resolve({ configuration: payload.configuration });
		}
		return Promise.resolve({});
	});

	await editor.save();

	expect(bridge.request).toHaveBeenCalledWith('ai-providers/upsert-model', {
		id: 'prof-123',
		providerConnectionId: 'prov-456',
		name: 'GPT 6.1 Sol',
		model: 'gpt-6.1-sol',
		apiFormat: 0,
	});
	expect(bridge.request).toHaveBeenCalledWith('ai-providers/save-model-configuration', expect.any(Object));
	expect(model.apiFormat).toBe(0);
	expect(onSaved).toHaveBeenCalled();
});

it('renders apiFormat selector and collapses advanced sampling by default', async () => {
	const draft = {
		model: 'gpt-6.1-sol',
		name: 'GPT 6.1 Sol',
		apiFormat: 1,
		hasProfile: true,
		reasoningEffort: '',
		isMultimodal: null,
		contextLength: 1048576,
		maxOutputTokens: 65536,
		sampling: {
			temperatureEnabled: false,
			temperature: 0.7,
			topPEnabled: false,
			topP: 0.7,
		},
	};

	const provider = {
		supportedFormats: [0, 1],
	};

	const wrapper = mount(ModelParameterFields, {
		props: { draft, provider },
	});

	// API Format selector is rendered
	const formatSelect = wrapper.find('#param-api-format');
	expect(formatSelect.exists()).toBe(true);
	expect(formatSelect.findAll('option').length).toBe(2);

	// Advanced sampling is collapsed by default
	const samplingGrid = wrapper.find('.sampling-grid');
	expect(samplingGrid.attributes('style')).toContain('display: none');

	// Click accordion toggle to expand
	const toggle = wrapper.find('.accordion-toggle');
	await toggle.trigger('click');
	expect(samplingGrid.attributes('style')).not.toContain('display: none');

	// Change API format to 0
	await formatSelect.setValue(0);
	expect(draft.apiFormat).toBe(0);
});
