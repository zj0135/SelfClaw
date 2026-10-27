import { afterEach, describe, expect, it } from 'vitest';
import { computed, ref } from 'vue';
import { mount } from '@vue/test-utils';
import TodoPlanDock from '../src/components/chat/todo/TodoPlanDock.vue';
import TodoPlanList from '../src/components/chat/todo/TodoPlanList.vue';
import TodoCard from '../src/components/chat/transcript/TodoCard.vue';
import { useChatTodo } from '../src/composables/useChatTodo.js';
import {
	normalizeTodoItems,
	todoActiveItem,
	todoAllCompleted,
	todoCounts,
	todoProgressLabel,
} from '../src/renderers/todo.js';

let wrapper;
afterEach(() => {
	wrapper?.unmount();
	wrapper = null;
});

const plan = (items) => ({ items });
const planItems = () => [
	{ content: '检查 Direct 工具装配', status: 'completed', activeForm: '正在检查工具装配' },
	{ content: '新增 todo_write 工具', status: 'completed' },
	{ content: '投影到 transcript 卡片', status: 'in_progress', activeForm: '正在投影' },
	{ content: '前端 dock 与内联卡片', status: 'pending' },
	{ content: '测试与验收', status: 'pending' },
];
const donePlan = () => plan(planItems().map((item) => ({ ...item, status: 'completed' })));
const todoSegment = (overrides = {}) => ({
	kind: 'todo',
	text: 'Updated plan (2/5)',
	status: 'completed',
	segmentId: 'segment-todo-1',
	detailText: 'Plan updated: 5 tasks (2 completed, 1 in progress).',
	todo: plan(planItems()),
	...overrides,
});

describe('todo renderers', () => {
	it('normalizes statuses, drops empty content and tolerates a missing plan', () => {
		const items = normalizeTodoItems(plan([
			{ content: '  保留这一条  ', status: 'in_progress', activeForm: ' 正在处理 ' },
			{ content: '未知状态', status: 'blocked' },
			{ content: '   ', status: 'pending' },
			null,
		]));

		expect(items).toEqual([
			{ content: '保留这一条', status: 'in_progress', activeForm: '正在处理' },
			{ content: '未知状态', status: 'pending', activeForm: '' },
		]);
		expect(normalizeTodoItems(null)).toEqual([]);
		expect(normalizeTodoItems({})).toEqual([]);
		expect(normalizeTodoItems({ items: 'nope' })).toEqual([]);
	});

	it('derives counts, progress label, active item and completion from the items alone', () => {
		const items = normalizeTodoItems(plan(planItems()));

		expect(todoCounts(items)).toEqual({ total: 5, completed: 2, inProgress: 1, pending: 2 });
		expect(todoProgressLabel(todoCounts(items))).toBe('2/5');
		expect(todoActiveItem(items)).toEqual({ content: '投影到 transcript 卡片', status: 'in_progress', activeForm: '正在投影' });
		expect(todoActiveItem([])).toBeNull();
		expect(todoAllCompleted(items)).toBe(false);
		expect(todoAllCompleted(normalizeTodoItems(donePlan()))).toBe(true);
		// 空计划不算「全部完成」：否则 dock 的隐藏条件会把「已清空」也一起吃掉。
		expect(todoAllCompleted([])).toBe(false);
		expect(todoProgressLabel(todoCounts([]))).toBe('');
	});
});

