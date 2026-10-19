import { afterEach, describe, expect, it, vi } from 'vitest';
import { effectScope, nextTick, reactive, ref } from 'vue';
import { mount } from '@vue/test-utils';
import {
	MaxAttachmentsPerInput,
	MaxAttachmentByteLength,
	stripDataUrlPrefix,
	useComposerAttachments,
} from '../src/composables/useComposerAttachments.js';
import { useChatComposer } from '../src/composables/useChatComposer.js';
import { useComposerModelSelection } from '../src/composables/useComposerModelSelection.js';
import ComposerAttachments from '../src/components/Chat/ComposerAttachments.vue';
import ComposerAddMenu from '../src/components/Chat/ComposerAddMenu.vue';
import ComposerPanel from '../src/components/Chat/ComposerPanel.vue';

const scopes = [];
const mounted = [];
afterEach(() => {
	scopes.splice(0).forEach((scope) => scope.stop());
	mounted.splice(0).forEach((wrapper) => wrapper.unmount());
	vi.restoreAllMocks();
});

const record = (attachmentId, overrides = {}) => ({
	attachmentId,
	fileName: `${attachmentId}.png`,
	mediaType: 'image/png',
	byteLength: 2048,
	previewUrl: `https://attachments.selfclaw.local/conv/${attachmentId}.png`,
	...overrides,
});

const pngFile = (name = 'shot.png') => new File([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10])], name, { type: 'image/png' });

function createAttachments(bridge, selected = '') {
	const selection = ref(selected);
	scopes.push(new effectScope());
	const scope = scopes[scopes.length - 1];
	const attachments = scope.run(() => useComposerAttachments(bridge, () => selection.value));
	return { attachments, selection };
}

function mountComponent(component, props = {}) {
	const wrapper = mount(component, { props });
	mounted.push(wrapper);
	return wrapper;
}

function dispatchImageEvent(target, type, property, files) {
	const event = new Event(type, { bubbles: true, cancelable: true });
	Object.defineProperty(event, property, {
		value: { files, types: ['Files'], dropEffect: '' },
		writable: true,
		configurable: true,
	});
	target.dispatchEvent(event);
	return event;
}

