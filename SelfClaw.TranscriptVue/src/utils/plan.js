export function planPanelStatusLabel(stateValue) {
	switch (stateValue) {
		case 'planning':
			return '规划中';
		case 'executing':
			return '执行中';
		case 'completed':
			return '已完成';
		case 'failed':
			return '失败';
		case 'cancelled':
			return '已停止';
		default:
			return '计划中';
	}
}

export function planStepStatusLabel(status) {
	switch (status) {
		case 'running':
			return '执行中';
		case 'completed':
			return '已完成';
		case 'failed':
			return '失败';
		case 'cancelled':
			return '已停止';
		default:
			return '待执行';
	}
}
