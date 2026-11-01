import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { installActivityHost } from './fixtures/activityHost.js';

let wrapper;
beforeEach(() => {
	vi.resetModules();
	localStorage.clear();
	installActivityHost();
});
afterEach(() => { wrapper?.unmount(); wrapper = null; });

const WORKSPACE = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const requestsOf = (type) => window.activityFixture.requests.filter((request) => request.type === type);

async function mountPane(props = {}) {
	const { default: Pane } = await import('../src/components/dock/ReviewPane.vue');
	wrapper = mount(Pane, { props: { workspaceRootId: WORKSPACE, visible: true, ...props } });
	await flushPromises();
	return wrapper;
}

const rows = () => wrapper.findAll('.file-row');
const activePath = () => wrapper.find('.file-row.active').attributes('data-path');
const scopeSelect = () => wrapper.find('select.scope-select');

async function clickRow(path) {
	await wrapper.find(`.file-row[data-path="${path}"]`).trigger('click');
	await flushPromises();
}

it('loads changes when visible, auto-selects the first file, and renders its unified diff', async () => {
	await mountPane();
	expect(requestsOf('git-diff/changes')).toHaveLength(1);
	expect(scopeSelect().findAll('option').map((option) => option.text())).toEqual(['未暂存 (3)', '已暂存 (1)']);
	expect(rows().map((row) => row.attributes('data-path'))).toEqual(['src/App.vue', 'src/fresh.js', 'assets/logo.png']);
	expect(rows()[0].find('.file-badge').text()).toBe('M');
	expect(rows()[1].find('.file-badge').text()).toBe('U');
	expect(rows()[2].find('.file-stat.muted').text()).toBe('二进制');
	expect(activePath()).toBe('src/App.vue');
	const fileRequests = requestsOf('git-diff/file');
	expect(fileRequests).toHaveLength(1);
	expect(fileRequests[0]).toMatchObject({ scope: 'unstaged', path: 'src/App.vue', kind: 'modified' });
	expect(wrapper.find('.diff-path').text()).toBe('src/App.vue');
	expect(wrapper.find('.diff-stat').text()).toBe('+3−1');
	expect(wrapper.findAll('.diff-hunk')).toHaveLength(1);
	expect(wrapper.find('.diff-hunk .code').text()).toBe('import X');
	expect(wrapper.findAll('.diff-line.add')).toHaveLength(3);
	expect(wrapper.findAll('.diff-line.remove')).toHaveLength(1);
	expect(wrapper.findAll('.diff-line.context')).toHaveLength(2);
	expect(wrapper.find('.diff-line.remove .code').text()).toBe('const a = 1;');
	// 单列行号：删除行显示旧行号，新增行显示新行号；.vue 的脚本段按 javascript 高亮。
	expect(wrapper.find('.diff-line.remove .gutter').text()).toBe('11');
	expect(wrapper.findAll('.diff-line.add .gutter').map((cell) => cell.text())).toEqual(['11', '12', '13']);
	expect(wrapper.find('.diff-line.remove .code .hljs-keyword').text()).toBe('const');
	expect(wrapper.find('.diff-line .mark').exists()).toBe(false);
	expect(wrapper.find('.pane-branch').text()).toBe('main');
});

it('does not request while hidden and refreshes once it becomes visible', async () => {
	await mountPane({ visible: false });
	expect(requestsOf('git-diff/changes')).toHaveLength(0);
	await wrapper.setProps({ visible: true });
	await flushPromises();
	expect(requestsOf('git-diff/changes')).toHaveLength(1);
	await wrapper.setProps({ visible: false });
	await wrapper.setProps({ visible: true });
	await flushPromises();
	expect(requestsOf('git-diff/changes')).toHaveLength(2);
});

