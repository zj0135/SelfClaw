import { expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import WindowControls from '../src/components/shell/WindowControls.vue';

it('renders normal window controls with maximize action and no maximized class', async () => {
	const wrapper = mount(WindowControls, {
		props: { isMaximized: false },
	});

	expect(wrapper.classes()).not.toContain('is-maximized');

	const buttons = wrapper.findAll('button');
	expect(buttons).toHaveLength(4);

	const maximizeButton = buttons[2];
	expect(maximizeButton.attributes('title')).toBe('最大化');
	expect(maximizeButton.attributes('aria-label')).toBe('最大化');

	await buttons[0].trigger('click');
	await buttons[1].trigger('click');
	await buttons[2].trigger('click');
	await buttons[3].trigger('click');

	const emitted = wrapper.emitted('action');
	expect(emitted).toEqual([
		['terminal'],
		['minimize'],
		['toggle-maximize'],
		['minimize-to-tray'],
	]);
});

it('applies is-maximized class and switches caption button to restore action when maximized', async () => {
	const wrapper = mount(WindowControls, {
		props: { isMaximized: true },
	});

	expect(wrapper.classes()).toContain('is-maximized');

	const maximizeButton = wrapper.findAll('button')[2];
	expect(maximizeButton.attributes('title')).toBe('还原');
	expect(maximizeButton.attributes('aria-label')).toBe('还原');

	await maximizeButton.trigger('click');
	expect(wrapper.emitted('action')).toEqual([['toggle-maximize']]);
});
