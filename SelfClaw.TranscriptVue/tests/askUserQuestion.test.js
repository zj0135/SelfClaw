import { afterEach, expect, it, vi } from 'vitest';
import { defineComponent, h, nextTick } from 'vue';
import { flushPromises, mount } from '@vue/test-utils';
import AskUserDock from '../src/components/chat/question/AskUserDock.vue';
import ComposerPanel from '../src/components/chat/composer/ComposerPanel.vue';
import { useChatQuestions } from '../src/composables/useChatQuestions.js';

const mounted = [];
afterEach(() => mounted.splice(0).forEach((wrapper) => wrapper.unmount()));

function attach(component, props = {}, options = {}) {
	const wrapper = mount(component, { props, ...options });
	mounted.push(wrapper);
	return wrapper;
}

// 单题：默认 tab 只有一道题，所以没有 tab 栏（design §12.2）。
const singleQuestion = () => ({
	requestId: 'req-1',
	conversationId: 'conv-1',
	prompts: [
		{
			promptId: 'p-1',
			header: '数据库',
			question: 'Which database should this service use?',
			multiSelect: false,
			options: [
				{ label: 'PostgreSQL', description: '关系型，运维熟悉' },
				{ label: 'SQLite', description: '嵌入式，零部署' },
			],
		},
	],
});

// 多题：第 2 题是多选，用来验证并集语义与 tab 切换。
const manyQuestion = () => ({
	requestId: 'req-2',
	conversationId: 'conv-2',
	prompts: [
		{
			promptId: 'p-1',
			header: '数据库',
			question: 'Which database should this service use?',
			multiSelect: false,
			options: [{ label: 'PostgreSQL', description: '' }, { label: 'SQLite', description: '' }],
		},
		{
			promptId: 'p-2',
			header: 'Lint',
			question: 'Which lint rules should be enabled?',
			multiSelect: true,
			options: [{ label: 'Semi', description: '' }, { label: 'Prettier', description: '' }],
		},
	],
});

const stateOf = (question, overrides = {}) => ({
	type: 'user-question/state',
	revision: 3,
	conversationTitle: '会话 A',
	question,
	...overrides,
});

// 假的宿主桥：request 在 get-state / resolve 两种消息上回不同载荷，
// push 模拟宿主的主动推送（user-question/state）。
function createBridge(initialState) {
	const handlers = new Map();
	const bridge = {
		on: vi.fn((type, handler) => {
			handlers.set(type, handler);
			return () => handlers.delete(type);
		}),
		request: vi.fn(async (type) => (type === 'user-question/get-state'
			? initialState
			: { type: 'operation-result', ok: true })),
		post: vi.fn(),
	};
	bridge.push = (payload) => handlers.get('user-question/state')?.(payload);
	bridge.subscribed = () => bridge.on.mock.calls.map(([type]) => type);
	return bridge;
}

// 按 ChatView 的方式接线：composable 持有状态，dock 只负责渲染与回传。
function mountHost(bridge) {
	let questions = null;
	const Host = defineComponent({
		setup() {
			questions = useChatQuestions(bridge);
			return () => h(AskUserDock, {
				question: questions.pending.value,
				resolving: questions.resolving.value,
				onAnswer: (payload) => questions.answer(payload.requestId, payload.answers),
				onSkip: (requestId) => questions.skip(requestId),
			});
		},
	});
	const wrapper = attach(Host);
	return { wrapper, questions: () => questions };
}

async function settle() {
	await flushPromises();
	await nextTick();
}

it('hides the tab strip for a single prompt and appends "其它" after the model options', () => {
	const wrapper = attach(AskUserDock, { question: singleQuestion() });

	expect(wrapper.find('[role="alertdialog"]').attributes('aria-label')).toBe('需要你的回答');
	expect(wrapper.find('[role="tablist"]').exists()).toBe(false);
	expect(wrapper.find('.ask-user-question').text()).toBe('Which database should this service use?');
	expect(wrapper.findAll('.ask-user-option').map((option) => option.attributes('data-label')))
		.toEqual(['PostgreSQL', 'SQLite', '其它']);
});

