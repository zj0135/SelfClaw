import { afterEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import CompressionNotice from '../src/components/chat/transcript/CompressionNotice.vue';

let wrapper;
afterEach(() => {
	wrapper?.unmount();
	wrapper = null;
});

const notice = (overrides = {}) => ({
	compactionId: 'c1', messageCount: 12, sourceTokens: 48200, summaryTokens: 1200,
	model: 'gpt-x', summary: '## 结论\n\n早前的决定保留。', ...overrides,
});

describe('CompressionNotice', () => {
	it('expands the summary as a button and reports its state', async () => {
		wrapper = mount(CompressionNotice, { props: { notice: notice() } });

		const head = wrapper.get('.compression-summary');
		expect(head.element.tagName).toBe('BUTTON');
		expect(head.attributes('type')).toBe('button');
		expect(head.attributes('aria-expanded')).toBe('false');
		expect(wrapper.find('.compression-markdown').exists()).toBe(false);

		await head.trigger('click');
		expect(head.attributes('aria-expanded')).toBe('true');
		const body = wrapper.get('.compression-markdown');
		expect(body.text()).toContain('早前的决定保留。');
		expect(body.find('h2').text()).toBe('结论');

		await head.trigger('click');
		expect(head.attributes('aria-expanded')).toBe('false');
		expect(wrapper.find('.compression-markdown').exists()).toBe(false);
	});

	it('degrades to a non-interactive row when there is no summary', () => {
		wrapper = mount(CompressionNotice, { props: { notice: notice({ summary: '   ' }) } });

		const head = wrapper.get('.compression-summary');
		expect(head.element.tagName).toBe('DIV');
		expect(head.attributes('aria-expanded')).toBeUndefined();
		expect(head.attributes('type')).toBeUndefined();
		expect(wrapper.find('button').exists()).toBe(false);
		expect(wrapper.find('.compression-chevron').exists()).toBe(false);
	});

	it('strips scripts out of the rendered summary', async () => {
		wrapper = mount(CompressionNotice, {
			props: { notice: notice({ summary: '正常摘要<script>alert(1)</script><img src=x onerror="alert(2)">' }) },
		});
		await wrapper.get('.compression-summary').trigger('click');

		const body = wrapper.get('.compression-markdown');
		const html = body.html().toLowerCase();
		expect(body.text()).toContain('正常摘要');
		// markdown-it 关闭了 html:true，脚本被转义成惰性文本；再用 DOM 断言没有可执行节点。
		expect(html).not.toContain('<script');
		expect(body.element.querySelector('script')).toBeNull();
		expect(body.element.querySelector('img')).toBeNull();
		expect(body.element.querySelector('[onerror]')).toBeNull();
	});

	it('keeps only the message count when the token sizes are unknown', () => {
		wrapper = mount(CompressionNotice, {
			props: { notice: notice({ sourceTokens: null, summaryTokens: null }) },
		});

		const subtitle = wrapper.get('.compression-subtitle').text();
		expect(subtitle).toBe('已压缩 12 条消息');
		expect(subtitle).not.toMatch(/\d+(?:\.\d+)?[KM]/);
		expect(subtitle).not.toContain('—');
	});

	it('puts the compression model on the row title', () => {
		wrapper = mount(CompressionNotice, { props: { notice: notice() } });
		expect(wrapper.get('.compression-summary').attributes('title')).toBe('压缩模型：gpt-x');
		wrapper.unmount();

		wrapper = mount(CompressionNotice, { props: { notice: notice({ model: '  ' }) } });
		expect(wrapper.get('.compression-summary').attributes('title')).toBeUndefined();
	});

	it('renders nothing without a compaction payload', () => {
		wrapper = mount(CompressionNotice, { props: { notice: null } });
		expect(wrapper.find('.compression-notice').exists()).toBe(false);
	});
});
