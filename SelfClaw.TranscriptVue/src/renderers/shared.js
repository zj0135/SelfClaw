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
		case 'failed':
			return '失败';
		case 'cancelled':
			return '已取消';
		default:
			return '成功';
	}
}