describe('useComposerAttachments', () => {
	it('adopts the references the host returns and reuses one conversation id', async () => {
		const bridge = { request: vi.fn(async () => ({ ok: true, attachments: [record('a1'), record('a2', { previewUrl: null })] })) };
		const { attachments } = createAttachments(bridge);

		await attachments.addFromPicker();

		const conversationId = attachments.resolveConversationId();
		expect(bridge.request).toHaveBeenCalledWith('attachments/pick', { conversationId, multiple: true });
		expect(attachments.items.value.map((item) => item.attachmentId)).toEqual(['a1', 'a2']);
		// 预览地址缺失时降级为空串，由 chip 渲染占位而不是破图。
		expect(attachments.items.value[1].previewUrl).toBe('');
		expect(attachments.error.value).toBe('');
	});

	it('sends bare base64 for pasted or dropped files', async () => {
		const bridge = { request: vi.fn(async () => ({ ok: true, attachments: [record('a1', { fileName: 'shot.png' })] })) };
		const { attachments } = createAttachments(bridge, 'conv-1');

		await attachments.addFromFiles([pngFile()]);

		const [type, payload] = bridge.request.mock.calls[0];
		expect(type).toBe('attachments/ingest-bytes');
		expect(payload).toMatchObject({ conversationId: 'conv-1', fileName: 'shot.png', mediaType: 'image/png' });
		expect(payload.dataBase64.startsWith('data:')).toBe(false);
		expect(Buffer.from(payload.dataBase64, 'base64')).toEqual(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]));
		expect(attachments.items.value).toHaveLength(1);
	});

	it('refuses more than the per-input limit and discards the surplus the host already stored', async () => {
		const bridge = { request: vi.fn(async () => ({ ok: true, attachments: Array.from({ length: 7 }, (_, index) => record(`a${index}`)) })) };
		const { attachments } = createAttachments(bridge);

		await attachments.addFromPicker();

		expect(attachments.items.value).toHaveLength(MaxAttachmentsPerInput);
		expect(attachments.canAdd(1)).toBe(false);
		expect(attachments.error.value).toContain('最多可发送 6 张图片');
		expect(bridge.request).toHaveBeenCalledWith('attachments/discard', { conversationId: expect.any(String), attachmentId: 'a6' });
	});

	it('refuses a single oversized file and unsupported types before reading bytes', async () => {
		const bridge = { request: vi.fn() };
		const { attachments } = createAttachments(bridge);

		await attachments.addFromFiles([{ name: 'huge.png', type: 'image/png', size: MaxAttachmentByteLength + 1 }]);
		expect(attachments.error.value).toContain('10 MB');
		expect(bridge.request).not.toHaveBeenCalled();

		await attachments.addFromFiles([{ name: 'notes.txt', type: 'text/plain', size: 12 }]);
		expect(attachments.error.value).toContain('不是支持的图片格式');
		expect(bridge.request).not.toHaveBeenCalled();
		expect(attachments.items.value).toHaveLength(0);
	});

	it('removes a chip through attachments/discard but keeps clear() local after a successful send', async () => {
		const bridge = { request: vi.fn(async () => ({ ok: true, attachments: [record('a1'), record('a2')] })) };
		const { attachments } = createAttachments(bridge, 'conv-1');
		await attachments.addFromPicker();
		bridge.request.mockClear();

		await attachments.remove('a1');
		expect(bridge.request).toHaveBeenCalledExactlyOnceWith('attachments/discard', { conversationId: 'conv-1', attachmentId: 'a1' });
		expect(attachments.items.value.map((item) => item.attachmentId)).toEqual(['a2']);

		bridge.request.mockClear();
		attachments.clear();
		expect(attachments.items.value).toHaveLength(0);
		expect(bridge.request).not.toHaveBeenCalled();
	});

	it('drops pending references when the conversation changes so images never cross conversations', async () => {
		const bridge = { request: vi.fn(async () => ({ ok: true, attachments: [record('a1')] })) };
		const { attachments, selection } = createAttachments(bridge, 'conv-A');
		await attachments.addFromPicker();
		bridge.request.mockClear();

		selection.value = 'conv-B';
		await nextTick();
		await nextTick();

		expect(attachments.items.value).toHaveLength(0);
		expect(bridge.request).toHaveBeenCalledWith('attachments/discard', { conversationId: 'conv-A', attachmentId: 'a1' });
	});

	it('keeps the accepted references when the host selects the reserved conversation before the ACK', async () => {
		const bridge = { request: vi.fn(async () => ({ ok: true, attachments: [record('a1')] })) };
		const { attachments, selection } = createAttachments(bridge);
		await attachments.addFromPicker();
		// 宿主受理后先发布新会话的选中态，prompt-submission ACK 还没回到渲染进程。
		const reserved = attachments.resolveConversationId();
		bridge.request.mockClear();

		selection.value = reserved;
		await nextTick();
		await nextTick();

		// 队列已持有引用：chip 留待提交路径 clear() 清空，文件绝不能被删。
		expect(attachments.items.value.map((item) => item.attachmentId)).toEqual(['a1']);
		expect(bridge.request).not.toHaveBeenCalled();
	});

	it('strips the data URL prefix before reporting base64', () => {
		expect(stripDataUrlPrefix('data:image/png;base64,QUJD')).toBe('QUJD');
		expect(stripDataUrlPrefix('QUJD')).toBe('');
	});
});

