import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ChatEmptyStarter from '../src/components/chat/ChatEmptyStarter.vue';

describe('ChatEmptyStarter', () => {
	it('renders hero title and all starter preset cards', () => {
		const wrapper = mount(ChatEmptyStarter);

		expect(wrapper.find('.hero-title').text()).toBe('想构建什么？');
		const cards = wrapper.findAll('.starter-card');
		expect(cards.length).toBe(4);
		expect(cards[0].find('.card-title').text()).toBe('探索工程架构');
		expect(cards[1].find('.card-title').text()).toBe('代码审查分析');
		expect(cards[2].find('.card-title').text()).toBe('编写单元测试');
		expect(cards[3].find('.card-title').text()).toBe('功能设计拆解');
	});

	it('emits select-preset with the prompt when a starter card is clicked', async () => {
		const wrapper = mount(ChatEmptyStarter);

		const firstCard = wrapper.findAll('.starter-card')[0];
		await firstCard.trigger('click');

		const emitted = wrapper.emitted('select-preset');
		expect(emitted).toBeTruthy();
		expect(emitted[0][0]).toContain('请分析当前工作区的工程架构');
	});

	it('emits trigger-mention with corresponding token when mention pill is clicked', async () => {
		const wrapper = mount(ChatEmptyStarter);

		const pills = wrapper.findAll('.mention-pill');
		expect(pills.length).toBe(3);

		// Click @ mention pill
		await pills[0].trigger('click');
		expect(wrapper.emitted('trigger-mention')?.[0][0]).toBe('@');

		// Click / mention pill
		await pills[1].trigger('click');
		expect(wrapper.emitted('trigger-mention')?.[1][0]).toBe('/');

		// Click $ mention pill
		await pills[2].trigger('click');
		expect(wrapper.emitted('trigger-mention')?.[2][0]).toBe('$');
	});
});
