import { describe, expect, it, vi } from 'vitest';
import { defineComponent, h, nextTick, ref } from 'vue';
import { mount } from '@vue/test-utils';
import { useStreamingContent } from '../src/composables/useStreamingContent.js';

describe('useStreamingContent', () => {
	it('mounts and renders initial html content', () => {
		const TestComponent = defineComponent({
			setup() {
				const containerRef = ref(null);
				const html = ref('<p>Initial text</p>');
				const isStreaming = ref(false);
				useStreamingContent(containerRef, html, isStreaming);
				return () => h('div', { ref: containerRef, class: 'markdown-content' });
			},
		});

		const wrapper = mount(TestComponent);
		expect(wrapper.html()).toContain('Initial text');
	});

	it('applies streaming patch with animation classes when streaming', async () => {
		vi.useFakeTimers();

		const html = ref('<p>Step 1</p>');
		const isStreaming = ref(true);

		const TestComponent = defineComponent({
			setup() {
				const containerRef = ref(null);
				useStreamingContent(containerRef, html, isStreaming);
				return () => h('div', { ref: containerRef, class: 'markdown-content' });
			},
		});

		const wrapper = mount(TestComponent);

		// Initial streaming mount has stream-fade-in
		expect(wrapper.find('.stream-fade-in').exists()).toBe(true);

		// Update html during streaming
		html.value = '<p>Step 1, Step 2</p>';
		await nextTick();

		// Trigger requestAnimationFrame
		vi.runAllTimers();

		expect(wrapper.text()).toContain('Step 1, Step 2');
		expect(wrapper.find('.stream-token').exists()).toBe(true);
		expect(wrapper.find('.stream-token').text()).toBe(', Step 2');

		vi.useRealTimers();
	});

	it('renders immediately when container becomes available (e.g. accordion expansion)', async () => {
		const html = ref('<p>Thinking details</p>');
		const isStreaming = ref(false);
		const isOpen = ref(false);

		const TestComponent = defineComponent({
			setup() {
				const containerRef = ref(null);
				useStreamingContent(containerRef, html, isStreaming);
				return () =>
					isOpen.value
						? h('div', { ref: containerRef, class: 'thinking-content' })
						: h('div', { class: 'collapsed' });
			},
		});

		const wrapper = mount(TestComponent);
		expect(wrapper.find('.thinking-content').exists()).toBe(false);

		// Expand
		isOpen.value = true;
		await nextTick();

		expect(wrapper.find('.thinking-content').exists()).toBe(true);
		expect(wrapper.find('.thinking-content').text()).toBe('Thinking details');
	});

	it('normalizes content when streaming ends', async () => {
		vi.useFakeTimers();

		const html = ref('<p>Stream part 1</p>');
		const isStreaming = ref(true);

		const TestComponent = defineComponent({
			setup() {
				const containerRef = ref(null);
				useStreamingContent(containerRef, html, isStreaming);
				return () => h('div', { ref: containerRef, class: 'markdown-content' });
			},
		});

		const wrapper = mount(TestComponent);

		html.value = '<p>Stream part 1 and part 2</p>';
		await nextTick();
		vi.runAllTimers();
		expect(wrapper.find('.stream-token').exists()).toBe(true);

		// Streaming ends
		isStreaming.value = false;
		html.value = '<p>Stream part 1 and part 2</p>';
		await nextTick();

		expect(wrapper.find('.stream-token').exists()).toBe(false);
		expect(wrapper.text()).toBe('Stream part 1 and part 2');

		vi.useRealTimers();
	});
});
