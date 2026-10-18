import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const { bridge } = vi.hoisted(() => ({
	bridge: {
		request: vi.fn(),
		requestLatest: vi.fn(() => Promise.resolve({ policy: { absoluteThresholdTokens: 64_000 } })),
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

import ComposerUsageMeter from '../src/components/Chat/ComposerUsageMeter.vue';

let wrapper;
afterEach(() => {
	wrapper?.unmount();
	wrapper = null;
});

const usage = (overrides = {}) => ({
	inputTokens: 1_240_000, outputTokens: 342_000, contextTokens: 12_400,
	contextWindowTokens: 32_000, contextIsEstimated: false, ...overrides,
});

function mountWithUsage(value) {
	wrapper = mount(ComposerUsageMeter, { props: { usage: value } });
	return wrapper;
}

describe('ComposerUsageMeter', () => {
	it('renders the circular progress ring and percentage', () => {
		mountWithUsage(usage());

		expect(wrapper.find('.usage-ring').exists()).toBe(true);
		expect(wrapper.get('.usage-pill-text').text()).toBe('39%');
	});

	it('calculates percentage against absolute threshold when context window is not declared', async () => {
		mountWithUsage(usage({ contextWindowTokens: null }));
		await flushPromises();

		// 12400 / 64000 = 19.375% -> 19%
		expect(wrapper.get('.usage-pill-text').text()).toBe('19%');
		expect(wrapper.get('.context-tag').text()).toBe('绝对阈值');
	});

	it('displays context and cumulative stats in hover popover', async () => {
		mountWithUsage(usage());

		expect(wrapper.get('.context-current').text()).toBe('12.4K');
		expect(wrapper.get('.context-limit').text()).toBe('32K');

		const totalVals = wrapper.findAll('.total-val').map((node) => node.text());
		expect(totalVals).toEqual(['1.24M', '342K', '1.58M']);
	});

	it('shows unavailable placeholder for unknown context without breaking', () => {
		mountWithUsage(usage({ contextTokens: null, contextWindowTokens: null }));

		expect(wrapper.get('.context-current').text()).toBe('不可用');
		expect(wrapper.get('.usage-pill-text').text()).toBe('0%');
	});

	it('marks an approximate context size with a tilde', () => {
		mountWithUsage(usage({ contextIsEstimated: true }));

		expect(wrapper.get('.context-current').text()).toBe('~12.4K');
		expect(wrapper.find('.usage-note').exists()).toBe(true);
	});

	it('renders nothing when usage is null', () => {
		mountWithUsage(null);
		expect(wrapper.find('.usage-pill').exists()).toBe(false);
	});
});