describe('TodoPlanDock', () => {
	it('shows the progress summary and stays collapsed until the header is activated', async () => {
		wrapper = mount(TodoPlanDock, { props: { todo: plan(planItems()), busy: true, open: false } });

		expect(wrapper.get('.todo-plan-dock').exists()).toBe(true);
		expect(wrapper.get('.todo-plan-progress').text()).toBe('2/5');
		// 播报区包住「当前步骤 + 计数」两段：只播计数会漏掉「同一计数下换了当前项」。
		expect(wrapper.get('.todo-plan-status').attributes('role')).toBe('status');
		expect(wrapper.get('.todo-plan-status').attributes('aria-live')).toBe('polite');
		expect(wrapper.get('.todo-plan-status').text()).toContain('2/5');
		expect(wrapper.get('.todo-plan-head').attributes('aria-expanded')).toBe('false');
		expect(wrapper.find('.todo-plan-list').exists()).toBe(false);

		await wrapper.get('.todo-plan-head').trigger('click');
		expect(wrapper.emitted('toggle')).toHaveLength(1);

		await wrapper.setProps({ open: true });
		expect(wrapper.get('.todo-plan-head').attributes('aria-expanded')).toBe('true');
		const controls = wrapper.get('.todo-plan-head').attributes('aria-controls');
		expect(wrapper.find(`#${controls}`).exists()).toBe(true);
		expect(wrapper.findAll('.todo-plan-item')).toHaveLength(5);
	});

	it('fills the 2px meter by completion ratio', () => {
		wrapper = mount(TodoPlanDock, { props: { todo: plan(planItems()), open: true } });

		const fill = wrapper.get('.todo-plan-meter-fill');
		expect(fill.attributes('style')).toContain('width: 40%');
	});

	it('shows the active form of the in-progress item only', () => {
		wrapper = mount(TodoPlanDock, { props: { todo: plan(planItems()), open: true } });

		const active = wrapper.findAll('.todo-plan-item-active');
		expect(active).toHaveLength(1);
		expect(active[0].text()).toBe('正在投影');
		expect(wrapper.get('.todo-plan-item.in_progress .todo-plan-item-text').text()).toBe('投影到 transcript 卡片');
	});

	it('puts the in-progress step into the collapsed header', () => {
		wrapper = mount(TodoPlanDock, { props: { todo: plan(planItems()), open: false } });

		const hint = wrapper.get('.todo-plan-active');
		expect(hint.text()).toBe('正在投影');
		// 位置：标题之后的播报区内，且只是状态文案——不可聚焦、不用 title 兜底。
		const classes = [...wrapper.get('.todo-plan-head-main').element.children]
			.map((el) => el.getAttribute('class'));
		expect(classes.indexOf('todo-plan-status')).toBe(classes.indexOf('todo-plan-title') + 1);
		const inner = [...wrapper.get('.todo-plan-status').element.children]
			.map((el) => el.getAttribute('class'));
		expect(inner.indexOf('todo-plan-progress')).toBe(inner.indexOf('todo-plan-active') + 1);
		expect(hint.attributes('tabindex')).toBeUndefined();
		expect(hint.attributes('title')).toBeUndefined();
		expect(wrapper.find('.todo-plan-list').exists()).toBe(false);
	});

	it('falls back to the plain content when the in-progress item has no active form', () => {
		wrapper = mount(TodoPlanDock, {
			props: {
				todo: plan([
					{ content: '已完成的项', status: 'completed' },
					{ content: '跑一遍验收命令', status: 'in_progress' },
				]),
				open: false,
			},
		});

		expect(wrapper.get('.todo-plan-active').text()).toBe('跑一遍验收命令');
	});

	it('renders no header hint when nothing is in progress', () => {
		wrapper = mount(TodoPlanDock, {
			props: {
				todo: plan([
					{ content: '待处理一', status: 'pending' },
					{ content: '已完成一', status: 'completed' },
				]),
				open: false,
			},
		});

		expect(wrapper.get('.todo-plan-dock').exists()).toBe(true);
		expect(wrapper.find('.todo-plan-active').exists()).toBe(false);
	});

	it('drops the header hint once expanded, where the list marks the item itself', async () => {
		wrapper = mount(TodoPlanDock, { props: { todo: plan(planItems()), open: false } });
		expect(wrapper.find('.todo-plan-active').exists()).toBe(true);

		await wrapper.setProps({ open: true });

		expect(wrapper.find('.todo-plan-active').exists()).toBe(false);
		expect(wrapper.get('.todo-plan-item.in_progress .todo-plan-item-text').text()).toBe('投影到 transcript 卡片');
		expect(wrapper.get('.todo-plan-item.in_progress .todo-plan-item-active').text()).toBe('正在投影');
	});

	it('renders nothing once every task is completed or the plan is missing', async () => {
		wrapper = mount(TodoPlanDock, { props: { todo: donePlan(), open: true } });
		expect(wrapper.find('.todo-plan-dock').exists()).toBe(false);

		wrapper.unmount();
		wrapper = mount(TodoPlanDock, { props: { todo: null, open: true } });
		expect(wrapper.find('.todo-plan-dock').exists()).toBe(false);

		wrapper.unmount();
		// 后端省略字段时组件拿到的是 undefined，而不是 {}。
		wrapper = mount(TodoPlanDock, { props: { open: true } });
		expect(wrapper.find('.todo-plan-dock').exists()).toBe(false);

		wrapper.unmount();
		wrapper = mount(TodoPlanDock, { props: { todo: plan([]), open: true } });
		expect(wrapper.find('.todo-plan-dock').exists()).toBe(false);
	});

	it('keeps long plans inside their own scroll area', () => {
		wrapper = mount(TodoPlanDock, { props: { todo: plan(planItems()), open: true } });
		expect(wrapper.get('.todo-plan-scroll').find('.todo-plan-list').exists()).toBe(true);
	});
});

