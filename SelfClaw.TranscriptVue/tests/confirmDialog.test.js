import { afterEach, expect, it } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import AppConfirmDialog from '../src/components/common/AppConfirmDialog.vue';
import { useConfirm } from '../src/composables/useConfirm.js';

const { confirm, confirmState, resolveConfirm } = useConfirm();

// 组件 Teleport 到 body，VTU 的 wrapper.find 看不到，只能查真实 DOM。
function teleported(selector) {
	const element = document.body.querySelector(selector);
	if (!element) {
		throw new Error(`未在 document.body 找到 ${selector}`);
	}
	return element;
}

afterEach(() => {
	resolveConfirm(false);
	document.body.innerHTML = '';
});

it('resolves true when the confirm button is clicked', async () => {
	const wrapper = mount(AppConfirmDialog);
	const decision = confirm({ title: '删除会话', message: '确认删除该会话？', confirmText: '删除', danger: true });
	await flushPromises();

	expect(teleported('#app-confirm-title').textContent).toBe('删除会话');
	expect(teleported('#app-confirm-message').textContent).toBe('确认删除该会话？');
	expect(teleported('.confirm').classList.contains('danger')).toBe(true);
	expect(teleported('.confirm-kicker').textContent).toBe('DANGER');
	expect(teleported('.confirm-btn.primary').textContent.trim()).toBe('删除');

	teleported('.confirm-btn.primary').click();

	await expect(decision).resolves.toBe(true);
	expect(confirmState.open).toBe(false);
	wrapper.unmount();
});

it('resolves false on cancel, Escape and backdrop clicks', async () => {
	const wrapper = mount(AppConfirmDialog);

	const cancelled = confirm('确认继续？');
	await flushPromises();
	teleported('.confirm-btn.ghost').click();
	await expect(cancelled).resolves.toBe(false);

	const escaped = confirm('确认继续？');
	await flushPromises();
	document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
	await expect(escaped).resolves.toBe(false);

	const backdrop = confirm('确认继续？');
	await flushPromises();
	teleported('.confirm-backdrop').dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
	await expect(backdrop).resolves.toBe(false);

	expect(confirmState.open).toBe(false);
	wrapper.unmount();
});

it('cancels the previous decision when a new one opens', async () => {
	const wrapper = mount(AppConfirmDialog);

	const first = confirm('第一条');
	await flushPromises();
	const second = confirm('第二条');
	await flushPromises();

	await expect(first).resolves.toBe(false);
	expect(teleported('#app-confirm-message').textContent).toBe('第二条');

	teleported('.confirm-btn.primary').click();
	await expect(second).resolves.toBe(true);
	wrapper.unmount();
});

it('focuses cancel for dangerous decisions and confirm otherwise', async () => {
	const wrapper = mount(AppConfirmDialog);

	const neutral = confirm('普通确认');
	await flushPromises();
	expect(document.activeElement).toBe(teleported('.confirm-btn.primary'));
	resolveConfirm(false);
	await expect(neutral).resolves.toBe(false);

	const dangerous = confirm({ title: '删除', danger: true });
	await flushPromises();
	expect(document.activeElement).toBe(teleported('.confirm-btn.ghost'));
	resolveConfirm(false);
	await expect(dangerous).resolves.toBe(false);

	wrapper.unmount();
});
