import { computed, effectScope, nextTick, reactive, ref } from 'vue';
import { expect, it } from 'vitest';
import { useActivityDetail } from '../src/composables/useActivityDetail.js';
import { useActivityDetailScroll } from '../src/composables/useActivityDetailScroll.js';

async function settle() { await nextTick(); await nextTick(); }

function createReader(preferences = { view: reactive({ taskId: null }), readings: new Map() }) {
	const scope = effectScope();
	const values = new Map(['first', 'second'].map((taskId) => [taskId, { taskId, task: {}, contentVersion: '1', blockOffset: 0, laterOffset: null }]));
	const panel = {
		subscription: ref('current'), selection: ref(null), section: ref({}),
		async selectDetail(taskId) {
			this.selection.value = crypto.randomUUID();
			this.section.value = { detailSelectionId: this.selection.value, detail: values.get(taskId) ?? null };
		},
	};
	const element = ref({ scrollHeight: 1000, scrollTop: 0, clientHeight: 200, querySelectorAll: () => [], getBoundingClientRect: () => ({ top: 0 }) });
	const detail = scope.run(() => useActivityDetail(panel, computed(() => preferences), ref(true)));
	const scrolling = scope.run(() => useActivityDetailScroll(detail, element));
	function publish(value) {
		values.set(value.taskId, value);
		panel.section.value = { detailSelectionId: panel.selection.value, detail: value };
	}
	return { scope, detail, scrolling, element, preferences, publish };
}

it('keeps a frozen reader on its window when the streaming tail rolls forward', async () => {
	const reader = createReader();
	await reader.detail.selectTask('first');
	await settle();
	reader.element.value.scrollTop = 0;
	reader.scrolling.onScroll();
	reader.publish({ taskId: 'first', task: {}, contentVersion: '2', blockOffset: 16, laterOffset: null });
	await settle();
	expect(reader.detail.displayed.value.contentVersion).toBe('1');
	expect(reader.element.value.scrollTop).toBe(0);
	expect(reader.detail.hasNewContent.value).toBe(true);
	await reader.detail.resumeLatest();
	await settle();
	expect(reader.detail.displayed.value.blockOffset).toBe(16);
	expect(reader.element.value.scrollTop).toBe(1000);
	reader.scope.stop();
});

it('restores independent task positions after selection changes and a component remount', async () => {
	const reader = createReader();
	await reader.detail.selectTask('first');
	await settle();
	reader.element.value.scrollTop = 120;
	reader.scrolling.onScroll();
	await reader.detail.selectTask('second');
	await settle();
	reader.element.value.scrollTop = 240;
	reader.scrolling.onScroll();
	await reader.detail.selectTask('first');
	await settle();
	expect(reader.element.value.scrollTop).toBe(120);
	reader.scope.stop();
	const remounted = createReader(reader.preferences);
	await settle();
	expect(remounted.element.value.scrollTop).toBe(120);
	remounted.scope.stop();
});

it('shows the latest paused output when a frozen reader reaches the bottom', async () => {
	const reader = createReader();
	await reader.detail.selectTask('first');
	await settle();
	reader.element.value.scrollTop = 0;
	reader.scrolling.onScroll();
	reader.publish({ taskId: 'first', task: {}, contentVersion: '2', blockOffset: 16, laterOffset: null });
	await settle();
	reader.element.value.scrollTop = 800;
	reader.scrolling.onScroll();
	await settle();
	expect(reader.detail.displayed.value.contentVersion).toBe('2');
	expect(reader.detail.hasNewContent.value).toBe(false);
	reader.scope.stop();
});