describe('useChatTodo', () => {
	it('defaults to the busy state and remembers a manual choice per conversation', () => {
		const conversationId = ref('todo-conv-a');
		const todo = useChatTodo(conversationId);
		const open = computed(() => todo.isOpen(true));

		expect(open.value).toBe(true);
		todo.toggle(true);
		expect(open.value).toBe(false);
		// 空闲时默认折叠，但手动展开过的会话仍然记住「展开」。
		expect(todo.isOpen(false)).toBe(false);

		conversationId.value = 'todo-conv-b';
		expect(todo.isOpen(false)).toBe(false);
		todo.toggle(false);
		expect(open.value).toBe(true);

		conversationId.value = 'todo-conv-a';
		expect(todo.isOpen(true)).toBe(false);
	});
});

describe('TodoPlanList', () => {
	it('maps every status to its own icon class and marks the compact variant', () => {
		wrapper = mount(TodoPlanList, { props: { items: normalizeTodoItems(plan(planItems())), compact: true } });

		expect(wrapper.classes()).toContain('compact');
		expect(wrapper.findAll('.todo-plan-item.pending')).toHaveLength(2);
		expect(wrapper.findAll('.todo-plan-item.in_progress')).toHaveLength(1);
		expect(wrapper.findAll('.todo-plan-item.completed')).toHaveLength(2);
		expect(wrapper.findAll('.todo-plan-item-icon')).toHaveLength(5);
	});
});

describe('TodoCard', () => {
	it('is collapsed by default and reveals the plan items when expanded', async () => {
		wrapper = mount(TodoCard, { props: { id: 'segment-todo-1', segment: todoSegment(), open: false } });

		const head = wrapper.get('.todo-summary');
		expect(head.element.tagName).toBe('BUTTON');
		expect(head.attributes('aria-expanded')).toBe('false');
		expect(head.text()).toContain('Updated plan (2/5)');
		expect(wrapper.find('.todo-plan-list').exists()).toBe(false);

		await head.trigger('click');
		expect(wrapper.emitted('toggle')).toHaveLength(1);

		await wrapper.setProps({ open: true });
		expect(wrapper.get('.todo-summary').attributes('aria-expanded')).toBe('true');
		expect(wrapper.findAll('.todo-plan-item')).toHaveLength(5);
		expect(wrapper.get('.todo-plan-list').classes()).toContain('compact');
	});

	it('degrades an emptied plan to a single non-interactive line', () => {
		wrapper = mount(TodoCard, {
			props: { id: 'segment-todo-2', segment: todoSegment({ text: 'Cleared plan', todo: plan([]) }), open: false },
		});

		const head = wrapper.get('.todo-summary');
		expect(head.element.tagName).toBe('DIV');
		expect(head.attributes('aria-expanded')).toBeUndefined();
		expect(head.text()).toBe('已清空任务计划');
		expect(wrapper.find('button').exists()).toBe(false);
		expect(wrapper.find('.todo-plan-list').exists()).toBe(false);
	});

	it('keeps the failure detail available inside the expanded row', () => {
		wrapper = mount(TodoCard, {
			props: {
				id: 'segment-todo-3',
				segment: todoSegment({ status: 'failed', text: '更新任务计划失败', detailText: '超过 30 项' }),
				open: true,
			},
		});

		expect(wrapper.get('.todo-block').classes()).toContain('failed');
		expect(wrapper.get('.todo-details-note').text()).toBe('超过 30 项');
	});

	it('does not repeat the progress that the summary text already carries', () => {
		wrapper = mount(TodoCard, { props: { id: 'segment-todo-4', segment: todoSegment(), open: false } });
		expect(wrapper.find('.todo-summary-progress').exists()).toBe(false);

		wrapper.unmount();
		wrapper = mount(TodoCard, {
			props: { id: 'segment-todo-5', segment: todoSegment({ text: '更新任务计划' }), open: false },
		});
		expect(wrapper.get('.todo-summary-progress').text()).toBe('2/5');
	});
});
