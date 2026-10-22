// 侧栏折叠分支的渲染契约：收起的分组 / 项目文件夹不挂载任何会话节点。
// 宿主一次性把全部会话推下来，隐藏而非卸载会让几千条会话常驻 DOM。
import { expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import AppSidebar from '../src/components/sidebar/AppSidebar.vue';

function conversation(id, label) {
	return { id, label, type: 'conversation', workspaceRootId: null, isManagedWorktree: false };
}

function items() {
	return [
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
					children: [conversation('c-1', '项目会话 A'), conversation('c-2', '项目会话 B')],
				},
			],
		},
		{ id: 'conversations', label: '对话', type: 'group', children: [conversation('c-3', '普通会话 A')] },
	];
}

function mountSidebar(props = {}) {
	return mount(AppSidebar, { props: { items: items(), activeId: null, collapsed: false, ...props } });
}

function nodeLabels(wrapper) {
	return wrapper.findAll('.node.kind-chat .ntext').map((node) => node.text());
}

it('mounts nothing under a collapsed group while keeping its header', () => {
	const wrapper = mountSidebar();

	// 「项目」分组开着，但里面的工作区文件夹默认收起 → 两个项目会话都不在 DOM 里。
	expect(wrapper.findAll('.group').length).toBe(2);
	expect(wrapper.find('.subfolder-body').exists()).toBe(false);
	expect(nodeLabels(wrapper)).toEqual(['普通会话 A']);
	wrapper.unmount();
});

it('mounts the branch when the group is expanded and removes it when collapsed again', async () => {
	const wrapper = mountSidebar();
	const projectHead = wrapper.findAll('.group-head')[0];

	// 两个分组默认都展开 → 两块 body；收起「项目」只剩「对话」那块。
	expect(wrapper.findAll('.group-body').length).toBe(2);
	await projectHead.trigger('click');
	expect(wrapper.findAll('.group-body').length).toBe(1);
	expect(wrapper.find('.project-folder').exists()).toBe(false);

	await projectHead.trigger('click');
	expect(wrapper.findAll('.group-body').length).toBe(2);
	expect(wrapper.find('.project-folder').exists()).toBe(true);
	wrapper.unmount();
});

it('mounts project conversations only while the workspace folder is open', async () => {
	const wrapper = mountSidebar();

	await wrapper.get('.project-folder').trigger('click');
	expect(nodeLabels(wrapper)).toEqual(['项目会话 A', '项目会话 B', '普通会话 A']);

	await wrapper.get('.project-folder').trigger('click');
	expect(nodeLabels(wrapper)).toEqual(['普通会话 A']);
	wrapper.unmount();
});

// 选中的会话在收起的工作区里时，必须能看见它，否则侧栏会像丢了选中项。
it('keeps the folder of the selected conversation expanded', () => {
	const wrapper = mountSidebar({ activeId: 'c-2' });

	expect(wrapper.get('.subfolder').classes()).toContain('open');
	expect(nodeLabels(wrapper)).toEqual(['项目会话 A', '项目会话 B', '普通会话 A']);
	wrapper.unmount();
});