it('switching scope reuses the loaded lists and only fetches the newly selected file', async () => {
	await mountPane();
	await scopeSelect().setValue('staged');
	await flushPromises();
	expect(requestsOf('git-diff/changes')).toHaveLength(1);
	expect(rows().map((row) => row.attributes('data-kind'))).toEqual(['renamed']);
	expect(activePath()).toBe('src/new-name.js');
	const fileRequests = requestsOf('git-diff/file');
	expect(fileRequests).toHaveLength(2);
	expect(fileRequests[1]).toMatchObject({ scope: 'staged', path: 'src/new-name.js', oldPath: 'src/old-name.js', kind: 'renamed' });
	expect(wrapper.find('.diff-path s').text()).toBe('src/old-name.js');
	expect(wrapper.find('.diff-notice').text()).toContain('仅重命名');
	await scopeSelect().setValue('unstaged');
	await flushPromises();
	expect(activePath()).toBe('src/App.vue');
	expect(requestsOf('git-diff/file')).toHaveLength(3);
});

it('clicking a file forwards its kind, untracked files render as added lines, binaries show a notice', async () => {
	await mountPane();
	await clickRow('src/fresh.js');
	expect(requestsOf('git-diff/file').at(-1)).toMatchObject({ path: 'src/fresh.js', kind: 'untracked' });
	expect(activePath()).toBe('src/fresh.js');
	expect(wrapper.findAll('.diff-line.add')).toHaveLength(2);
	expect(wrapper.findAll('.diff-line.remove')).toHaveLength(0);
	// 新文件从第 1 行开始，没有被省略的行，不出分隔条。
	expect(wrapper.findAll('.diff-hunk')).toHaveLength(0);
	await clickRow('assets/logo.png');
	expect(wrapper.find('.diff-notice').text()).toBe('二进制文件，不显示内容');
	expect(wrapper.find('.diff-table').exists()).toBe(false);
});

it('arrow keys move the selection through the file list', async () => {
	await mountPane();
	await wrapper.find('.file-list').trigger('keydown', { key: 'ArrowDown' });
	await flushPromises();
	expect(activePath()).toBe('src/fresh.js');
	await wrapper.find('.file-list').trigger('keydown', { key: 'ArrowUp' });
	await flushPromises();
	expect(activePath()).toBe('src/App.vue');
	await wrapper.find('.file-list').trigger('keydown', { key: 'ArrowUp' });
	await flushPromises();
	expect(activePath()).toBe('src/App.vue');
});

it('prev / next buttons step through the current list and disable at the ends', async () => {
	await mountPane();
	const previous = () => wrapper.find('.pane-step-prev');
	const next = () => wrapper.find('.pane-step-next');
	expect(previous().attributes('disabled')).toBeDefined();
	expect(next().attributes('disabled')).toBeUndefined();
	await next().trigger('click');
	await flushPromises();
	expect(activePath()).toBe('src/fresh.js');
	await next().trigger('click');
	await flushPromises();
	expect(activePath()).toBe('assets/logo.png');
	expect(next().attributes('disabled')).toBeDefined();
	await previous().trigger('click');
	await flushPromises();
	expect(activePath()).toBe('src/fresh.js');
	expect(requestsOf('git-diff/file')).toHaveLength(4);
});

it('the file list collapses from the toolbar and the preference survives a remount', async () => {
	await mountPane();
	expect(wrapper.find('.review-files').attributes('style') ?? '').not.toContain('display: none');
	await wrapper.find('.pane-collapse-files').trigger('click');
	expect(wrapper.find('.review-files').attributes('style')).toContain('display: none');
	expect(wrapper.find('.review-body').classes()).toContain('files-collapsed');
	expect(localStorage.getItem('selfclaw:review-files-collapsed')).toBe('true');
	// 折叠后上一个 / 下一个仍可用。
	await wrapper.find('.pane-step-next').trigger('click');
	await flushPromises();
	expect(wrapper.find('.diff-path').text()).toBe('src/fresh.js');
	wrapper.unmount();
	await mountPane();
	expect(wrapper.find('.review-files').attributes('style')).toContain('display: none');
	await wrapper.find('.pane-collapse-files').trigger('click');
	expect(wrapper.find('.review-files').attributes('style') ?? '').not.toContain('display: none');
	expect(localStorage.getItem('selfclaw:review-files-collapsed')).toBe('false');
});

