import { expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import AppSidebar from '../src/components/sidebar/AppSidebar.vue';

function conversation(id, label) {
	return { id, label, type: 'conversation', workspaceRootId: null, isManagedWorktree: false };
}

function items() {
	return [
		{ id: 'new-chat', label: '新建对话', type: 'action' },
		{ id: 'search', label: '搜索', type: 'action' },
		{
			id: 'projects',
			label: '项目',
			type: 'group',
			children: [
				{
					id: 'workspace-repo',
					label: 'repo',
					type: 'folder',
					workspaceRootId: 'root-1',
					children: [conversation('c-1', '重构侧栏搜索'), conversation('c-2', '修复登录超时')],
				},
			],
		},
		{
			id: 'conversations',
			label: '对话',
			type: 'group',
			children: [conversation('c-3', '搜索历史设计'), conversation('c-4', '周会纪要')],
		},
	];
}

function mountSidebar(props = {}, options = {}) {
	return mount(AppSidebar, { props: { items: items(), activeId: null, collapsed: false, ...props }, ...options });
}

function nodeLabels(wrapper) {
	return wrapper.findAll('.node.kind-chat .ntext').map((node) => node.text());
}

// 侧栏顶部的搜索是一个真输入框，而不是点了没反应的按钮。
it('renders a search input above the functional entries', () => {
	const wrapper = mountSidebar();

	const input = wrapper.get('.tool-list .tool-search input');
	expect(input.element.getAttribute('placeholder')).toBe('搜索');
	expect(wrapper.findAll('.tool-btn')).toHaveLength(0);
	wrapper.unmount();
});

it('filters the conversation list by title as the user types', async () => {
	const wrapper = mountSidebar();

	await wrapper.get('.search-input').setValue('搜索');

	expect(nodeLabels(wrapper)).toEqual(['重构侧栏搜索', '搜索历史设计']);
	wrapper.unmount();
});

// 项目分组是「工作区 → 会话」两级：命中会话时父级工作区必须跟着留下并自动展开。
it('keeps the workspace folder of a matching conversation and expands it', async () => {
	const wrapper = mountSidebar();

	await wrapper.get('.search-input').setValue('登录');

	expect(wrapper.get('.subfolder .project-folder .folder-name').text()).toBe('repo');
	expect(wrapper.get('.subfolder').classes()).toContain('open');
	expect(nodeLabels(wrapper)).toEqual(['修复登录超时']);
	wrapper.unmount();
});

// 只按标题检索：工作区名命中而会话标题不命中时不给结果。
it('ignores workspace names that no conversation title matches', async () => {
	const wrapper = mountSidebar();

	await wrapper.get('.search-input').setValue('repo');

	expect(nodeLabels(wrapper)).toEqual([]);
	expect(wrapper.get('.search-empty').text()).toContain('repo');
	wrapper.unmount();
});

it('restores the full list when the query is cleared', async () => {
	const wrapper = mountSidebar();

	await wrapper.get('.search-input').setValue('搜索');
	await wrapper.get('.search-clear').trigger('click');

	// 清空后回到检索前的折叠状态：项目文件夹收起（子节点不再挂载），对话分组照旧展开。
	expect(wrapper.find('.subfolder-body').exists()).toBe(false);
	expect(nodeLabels(wrapper)).toEqual(['搜索历史设计', '周会纪要']);
	expect(wrapper.find('.search-empty').exists()).toBe(false);
	wrapper.unmount();
});

it('clears the query on Escape and drops focus once the field is empty', async () => {
	const wrapper = mountSidebar({}, { attachTo: document.body });

	const input = wrapper.get('.search-input');
	await input.setValue('登录');
	await input.trigger('keydown', { key: 'Escape' });

	expect(input.element.value).toBe('');
	expect(nodeLabels(wrapper)).toEqual(['搜索历史设计', '周会纪要']);
	wrapper.unmount();
});

// Ctrl K 是输入框右侧写着的快捷键，按下就必须把焦点交进去。
it('focuses the search field on Ctrl K', async () => {
	const wrapper = mountSidebar({}, { attachTo: document.body });

	document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }));
	await nextTick();

	expect(document.activeElement).toBe(wrapper.get('.search-input').element);
	wrapper.unmount();
});

// 输入框周围（图标、留白）也属于搜索区域，点上去应当落到输入框里。
it('focuses the input when the search row itself is clicked', async () => {
	const wrapper = mountSidebar({}, { attachTo: document.body });

	await wrapper.get('.search-ico').trigger('click');

	expect(document.activeElement).toBe(wrapper.get('.search-input').element);
	wrapper.unmount();
});

it('expands the collapsed sidebar before focusing the search field', async () => {
	const wrapper = mountSidebar({ collapsed: true });

	document.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }));
	await nextTick();

	expect(wrapper.emitted('toggle-collapse')).toHaveLength(1);
	wrapper.unmount();
});