it('renders one tab per prompt, switches panels on click and on arrow keys', async () => {
	const wrapper = attach(AskUserDock, { question: manyQuestion() }, { attachTo: document.body });

	const tabs = wrapper.findAll('[role="tab"]');
	expect(tabs).toHaveLength(2);
	expect(tabs[0].attributes('aria-selected')).toBe('true');
	expect(tabs.map((tab) => tab.text())).toEqual(['数据库', 'Lint']);

	await tabs[1].trigger('click');
	expect(wrapper.findAll('[role="tab"]')[1].attributes('aria-selected')).toBe('true');
	expect(wrapper.find('.ask-user-question').text()).toBe('Which lint rules should be enabled?');

	document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
	await nextTick();
	expect(wrapper.findAll('[role="tab"]')[0].attributes('aria-selected')).toBe('true');
	expect(wrapper.find('.ask-user-question').text()).toBe('Which database should this service use?');
});

it('keeps exactly one choice for a single-select prompt and lets "其它" replace it', async () => {
	const wrapper = attach(AskUserDock, { question: singleQuestion() });
	const pressed = (label) => wrapper.find(`[data-label="${label}"]`).attributes('aria-pressed');

	await wrapper.find('[data-label="PostgreSQL"]').trigger('click');
	expect(pressed('PostgreSQL')).toBe('true');

	await wrapper.find('[data-label="SQLite"]').trigger('click');
	expect(pressed('PostgreSQL')).toBe('false');
	expect(pressed('SQLite')).toBe('true');

	await wrapper.find('[data-label="其它"]').trigger('click');
	expect(pressed('SQLite')).toBe('false');
	expect(pressed('其它')).toBe('true');

	await wrapper.find('textarea.ask-user-other').setValue('DuckDB');
	await wrapper.find('.ask-user-btn.submit').trigger('click');
	expect(wrapper.emitted('answer')[0][0]).toEqual({
		requestId: 'req-1',
		answers: [{ promptId: 'p-1', selected: [], other: 'DuckDB' }],
	});
});

it('unions multi-select choices and keeps them alongside the free text', async () => {
	const wrapper = attach(AskUserDock, { question: manyQuestion() });
	await wrapper.findAll('[role="tab"]')[1].trigger('click');

	await wrapper.find('[data-label="Semi"]').trigger('click');
	await wrapper.find('[data-label="Prettier"]').trigger('click');
	await wrapper.find('[data-label="其它"]').trigger('click');
	await wrapper.find('textarea.ask-user-other').setValue('  Biome  ');

	expect(wrapper.find('[data-label="Semi"]').attributes('aria-pressed')).toBe('true');
	expect(wrapper.find('[data-label="Prettier"]').attributes('aria-pressed')).toBe('true');

	await wrapper.find('.ask-user-btn.submit').trigger('click');
	expect(wrapper.emitted('answer')[0][0].answers).toEqual([
		{ promptId: 'p-2', selected: ['Semi', 'Prettier'], other: 'Biome' },
	]);
});

it('reports multi-select answers in option order, not click order', async () => {
	// C、B、D 的点击顺序必须折成 B、C、D：模型读到的顺序要和界面上看到的顺序一致。
	const question = {
		requestId: 'req-order',
		prompts: [{
			promptId: 'p-order',
			header: '四项',
			question: 'Pick any',
			multiSelect: true,
			options: [
				{ label: 'A', description: '' },
				{ label: 'B', description: '' },
				{ label: 'C', description: '' },
				{ label: 'D', description: '' },
			],
		}],
	};
	const wrapper = attach(AskUserDock, { question });

	await wrapper.find('[data-label="D"]').trigger('click');
	await wrapper.find('[data-label="C"]').trigger('click');
	await wrapper.find('[data-label="A"]').trigger('click');

	await wrapper.find('.ask-user-btn.submit').trigger('click');
	expect(wrapper.emitted('answer')[0][0].answers).toEqual([
		{ promptId: 'p-order', selected: ['A', 'C', 'D'], other: '' },
	]);
});

