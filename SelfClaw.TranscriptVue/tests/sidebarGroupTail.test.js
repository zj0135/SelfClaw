// 分组标题右端的对齐契约：计数与加号共用同一个槽位（叠放），
// 否则没有加号的「归档」会把数字顶到最右边，三组数字不在同一条竖线上。
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import AppSidebar from '../src/components/sidebar/AppSidebar.vue';

const sidebar = readFileSync(resolve(process.cwd(), 'src/components/sidebar/AppSidebar.vue'), 'utf8');

function conversation(id) {
	return { id, label: id, type: 'conversation', workspaceRootId: null, isManagedWorktree: false };
}

function items() {
	return [
		{ id: 'projects', label: '项目', type: 'group', layout: 'folders', canCreate: true, emptyText: '暂无项目会话', children: [] },
		{ id: 'conversations', label: '对话', type: 'group', layout: 'list', canCreate: true, emptyText: '暂无会话记录', children: [conversation('c-1')] },
		{ id: 'archived', label: '归档', type: 'group', layout: 'folders', canCreate: false, emptyText: '暂无归档会话', children: [] },
	];
}

function mountSidebar() {
	return mount(AppSidebar, { props: { items: items(), activeId: null, collapsed: false } });
}

describe('分组尾部槽位', () => {
	it('每个分组的计数都在同一个尾部槽位里', () => {
		const wrapper = mountSidebar();

		const tails = wrapper.findAll('.group-head .group-tail');
		expect(tails).toHaveLength(3);
		for (const tail of tails) {
			expect(tail.find('.group-count').exists()).toBe(true);
		}
		// 槽位是标题之外的最后一个子元素：它决定数字落在哪条竖线上。
		for (const head of wrapper.findAll('.group-head')) {
			expect(head.element.lastElementChild.classList.contains('group-tail')).toBe(true);
		}
		wrapper.unmount();
	});

	it('只有带加号的分组标 has-create，hover 时才会把数字让出去', () => {
		const wrapper = mountSidebar();

		const heads = wrapper.findAll('.group-head');
		expect(heads[0].classes()).toContain('has-create');
		expect(heads[1].classes()).toContain('has-create');
		// 归档没有「新建归档」，数字必须一直可见。
		expect(heads[2].classes()).not.toContain('has-create');
		expect(heads[2].find('.group-add').exists()).toBe(false);
		wrapper.unmount();
	});

	it('样式里计数与加号是叠放（同一 grid 格），且 hover 让位只作用于 has-create', () => {
		expect(sidebar).toMatch(/\.group-tail > \*\s*\{[^}]*grid-area:\s*1\s*\/\s*1/);
		expect(sidebar).toContain('.group-head.has-create:hover .group-count');
		// 加号本身仍由 head 的 hover 唤出。
		expect(sidebar).toContain('.group-head:hover .group-add');
		// 叠放的两个子元素都不吃鼠标事件：真正接事件的是整格 .group-tail，
		// 否则鼠标落在数字 / 图标缝隙上会穿透到标题，点加号变成折叠分组。
		expect(sidebar).toMatch(/\.group-tail > \*\s*\{[^}]*pointer-events:\s*none/);
	});

	// 内部元素收不到鼠标事件，`.group-add:hover` 永远不会命中：强调色底必须挂在槽位上，
	// 否则加号 hover 又会退回「没有反馈」。
	it('加号的 hover 强调色挂在槽位上，而不是加号自己', () => {
		expect(sidebar).toMatch(/\.group-head\.has-create \.group-tail:hover \.group-add\s*\{[^}]*background:\s*var\(--sb-accent-soft\)[^}]*color:\s*var\(--sb-accent\)/);
		// 只拦「规则」，注释里可以照旧提到这个失效的选择器。
		expect(sidebar).not.toMatch(/(^|\n)\s*\.group-add:hover\s*\{/);
	});

	it('尾部的整格是加号热区：点哪里都发出 add-<groupId>，且不折叠分组', async () => {
		const wrapper = mountSidebar();
		const group = wrapper.findAll('.group')[0];

		await group.get('.group-tail').trigger('click');

		expect(wrapper.emitted('action')).toEqual([['add-projects']]);
		// 折叠状态没被顺手切掉。
		expect(group.classes()).toContain('open');
		wrapper.unmount();
	});

	it('没有加号的分组：尾部点击照旧落到标题做折叠，不发 action', async () => {
		const wrapper = mountSidebar();
		const archived = wrapper.findAll('.group')[2];

		// 归档分组默认是收起的：这一格没有热区，点击应当照常展开它。
		expect(archived.classes()).not.toContain('open');

		await archived.get('.group-tail').trigger('click');

		expect(wrapper.emitted('action')).toBeUndefined();
		expect(archived.classes()).toContain('open');
		wrapper.unmount();
	});
});
