/*
 * 任务计划（todo_write）的纯派生逻辑：状态归一、计数、进度文案、当前项、完成判定。
 *
 * 后端只投递 pending / in_progress / completed 三个字符串，不传任何派生态；进度与
 * 「当前项」一律由这里从 items 推导，避免前后端各算一套而漂移。
 * 本文件无 DOM、无 Vue：dock、内联卡片与测试共用同一份实现。
 */

const TODO_STATUSES = new Set(['pending', 'in_progress', 'completed']);

function normalizeText(value) {
	return typeof value === 'string' ? value.trim() : '';
}

// 容错归一：content 为空的项丢弃（后端会拒绝，但历史回放或旧快照可能带脏数据），
// 未知 status 一律降级为 pending——宁可显示成待办，也不要让条目从列表里消失。
export function normalizeTodoItems(plan) {
	const rawItems = Array.isArray(plan?.items) ? plan.items : [];
	const items = [];

	for (const raw of rawItems) {
		if (!raw || typeof raw !== 'object') {
			continue;
		}

		const content = normalizeText(raw.content);
		if (!content) {
			continue;
		}

		items.push({
			content,
			status: TODO_STATUSES.has(raw.status) ? raw.status : 'pending',
			activeForm: normalizeText(raw.activeForm),
		});
	}

	return items;
}

export function todoCounts(items) {
	const list = Array.isArray(items) ? items : [];
	let completed = 0;
	let inProgress = 0;

	for (const item of list) {
		if (item?.status === 'completed') {
			completed += 1;
		} else if (item?.status === 'in_progress') {
			inProgress += 1;
		}
	}

	return { total: list.length, completed, inProgress, pending: list.length - completed - inProgress };
}

export function todoProgressLabel(counts) {
	const total = Number(counts?.total) || 0;
	if (total <= 0) {
		return '';
	}

	return `${Number(counts.completed) || 0}/${total}`;
}

export function todoActiveItem(items) {
	const list = Array.isArray(items) ? items : [];
	return list.find((item) => item?.status === 'in_progress') || null;
}

export function todoAllCompleted(items) {
	const counts = todoCounts(items);
	return counts.total > 0 && counts.completed === counts.total;
}