it('shows the not-a-repository empty state without fetching any file', async () => {
	window.activityFixture.gitRepository(false);
	await mountPane();
	expect(wrapper.find('.review-empty strong').text()).toBe('当前 workspace 不在 Git 仓库中');
	expect(wrapper.find('.review-empty p').text()).toContain('workspace 作用域内的改动');
	expect(rows()).toHaveLength(0);
	expect(requestsOf('git-diff/file')).toHaveLength(0);
	expect(scopeSelect().exists()).toBe(true);
});

it('shows the clean-workspace empty state per scope', async () => {
	window.activityFixture.gitEmpty(true);
	await mountPane();
	expect(wrapper.find('.review-empty strong').text()).toBe('没有未暂存的改动');
	await scopeSelect().setValue('staged');
	await flushPromises();
	expect(wrapper.find('.review-empty strong').text()).toBe('没有已暂存的改动');
	expect(requestsOf('git-diff/file')).toHaveLength(0);
});

it('shows the no-workspace empty state without touching the host', async () => {
	await mountPane({ workspaceRootId: null });
	expect(requestsOf('git-diff/changes')).toHaveLength(0);
	expect(wrapper.find('.review-empty strong').text()).toBe('尚未选择工作目录');
	expect(wrapper.find('.pane-refresh').attributes('disabled')).toBeDefined();
});

it('surfaces host errors inline and recovers on refresh', async () => {
	window.activityFixture.gitError('git exploded');
	await mountPane();
	expect(wrapper.find('.pane-error').text()).toBe('git exploded');
	expect(rows()).toHaveLength(0);
	window.activityFixture.gitError(null);
	await wrapper.find('.pane-refresh').trigger('click');
	await flushPromises();
	expect(wrapper.find('.pane-error').exists()).toBe(false);
	expect(rows()).toHaveLength(3);
});

it('flags truncated patches', async () => {
	window.activityFixture.gitTruncated(true);
	await mountPane();
	expect(wrapper.find('.diff-truncated').text()).toContain('已截断');
});

it('refresh re-reads the lists and keeps the current selection', async () => {
	await mountPane();
	await clickRow('assets/logo.png');
	await wrapper.find('.pane-refresh').trigger('click');
	await flushPromises();
	expect(requestsOf('git-diff/changes')).toHaveLength(2);
	expect(activePath()).toBe('assets/logo.png');
	expect(requestsOf('git-diff/file').at(-1)).toMatchObject({ path: 'assets/logo.png' });
});

it('a workspace change refreshes while visible and only clears the selection while hidden', async () => {
	await mountPane();
	await clickRow('src/fresh.js');
	await wrapper.setProps({ workspaceRootId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd' });
	await flushPromises();
	expect(requestsOf('git-diff/changes')).toHaveLength(2);
	expect(activePath()).toBe('src/App.vue');
	await wrapper.setProps({ visible: false });
	await wrapper.setProps({ workspaceRootId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee' });
	await flushPromises();
	expect(requestsOf('git-diff/changes')).toHaveLength(2);
	expect(wrapper.find('.file-row.active').exists()).toBe(false);
	await wrapper.setProps({ visible: true });
	await flushPromises();
	expect(requestsOf('git-diff/changes')).toHaveLength(3);
	expect(activePath()).toBe('src/App.vue');
});

it('discards a stale file response when a newer selection superseded it', async () => {
	await mountPane();
	window.activityFixture.holdResponses(true);
	await clickRow('src/fresh.js');
	await clickRow('assets/logo.png');
	expect(wrapper.find('.diff-status').text()).toContain('加载中');
	window.activityFixture.release(0);
	await flushPromises();
	expect(wrapper.find('.diff-table').exists()).toBe(false);
	window.activityFixture.release(0);
	await flushPromises();
	expect(activePath()).toBe('assets/logo.png');
	expect(wrapper.find('.diff-notice').text()).toContain('二进制');
	expect(wrapper.find('.diff-view .pane-error').exists()).toBe(false);
});
