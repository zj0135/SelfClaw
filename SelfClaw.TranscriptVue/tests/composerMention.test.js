import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createSkillChip } from '../src/renderers/composerText.js';

const { bridge } = vi.hoisted(() => ({
	bridge: {
		request: vi.fn(),
		requestLatest: vi.fn(),
		on: vi.fn(() => () => {}),
		post: vi.fn(),
		hasHost: () => true,
	},
}));

vi.mock('../src/composables/hostBridge.js', () => ({
	hostBridge: bridge,
	useHostBridge: () => bridge,
	isSuperseded: () => false,
}));

import ComposerInput from '../src/components/chat/composer/ComposerInput.vue';

const mounted = [];
afterEach(() => {
	mounted.splice(0).forEach((wrapper) => wrapper.unmount());
	bridge.request.mockReset();
	bridge.requestLatest.mockReset();
});

const workspace = { current: { id: 'root-1', name: 'SelfClaw', path: 'C:/Git/SelfClaw', isFallback: false } };

function attach(props = {}) {
	const wrapper = mount(ComposerInput, {
		attachTo: document.body,
		props: {
			agentMode: 'direct',
			selectedAgentId: 'build',
			selectedAgentName: 'build',
			workspaceSelection: { current: {} },
			...props,
		},
	});
	mounted.push(wrapper);
	return wrapper;
}

const editorOf = (wrapper) => wrapper.find('.composer-editor');
const popoverOf = (wrapper) => wrapper.find('.mention-popover');
const lastValue = (wrapper) => (wrapper.emitted('update:modelValue') || []).at(-1)?.[0];

function caretToEnd(element) {
	const node = element.lastChild;
	const range = document.createRange();
	if (node?.nodeType === 3) {
		range.setStart(node, node.data.length);
	} else {
		range.selectNodeContents(element);
		range.collapse(false);
	}

	range.collapse(true);
	const selection = window.getSelection();
	selection.removeAllRanges();
	selection.addRange(range);
}

// 从头敲字：替换全部内容。
async function type(wrapper, text) {
	const editor = editorOf(wrapper);
	editor.element.textContent = text;
	caretToEnd(editor.element);
	await editor.trigger('input');
	await flushPromises();
	return editor;
}

// 继续敲字：追加到同一个文本节点——真实打字不会换节点，Esc 的「同一位置不重开」依赖这一点。
async function typeMore(wrapper, text) {
	const editor = editorOf(wrapper);
	editor.element.lastChild.appendData(text);
	caretToEnd(editor.element);
	await editor.trigger('input');
	await flushPromises();
	return editor;
}

async function press(wrapper, key) {
	await editorOf(wrapper).trigger('keydown', { key });
	await flushPromises();
}

describe('输入框快捷触发：$ 技能', () => {
	it('CLI 模式下 $ 不弹层，文本原样保留', async () => {
		const wrapper = attach({ agentMode: 'cli' });
		await type(wrapper, '$');
		expect(popoverOf(wrapper).exists()).toBe(false);
		expect(lastValue(wrapper)).toBe('$');
	});

	it('Direct 下 $ 打开当前 Agent 的技能列表，继续输入过滤，Enter 把触发文本换成 chip 而不是发送', async () => {
		bridge.requestLatest.mockResolvedValue({
			skills: [
				{ id: 'git-commit', name: 'Git Commit', description: 'Auto commits' },
				{ id: 'code-review', name: 'Code Review' },
			],
		});
		const wrapper = attach();
		await type(wrapper, '请用 $');
		expect(bridge.requestLatest).toHaveBeenCalledWith('effective-skills', 'extensions/list-effective-skills', { agentId: 'build' });
		expect(popoverOf(wrapper).exists()).toBe(true);
		expect(wrapper.find('.mention-head strong').text()).toBe('插入技能');
		expect(wrapper.findAll('.mention-row')).toHaveLength(2);

		await typeMore(wrapper, 'code');
		expect(wrapper.findAll('.mention-row')).toHaveLength(1);
		expect(wrapper.find('.mention-row-tag').text()).toBe('code-review');

		await press(wrapper, 'Enter');
		expect(wrapper.emitted('submit')).toBeUndefined();
		expect(lastValue(wrapper)).toBe('请用 [/code-review] ');
		const chip = editorOf(wrapper).find('[data-chip-kind="skill"]');
		expect(chip.attributes('data-chip-value')).toBe('code-review');
		expect(chip.attributes('contenteditable')).toBe('false');
		expect(chip.classes()).toContain('inline-chip--skill');
		expect(chip.find('.inline-chip-label').text()).toBe('code-review');
		expect(popoverOf(wrapper).exists()).toBe(false);
	});

	it('Esc 关闭后同一位置继续输入不重开；空白之后的新触发符再开', async () => {
		bridge.requestLatest.mockResolvedValue({ skills: [{ id: 'x', name: 'X' }] });
		bridge.request.mockResolvedValue({ ok: true, entries: [] });
		const wrapper = attach({ workspaceSelection: workspace });
		await type(wrapper, '$');
		expect(popoverOf(wrapper).exists()).toBe(true);

		await press(wrapper, 'Escape');
		expect(popoverOf(wrapper).exists()).toBe(false);
		await typeMore(wrapper, 'x');
		expect(popoverOf(wrapper).exists()).toBe(false);

		await typeMore(wrapper, ' @');
		expect(popoverOf(wrapper).exists()).toBe(true);
		expect(wrapper.find('.mention-head strong').text()).toBe('引用文件');
	});

	it('弹层打开且没有可选项时 Enter 只是被吞掉，不发送', async () => {
		bridge.requestLatest.mockResolvedValue({ skills: [] });
		// useEffectiveSkills 按 agentId 缓存，换一个 Agent 才能拿到这里 mock 的空列表。
		const wrapper = attach({ selectedAgentId: 'no-skills', selectedAgentName: 'no-skills' });
		await type(wrapper, '$');
		expect(wrapper.find('.mention-empty').text()).toBe('当前 Agent 没有可用技能');
		await press(wrapper, 'Enter');
		expect(wrapper.emitted('submit')).toBeUndefined();
		expect(lastValue(wrapper)).toBe('$');
	});
});

