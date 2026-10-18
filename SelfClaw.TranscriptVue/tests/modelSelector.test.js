import { afterEach, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

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

import ModelSelector from '../src/components/Chat/ModelSelector.vue';

const mounted = [];
afterEach(() => {
	mounted.splice(0).forEach((wrapper) => wrapper.unmount());
	bridge.request.mockReset();
	bridge.requestLatest.mockReset();
});

it('renders thinking levels matching system model settings with Default as the first option', async () => {
	bridge.requestLatest.mockImplementation((key) => {
		if (key === 'composer-direct-models') {
			return Promise.resolve({
				defaultModelProfileId: 'profile-1',
				models: [
					{
						modelProfileId: 'profile-1',
						providerId: 'google',
						providerName: 'Google',
						model: 'gemini-2.5-pro',
						name: 'Gemini 2.5 Pro',
					},
				],
			});
		}
		if (key === 'composer-model-configs') {
			return Promise.resolve({
				configurations: [
					{
						model: 'gemini-2.5-pro',
						reasoningEffort: 'high',
					},
				],
			});
		}
		return Promise.resolve({});
	});

	const wrapper = mount(ModelSelector, {
		props: { executionMode: 'direct' },
	});
	mounted.push(wrapper);
	await flushPromises();

	// Open dropdown
	await wrapper.get('.composer-model').trigger('click');
	await flushPromises();

	// Check model row
	const modelRow = wrapper.get('.model-row');
	expect(modelRow.text()).toContain('Gemini 2.5 Pro');
	expect(modelRow.text()).toContain('High');

	// Hover over model row to trigger thinking flyout
	await modelRow.trigger('mouseenter');
	await flushPromises();

	const flyout = wrapper.get('.thinking-flyout');
	expect(flyout.isVisible()).toBe(true);

	const items = flyout.findAll('.thinking-item');
	const itemLabels = items.map((item) => item.text().trim());
	expect(itemLabels).toEqual([
		'Default',
		'None',
		'Minimal',
		'Low',
		'Medium',
		'High',
		'X-High',
		'Max',
	]);

	// Click 'X-High'
	const xhighBtn = items.find((item) => item.text().includes('X-High'));
	expect(xhighBtn).toBeTruthy();
	await xhighBtn.trigger('click');
	await flushPromises();

	expect(bridge.request).toHaveBeenCalledWith('ai-providers/save-model-configuration', {
		configuration: expect.objectContaining({
			model: 'gemini-2.5-pro',
			reasoningEffort: 'xhigh',
		}),
	});
});

it('anchors thinking flyout to the bottom when popover placement is up', async () => {
	bridge.requestLatest.mockImplementation((key) => {
		if (key === 'composer-direct-models') {
			return Promise.resolve({
				defaultModelProfileId: 'profile-1',
				models: [{ modelProfileId: 'profile-1', model: 'gemini-2.5-pro', name: 'Gemini 2.5 Pro' }],
			});
		}
		return Promise.resolve({});
	});

	const wrapper = mount(ModelSelector, {
		props: { executionMode: 'direct' },
	});
	mounted.push(wrapper);
	await flushPromises();

	await wrapper.get('.composer-model').trigger('click');
	await flushPromises();

	// Hover over model row with custom bounding box simulation
	const modelRow = wrapper.get('.model-row');
	const mockRowRect = { top: 500, bottom: 536 };
	const mockPopoverRect = { top: 400, bottom: 540, height: 140 };
	
	vi.spyOn(modelRow.element, 'getBoundingClientRect').mockReturnValue(mockRowRect);
	vi.spyOn(wrapper.get('.model-popover').element, 'getBoundingClientRect').mockReturnValue(mockPopoverRect);

	// Force placement to 'up'
	wrapper.vm.placement = 'up';
	await wrapper.vm.$nextTick();

	await modelRow.trigger('mouseenter');
	await flushPromises();

	const flyout = wrapper.get('.thinking-flyout');
	expect(flyout.attributes('style')).toContain('bottom:');
	expect(flyout.attributes('style')).not.toContain('top:');
});

