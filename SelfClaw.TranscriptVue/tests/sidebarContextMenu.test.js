import { expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import AppSidebar from '../src/components/SideBar/AppSidebar.vue';

const workspaceRootId = '11111111-1111-1111-1111-111111111111';

function projectItems(conversationOverrides = {}) {
	const folder = {
		id: 'workspace-repo',
		label: 'repo',
		type: 'folder',
		workspaceRootId,
		children: [
			{
				id: 'c-1',
				label: '会话 A',
				type: 'conversation',
				workspaceRootId,
				isManagedWorktree: false,
				...conversationOverrides,
			},
		],
	};
	return [{ id: 'projects', label: '项目', type: 'group', children: [folder] }];
}

function mountSidebar(items) {
	return mount(AppSidebar, { props: { items, activeId: null, collapsed: false } });
}

function labelsOf(wrapper) {
	return wrapper.findAll('.context-menu-item .context-menu-label').map((node) => node.text());
}

function menuButton(wrapper, label) {
	return wrapper.findAll('.context-menu-item').find((node) => node.text() === label);
}

it('renders the folder menu in the agreed order', async () => {
	const wrapper = mountSidebar(projectItems());

	await wrapper.get('.project-folder').trigger('contextmenu');

	expect(labelsOf(wrapper)).toEqual(['工作目录', '重命名', '在资源管理器打开', '清空会话列表']);
	wrapper.unmount();
});

it('renders the conversation menu in the agreed order', async () => {
	const wrapper = mountSidebar(projectItems());

	await wrapper.get('.subfolder-body .node').trigger('contextmenu');

	expect(labelsOf(wrapper)).toEqual(['工作目录', '重命名', '在资源管理器打开', '删除']);
	wrapper.unmount();
});

it('emits the workspace root id when opening a project folder in explorer', async () => {
	const wrapper = mountSidebar(projectItems());

	await wrapper.get('.project-folder').trigger('contextmenu');
	await menuButton(wrapper, '在资源管理器打开').trigger('click');

	expect(wrapper.emitted('action')).toEqual([[{ id: 'open-in-explorer', workspaceRootId }]]);
	wrapper.unmount();
});

it('hides workspace actions for conversations without a workspace root', async () => {
	const wrapper = mountSidebar([
		{
			id: 'conversations',
			label: '对话',
			type: 'group',
			children: [{ id: 'c-1', label: '无工作区', type: 'conversation', workspaceRootId: null }],
		},
	]);

	await wrapper.get('.node.kind-chat').trigger('contextmenu');

	expect(labelsOf(wrapper)).toEqual(['重命名', '删除']);
	wrapper.unmount();
});