describe('useChatComposer with attachments', () => {
	function createComposer(attachmentState) {
		const response = Promise.withResolvers();
		const bridge = { request: vi.fn(() => response.promise), post: vi.fn() };
		const workspace = { refresh: vi.fn(async () => {}) };
		const scrolling = { resumeFollow: vi.fn() };
		const session = reactive({ isBusy: false });
		scopes.push(new effectScope());
		const scope = scopes[scopes.length - 1];
		const composer = scope.run(() => useChatComposer(session, workspace, scrolling, bridge, attachmentState));
		return { composer, bridge, response, workspace, scrolling };
	}

	function attachmentState(items) {
		const state = {
			items: { value: items },
			clear: vi.fn(),
			resolveConversationId: () => 'generated-conversation',
		};
		return state;
	}

	it('sends only the four attachment fields and clears the chips once accepted', async () => {
		const state = attachmentState([{ attachmentId: 'a1', fileName: 'x.png', mediaType: 'image/png', byteLength: 10, previewUrl: 'https://preview' }]);
		const { composer, bridge, response } = createComposer(state);
		const accept = vi.fn();

		const sent = composer.submit({ prompt: 'look at this', accept });
		expect(bridge.request).toHaveBeenCalledWith('send-prompt', expect.objectContaining({
			conversationId: 'generated-conversation',
			attachments: [{ attachmentId: 'a1', fileName: 'x.png', mediaType: 'image/png', byteLength: 10 }],
		}), { timeout: 120000 });
		response.resolve({ accepted: true });
		await sent;

		expect(accept).toHaveBeenCalledOnce();
		expect(state.clear).toHaveBeenCalledOnce();
	});

	it('issues a fresh payload when the attachment set changed between retries', async () => {
		const state = attachmentState([{ attachmentId: 'a1', fileName: 'x.png', mediaType: 'image/png', byteLength: 10 }]);
		const { composer, bridge } = createComposer(state);
		bridge.request.mockResolvedValueOnce({ accepted: false, error: '图片已失效，请重新添加。' })
			.mockResolvedValueOnce({ accepted: true });

		await composer.submit({ prompt: 'same text' });
		state.items.value = [{ attachmentId: 'a2', fileName: 'y.png', mediaType: 'image/png', byteLength: 10 }];
		await composer.submit({ prompt: 'same text' });

		const [first, second] = bridge.request.mock.calls;
		expect(first[1].attachments[0].attachmentId).toBe('a1');
		expect(second[1].attachments[0].attachmentId).toBe('a2');
		expect(first[1].clientRequestId).not.toBe(second[1].clientRequestId);
		// 失败时附件必须留待重试。
		expect(state.clear).toHaveBeenCalledOnce();
	});

	it('falls back to the host reason text when the rejection carries no message', async () => {
		const state = attachmentState([]);
		const { composer, bridge } = createComposer(state);
		bridge.request.mockResolvedValueOnce({ accepted: false, reason: 'image-unsupported-by-model' });

		await composer.submit({ prompt: 'no picture allowed' });

		expect(composer.error.value).toContain('当前模型不支持图片输入');
		expect(state.clear).not.toHaveBeenCalled();
	});

	it('keeps the stored image alive when the host selects the new conversation before the ACK', async () => {
		// 宿主受理时先发布新会话选中态，prompt-submission ACK 随后才到；这条时序不能触发附件丢弃。
		const calls = [];
		const ack = Promise.withResolvers();
		const bridge = {
			request: vi.fn((type, payload) => {
				calls.push([type, payload]);
				if (type === 'attachments/pick') {
					return Promise.resolve({ ok: true, attachments: [{ ...record('a1'), previewUrl: '' }] });
				}
				return type === 'send-prompt' ? ack.promise : Promise.resolve({ ok: true });
			}),
			post: vi.fn(),
		};
		const session = reactive({ selectedConversationId: null, isBusy: false, agentMode: 'direct' });
		scopes.push(new effectScope());
		const scope = scopes[scopes.length - 1];
		const attachments = scope.run(() => useComposerAttachments(bridge, () => session.selectedConversationId));
		const composer = scope.run(() => useChatComposer(session, { refresh: async () => {} }, { resumeFollow: () => {} }, bridge, attachments));

		await attachments.addFromPicker();
		const sent = composer.submit({ prompt: 'describe this', accept: () => {} });
		const reservedId = calls.find(([type]) => type === 'send-prompt')[1].conversationId;

		session.selectedConversationId = reservedId;
		await nextTick();
		expect(attachments.items.value.map((item) => item.attachmentId)).toEqual(['a1']);
		expect(calls.filter(([type]) => type === 'attachments/discard')).toHaveLength(0);

		ack.resolve({ accepted: true });
		await sent;
		await nextTick();

		expect(attachments.items.value).toHaveLength(0);
		expect(calls.filter(([type]) => type === 'attachments/discard')).toHaveLength(0);
	});
});

