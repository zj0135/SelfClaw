import { expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import AppSidebar from '../src/components/sidebar/AppSidebar.vue';

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

// 折叠的项目文件夹不渲染子节点（与真实交互一致）：要用会话行先点开文件夹。
async function expandProjects(wrapper) {
	if (!wrapper.find('.subfolder-body').exists()) {
		await wrapper.get('.project-folder').trigger('click');
	}

	return wrapper;
}

it('renders the folder menu in the agreed order', async () => {
	const wrapper = mountSidebar(projectItems());

	await wrapper.get('.project-folder').trigger('contextmenu');

	expect(labelsOf(wrapper)).toEqual(['工作目录', '重命名', '在资源管理器打开', '清空会话列表']);
	wrapper.unmount();
});

it('renders the conversation menu in the agreed order', async () => {
	const wrapper = mountSidebar(projectItems());

	await expandProjects(wrapper);
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

async function openRenameEditor(wrapper, selector = '.subfolder-body .node') {
	await expandProjects(wrapper);
	await wrapper.get(selector).trigger('contextmenu');
	await menuButton(wrapper, '重命名').trigger('click');
	return wrapper.get('.node.renaming input.rename-input');
}

it('renders an inline editor with confirm and cancel when renaming a conversation', async () => {
	const wrapper = mountSidebar(projectItems());

	const input = await openRenameEditor(wrapper);

	expect(input.element.value).toBe('会话 A');
	expect(wrapper.find('.node.renaming .rename-action.confirm').exists()).toBe(true);
	expect(wrapper.find('.node.renaming .rename-action.cancel').exists()).toBe(true);
	expect(wrapper.find('.context-menu').exists()).toBe(false);
	wrapper.unmount();
});

it('emits the rename action with the trimmed title on confirm', async () => {
	const wrapper = mountSidebar(projectItems());

	const input = await openRenameEditor(wrapper);
	await input.setValue('  会话 B  ');
	await wrapper.get('.node.renaming .rename-action.confirm').trigger('click');

	expect(wrapper.emitted('action')).toEqual([
		[{ id: 'rename-conversation', conversationId: 'c-1', title: '会话 B' }],
	]);
	expect(wrapper.find('.node.renaming').exists()).toBe(false);
	wrapper.unmount();
});

it('confirms the rename when Enter is pressed', async () => {
	const wrapper = mountSidebar(projectItems());

	const input = await openRenameEditor(wrapper);
	await input.setValue('会话 C');
	await input.trigger('keydown', { key: 'Enter' });

	expect(wrapper.emitted('action')).toEqual([
		[{ id: 'rename-conversation', conversationId: 'c-1', title: '会话 C' }],
	]);
	wrapper.unmount();
});

it('drops the edit without emitting when rename is cancelled', async () => {
	const wrapper = mountSidebar(projectItems());

	const input = await openRenameEditor(wrapper);
	await input.setValue('丢弃');
	await wrapper.get('.node.renaming .rename-action.cancel').trigger('click');

	expect(wrapper.emitted('action')).toBeUndefined();
	expect(wrapper.find('.node.renaming').exists()).toBe(false);
	expect(wrapper.get('.subfolder-body .node .ntext').text()).toBe('会话 A');
	wrapper.unmount();
});

it('drops the edit without emitting when Escape is pressed', async () => {
	const wrapper = mountSidebar(projectItems());

	const input = await openRenameEditor(wrapper);
	await input.setValue('丢弃');
	await input.trigger('keydown', { key: 'Escape' });

	expect(wrapper.emitted('action')).toBeUndefined();
	expect(wrapper.find('.node.renaming').exists()).toBe(false);
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
