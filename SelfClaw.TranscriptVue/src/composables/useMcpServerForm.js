import { reactive } from 'vue';

function emptyEntry() {
	return { key: '', value: '', isSecret: false, hasSecret: false, clearSecret: false };
}

function createInitialForm() {
	return {
		id: null,
		displayName: '',
		transport: 'stdio',
		command: '',
		arguments: [''],
		workingDirectoryMode: 'workspace',
		requiresWorkspace: true,
		environment: [],
		endpoint: '',
		transportMode: 'auto',
		connectionTimeoutSeconds: 15,
		headers: [],
		enabled: false,
	};
}

export function useMcpServerForm() {
	const form = reactive(createInitialForm());
	const errors = reactive({});

	function reset(server = null) {
		Object.assign(form, createInitialForm());
		for (const key of Object.keys(errors)) delete errors[key];
		if (!server) return;
		Object.assign(form, {
			id: server.id,
			displayName: server.name,
			transport: server.transport,
			command: server.command || '',
			arguments: server.arguments?.length ? [...server.arguments] : [''],
			workingDirectoryMode: server.workingDirectoryMode || 'workspace',
			requiresWorkspace: Boolean(server.requiresWorkspace),
			environment: (server.environment || []).map((entry) => ({ ...emptyEntry(), ...entry })),
			endpoint: server.endpoint || '',
			transportMode: server.transportMode || 'auto',
			connectionTimeoutSeconds: server.connectionTimeoutSeconds || 15,
			headers: (server.headers || []).map((entry) => ({ ...emptyEntry(), ...entry })),
			enabled: Boolean(server.enabled),
		});
	}

	function addArgument() {
		form.arguments.push('');
	}

	function removeArgument(index) {
		form.arguments.splice(index, 1);
		if (!form.arguments.length) form.arguments.push('');
	}

	function addEntry(collection) {
		form[collection].push(emptyEntry());
	}

	function removeEntry(collection, index) {
		form[collection].splice(index, 1);
	}

	function validate() {
		for (const key of Object.keys(errors)) delete errors[key];
		if (!form.displayName.trim()) errors.displayName = '请输入服务器名称。';
		if (form.transport === 'stdio' && !form.command.trim()) errors.command = '请输入启动命令。';
		if (form.transport === 'http') {
			try {
				const endpoint = new URL(form.endpoint);
				if (!['http:', 'https:'].includes(endpoint.protocol)) throw new Error();
			} catch {
				errors.endpoint = '请输入有效的 HTTP 或 HTTPS 地址。';
			}
			if (!Number.isInteger(Number(form.connectionTimeoutSeconds)) || Number(form.connectionTimeoutSeconds) <= 0) {
				errors.connectionTimeoutSeconds = '连接超时必须大于 0。';
			}
		}
		for (const collection of ['environment', 'headers']) {
			const seen = new Set();
			for (const entry of form[collection]) {
				const key = entry.key.trim();
				if (!key) errors[collection] = '键名不能为空。';
				else if (seen.has(key.toLowerCase())) errors[collection] = '键名不能重复。';
				seen.add(key.toLowerCase());
			}
		}
		return Object.keys(errors).length === 0;
	}

	function toCommand() {
		if (!validate()) return null;
		const entries = (collection) =>
			form[collection].map((entry) => ({
				key: entry.key.trim(),
				value: entry.isSecret && entry.hasSecret && !entry.clearSecret && !entry.value ? null : entry.value,
				isSecret: entry.isSecret,
				clearSecret: Boolean(entry.clearSecret),
			}));
		return {
			id: form.id,
			displayName: form.displayName.trim(),
			transport: form.transport,
			command: form.transport === 'stdio' ? form.command.trim() : null,
			arguments: form.transport === 'stdio' ? form.arguments.filter((argument) => argument !== '') : [],
			workingDirectoryMode: form.transport === 'stdio' ? form.workingDirectoryMode : null,
			requiresWorkspace: form.transport === 'stdio' && form.requiresWorkspace,
			environment: form.transport === 'stdio' ? entries('environment') : [],
			endpoint: form.transport === 'http' ? form.endpoint.trim() : null,
			transportMode: form.transport === 'http' ? form.transportMode : null,
			connectionTimeoutSeconds: form.transport === 'http' ? Number(form.connectionTimeoutSeconds) : null,
			headers: form.transport === 'http' ? entries('headers') : [],
			enabled: form.enabled,
		};
	}

	return {
		form,
		errors,
		reset,
		addArgument,
		removeArgument,
		addEntry,
		removeEntry,
		toCommand,
	};
}
