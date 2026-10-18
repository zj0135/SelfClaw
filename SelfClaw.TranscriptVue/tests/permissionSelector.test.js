import { afterEach, expect, it } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import PermissionSelector from '../src/components/Chat/PermissionSelector.vue';

const mounted = [];
afterEach(() => {
	mounted.splice(0).forEach((wrapper) => wrapper.unmount());
});

it('renders current permission label without background box or border', () => {
	const wrapper = mount(PermissionSelector, {
		props: { mode: 'require-approval' },
		attachTo: document.body,
	});
	mounted.push(wrapper);
	const btn = wrapper.get('.status-perm-btn');
	expect(btn.text()).toContain('需确认');
	expect(btn.classes()).toContain('status-perm-btn--require-approval');
});

it('opens upwards by default to prevent viewport clipping and emits selection on click', async () => {
	const wrapper = mount(PermissionSelector, {
		props: { mode: 'require-approval' },
		attachTo: document.body,
	});
	mounted.push(wrapper);

	const btn = wrapper.get('.status-perm-btn');
	expect(wrapper.find('.perm-menu').isVisible()).toBe(false);

	await btn.trigger('click');
	await flushPromises();

	const menu = wrapper.get('.perm-menu');
	expect(menu.isVisible()).toBe(true);
	expect(menu.classes()).toContain('perm-menu--up');

	const options = wrapper.findAll('.perm-opt');
	expect(options).toHaveLength(2);
	expect(options[0].text()).toContain('需确认');
	expect(options[1].text()).toContain('自动允许');

	await options[1].trigger('click');
	await flushPromises();

	expect(wrapper.emitted('select')).toEqual([['full-access']]);
	expect(wrapper.find('.perm-menu').isVisible()).toBe(false);
});