describe('输入框快捷触发：@ 文件', () => {
	it('没有工作目录时只给提示，不请求宿主', async () => {
		const wrapper = attach();
		await type(wrapper, '@');
		expect(popoverOf(wrapper).exists()).toBe(true);
		expect(wrapper.find('.mention-empty').text()).toContain('请先在下方状态栏选择工作目录');
		expect(bridge.request).not.toHaveBeenCalled();
	});

	it('列出工作目录树；Enter 选文件、Tab 引用目录；输入字符后在已加载条目里扁平过滤', async () => {
		bridge.request.mockResolvedValue({
			ok: true,
			entries: [
				{ name: 'src', relativePath: 'src', isDirectory: true },
				{ name: 'README.md', relativePath: 'README.md', isDirectory: false, sizeBytes: 10 },
			],
		});
		const wrapper = attach({ workspaceSelection: workspace });
		await type(wrapper, '@');
		expect(bridge.request).toHaveBeenCalledWith('workspace-tree/list', { workspaceRootId: 'root-1', relativePath: '' });
		expect(wrapper.find('.mention-subtitle').text()).toBe('SelfClaw');
		expect(wrapper.findAll('.mention-row-name').map((node) => node.text())).toEqual(['src', 'README.md']);

		await press(wrapper, 'ArrowDown');
		await press(wrapper, 'Enter');
		expect(lastValue(wrapper)).toBe('@README.md ');
		expect(editorOf(wrapper).find('[data-chip-kind="file"]').attributes('data-chip-value')).toBe('README.md');
		expect(popoverOf(wrapper).exists()).toBe(false);

		await type(wrapper, '@');
		await press(wrapper, 'Tab');
		expect(lastValue(wrapper)).toBe('@src/ ');

		await type(wrapper, '@read');
		const rows = wrapper.findAll('.mention-row');
		expect(rows).toHaveLength(1);
		expect(rows[0].find('.mention-row-meta').text()).toBe('README.md');
	});
});

describe('程序插入 chip', () => {
	it('紧贴前文时补空格，光标落在 chip 后的空格里', async () => {
		const wrapper = attach();
		await type(wrapper, 'hello');
		wrapper.vm.insertChip(createSkillChip('code-review'));
		await flushPromises();
		expect(lastValue(wrapper)).toBe('hello [/code-review] ');
		const selection = window.getSelection();
		expect(selection.anchorNode?.data).toBe(' ');
		expect(selection.anchorOffset).toBe(1);
	});

	it('clear 清空内容并归零', async () => {
		const wrapper = attach();
		await type(wrapper, 'draft');
		wrapper.vm.clear();
		await flushPromises();
		expect(lastValue(wrapper)).toBe('');
		expect(editorOf(wrapper).element.childNodes).toHaveLength(0);
	});
});
