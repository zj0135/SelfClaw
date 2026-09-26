export const escapeHtml = (value) =>
	String(value ?? '')
		.replaceAll('&', '&amp;')
		.replaceAll('<', '&lt;')
		.replaceAll('>', '&gt;')
		.replaceAll('"', '&quot;')
		.replaceAll("'", '&#39;');

export function toolStatusLabel(status) {
	switch (status) {
		case 'running':
			return '执行中';
		case 'awaitingapproval':
			return '等待确认';
		case 'completed':
			return '成功';
		case 'failed':
			return '失败';
		case 'blocked':
			return '已拦截';
		case 'cancelled':
			return '已取消';
		default:
			// 未知状态原样显示，不再伪装成“成功”。
			return status ? String(status) : '';
	}
}
