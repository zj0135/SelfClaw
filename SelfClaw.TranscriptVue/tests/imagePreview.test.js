import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { defineComponent, h } from 'vue';

// Escape 在窗口层就被宿主接走（MainWindow.HandlePreviewKeyDown），所以浮层必须把开合状态
// 推给宿主；这里用一个假的 chrome.webview 观察真正发出去的帧，并模拟宿主推回 dismiss。
const posted = [];
let deliver;

beforeEach(() => {
	posted.length = 0;
	vi.resetModules();
	const listeners = new Set();
	window.chrome = {
		webview: {
			addEventListener(type, listener) {
				if (type === 'message') listeners.add(listener);
			},
			postMessage(message) {
				posted.push(message);
			},
		},
	};
	deliver = (payload) => {
		for (const listener of listeners) listener({ data: payload });
	};
});

afterEach(() => {
	wrapper?.unmount();
	wrapper = null;
	delete window.chrome;
});

let wrapper;

async function mountPreview() {
	const { useImagePreview } = await import('../src/composables/useImagePreview.js');
	let api;
	const Harness = defineComponent({
		setup() {
			api = useImagePreview();
			return () => h('div');
		},
	});
	wrapper = mount(Harness);
	return api;
}

it('tells the host when the preview opens and closes', async () => {
	const preview = await mountPreview();

	preview.open({ src: 'https://attachments.selfclaw.local/a.png', alt: 'a.png' });
	expect(preview.preview.value).toMatchObject({ alt: 'a.png' });
	expect(posted).toEqual([{ type: 'image-preview/open' }]);

	preview.close();
	expect(preview.preview.value).toBeNull();
	expect(posted).toEqual([{ type: 'image-preview/open' }, { type: 'image-preview/close' }]);

	// 重复关闭是空操作：宿主已经把它判给浮层，不该为同一次关合再收一遍帧。
	preview.close();
	expect(posted).toHaveLength(2);
});

it('closes on Escape and on the host dismiss push', async () => {
	const preview = await mountPreview();

	preview.open({ src: 'first.png', alt: 'first' });
	document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
	expect(preview.preview.value).toBeNull();

	preview.open({ src: 'second.png', alt: 'second' });
	deliver({ type: 'image-preview/dismiss' });
	expect(preview.preview.value).toBeNull();
	expect(posted.filter((message) => message.type === 'image-preview/close')).toHaveLength(2);
});
