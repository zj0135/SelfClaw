import { afterEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ComposerStatusBar from '../src/components/Chat/ComposerStatusBar.vue';

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
	wrapper = mount(ComposerStatusBar, { props: { usage: value } });
	return wrapper;
}

describe('ComposerStatusBar usage meter', () => {
	it('renders the context size against its window plus the cumulative totals', () => {
		mountWithUsage(usage());

		const values = wrapper.get('.usage-meter').findAll('.usage-value').map((node) => node.text());
		expect(values).toEqual(['12.4K / 32K', '1.24M', '342K']);
		expect(wrapper.get('.usage-meter').text()).toContain('上下文');
		expect(wrapper.get('.usage-meter').text()).toContain('累计');
		expect(wrapper.get('.usage-totals').text()).toContain('输入');
		expect(wrapper.get('.usage-totals').text()).toContain('输出');
	});

	it('shows an unavailable context without inventing numbers', () => {
		mountWithUsage(usage({ contextTokens: null, contextWindowTokens: null }));

		const context = wrapper.get('.usage-meter .usage-value').text();
		expect(context).toBe('不可用');
		expect(wrapper.get('.usage-label').text()).toBe('上下文');
		expect(wrapper.find('.usage-meter').text().slice(0, 6)).not.toMatch(/\d/);
	});

	it('omits the cumulative part while it is unknown or zero', async () => {
		mountWithUsage(usage({ inputTokens: 0, outputTokens: 0 }));
		expect(wrapper.find('.usage-totals').exists()).toBe(false);
		expect(wrapper.find('.usage-divider').exists()).toBe(false);
		// 上下文本身仍然显示，只丢掉没有信息的累计段。
		expect(wrapper.get('.usage-value').text()).toBe('12.4K / 32K');
		wrapper.unmount();

		mountWithUsage(usage({ inputTokens: null, outputTokens: null }));
		expect(wrapper.find('.usage-totals').exists()).toBe(false);
		expect(wrapper.findAll('.usage-value')).toHaveLength(1);
	});

	it('marks an approximate context size with a tilde and explains it in the tooltip', () => {
		mountWithUsage(usage({ contextIsEstimated: true }));

		expect(wrapper.get('.usage-value').text()).toBe('~12.4K / 32K');
		expect(wrapper.get('.usage-meter').attributes('title')).toBe('由本地计数近似');
		wrapper.unmount();

		mountWithUsage(usage());
		expect(wrapper.get('.usage-meter').attributes('title')).toBeFalsy();
	});

	it('renders no usage block when the host reports none', () => {
		mountWithUsage(null);
		expect(wrapper.find('.usage-meter').exists()).toBe(false);
		// 工作区上下文仍在，确认只隐藏了用量块。
		expect(wrapper.get('.composer-statusbar').exists()).toBe(true);
	});
});
