export const activityStatusLabel = (status) => ({
	queued: '排队中', running: '运行中', succeeded: '已完成', failed: '失败', cancelled: '已取消', interrupted: '已中断',
	initializing: '初始化', requesting: '请求中', thinking: '思考中', responding: '输出中', tool: '执行工具',
	'waiting-approval': '等待审批', cancelling: '取消中', synchronizing: '状态同步中', 'recording-error': '记录异常',
}[status] || status);
export const deliveryStatusLabel = (status) => ({ none: '', pending: '等待父代理处理', leased: '父代理处理中', delivered: '已交付', deadletter: '交付失败' }[status] || '');
export const activityErrorLabel = (error) => error ? ({
	'activity-scope-closed': '会话正在删除，任务活动已关闭。',
	'activity-scope-mismatch': '正在切换会话，请稍后刷新。',
	'activity-subscription-invalid': '活动连接已重置，请刷新。',
	'activity-subscription-changed': '任务选择已改变，请重新选择。',
	'activity-detail-selection-invalid': '请选择要查看的任务。',
	'task-not-found': '该任务已不存在，请刷新任务列表。',
	'task-list-changed': '任务列表已变化，请刷新后继续翻页。',
	'content-changed': '内容已更新，请返回最新内容后重新读取。',
	'content-not-found': '这段内容已不可用，请刷新任务详情。',
}[error] || '暂时无法读取任务活动，请重试。') : '';

export function activityTimingLabel(task, now) {
	const end = task.completedAtUtc ? Date.parse(task.completedAtUtc) : now;
	const queued = Date.parse(task.queuedAtUtc);
	if (!Number.isFinite(queued) || !Number.isFinite(end)) return '';
	if (!task.startedAtUtc) return `排队 ${duration(end - queued)}`;
	const started = Date.parse(task.startedAtUtc);
	return `排队 ${duration(started - queued)} · 运行 ${duration(end - started)}`;
}

function duration(milliseconds) {
	const seconds = Math.max(0, Math.floor(milliseconds / 1000));
	return seconds >= 60 ? `${Math.floor(seconds / 60)}m ${seconds % 60}s` : `${seconds}s`;
}