it('keeps option order after deselecting and reselecting a middle choice', async () => {
	const question = {
		requestId: 'req-order-2',
		prompts: [{
			promptId: 'p-order-2',
			header: '',
			question: 'Pick any',
			multiSelect: true,
			options: [{ label: 'A', description: '' }, { label: 'B', description: '' }, { label: 'C', description: '' }],
		}],
	};
	const wrapper = attach(AskUserDock, { question });

	await wrapper.find('[data-label="B"]').trigger('click');
	await wrapper.find('[data-label="A"]').trigger('click');
	// 取消再选回来：B 不能因此掉到队尾。
	await wrapper.find('[data-label="A"]').trigger('click');
	await wrapper.find('[data-label="A"]').trigger('click');
	await wrapper.find('[data-label="C"]').trigger('click');

	await wrapper.find('.ask-user-btn.submit').trigger('click');
	expect(wrapper.emitted('answer')[0][0].answers).toEqual([
		{ promptId: 'p-order-2', selected: ['A', 'B', 'C'], other: '' },
	]);
});

it('reveals the "其它" textarea on demand and feeds its text into the payload', async () => {
	const wrapper = attach(AskUserDock, { question: singleQuestion() });
	expect(wrapper.find('textarea.ask-user-other').exists()).toBe(false);
	expect(wrapper.find('.ask-user-btn.submit').attributes('disabled')).toBeDefined();

	await wrapper.find('[data-label="其它"]').trigger('click');
	const other = wrapper.find('textarea.ask-user-other');
	expect(other.exists()).toBe(true);
	expect(other.attributes('placeholder')).toBe('输入你的回答…');

	// 只有选中"其它"、还没输入内容时仍算未答。
	expect(wrapper.find('.ask-user-btn.submit').attributes('disabled')).toBeDefined();

	await other.setValue('  DuckDB  ');
	expect(wrapper.find('.ask-user-btn.submit').attributes('disabled')).toBeUndefined();

	await wrapper.find('.ask-user-btn.submit').trigger('click');
	expect(wrapper.emitted('answer')[0][0].answers).toEqual([
		{ promptId: 'p-1', selected: [], other: 'DuckDB' },
	]);
});

it('enables submit as soon as one prompt is answered and never requires the rest', async () => {
	const wrapper = attach(AskUserDock, { question: manyQuestion() });
	const submit = () => wrapper.find('.ask-user-btn.submit');

	expect(wrapper.find('[role="status"]').text()).toBe('0／2');
	expect(submit().attributes('disabled')).toBeDefined();

	await wrapper.find('[data-label="PostgreSQL"]').trigger('click');
	expect(submit().attributes('disabled')).toBeUndefined();
	expect(wrapper.findAll('[role="tab"]')[0].text()).toContain('数据库');

	await submit().trigger('click');
	expect(wrapper.emitted('answer')[0][0].answers).toEqual([
		{ promptId: 'p-1', selected: ['PostgreSQL'], other: '' },
	]);
});

it('keeps submit disabled and silent while no prompt is answered', async () => {
	const wrapper = attach(AskUserDock, { question: singleQuestion() });
	const submit = wrapper.find('.ask-user-btn.submit');

	expect(submit.attributes('disabled')).toBeDefined();
	await submit.trigger('click');
	expect(wrapper.emitted('answer')).toBeUndefined();
});

it('freezes every control while a submission is in flight', () => {
	const wrapper = attach(AskUserDock, { question: singleQuestion(), resolving: true });

	expect(wrapper.find('[data-label="PostgreSQL"]').attributes('disabled')).toBeDefined();
	expect(wrapper.find('[data-label="其它"]').attributes('disabled')).toBeDefined();
	expect(wrapper.find('.ask-user-btn.skip').attributes('disabled')).toBeDefined();
	expect(wrapper.find('.ask-user-btn.submit').attributes('disabled')).toBeDefined();
});

it('emits the batch id when the whole batch is skipped', async () => {
	const wrapper = attach(AskUserDock, { question: singleQuestion() });
	await wrapper.find('.ask-user-btn.skip').trigger('click');
	expect(wrapper.emitted('skip')).toEqual([['req-1']]);
});

it('answers from the keyboard: digits pick options and Ctrl+Enter submits', async () => {
	const wrapper = attach(AskUserDock, { question: singleQuestion() }, { attachTo: document.body });

	const press = async (key, init = {}) => {
		document.body.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, ...init }));
		await nextTick();
	};

	await press('2');
	expect(wrapper.find('[data-label="SQLite"]').attributes('aria-pressed')).toBe('true');

	await press('Enter', { ctrlKey: true });
	expect(wrapper.emitted('answer')[0][0].answers).toEqual([
		{ promptId: 'p-1', selected: ['SQLite'], other: '' },
	]);
});

