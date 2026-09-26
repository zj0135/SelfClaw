// Permission tokens are a disclosure list, so the dialog explains the ones the host
// actually enforces and echoes anything else verbatim rather than hiding it.
const permissionDescriptions = {
	'ui.panel': '在右侧插件面板区域添加自定义界面',
	'host.context.read': '读取当前会话、工作区与选中 Agent 的上下文',
	'host.transcript.read': '读取当前对话的转录内容',
	'host.composer.write': '向消息输入框写入文本',
	'host.workspace.read': '读取当前工作区的信息',
	'hooks.run': '在回合开始/结束时运行插件命令；可读取你的提示词与模型的最终回复，可阻止回合并注入上下文',
	'hooks.tool': '拦截工具调用；可读取工具参数与结果，可拒绝、要求确认、修改参数（完全访问模式下修改后的参数不经确认直接执行）并向模型追加反馈',
	'hooks.http': '观察模型 HTTP 请求与响应的元数据（已脱敏），可追加 x-* 与链路追踪请求头',
	'hooks.http.body': '读取发送给模型提供商的请求体（含对话内容，≤ 1 MiB）',
};

const hookPermissionPrefix = 'hooks.';
const networkFetchPrefix = 'network.fetch:';

export function describePluginPermission(token) {
	if (!token) {
		return '';
	}

	if (token.startsWith(networkFetchPrefix)) {
		return `允许面板向 ${token.slice(networkFetchPrefix.length)} 发起网络请求`;
	}

	return permissionDescriptions[token] || String(token);
}

// hooks.http.body is the only token that hands the plugin the conversation itself,
// so the dialog renders it with the caution treatment instead of a plain row.
export function isSensitivePluginPermission(token) {
	return token === 'hooks.http.body';
}

export function hasHookPermissions(permissions) {
	return (permissions || []).some((permission) => String(permission).startsWith(hookPermissionPrefix));
}