describe('image capability gate', () => {
	it('treats a missing or null isMultimodal as unsupported', async () => {
		const Harness = {
			setup() {
				return { selection: useComposerModelSelection(() => 'direct', () => {}) };
			},
			template: '<div />',
		};
		const wrapper = mount(Harness);
		mounted.push(wrapper);
		const { selection } = wrapper.vm;

		selection.directModels.value = [
			{ modelProfileId: 'm1', model: 'text-only' },
			{ modelProfileId: 'm2', model: 'nullable', isMultimodal: null },
			{ modelProfileId: 'm3', model: 'vision', isMultimodal: true },
		];

		selection.activeDirectModelProfileId.value = 'm1';
		expect(selection.selectedModelSupportsImages.value).toBe(false);

		selection.activeDirectModelProfileId.value = 'm2';
		expect(selection.selectedModelSupportsImages.value).toBe(false);

		selection.activeDirectModelProfileId.value = 'm3';
		expect(selection.selectedModelSupportsImages.value).toBe(true);
		await nextTick();
	});
});

describe('ComposerAttachments', () => {
	it('renders every pending reference, shows a placeholder without a preview and forwards removal', async () => {
		const wrapper = mountComponent(ComposerAttachments, {
			items: [
				{ attachmentId: 'a1', fileName: 'chart.png', byteLength: 2048, previewUrl: 'https://attachments.selfclaw.local/conv/a1.png' },
				{ attachmentId: 'a2', fileName: 'gone.png', byteLength: 1024, previewUrl: '' },
			],
		});

		expect(wrapper.findAll('.attachment-chip')).toHaveLength(2);
		expect(wrapper.text()).toContain('chart.png');
		expect(wrapper.find('img').attributes('src')).toContain('a1.png');
		expect(wrapper.findAll('.chip-thumb.missing')).toHaveLength(1);

		await wrapper.find('img').trigger('click');
		expect(wrapper.emitted('preview-image')[0][0]).toEqual({
			src: 'https://attachments.selfclaw.local/conv/a1.png',
			alt: 'chart.png',
		});

		await wrapper.findAll('[aria-label^="移除"]')[0].trigger('click');
		expect(wrapper.emitted('remove')[0]).toEqual(['a1']);
	});

	it('surfaces the composable error next to the strip', () => {
		const wrapper = mountComponent(ComposerAttachments, { items: [], error: '最多可发送 6 张图片。' });
		expect(wrapper.find('[role="alert"]').text()).toContain('最多可发送 6 张图片');
	});
});

describe('ComposerAddMenu image entry', () => {
	it('disables the image item with a readable reason when images are unavailable', async () => {
		const disabled = mountComponent(ComposerAddMenu, { imagesEnabled: false, imagesDisabledReason: '当前模型不支持图片输入' });
		const item = disabled.findAll('.add-item').find((entry) => entry.text().includes('添加图片'));
		expect(item).toBeDefined();
		expect(item.attributes('disabled')).toBeDefined();
		expect(item.attributes('title')).toContain('当前模型不支持图片输入');
		await item.trigger('click');
		expect(disabled.emitted('add-image')).toBeUndefined();

		const enabled = mountComponent(ComposerAddMenu, { imagesEnabled: true });
		const enabledItem = enabled.findAll('.add-item').find((entry) => entry.text().includes('添加图片'));
		expect(enabledItem.attributes('disabled')).toBeUndefined();
		await enabledItem.trigger('click');
		expect(enabled.emitted('add-image')).toHaveLength(1);
	});
});