it('subscribes once and resolves answers with the batch id as the request id', async () => {
	const bridge = createBridge(stateOf(singleQuestion()));
	const { wrapper, questions } = mountHost(bridge);
	await settle();

	expect(bridge.subscribed()).toEqual(['user-question/state']);
	expect(questions().pending.value.requestId).toBe('req-1');
	expect(questions().pending.value.conversationTitle).toBe('会话 A');

	await wrapper.find('[data-label="PostgreSQL"]').trigger('click');
	await wrapper.find('.ask-user-btn.submit').trigger('click');
	await settle();

	// 批次 id 走独立字段，传输 id 仍由 hostBridge 生成：混用会让状态推送与回包抢同一个槽位。
	expect(bridge.request).toHaveBeenCalledWith(
		'resolve-user-question',
		{
			questionRequestId: 'req-1',
			answers: [{ promptId: 'p-1', selected: ['PostgreSQL'], other: '' }],
		},
	);
	expect(bridge.request).toHaveBeenCalledWith('user-question/get-state');
	expect(questions().resolving.value).toBe(false);
});

it('sends skipped: true and no answers when the batch is skipped', async () => {
	const bridge = createBridge(stateOf(manyQuestion()));
	const { wrapper } = mountHost(bridge);
	await settle();

	await wrapper.find('.ask-user-btn.skip').trigger('click');
	await settle();

	expect(bridge.request).toHaveBeenCalledWith(
		'resolve-user-question',
		{ questionRequestId: 'req-2', skipped: true },
	);
});

it('drops a stale revision and collapses when the host reports no pending question', async () => {
	const bridge = createBridge(stateOf(singleQuestion(), { revision: 7 }));
	const { wrapper } = mountHost(bridge);
	await settle();
	expect(wrapper.find('.ask-user-dock').exists()).toBe(true);

	// 更小的 revision：乱序到达的旧快照不能把已经答完的批次弹回来。
	bridge.push(stateOf(singleQuestion(), { revision: 6 }));
	await nextTick();
	expect(wrapper.find('.ask-user-dock').exists()).toBe(true);

	bridge.push(stateOf(null, { revision: 8, conversationTitle: '' }));
	await settle();
	await new Promise((resolve) => setTimeout(resolve, 60));
	expect(wrapper.find('.ask-user-dock').exists()).toBe(false);
});

it('blocks the composer from sending while a question is pending, keeping stop as the escape hatch', async () => {
	const wrapper = attach(ComposerPanel, {
		busy: true,
		queueable: true,
		stoppable: true,
		questionPending: true,
	}, {
		global: {
			stubs: {
				ComposerStatusBar: true, ComposerAddMenu: true, AgentSelector: true,
				ModelSelector: true, PermissionSelector: true,
			},
		},
	});

	expect(wrapper.find('textarea').attributes('placeholder')).toBe('请先回答上方的问题');
	// 输入框空着时这一格是停止按钮：待答期间它必须留着，否则用户没有逃生口。
	expect(wrapper.find('[aria-label="停止生成"]').attributes('disabled')).toBeUndefined();

	await wrapper.find('textarea').setValue('draft typed while answering');
	const send = wrapper.find('[aria-label="发送"]');
	expect(send.exists()).toBe(true);
	expect(send.attributes('disabled')).toBeDefined();

	await send.trigger('click');
	await wrapper.find('textarea').trigger('keydown', { key: 'Enter' });
	expect(wrapper.emitted('submit')).toBeUndefined();
});

it('keeps the ordinary send path when no question is pending', async () => {
	const wrapper = attach(ComposerPanel, { busy: false, queueable: true }, {
		global: { stubs: { ComposerStatusBar: true, ComposerAddMenu: true, ModelSelector: true } },
	});

	expect(wrapper.find('textarea').attributes('placeholder')).toBe('让助手帮你处理项目...');
	await wrapper.find('textarea').setValue('ordinary');
	await wrapper.find('[aria-label="发送"]').trigger('click');
	expect(wrapper.emitted('submit')[0][0].prompt).toBe('ordinary');
});
