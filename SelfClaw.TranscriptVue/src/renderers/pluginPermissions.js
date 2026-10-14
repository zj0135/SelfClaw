// Permission tokens are a disclosure list, so the dialog explains the ones the host
// actually enforces and echoes anything else verbatim rather than hiding it.
const permissionDescriptions = {
	'ui.panel': '在右侧插件面板区域添加自定义界面',
	'host.context.read': '读取当前会话、工作区与选中 Agent 的上下文',
	'host.transcript.read': '读取当前对话的转录内容',
	'host.composer.write': '向消息输入框写入文本',
	'host.workspace.read': '读取当前工作区的信息',
};

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
