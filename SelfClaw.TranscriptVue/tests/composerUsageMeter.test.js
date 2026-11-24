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

import ComposerUsageMeter from '../src/components/chat/composer/ComposerUsageMeter.vue';

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

	it('shows the cache hit rate next to the context size and no explanatory caption', async () => {
		mountWithUsage(usage({ cachedInputTokens: 1_200_000 }));

		expect(wrapper.get('.context-cache').text()).toBe('96.8%');
		// The caption line was removed: the window tag already says what the limit means.
		expect(wrapper.find('.context-caption').exists()).toBe(false);
		expect(wrapper.text()).not.toContain('按模型声明的上下文窗口计算');
	});

	it('omits the cache hit rate when no cached count was reported', () => {
		mountWithUsage(usage());

		expect(wrapper.find('.context-cache').exists()).toBe(false);
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

	describe('compaction trigger', () => {
		// 本机复现配置：窗口 1,048,576，比例 0.5，触发点 524,288，压缩后读数落在 25%。
		const triggered = (overrides = {}) => usage({
			contextTokens: 262_144, contextWindowTokens: 1_048_576, compactionTriggerTokens: 524_288, ...overrides,
		});

		function pushPolicy(policy) {
			const [, handler] = bridge.on.mock.calls.findLast(([type]) => type === 'direct-compression/state');
			handler({ policy });
		}

		afterEach(() => pushPolicy({ enabled: true }));

		it('marks the trigger on the ring and on the bar, with its size spelled out', () => {
			mountWithUsage(triggered());

			expect(wrapper.get('.usage-pill-text').text()).toBe('25%');
			// 50% 在环的正对面：svg 整体旋转了 -90°，这里是旋转前的坐标。
			expect(wrapper.get('.usage-ring-trigger').attributes()).toMatchObject({ x1: '4.7', y1: '10', x2: '0.3', y2: '10' });
			expect(wrapper.get('.progress-bar-trigger').attributes('style')).toContain('left: 50%');
			const row = wrapper.get('.trigger-row').text();
			expect(row).toContain('自动压缩');
			expect(row).toContain('524.29K');
			expect(row).toContain('50%');
			expect(wrapper.get('.usage-pill').attributes('title')).toContain('自动压缩 524.29K');
		});

		it('draws no mark while the host has not resolved a trigger', () => {
			mountWithUsage(triggered({ compactionTriggerTokens: null }));

			expect(wrapper.find('.usage-ring-trigger').exists()).toBe(false);
			expect(wrapper.find('.progress-bar-trigger').exists()).toBe(false);
			expect(wrapper.find('.trigger-row').exists()).toBe(false);
			expect(wrapper.get('.usage-pill').attributes('title')).not.toContain('自动压缩');
		});

		it('removes the mark as soon as compaction is switched off in settings', async () => {
			mountWithUsage(triggered());
			expect(wrapper.find('.usage-ring-trigger').exists()).toBe(true);

			pushPolicy({ enabled: false });
			await flushPromises();

			expect(wrapper.find('.usage-ring-trigger').exists()).toBe(false);
			expect(wrapper.find('.trigger-row').exists()).toBe(false);
		});
	});
});