describe('ComposerPanel attachment gestures', () => {
	function attachPanel(props = {}) {
		const wrapper = mount(ComposerPanel, {
			props: { imagesEnabled: true, ...props },
			global: {
				stubs: {
					ComposerStatusBar: true, ComposerAddMenu: true, AgentSelector: true,
					ModelSelector: true, PermissionSelector: true,
				},
			},
		});
		mounted.push(wrapper);
		return wrapper;
	}

	it('hands pasted screenshots and dropped files to the attachments composable', async () => {
		const wrapper = attachPanel();
		const textarea = wrapper.find('textarea');

		const pasted = dispatchImageEvent(textarea.element, 'paste', 'clipboardData', [pngFile('pasted.png')]);
		expect(pasted.defaultPrevented).toBe(true);
		expect(wrapper.emitted('add-attachments')[0][0].map((file) => file.name)).toEqual(['pasted.png']);

		const dropped = dispatchImageEvent(textarea.element, 'drop', 'dataTransfer', [pngFile('dropped.png')]);
		expect(dropped.defaultPrevented).toBe(true);
		expect(wrapper.emitted('add-attachments')[1][0].map((file) => file.name)).toEqual(['dropped.png']);
	});

	it('ignores non-image drags and keeps text paste untouched', async () => {
		const wrapper = attachPanel();
		const textarea = wrapper.find('textarea');

		const pasted = dispatchImageEvent(textarea.element, 'paste', 'clipboardData', [{ name: 'notes.txt', type: 'text/plain', size: 4 }]);
		expect(pasted.defaultPrevented).toBe(false);

		const dropped = dispatchImageEvent(textarea.element, 'drop', 'dataTransfer', [{ name: 'notes.txt', type: 'text/plain', size: 4 }]);
		expect(dropped.defaultPrevented).toBe(false);
		expect(wrapper.emitted('add-attachments')).toBeUndefined();
	});

	it('disables every image entry point outside Direct mode', async () => {
		const wrapper = attachPanel({ imagesEnabled: false, agentMode: 'cli' });
		const textarea = wrapper.find('textarea');

		const pasted = dispatchImageEvent(textarea.element, 'paste', 'clipboardData', [pngFile()]);
		expect(pasted.defaultPrevented).toBe(false);
		const entered = dispatchImageEvent(textarea.element, 'dragenter', 'dataTransfer', []);
		expect(entered.defaultPrevented).toBe(false);
		expect(wrapper.find('.composer-shell').classes()).not.toContain('dragging');
		expect(wrapper.emitted('add-attachments')).toBeUndefined();
	});

	it('highlights the composer while image files hover over it', async () => {
		const wrapper = attachPanel();
		const textarea = wrapper.find('textarea');
		dispatchImageEvent(textarea.element, 'dragenter', 'dataTransfer', []);
		await nextTick();
		expect(wrapper.find('.composer-shell').classes()).toContain('dragging');
		textarea.element.dispatchEvent(new Event('dragleave', { bubbles: true }));
		await nextTick();
		expect(wrapper.find('.composer-shell').classes()).not.toContain('dragging');
	});

	it('submits the pending attachments with the prompt', async () => {
		const wrapper = attachPanel({
			attachments: [{ attachmentId: 'a1', fileName: 'chart.png', mediaType: 'image/png', byteLength: 2048, previewUrl: '' }],
		});
		await wrapper.find('textarea').setValue('describe the chart');
		await wrapper.find('[aria-label="发送"]').trigger('click');

		const submission = wrapper.emitted('submit')[0][0];
		expect(submission.prompt).toBe('describe the chart');
		expect(submission.attachments.map((item) => item.attachmentId)).toEqual(['a1']);
		expect(wrapper.findAll('.attachment-chip')).toHaveLength(1);
	});
});
