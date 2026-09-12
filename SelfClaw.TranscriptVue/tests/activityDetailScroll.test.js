import { effectScope, nextTick, ref } from 'vue';
import { expect, it } from 'vitest';
import { useActivityDetailScroll } from '../src/composables/useActivityDetailScroll.js';

it('keeps a frozen reader on its window when the streaming tail rolls forward', async () => {
	const scope = effectScope();
	const detail = ref({ taskId: 'first', contentVersion: '1', blockOffset: 0 });
	const element = ref({ scrollHeight: 1000, scrollTop: 0, clientHeight: 200 });
	const scrolling = scope.run(() => useActivityDetailScroll(detail, element));
	await nextTick();
	scrolling.onScroll();
	detail.value = { taskId: 'first', contentVersion: '2', blockOffset: 16 };
	await nextTick();
	expect(scrolling.displayed.value.contentVersion).toBe('1');
	expect(element.value.scrollTop).toBe(0);
	expect(scrolling.hasNewContent.value).toBe(true);
	await scrolling.resume();
	expect(scrolling.displayed.value.blockOffset).toBe(16);
	expect(element.value.scrollTop).toBe(1000);
	scope.stop();
});

it('restores independent task positions after selection changes and a component remount', async () => {
	const positions = new Map();
	const scope = effectScope();
	const detail = ref({ taskId: 'first', contentVersion: '1', blockOffset: 0 });
	const element = ref({ scrollHeight: 1000, scrollTop: 0, clientHeight: 200 });
	const scrolling = scope.run(() => useActivityDetailScroll(detail, element, undefined, positions));
	await nextTick();
	element.value.scrollTop = 120;
	scrolling.onScroll();
	detail.value = null;
	await nextTick();
	detail.value = { taskId: 'second', contentVersion: '1', blockOffset: 0 };
	await nextTick();
	await nextTick();
	expect(element.value.scrollTop).toBe(0);
	element.value.scrollTop = 240;
	scrolling.onScroll();
	detail.value = { taskId: 'first', contentVersion: '1', blockOffset: 0 };
	await nextTick();
	await nextTick();
	expect(element.value.scrollTop).toBe(120);
	scope.stop();
	const remounted = effectScope();
	element.value.scrollTop = 0;
	remounted.run(() => useActivityDetailScroll(detail, element, undefined, positions));
	await nextTick();
	expect(element.value.scrollTop).toBe(120);
	remounted.stop();
});

it('shows the latest paused output when a frozen reader reaches the bottom', async () => {
	const scope = effectScope();
	const detail = ref({ taskId: 'first', contentVersion: '1', blockOffset: 0 });
	const element = ref({ scrollHeight: 1000, scrollTop: 0, clientHeight: 200 });
	const scrolling = scope.run(() => useActivityDetailScroll(detail, element));
	await nextTick();
	scrolling.onScroll();
	detail.value = { taskId: 'first', contentVersion: '2', blockOffset: 16 };
	await nextTick();
	element.value.scrollTop = 800;
	scrolling.onScroll();
	await nextTick();
	expect(scrolling.displayed.value.contentVersion).toBe('2');
	expect(scrolling.hasNewContent.value).toBe(false);
	scope.stop();
});
