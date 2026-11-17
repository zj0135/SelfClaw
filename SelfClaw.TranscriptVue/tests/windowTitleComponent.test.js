import { expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import WindowTitle from '../src/components/shell/WindowTitle.vue';

it('renders the conversation title and stays out of the drag path', () => {
	const wrapper = mount(WindowTitle, { props: { title: '排查压缩阈值异常' } });

	const root = wrapper.get('.window-title');
	expect(root.text()).toBe('排查压缩阈值异常');
	// 标题是纯展示层：必须让鼠标事件穿透到窗口拖动区，否则标题栏中间一段拖不动窗口。
	expect(root.classes()).not.toContain('is-maximized');
	expect(wrapper.find('.window-title-text').exists()).toBe(true);
});

it('renders nothing when there is no conversation to name', () => {
	const wrapper = mount(WindowTitle, { props: { title: '' } });
	expect(wrapper.find('.window-title').exists()).toBe(false);
});

it('shifts with the caption row while maximized', () => {
	const wrapper = mount(WindowTitle, { props: { title: '生图模型能力参数', isMaximized: true } });
	expect(wrapper.get('.window-title').classes()).toContain('is-maximized');
});
