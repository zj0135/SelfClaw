// 侧栏「归档」分组的数据契约：归档只看最后一次对话时间（滚动 7×24 小时），
// 三个分组互斥；归档内沿用「工作区文件夹 → 会话」的形态。
import { describe, expect, it } from 'vitest';
import {
	ARCHIVE_AFTER_MS,
	ARCHIVE_UNBOUND_FOLDER_ID,
	buildSidebarGroups,
	isArchived,
} from '../src/renderers/sidebarGroups.js';

const NOW = Date.parse('2026-03-10T12:00:00.000Z');
const DAY = 24 * 60 * 60 * 1000;

function conversation(id, { title = id, ago = DAY, workspace = null, extra = {} } = {}) {
	const updatedAtUtc = new Date(NOW - ago).toISOString();
	return {
		id,
		title,
		timestamp: '2026-03-01 08:00',
		workspaceRootId: workspace?.id ?? null,
		workspaceRootName: workspace?.name ?? null,
		workspaceRootPath: workspace?.path ?? null,
		gitRepositoryId: workspace?.gitId ?? null,
		gitRepositoryName: workspace?.gitName ?? null,
		isManagedWorktree: false,
		isRunning: false,
		updatedAtUtc,
		...extra,
	};
}

const repoA = { id: 'root-a', name: 'repo-a', path: 'E:\\repo-a', gitId: 'git-a', gitName: 'repo-a' };
const repoB = { id: 'root-b', name: 'repo-b', path: 'E:\\repo-b', gitId: 'git-b', gitName: 'repo-b' };

function build(conversations, extra = {}) {
	return buildSidebarGroups({ conversations, now: NOW, ...extra });
}

describe('isArchived', () => {
	it('只把严格超过 168 小时的会话算作归档', () => {
		expect(isArchived(conversation('c', { ago: 6 * DAY }), NOW)).toBe(false);
		expect(isArchived(conversation('c', { ago: ARCHIVE_AFTER_MS - 1 }), NOW)).toBe(false);
		// 恰好 7 天不算「超过 7 天」。
		expect(isArchived(conversation('c', { ago: ARCHIVE_AFTER_MS }), NOW)).toBe(false);
		expect(isArchived(conversation('c', { ago: ARCHIVE_AFTER_MS + 1000 }), NOW)).toBe(true);
	});

	it('时间缺失或不可解析时留在原位', () => {
		expect(isArchived(conversation('c', { extra: { updatedAtUtc: null } }), NOW)).toBe(false);
		expect(isArchived(conversation('c', { extra: { updatedAtUtc: '' } }), NOW)).toBe(false);
		expect(isArchived(conversation('c', { extra: { updatedAtUtc: 'not-a-date' } }), NOW)).toBe(false);
		// 时钟回拨导致的「未来时间」也不该归档。
		expect(isArchived(conversation('c', { ago: -DAY }), NOW)).toBe(false);
	});
});

describe('buildSidebarGroups', () => {
	it('三个分组互斥：有工作区且新鲜 → 项目，无工作区且新鲜 → 对话，其余 → 归档', () => {
		const groups = build([
			conversation('fresh-project', { workspace: repoA, ago: 2 * DAY }),
			conversation('fresh-plain', { ago: 2 * DAY }),
			conversation('old-project', { workspace: repoA, ago: 30 * DAY }),
			conversation('old-plain', { ago: 30 * DAY }),
		]);

		expect(groups.projects.flatMap((folder) => folder.children.map((child) => child.id))).toEqual(['fresh-project']);
		expect(groups.conversations.map((node) => node.id)).toEqual(['fresh-plain']);
		expect(groups.archived.flatMap((folder) => folder.children.map((child) => child.id))).toEqual(['old-project', 'old-plain']);
	});

	it('归档内按工作区分文件夹，未绑定工作区的会话收进伪文件夹并排在最后', () => {
		const groups = build([
			conversation('old-plain', { ago: 30 * DAY }),
			conversation('old-a', { workspace: repoA, ago: 40 * DAY }),
			conversation('old-b', { workspace: repoB, ago: 20 * DAY }),
		]);

		expect(groups.archived.map((folder) => folder.id)).toEqual(['archive-workspace-git-b', 'archive-workspace-git-a', ARCHIVE_UNBOUND_FOLDER_ID]);
		expect(groups.archived[2].label).toBe('未绑定工作区');
		expect(groups.archived[2].archive).toBe(true);
	});

	it('没有未绑定工作区的归档会话时不出现伪文件夹', () => {
		const groups = build([conversation('old-a', { workspace: repoA, ago: 40 * DAY })]);

		expect(groups.archived.map((folder) => folder.id)).toEqual(['archive-workspace-git-a']);
	});

	it('归档文件夹的 id 前缀与项目不一致：两份折叠状态和分页互不干扰', () => {
		const groups = build([
			conversation('old-a', { workspace: repoA, ago: 40 * DAY }),
			conversation('fresh-a', { workspace: repoA, ago: DAY }),
		]);

		expect(groups.projects[0].id).toBe('workspace-git-a');
		expect(groups.archived[0].id).toBe('archive-workspace-git-a');
	});

	it('文件夹按组内最新会话时间降序，文件夹内会话也按时间降序', () => {
		const groups = build([
			conversation('a-oldest', { workspace: repoA, ago: 60 * DAY }),
			conversation('b-newest', { workspace: repoB, ago: 10 * DAY }),
			conversation('a-middle', { workspace: repoA, ago: 20 * DAY }),
		]);

		expect(groups.archived.map((folder) => folder.id)).toEqual(['archive-workspace-git-b', 'archive-workspace-git-a']);
		expect(groups.archived[1].children.map((child) => child.id)).toEqual(['a-middle', 'a-oldest']);
	});

	it('节点沿用既有的会话字段：标题、时间提示、工作区根与运行态', () => {
		const groups = build([conversation('running', { workspace: repoA, ago: DAY })], {
			selectedConversationId: 'running',
			selectedIsBusy: true,
		});
		const node = groups.projects[0].children[0];

		expect(node).toMatchObject({
			id: 'running',
			label: 'running',
			time: '2026-03-01 08:00',
			type: 'conversation',
			workspaceRootId: 'root-a',
			workspaceRootName: 'repo-a',
			workspaceRootPath: 'E:\\repo-a',
			// 选中会话正在跑：宿主没标 isRunning 时也按忙碌显示。
			isRunning: true,
		});
	});
});
