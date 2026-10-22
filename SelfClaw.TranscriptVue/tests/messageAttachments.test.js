import { afterEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import MessageContent from '../src/components/chat/transcript/MessageContent.vue';

const mounted = [];
afterEach(() => {
	mounted.splice(0).forEach((wrapper) => wrapper.unmount());
});

function mountMessage(props = {}) {
	const defaultCollapse = {
		isThinkingOpen: () => false,
		isToolOpen: () => false,
		toggleThinking: () => {},
		toggleTool: () => {},
	};

	const wrapper = mount(MessageContent, {
		props: {
			collapse: defaultCollapse,
			item: {
				id: 'msg-1',
				kind: 'message',
				role: 'user',
				status: 'completed',
				timestamp: '18:40',
				segments: [{ kind: 'content', markdown: '这两个图片文本都是什么', segmentId: 'msg-1:text:0' }],
				attachments: [
					{
						attachmentId: 'att-1',
						fileName: 'QQ_1792319281905.png',
						byteLength: 17408,
						mediaType: 'image/png',
						sourceUrl: 'https://attachments.selfclaw.local/conv/att-1.png',
					},
					{
						attachmentId: 'att-2',
						fileName: 'QQ_1792319286103.png',
						byteLength: 23552,
						mediaType: 'image/png',
						sourceUrl: 'https://attachments.selfclaw.local/conv/att-2.png',
					},
				],
				...props.item,
			},
			...props,
		},
	});
	mounted.push(wrapper);
	return wrapper;
}

describe('MessageContent attachments', () => {
	it('renders attachments directly below user message card without being wrapped in the card', () => {
		const wrapper = mountMessage();

		const mainChildren = wrapper.findAll('.message-main > *');
		expect(mainChildren).toHaveLength(2);

		// First child should be the user message article (.item), second child should be standalone message-attachments
		expect(mainChildren[0].classes()).toContain('item');
		expect(mainChildren[1].classes()).toContain('message-attachments');

		// Attachments must NOT be inside the user message bubble
		expect(wrapper.find('.item .message-attachments').exists()).toBe(false);

		// File name and size should NOT be rendered in message attachment
		expect(wrapper.find('.message-attachments').text()).toBe('');
		expect(wrapper.find('figcaption').exists()).toBe(false);

		// Both images should be rendered with their sourceUrl and alt without hover title
		const images = wrapper.findAll('.message-attachment-image');
		expect(images).toHaveLength(2);
		expect(images[0].attributes('src')).toBe('https://attachments.selfclaw.local/conv/att-1.png');
		expect(images[0].attributes('alt')).toBe('QQ_1792319281905.png');
		expect(images[0].attributes('title')).toBeUndefined();
		expect(images[1].attributes('src')).toBe('https://attachments.selfclaw.local/conv/att-2.png');
	});

	it('emits preview-image when clicking a thumbnail image', async () => {
		const wrapper = mountMessage();

		const firstThumb = wrapper.findAll('.message-attachment')[0];
		await firstThumb.trigger('click');

		expect(wrapper.emitted('preview-image')).toBeDefined();
		expect(wrapper.emitted('preview-image')[0][0]).toEqual({
			src: 'https://attachments.selfclaw.local/conv/att-1.png',
			alt: 'QQ_1792319281905.png',
		});
	});

	it('renders a missing placeholder when sourceUrl is absent', () => {
		const wrapper = mountMessage({
			item: {
				id: 'msg-2',
				kind: 'message',
				role: 'user',
				status: 'completed',
				timestamp: '18:41',
				segments: [],
				attachments: [
					{
						attachmentId: 'att-3',
						fileName: 'broken.png',
						byteLength: 512,
						mediaType: 'image/png',
						sourceUrl: '',
					},
				],
			},
		});

		expect(wrapper.findAll('.message-attachment-image.missing')).toHaveLength(1);
		expect(wrapper.find('.message-blocks').exists()).toBe(false);
		expect(wrapper.find('.message-attachments').exists()).toBe(true);
	});
});
