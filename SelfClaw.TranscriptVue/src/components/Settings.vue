<script setup>
import { computed, reactive, ref } from 'vue';
import SettingsIcon from './SettingsIcon.vue';

// ---------------------------------------------------------------------------
// Settings view — front-end only. All data below is mock/sample content that
// mirrors the original HTML design. No backend wiring; interactions are local.
// ---------------------------------------------------------------------------

const emit = defineEmits(['close']);

const NAV_GROUPS = [
	{
		label: '系统',
		items: [{ id: 'sys', text: '系统设置', icon: 'code' }],
	},
	{
		label: 'AI 能力',
		items: [
			{ id: 'providers', text: 'AI 服务商', icon: 'providers', badge: 4 },
			{ id: 'models', text: '模型管理', icon: 'models', badge: 5 },
		],
	},
	{
		label: '扩展与集成',
		items: [
			{ id: 'plugins', text: '插件', icon: 'plugins' },
			{ id: 'mcp', text: 'MCP 服务器', icon: 'mcp', badge: 3 },
		],
	},
	{
		label: '关于',
		items: [{ id: 'about', text: '关于', icon: 'about' }],
	},
];

const activePage = ref('sys');
const scrollRef = ref(null);

function selectPage(id) {
	activePage.value = id;
	if (scrollRef.value) {
		scrollRef.value.scrollTop = 0;
	}
}

// ---------------------------------------------------------------------------
// System settings page
// ---------------------------------------------------------------------------
const DEFAULT_PROXY = 'http://127.0.0.1:7890';
const SHELL_MAP = {
	auto: '自动，将在执行时选择',
	pwsh: 'pwsh -NoProfile -Command',
	powershell: 'powershell.exe -Command',
	cmd: 'cmd.exe /c',
	gitbash: 'C:\\Program Files\\Git\\bin\\bash.exe -lc',
	wsl: 'wsl.exe bash -lc',
};

const sys = reactive({
	theme: 'system',
	language: '简体中文',
	autostart: false,
	shell: 'auto',
	proxy: DEFAULT_PROXY,
	proxyForModels: true,
	logLevel: 'Information',
});

const shellResolveText = computed(() => `当前解析：${SHELL_MAP[sys.shell] || sys.shell}`);

function setTheme(value) {
	sys.theme = value;
	markDirty();
}

function resetProxy() {
	sys.proxy = DEFAULT_PROXY;
	toast('已恢复默认代理');
	markDirty();
}

// ---------------------------------------------------------------------------
// Providers page
// ---------------------------------------------------------------------------
const providers = reactive([
	{
		id: 'anthropic',
		name: 'Anthropic',
		kind: 'Anthropic',
		led: 'an',
		icon: 'anthropic',
		endpoint: 'api.anthropic.com',
		meta: '鉴权 API Key · 3 个模型',
		enabled: true,
		canTest: true,
		apiKey: 'sk-ant-api03-7Qf3xR',
		revealed: false,
		testing: false,
		tested: false,
		chips: ['超时 120s', '代理 跟随系统', 'API 格式 Messages'],
	},
	{
		id: 'openai',
		name: 'OpenAI',
		kind: 'OpenAI',
		led: 'oa',
		icon: 'openai',
		endpoint: 'api.openai.com/v1',
		meta: '鉴权 API Key · 2 个模型',
		enabled: true,
		canTest: true,
		apiKey: 'sk-proj-A1b2C3d4',
		revealed: false,
		testing: false,
		tested: false,
		chips: ['组织 org-default', '超时 120s', 'API 格式 Responses'],
	},
	{
		id: 'deepseek',
		name: 'DeepSeek',
		kind: 'DeepSeek',
		led: 'ds',
		icon: 'deepseek',
		endpoint: 'api.deepseek.com',
		meta: '鉴权 API Key · 1 个模型',
		enabled: true,
		canTest: true,
		apiKey: 'sk-ds-9f8e7d',
		revealed: false,
		testing: false,
		tested: false,
		chips: [],
	},
	{
		id: 'compat',
		name: '本地 Ollama',
		kind: 'OpenAI 兼容',
		led: 'cp',
		icon: 'box',
		endpoint: 'http://localhost:11434/v1',
		meta: '无需鉴权',
		enabled: false,
		canTest: false,
		stopped: true,
		apiKey: '',
		revealed: false,
		testing: false,
		tested: false,
		chips: [],
	},
]);

function maskKey(value) {
	if (!value) {
		return '';
	}
	return '•'.repeat(Math.max(12, value.length));
}

function toggleReveal(provider) {
	provider.revealed = !provider.revealed;
}

async function copyKey(provider) {
	try {
		await navigator.clipboard?.writeText(provider.apiKey);
	} catch {
		// clipboard may be unavailable in the host; ignore.
	}
	toast('密钥已复制到剪贴板');
}

function testConnection(provider) {
	if (provider.testing) {
		return;
	}
	provider.testing = true;
	provider.tested = false;
	setTimeout(() => {
		provider.testing = false;
		provider.tested = true;
		setTimeout(() => {
			provider.tested = false;
		}, 1600);
	}, 950);
}

// ---------------------------------------------------------------------------
// Models page
// ---------------------------------------------------------------------------
const models = reactive([
	{ name: '主力对话', model: 'claude-sonnet-4-5', provider: 'Anthropic', format: 'Messages', primary: true },
	{ name: '深度推理', model: 'o3', provider: 'OpenAI', format: 'Responses', primary: false },
	{ name: '快速草稿', model: 'claude-haiku-latest', provider: 'Anthropic', format: 'Messages', primary: false },
	{ name: '经济模型', model: 'deepseek-chat', provider: 'DeepSeek', format: 'Chat Completions', primary: false },
	{ name: '语音转写', model: 'gpt-4o-transcribe', provider: 'OpenAI', format: 'Audio', primary: false },
]);

const modelSearch = ref('');
const visibleModels = computed(() => {
	const query = modelSearch.value.trim().toLowerCase();
	if (!query) {
		return models;
	}
	return models.filter((m) => `${m.name} ${m.model} ${m.provider} ${m.format}`.toLowerCase().includes(query));
});

// ---------------------------------------------------------------------------
// Plugins page
// ---------------------------------------------------------------------------
const plugins = reactive([
	{ name: '联网搜索', tag: '内置', icon: 'globe', desc: '通过搜索引擎为代理补充实时信息', version: 'v1.4.0', enabled: true, locked: false },
	{ name: '技能市场', tag: '内置', icon: 'layers', desc: '下载并管理可复用技能包', version: 'v0.9.2', enabled: true, locked: false },
	{ name: '迁移中心', tag: '内置', icon: 'refresh', desc: '从其他客户端导入会话与配置', version: 'v1.1.0', enabled: false, locked: false },
	{ name: '转写视图', tag: '已安装', icon: 'chatDoc', desc: '以富文本渲染会话记录，支持代码与 Markdown', version: 'v2.0.1', enabled: true, locked: false },
	{ name: '工作区工具', tag: '核心 · 不可禁用', tagKind: 'warn', icon: 'folder', desc: '文件读写、grep、补丁等工作区内置工具', version: 'core', enabled: true, locked: true },
]);

// ---------------------------------------------------------------------------
// MCP servers page
// ---------------------------------------------------------------------------
const mcpServers = reactive([
	{
		name: 'filesystem',
		icon: 'server',
		desc: '本地文件系统访问',
		tools: 12,
		enabled: true,
		running: true,
		command: 'npx',
		flag: '-y',
		args: '@modelcontextprotocol/server-filesystem ~/workspace ~/Documents',
		chips: ['环境变量 0', '自动启动 开'],
	},
	{
		name: 'github',
		icon: 'git',
		desc: '仓库、Issue 与 PR 操作',
		tools: 26,
		enabled: false,
		running: false,
		command: 'npx',
		flag: '-y',
		args: '@modelcontextprotocol/server-github',
		chips: ['GITHUB_TOKEN · ghp_••••', '自动启动 关'],
		monoFirstChip: true,
	},
	{
		name: 'sqlite',
		icon: 'database',
		desc: '查询本地数据库',
		tools: 6,
		enabled: true,
		running: true,
		command: 'uvx',
		flag: '--db',
		args: 'mcp-server-sqlite ./data/app.db',
		chips: ['环境变量 0', '自动启动 开'],
	},
]);

function toggleMcp(server) {
	server.running = !server.running;
	server.enabled = server.running;
	toast(server.running ? `已启动 ${server.name}` : `已停止 ${server.name}`);
}

// ---------------------------------------------------------------------------
// Unsaved bar
// ---------------------------------------------------------------------------
const dirty = ref(false);

function markDirty() {
	dirty.value = true;
}

function onFormInput(event) {
	if (event.target?.dataset?.nodirty !== undefined) {
		return;
	}
	markDirty();
}

function saveChanges() {
	dirty.value = false;
	toast('设置已保存');
}

function discardChanges() {
	dirty.value = false;
	toast('已放弃更改');
}

// ---------------------------------------------------------------------------
// Toasts
// ---------------------------------------------------------------------------
let toastSeed = 0;
const toasts = reactive([]);

function toast(message) {
	const id = ++toastSeed;
	toasts.push({ id, message });
	setTimeout(() => {
		const index = toasts.findIndex((t) => t.id === id);
		if (index !== -1) {
			toasts.splice(index, 1);
		}
	}, 2100);
}

// ---------------------------------------------------------------------------
// Drawer (add / edit provider, model, mcp)
// ---------------------------------------------------------------------------
const PROVIDER_PRESETS = {
	Anthropic: { kind: 'Anthropic', endpoint: 'https://api.anthropic.com', key: 'sk-ant-api03-7Qf3xR' },
	OpenAI: { kind: 'OpenAI', endpoint: 'https://api.openai.com/v1', key: 'sk-proj-A1b2C3d4' },
	DeepSeek: { kind: 'DeepSeek', endpoint: 'https://api.deepseek.com', key: 'sk-ds-9f8e7d' },
	'本地 Ollama': { kind: 'OpenAI 兼容', endpoint: 'http://localhost:11434/v1', key: '' },
};
const MCP_PRESETS = {
	filesystem: { command: 'npx', args: '-y\n@modelcontextprotocol/server-filesystem\n~/workspace' },
	github: { command: 'npx', args: '-y\n@modelcontextprotocol/server-github' },
	sqlite: { command: 'uvx', args: 'mcp-server-sqlite\n--db\n./data/app.db' },
};
const DRAWER_META = {
	provider: { title: '服务商', sub: '连接到模型供应商' },
	model: { title: '模型档案', sub: '绑定到服务商连接的模型配置' },
	mcp: { title: 'MCP 服务器', sub: '以 stdio 启动外部工具进程' },
};

const drawer = reactive({
	open: false,
	type: null,
	mode: 'add',
	title: '',
	sub: '',
	saveLabel: '添加',
	revealKey: false,
	form: {},
});

function freshForm(type, edit) {
	if (type === 'provider') {
		const preset = edit ? PROVIDER_PRESETS[edit] : null;
		return {
			name: edit || '',
			kind: preset ? preset.kind : 'Anthropic',
			endpoint: preset ? preset.endpoint : '',
			apiKey: preset ? preset.key : '',
			org: '',
			timeout: '120',
			useProxy: true,
		};
	}
	if (type === 'model') {
		return {
			name: edit || '',
			provider: 'Anthropic',
			model: edit === '深度推理' ? 'o3' : 'claude-sonnet-4-5',
			format: 'Messages',
			temperature: 1,
			topP: 1,
			maxTokens: 8192,
			reasoning: edit === '深度推理' ? 'high' : '关闭',
			parallelTools: true,
			isPrimary: edit === '主力对话',
		};
	}
	// mcp
	const preset = edit ? MCP_PRESETS[edit] : null;
	return {
		name: edit || '',
		transport: 'stdio (本地子进程)',
		command: preset ? preset.command : '',
		args: preset ? preset.args : '',
		env: edit === 'github' ? [{ key: 'GITHUB_TOKEN', value: 'ghp_xxx' }] : [{ key: '', value: '' }],
		autostart: true,
	};
}

function openDrawer(type, edit = null) {
	const meta = DRAWER_META[type];
	if (!meta) {
		return;
	}
	drawer.type = type;
	drawer.mode = edit ? 'edit' : 'add';
	drawer.title = `${edit ? '编辑' : '添加'}${meta.title}`;
	drawer.sub = edit ? edit : meta.sub;
	drawer.saveLabel = edit ? '保存更改' : '添加';
	drawer.revealKey = false;
	drawer.form = freshForm(type, edit);
	drawer.open = true;
}

function closeDrawer() {
	drawer.open = false;
}

function addEnvRow() {
	drawer.form.env.push({ key: '', value: '' });
}

function removeEnvRow(index) {
	drawer.form.env.splice(index, 1);
}

const sliderText = computed(() => ({
	temperature: Number(drawer.form.temperature).toFixed(1),
	topP: Number(drawer.form.topP).toFixed(2),
	maxTokens: String(drawer.form.maxTokens),
}));

function saveDrawer() {
	const form = drawer.form;
	const name = (form.name || '').trim() || '未命名';
	if (drawer.mode === 'add') {
		appendDrawerItem(name);
		toast(`已添加「${name}」`);
	} else {
		toast(`已保存「${name}」`);
	}
	closeDrawer();
}

function appendDrawerItem(name) {
	const form = drawer.form;
	if (drawer.type === 'provider') {
		const led = { OpenAI: 'oa', Anthropic: 'an', DeepSeek: 'ds', 'OpenAI 兼容': 'cp' }[form.kind] || 'cp';
		providers.push({
			id: `custom-${Date.now()}`,
			name,
			kind: form.kind,
			led,
			icon: 'providers',
			endpoint: form.endpoint || '—',
			meta: '鉴权 API Key',
			enabled: true,
			canTest: true,
			isNew: true,
			apiKey: form.apiKey || '',
			revealed: false,
			testing: false,
			tested: false,
			chips: [],
		});
	} else if (drawer.type === 'model') {
		models.push({
			name,
			model: form.model || '—',
			provider: form.provider,
			format: form.format,
			primary: false,
		});
	} else if (drawer.type === 'mcp') {
		mcpServers.push({
			name,
			icon: 'mcp',
			desc: '新添加 · 启动后探测工具',
			tools: 0,
			enabled: false,
			running: false,
			command: form.command || 'npx',
			flag: '',
			args: (form.args || '').split('\n').join(' ').trim(),
			chips: [],
			isNew: true,
		});
	}
}
</script>

<template>
	<div class="settings-root">
		<div class="settings-window">
			<div class="sw-body">
				<!-- 侧栏 -->
				<aside class="sidebar">
					<div class="sb-head">
						<h1>设置</h1>
						<p>偏好与模型设置</p>
					</div>
					<nav class="sb-nav">
						<div v-for="group in NAV_GROUPS" :key="group.label" class="sb-group">
							<div class="sb-label">{{ group.label }}</div>
							<button v-for="item in group.items" :key="item.id" type="button" class="nav-item"
								:class="{ active: activePage === item.id }" @click="selectPage(item.id)">
								<SettingsIcon class="ni-ico" :name="item.icon" />
								{{ item.text }}
								<span v-if="item.badge" class="ni-badge num">{{ item.badge }}</span>
							</button>
						</div>
					</nav>
					<div class="sb-foot"><span class="liv"></span> Powered by OpenCowork</div>
				</aside>

				<!-- 内容区 -->
				<main class="content">
					<div ref="scrollRef" class="scroll" @input="onFormInput" @change="onFormInput">

						<!-- ===== 系统设置 ===== -->
						<section v-show="activePage === 'sys'" class="page">
							<div class="page-head">
								<h2>系统设置</h2>
								<p class="sub">Shell 运行时、代理与系统级执行偏好</p>
							</div>

							<div class="block">
								<div class="block-title">
									<SettingsIcon class="bt-ico" name="sun" />
									<h3>外观与语言</h3>
								</div>
								<div class="card">
									<div class="row">
										<div class="rl">
											<div class="t">主题</div>
											<div class="d">界面配色，默认跟随操作系统</div>
										</div>
										<div class="rc">
											<div class="seg">
												<button type="button" :class="{ on: sys.theme === 'system' }" @click="setTheme('system')">跟随系统</button>
												<button type="button" :class="{ on: sys.theme === 'light' }" @click="setTheme('light')">浅色</button>
												<button type="button" :class="{ on: sys.theme === 'dark' }" @click="setTheme('dark')">深色</button>
											</div>
										</div>
									</div>
									<div class="row">
										<div class="rl">
											<div class="t">界面语言</div>
											<div class="d">重启后对全部窗口生效</div>
										</div>
										<div class="rc">
											<select v-model="sys.language" style="width:170px">
												<option>简体中文</option>
												<option>English</option>
												<option>繁體中文</option>
												<option>日本語</option>
											</select>
										</div>
									</div>
									<div class="row">
										<div class="rl">
											<div class="t">开机自启</div>
											<div class="d">登录系统后在后台启动 OpenCowork</div>
										</div>
										<div class="rc">
											<label class="switch"><input type="checkbox" v-model="sys.autostart"><span class="tk"></span></label>
										</div>
									</div>
								</div>
							</div>

							<div class="block">
								<div class="block-title">
									<SettingsIcon class="bt-ico" name="code" />
									<h3>Shell 执行端</h3>
								</div>
								<div class="card">
									<div class="card-h">
										<div class="grow">
											<div class="ch-title" style="font-weight:550;font-size:13.5px">默认 Shell</div>
											<div class="ch-desc">选择 OpenCowork 在 Bash 工具调用中使用哪个 shell。可选项会根据当前操作系统过滤。</div>
										</div>
										<span class="chip mono">当前系统：win32</span>
									</div>
									<div style="margin-top:12px">
										<select v-model="sys.shell">
											<option value="auto">自动（系统默认）</option>
											<option value="pwsh">PowerShell 7 (pwsh)</option>
											<option value="powershell">Windows PowerShell</option>
											<option value="cmd">命令提示符 (cmd.exe)</option>
											<option value="gitbash">Git Bash</option>
											<option value="wsl">WSL · bash</option>
										</select>
										<div class="help">使用应用默认解析：macOS / Linux 优先 <span style="font-family:var(--mono)">$SHELL</span> → zsh → bash → sh，Windows 使用系统命令 shell。</div>
										<div class="shell-resolve">{{ shellResolveText }}</div>
									</div>
								</div>
							</div>

							<div class="block">
								<div class="block-title">
									<SettingsIcon class="bt-ico" name="globe" />
									<h3>系统代理</h3>
								</div>
								<div class="card">
									<div class="ch-desc" style="margin-bottom:12px">设置整个应用进程使用的代理地址，例如
										<span class="mono accent-ink">http://127.0.0.1:7890</span> 或
										<span class="mono accent-ink">http://host:port</span>。留空则不使用代理。
									</div>
									<div style="display:flex;gap:12px;align-items:center">
										<input type="text" v-model="sys.proxy" class="input-mono" style="flex:1">
										<button class="linkbtn" @click="resetProxy">恢复默认</button>
									</div>
									<div class="row" style="margin-top:6px;border-top:1px solid var(--border)">
										<div class="rl">
											<div class="t">模型请求也走代理</div>
											<div class="d">关闭后服务商连接将直连，仅工具调用使用代理</div>
										</div>
										<div class="rc">
											<label class="switch"><input type="checkbox" v-model="sys.proxyForModels"><span class="tk"></span></label>
										</div>
									</div>
								</div>
							</div>

							<div class="block">
								<div class="block-title">
									<SettingsIcon class="bt-ico" name="folder" />
									<h3>数据与日志</h3>
								</div>
								<div class="card">
									<div class="row" style="padding-top:2px">
										<div class="rl">
											<div class="t">数据目录</div>
											<div class="d input-mono" style="font-size:11.5px;color:var(--muted)">%AppData%\OpenCowork\store</div>
										</div>
										<div class="rc">
											<button class="btn sm" @click="toast('已打开数据目录')">打开</button>
											<button class="btn sm" @click="toast('请选择新的数据目录')">更改…</button>
										</div>
									</div>
									<div class="row">
										<div class="rl">
											<div class="t">日志级别</div>
											<div class="d">影响诊断日志的详细程度</div>
										</div>
										<div class="rc">
											<select v-model="sys.logLevel" style="width:150px">
												<option>Information</option>
												<option>Debug</option>
												<option>Warning</option>
												<option>Error</option>
											</select>
										</div>
									</div>
									<div class="row">
										<div class="rl">
											<div class="t">清除缓存</div>
											<div class="d">转写、缩略图与临时工作区文件 · 当前约 <span class="num">182&nbsp;MB</span></div>
										</div>
										<div class="rc">
											<button class="btn sm danger" @click="toast('缓存已清除')">清除…</button>
										</div>
									</div>
								</div>
							</div>
						</section>

						<!-- ===== AI 服务商 ===== -->
						<section v-show="activePage === 'providers'" class="page">
							<div class="page-head head-row">
								<div class="grow">
									<h2>AI 服务商</h2>
									<p class="sub">连接到模型供应商。密钥以加密引用存储，绝不写入明文配置。</p>
								</div>
								<button class="btn primary" @click="openDrawer('provider')">
									<SettingsIcon name="plus" />添加服务商
								</button>
							</div>
							<div>
								<div v-for="p in providers" :key="p.id" class="lcard" :class="{ off: !p.enabled }">
									<div class="lc-top">
										<div class="lc-ico"><SettingsIcon :name="p.icon" /></div>
										<div class="lc-grow">
											<div class="lc-name">
												{{ p.name }}
												<span class="chip"><span class="led" :class="p.led"></span>{{ p.kind }}</span>
												<span v-if="p.stopped" class="chip stop"><span class="led"></span>已停用</span>
											</div>
											<div class="lc-meta"><span class="mono">{{ p.endpoint }}</span> · {{ p.meta }}</div>
										</div>
										<div class="lc-act">
											<button v-if="p.canTest" class="btn sm test"
												:class="{ ok: p.tested }" :disabled="p.testing" @click="testConnection(p)">
												<SettingsIcon :name="p.testing ? 'spin' : (p.tested ? 'check' : 'zap')" />
												{{ p.testing ? '连接中…' : (p.tested ? '连接正常' : '测试连接') }}
											</button>
											<button class="btn sm" @click="openDrawer('provider', p.name)">编辑</button>
											<label class="switch"><input type="checkbox" v-model="p.enabled"><span class="tk"></span></label>
										</div>
									</div>
									<div v-if="p.enabled && p.apiKey" class="lc-body">
										<div class="kv">
											<span class="k">API 密钥</span>
											<span class="v">
												<span class="input-wrap" style="max-width:340px">
													<input :type="p.revealed ? 'text' : 'password'"
														:value="p.revealed ? p.apiKey : maskKey(p.apiKey)"
														class="input-mono" readonly style="padding-right:64px">
													<span class="in-actions">
														<button class="icon-btn" title="显示/隐藏" @click="toggleReveal(p)">
															<SettingsIcon :name="p.revealed ? 'eyeOff' : 'eye'" />
														</button>
														<button class="icon-btn" title="复制" @click="copyKey(p)">
															<SettingsIcon name="copy" />
														</button>
													</span>
												</span>
											</span>
										</div>
										<div v-if="p.chips.length" style="display:flex;gap:8px;margin-top:8px;flex-wrap:wrap">
											<span v-for="chip in p.chips" :key="chip" class="chip">{{ chip }}</span>
										</div>
									</div>
								</div>
							</div>
						</section>

						<!-- ===== 模型管理 ===== -->
						<section v-show="activePage === 'models'" class="page">
							<div class="page-head head-row">
								<div class="grow">
									<h2>模型管理</h2>
									<p class="sub">模型档案绑定到服务商连接，携带 API 格式与采样参数。</p>
								</div>
								<button class="btn primary" @click="openDrawer('model')">
									<SettingsIcon name="plus" />添加模型
								</button>
							</div>
							<div style="position:relative;margin-bottom:14px">
								<SettingsIcon class="search-affix" name="search" />
								<input type="text" v-model="modelSearch" data-nodirty placeholder="搜索名称或模型 id…" style="padding-left:34px">
							</div>
							<div class="table">
								<div class="thead">
									<div>名称</div>
									<div>模型</div>
									<div class="c-prov">服务商</div>
									<div class="c-fmt">API 格式</div>
									<div></div>
								</div>
								<div>
									<div v-for="m in visibleModels" :key="m.name" class="trow" @click="openDrawer('model', m.name)">
										<div class="nm">
											<SettingsIcon v-if="m.primary" class="star" name="star" />
											{{ m.name }}
										</div>
										<div class="mdl">{{ m.model }}</div>
										<div class="sub c-prov">{{ m.provider }}</div>
										<div class="sub c-fmt">{{ m.format }}</div>
										<div class="ar"><SettingsIcon name="chevronRight" /></div>
									</div>
								</div>
							</div>
							<p class="table-note">提示：被标记
								<SettingsIcon class="inline-star" name="star" /> 的模型为默认主力，新会话将自动选用。
							</p>
						</section>

						<!-- ===== 插件 ===== -->
						<section v-show="activePage === 'plugins'" class="page">
							<div class="page-head head-row">
								<div class="grow">
									<h2>插件</h2>
									<p class="sub">为代理扩展能力。内置插件随应用更新，第三方插件可单独启用或停用。</p>
								</div>
								<button class="btn" @click="toast('已打开插件目录')">
									<SettingsIcon name="search" />浏览插件目录
								</button>
							</div>
							<div>
								<div v-for="plugin in plugins" :key="plugin.name" class="lcard">
									<div class="lc-top">
										<div class="lc-ico"><SettingsIcon :name="plugin.icon" /></div>
										<div class="lc-grow">
											<div class="lc-name">
												{{ plugin.name }}
												<span class="chip" :class="{ warn: plugin.tagKind === 'warn' }">{{ plugin.tag }}</span>
											</div>
											<div class="lc-meta">{{ plugin.desc }} · <span class="mono">{{ plugin.version }}</span></div>
										</div>
										<div class="lc-act">
											<button class="btn sm" :disabled="plugin.locked" @click="toast(`打开「${plugin.name}」配置`)">配置</button>
											<label class="switch">
												<input type="checkbox" v-model="plugin.enabled" :disabled="plugin.locked"><span class="tk"></span>
											</label>
										</div>
									</div>
								</div>
							</div>
						</section>

						<!-- ===== MCP 服务器 ===== -->
						<section v-show="activePage === 'mcp'" class="page">
							<div class="page-head head-row">
								<div class="grow">
									<h2>MCP 服务器</h2>
									<p class="sub">通过 Model Context Protocol 接入外部工具。以 stdio 启动子进程，代理按需调用其工具。</p>
								</div>
								<button class="btn primary" @click="openDrawer('mcp')">
									<SettingsIcon name="plus" />添加 MCP 服务器
								</button>
							</div>
							<div>
								<div v-for="server in mcpServers" :key="server.name" class="lcard" :class="{ off: !server.running }">
									<div class="lc-top">
										<div class="lc-ico"><SettingsIcon :name="server.icon" /></div>
										<div class="lc-grow">
											<div class="lc-name">
												{{ server.name }}
												<span class="chip mono">stdio</span>
												<span class="chip" :class="server.running ? 'run' : 'stop'">
													<span class="led"></span>{{ server.running ? '运行中' : '已停止' }}
												</span>
											</div>
											<div class="lc-meta">
												{{ server.desc }} ·
												<template v-if="server.tools">暴露 <span class="num">{{ server.tools }}</span> 个工具</template>
												<template v-else>启动后探测工具</template>
											</div>
										</div>
										<div class="lc-act">
											<button class="btn sm" @click="toggleMcp(server)">
												<SettingsIcon :name="server.running ? 'stop' : 'play'" />
												{{ server.running ? '停止' : '启动' }}
											</button>
											<button class="btn sm" @click="openDrawer('mcp', server.name)">编辑</button>
											<label class="switch"><input type="checkbox" v-model="server.enabled"><span class="tk"></span></label>
										</div>
									</div>
									<div class="lc-body">
										<div class="cmd">
											<span class="tok">{{ server.command }}</span>
											<template v-if="server.flag"> <span class="flag">{{ server.flag }}</span></template>
											{{ ' ' + server.args }}
										</div>
										<div v-if="server.chips.length" style="display:flex;gap:8px;margin-top:8px;flex-wrap:wrap">
											<span v-for="(chip, idx) in server.chips" :key="chip"
												class="chip" :class="{ mono: server.monoFirstChip && idx === 0 }">{{ chip }}</span>
										</div>
									</div>
								</div>
							</div>
						</section>

						<!-- ===== 关于 ===== -->
						<section v-show="activePage === 'about'" class="page">
							<div class="page-head">
								<h2>关于</h2>
								<p class="sub">版本信息与运行环境</p>
							</div>
							<div class="card">
								<div class="about-hero">
									<div class="about-mark"><SettingsIcon name="code" /></div>
									<div>
										<h2>OpenCowork</h2>
										<div class="ver">SelfClaw 桌面客户端 · v0.4.2 (build 1782)</div>
									</div>
									<div style="margin-left:auto">
										<button class="btn primary" @click="toast('已是最新版本')">检查更新</button>
									</div>
								</div>
								<div class="meta-grid">
									<div class="kv"><span class="k">运行时</span><span class="v">.NET 10.0 · WPF</span></div>
									<div class="kv"><span class="k">系统</span><span class="v">Windows · win32 x64</span></div>
									<div class="kv"><span class="k">数据目录</span><span class="v">%AppData%\OpenCowork</span></div>
									<div class="kv"><span class="k">许可证</span><span class="v">MIT</span></div>
								</div>
								<div style="display:flex;gap:10px;margin-top:16px">
									<button class="btn sm" @click="toast('已在浏览器打开项目主页')">项目主页</button>
									<button class="btn sm" @click="toast('已打开开源许可清单')">开源许可</button>
									<button class="btn sm" @click="toast('诊断信息已复制')">复制诊断信息</button>
								</div>
							</div>
						</section>

					</div>

					<!-- 未保存条 -->
					<div class="savebar" :class="{ show: dirty }">
						<div class="sv-msg"><span class="pulse"></span>有未保存的更改</div>
						<button class="btn sm" @click="discardChanges">放弃</button>
						<button class="btn sm primary" @click="saveChanges">保存更改</button>
					</div>
				</main>
			</div>
		</div>

		<!-- 抽屉 -->
		<div class="scrim" :class="{ open: drawer.open }" @click="closeDrawer"></div>
		<aside class="drawer" :class="{ open: drawer.open }" :aria-hidden="!drawer.open">
			<div class="dr-head">
				<div class="grow">
					<h3>{{ drawer.title }}</h3>
					<p>{{ drawer.sub }}</p>
				</div>
				<button class="icon-btn" aria-label="关闭" @click="closeDrawer"><SettingsIcon name="close" /></button>
			</div>
			<div class="dr-body">
				<!-- 服务商表单 -->
				<template v-if="drawer.type === 'provider'">
					<label class="fld"><span class="lab">显示名称</span>
						<input type="text" v-model="drawer.form.name" placeholder="例如：我的 Claude"></label>
					<label class="fld"><span class="lab">供应商类型</span>
						<select v-model="drawer.form.kind">
							<option>OpenAI</option><option>OpenAI 兼容</option><option>DeepSeek</option><option>Anthropic</option>
						</select></label>
					<label class="fld"><span class="lab">接口地址 (Endpoint)</span>
						<input type="text" class="input-mono" v-model="drawer.form.endpoint" placeholder="https://api.example.com/v1"></label>
					<div class="dr-sec">鉴权</div>
					<label class="fld"><span class="lab">API 密钥</span>
						<span class="input-wrap">
							<input :type="drawer.revealKey ? 'text' : 'password'" class="input-mono"
								v-model="drawer.form.apiKey" placeholder="粘贴密钥，将以加密引用存储" style="padding-right:40px">
							<span class="in-actions">
								<button type="button" class="icon-btn" @click="drawer.revealKey = !drawer.revealKey">
									<SettingsIcon :name="drawer.revealKey ? 'eyeOff' : 'eye'" />
								</button>
							</span>
						</span>
						<span class="help">密钥仅以 <span style="font-family:var(--mono)">secret ref</span> 形式保存，明文不写入配置文件。</span>
					</label>
					<div class="dr-sec">连接选项</div>
					<div class="field-2">
						<label class="fld"><span class="lab">组织 (可选)</span><input type="text" v-model="drawer.form.org" placeholder="org-…"></label>
						<label class="fld"><span class="lab">请求超时 (秒)</span><input type="text" v-model="drawer.form.timeout"></label>
					</div>
					<div class="row" style="border:0;padding:8px 0 0">
						<div class="rl"><div class="t">通过系统代理</div><div class="d">复用「系统设置」中的代理地址</div></div>
						<div class="rc"><label class="switch"><input type="checkbox" v-model="drawer.form.useProxy"><span class="tk"></span></label></div>
					</div>
				</template>

				<!-- 模型表单 -->
				<template v-else-if="drawer.type === 'model'">
					<label class="fld"><span class="lab">档案名称</span>
						<input type="text" v-model="drawer.form.name" placeholder="例如：主力对话"></label>
					<label class="fld"><span class="lab">服务商连接</span>
						<select v-model="drawer.form.provider">
							<option>Anthropic</option><option>OpenAI</option><option>DeepSeek</option><option>本地 Ollama</option>
						</select></label>
					<label class="fld"><span class="lab">模型 id</span>
						<input type="text" class="input-mono" v-model="drawer.form.model" placeholder="claude-sonnet-4-5"></label>
					<label class="fld"><span class="lab">API 格式</span>
						<select v-model="drawer.form.format">
							<option>Messages</option><option>Chat Completions</option><option>Responses</option><option>Audio</option>
						</select></label>
					<div class="dr-sec">采样参数</div>
					<div class="slider-row">
						<span class="sl-lab">temperature</span>
						<input type="range" min="0" max="2" step="0.1" v-model.number="drawer.form.temperature">
						<span class="sl-val">{{ sliderText.temperature }}</span>
					</div>
					<div class="slider-row">
						<span class="sl-lab">top_p</span>
						<input type="range" min="0" max="1" step="0.05" v-model.number="drawer.form.topP">
						<span class="sl-val">{{ sliderText.topP }}</span>
					</div>
					<div class="slider-row">
						<span class="sl-lab">最大输出</span>
						<input type="range" min="1024" max="64000" step="1024" v-model.number="drawer.form.maxTokens">
						<span class="sl-val">{{ sliderText.maxTokens }}</span>
					</div>
					<div class="dr-sec">高级</div>
					<label class="fld"><span class="lab">推理强度</span>
						<select v-model="drawer.form.reasoning">
							<option>关闭</option><option>low</option><option>medium</option><option>high</option>
						</select></label>
					<div class="row" style="border:0;padding:6px 0">
						<div class="rl"><div class="t">并行工具调用</div></div>
						<div class="rc"><label class="switch"><input type="checkbox" v-model="drawer.form.parallelTools"><span class="tk"></span></label></div>
					</div>
					<div class="row" style="border-top:1px solid var(--border);padding:10px 0 0">
						<div class="rl"><div class="t">设为默认主力模型</div></div>
						<div class="rc"><label class="switch"><input type="checkbox" v-model="drawer.form.isPrimary"><span class="tk"></span></label></div>
					</div>
				</template>

				<!-- MCP 表单 -->
				<template v-else-if="drawer.type === 'mcp'">
					<label class="fld"><span class="lab">显示名称</span>
						<input type="text" v-model="drawer.form.name" placeholder="例如：filesystem"></label>
					<label class="fld"><span class="lab">传输方式</span>
						<select v-model="drawer.form.transport">
							<option>stdio (本地子进程)</option><option>SSE (远程)</option>
						</select></label>
					<label class="fld"><span class="lab">启动命令</span>
						<input type="text" class="input-mono" v-model="drawer.form.command" placeholder="npx / uvx / node"></label>
					<label class="fld"><span class="lab">参数 (每行一个或空格分隔)</span>
						<textarea v-model="drawer.form.args" placeholder="-y&#10;@modelcontextprotocol/server-filesystem&#10;~/workspace"></textarea></label>
					<div class="dr-sec">环境变量</div>
					<div>
						<div v-for="(row, index) in drawer.form.env" :key="index" class="env-row">
							<input type="text" placeholder="KEY" v-model="row.key">
							<input type="text" placeholder="value" v-model="row.value">
							<button type="button" class="icon-btn" @click="removeEnvRow(index)"><SettingsIcon name="trash" /></button>
						</div>
					</div>
					<button type="button" class="btn sm" style="margin-top:2px" @click="addEnvRow">
						<SettingsIcon name="plus" />添加变量
					</button>
					<div class="row" style="border-top:1px solid var(--border);padding:12px 0 0;margin-top:14px">
						<div class="rl"><div class="t">应用启动时自动运行</div></div>
						<div class="rc"><label class="switch"><input type="checkbox" v-model="drawer.form.autostart"><span class="tk"></span></label></div>
					</div>
				</template>
			</div>
			<div class="dr-foot">
				<button class="btn" @click="closeDrawer">取消</button>
				<button class="btn primary" @click="saveDrawer">{{ drawer.saveLabel }}</button>
			</div>
		</aside>

		<!-- Toast -->
		<div class="toasts">
			<div v-for="t in toasts" :key="t.id" class="toast">
				<span class="tg"><SettingsIcon name="check" /></span>{{ t.message }}
			</div>
		</div>
	</div>
</template>

<style scoped>
/* ============================================================
   OpenCowork · 设置视图（嵌入 WebView2 中间区域）
   浅色专业工作台 · 冷中性灰底 + 白卡 + 1px 细边框
   ============================================================ */
.settings-root {
	--bg: #f3f4f6;
	--surface: #ffffff;
	--surface-2: #fafbfc;
	--sidebar: #f6f7f9;
	--fg: #15171c;
	--fg-2: #3a3f4a;
	--muted: #6b7180;
	--faint: #868d9c;
	--border: #e4e7ec;
	--border-2: #d3d7df;
	--pill: #1f232b;
	--pill-fg: #ffffff;
	--accent: #2f6feb;
	--accent-ink: #1f5fe0;
	--accent-wash: #eaf1fe;
	--success: #18794e;
	--success-wash: #e7f4ed;
	--warn: #9a6b00;
	--warn-wash: #fbf2dd;
	--danger: #b42318;
	--danger-wash: #fdeceb;
	--code-bg: #f1f2f5;

	--font: -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", system-ui, sans-serif;
	--mono: "SF Mono", "Cascadia Code", "JetBrains Mono", ui-monospace, Consolas, Menlo, monospace;

	--r-sm: 6px;
	--r-md: 9px;
	--r-lg: 12px;
	--sb-w: 264px;
	--ease: cubic-bezier(.2, .7, .3, 1);

	width: 100%;
	height: 100%;
	background: var(--surface);
	font-family: var(--font);
	color: var(--fg);
	font-size: 14px;
	line-height: 1.5;
	-webkit-font-smoothing: antialiased;
	-moz-osx-font-smoothing: grayscale;
	overflow: hidden;
}

.settings-root *,
.settings-root *::before,
.settings-root *::after {
	box-sizing: border-box;
}

.num {
	font-variant-numeric: tabular-nums;
	font-feature-settings: "tnum";
}

.mono {
	font-family: var(--mono);
}

.accent-ink {
	color: var(--accent-ink);
}

.settings-window {
	width: 100%;
	height: 100%;
	display: flex;
	flex-direction: column;
	overflow: hidden;
}

/* ---------- 主体：侧栏 + 内容 ---------- */
.sw-body {
	flex: 1;
	display: flex;
	min-height: 0;
}

.sidebar {
	width: var(--sb-w);
	flex: none;
	background: var(--sidebar);
	border-right: 1px solid var(--border);
	display: flex;
	flex-direction: column;
	min-height: 0;
}

.sb-head {
	padding: 20px 20px 14px;
}

.sb-head h1 {
	font-size: 19px;
	font-weight: 650;
	letter-spacing: -.01em;
}

.sb-head p {
	font-size: 12.5px;
	color: var(--muted);
	margin-top: 2px;
}

.sb-nav {
	flex: 1;
	overflow-y: auto;
	padding: 2px 12px 14px;
}

.sb-nav::-webkit-scrollbar {
	width: 9px;
}

.sb-nav::-webkit-scrollbar-thumb {
	background: #d7dae1;
	border-radius: 9px;
	border: 2px solid var(--sidebar);
}

.sb-group {
	margin-top: 14px;
}

.sb-group:first-child {
	margin-top: 0;
}

.sb-label {
	font-size: 11px;
	font-weight: 600;
	letter-spacing: .07em;
	text-transform: uppercase;
	color: var(--faint);
	padding: 6px 10px 6px;
}

.nav-item {
	display: flex;
	align-items: center;
	gap: 11px;
	width: 100%;
	padding: 8px 10px;
	border-radius: 8px;
	border: 0;
	background: transparent;
	color: var(--fg-2);
	font-size: 13.5px;
	font-weight: 500;
	font-family: inherit;
	cursor: pointer;
	text-align: left;
	line-height: 1.2;
	transition: background .14s var(--ease), color .14s var(--ease);
}

.nav-item:hover {
	background: rgba(20, 23, 28, .05);
	color: var(--fg);
}

.nav-item.active {
	background: var(--pill);
	color: var(--pill-fg);
	font-weight: 550;
}

.nav-item.active .ni-ico {
	color: var(--pill-fg);
}

.ni-ico {
	width: 17px;
	height: 17px;
	flex: none;
	color: var(--muted);
}

.nav-item:hover .ni-ico {
	color: var(--fg-2);
}

.ni-badge {
	margin-left: auto;
	font-size: 11px;
	font-weight: 600;
	padding: 1px 7px;
	border-radius: 999px;
	background: rgba(20, 23, 28, .07);
	color: var(--muted);
}

.nav-item.active .ni-badge {
	background: rgba(255, 255, 255, .18);
	color: #fff;
}

.sb-foot {
	padding: 11px 20px;
	border-top: 1px solid var(--border);
	font-size: 11px;
	color: var(--faint);
	display: flex;
	align-items: center;
	gap: 7px;
}

.sb-foot .liv {
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: var(--success);
}

/* 内容区 */
.content {
	flex: 1;
	min-width: 0;
	display: flex;
	flex-direction: column;
	background: var(--surface);
	position: relative;
}

.scroll {
	flex: 1;
	overflow-y: auto;
	min-height: 0;
	scroll-behavior: smooth;
}

.scroll::-webkit-scrollbar {
	width: 11px;
}

.scroll::-webkit-scrollbar-thumb {
	background: #dcdfe5;
	border-radius: 11px;
	border: 3px solid var(--surface);
}

.scroll::-webkit-scrollbar-thumb:hover {
	background: #cbcfd7;
}

.page {
	max-width: 760px;
	margin: 0 auto;
	padding: 34px 40px 90px;
	animation: settings-fade .26s var(--ease);
}

@keyframes settings-fade {
	from {
		opacity: 0;
		transform: translateY(6px);
	}

	to {
		opacity: 1;
		transform: none;
	}
}

/* ---------- 页头 ---------- */
.page-head {
	margin-bottom: 24px;
}

.page-head h2 {
	font-size: 21px;
	font-weight: 650;
	letter-spacing: -.015em;
}

.page-head .sub {
	font-size: 13.5px;
	color: var(--muted);
	margin-top: 4px;
}

.head-row {
	display: flex;
	align-items: flex-start;
	gap: 16px;
}

.head-row .grow {
	flex: 1;
	min-width: 0;
}

/* ---------- 区块 ---------- */
.block {
	margin-top: 30px;
}

.block:first-of-type {
	margin-top: 4px;
}

.block-title {
	display: flex;
	align-items: center;
	gap: 9px;
	margin-bottom: 13px;
}

.block-title h3 {
	font-size: 14px;
	font-weight: 600;
	letter-spacing: -.005em;
}

.block-title .bt-ico {
	width: 16px;
	height: 16px;
	color: var(--muted);
}

/* ---------- 卡片 ---------- */
.card {
	background: var(--surface);
	border: 1px solid var(--border);
	border-radius: var(--r-lg);
	padding: 18px 20px;
}

.card+.card {
	margin-top: 12px;
}

.card-h {
	display: flex;
	align-items: center;
	gap: 11px;
	margin-bottom: 6px;
}

.card-h .ch-title {
	font-size: 14px;
	font-weight: 600;
}

.card-h .ch-desc {
	font-size: 12.5px;
	color: var(--muted);
	margin-top: 2px;
	line-height: 1.5;
}

.card-h .grow {
	flex: 1;
	min-width: 0;
}

.ch-desc {
	font-size: 12.5px;
	color: var(--muted);
	line-height: 1.5;
}

.shell-resolve {
	margin-top: 12px;
	font-size: 12.5px;
	color: var(--muted);
	font-family: var(--mono);
	background: var(--surface-2);
	border: 1px solid var(--border);
	border-radius: 8px;
	padding: 9px 12px;
}

/* 设置行 */
.row {
	display: flex;
	align-items: center;
	gap: 18px;
	padding: 13px 0;
	border-top: 1px solid var(--border);
}

.row:first-child {
	border-top: 0;
	padding-top: 2px;
}

.row .rl {
	flex: 1;
	min-width: 0;
}

.row .rl .t {
	font-size: 13.5px;
	font-weight: 550;
}

.row .rl .d {
	font-size: 12px;
	color: var(--muted);
	margin-top: 2px;
}

.row .rc {
	flex: none;
	display: flex;
	align-items: center;
	gap: 9px;
}

/* ---------- 表单控件 ---------- */
label.fld {
	display: block;
	margin-bottom: 14px;
}

label.fld:last-child {
	margin-bottom: 0;
}

.fld>.lab {
	display: block;
	font-size: 12.5px;
	font-weight: 550;
	color: var(--fg-2);
	margin-bottom: 6px;
}

.fld .help {
	font-size: 11.5px;
	color: var(--muted);
	margin-top: 6px;
	line-height: 1.5;
}

.help {
	font-size: 11.5px;
	color: var(--muted);
	line-height: 1.5;
}

input[type=text],
input[type=url],
input[type=password],
input[type=number],
select,
textarea {
	width: 100%;
	font-family: inherit;
	font-size: 13.5px;
	color: var(--fg);
	background: var(--surface);
	border: 1px solid var(--border-2);
	border-radius: var(--r-sm);
	padding: 9px 11px;
	outline: none;
	transition: border-color .14s, box-shadow .14s;
}

input::placeholder,
textarea::placeholder {
	color: var(--faint);
}

input:focus,
select:focus,
textarea:focus {
	border-color: var(--accent);
	box-shadow: 0 0 0 3px var(--accent-wash);
}

textarea {
	resize: vertical;
	min-height: 64px;
	font-family: var(--mono);
	font-size: 12.5px;
	line-height: 1.6;
}

select {
	appearance: none;
	cursor: pointer;
	padding-right: 34px;
	background-image: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16' viewBox='0 0 24 24' fill='none' stroke='%236b7180' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><polyline points='6 9 12 15 18 9'/></svg>");
	background-repeat: no-repeat;
	background-position: right 10px center;
}

.input-mono {
	font-family: var(--mono);
	font-size: 12.5px;
}

.input-wrap {
	position: relative;
	display: flex;
	align-items: center;
}

.in-actions {
	position: absolute;
	right: 6px;
	display: flex;
	gap: 2px;
}

.icon-btn {
	width: 28px;
	height: 28px;
	border: 0;
	background: transparent;
	border-radius: 6px;
	color: var(--muted);
	display: grid;
	place-items: center;
	cursor: pointer;
}

.icon-btn:hover {
	background: var(--surface-2);
	color: var(--fg);
}

.icon-btn :deep(svg),
.icon-btn .settings-icon {
	width: 15px;
	height: 15px;
}

.search-affix {
	position: absolute;
	left: 11px;
	top: 50%;
	transform: translateY(-50%);
	width: 16px;
	height: 16px;
	color: var(--faint);
}

/* 分段控件 */
.seg {
	display: inline-flex;
	background: var(--surface-2);
	border: 1px solid var(--border);
	border-radius: 8px;
	padding: 3px;
	gap: 2px;
}

.seg button {
	border: 0;
	background: transparent;
	font-family: inherit;
	font-size: 12.5px;
	font-weight: 550;
	color: var(--muted);
	padding: 6px 14px;
	border-radius: 6px;
	cursor: pointer;
	display: flex;
	align-items: center;
	gap: 6px;
	transition: .14s var(--ease);
}

.seg button.on {
	background: var(--surface);
	color: var(--fg);
	box-shadow: 0 1px 2px rgba(20, 23, 28, .1);
}

/* 开关 */
.switch {
	position: relative;
	width: 38px;
	height: 22px;
	flex: none;
	cursor: pointer;
}

.switch input {
	position: absolute;
	opacity: 0;
	width: 100%;
	height: 100%;
	margin: 0;
	cursor: pointer;
}

.switch .tk {
	position: absolute;
	inset: 0;
	border-radius: 999px;
	background: #cdd2da;
	transition: background .18s var(--ease);
}

.switch .tk::after {
	content: "";
	position: absolute;
	top: 2px;
	left: 2px;
	width: 18px;
	height: 18px;
	background: #fff;
	border-radius: 50%;
	box-shadow: 0 1px 2px rgba(0, 0, 0, .25);
	transition: transform .18s var(--ease);
}

.switch input:checked+.tk {
	background: var(--accent);
}

.switch input:checked+.tk::after {
	transform: translateX(16px);
}

.switch input:focus-visible+.tk {
	box-shadow: 0 0 0 3px var(--accent-wash);
}

.switch input:disabled+.tk {
	opacity: .5;
	cursor: not-allowed;
}

/* ---------- 按钮 ---------- */
.btn {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	justify-content: center;
	font-family: inherit;
	font-size: 13px;
	font-weight: 550;
	letter-spacing: .005em;
	padding: 8px 14px;
	border-radius: var(--r-sm);
	border: 1px solid var(--border-2);
	background: var(--surface);
	color: var(--fg);
	cursor: pointer;
	white-space: nowrap;
	transition: background .14s, border-color .14s, box-shadow .14s, transform .04s;
}

.btn:hover {
	background: var(--surface-2);
	border-color: var(--border-2);
}

.btn:active {
	transform: translateY(1px);
}

.btn :deep(svg),
.btn .settings-icon {
	width: 15px;
	height: 15px;
}

.btn.primary {
	background: var(--accent);
	border-color: var(--accent);
	color: #fff;
}

.btn.primary:hover {
	background: var(--accent-ink);
	border-color: var(--accent-ink);
}

.btn.sm {
	padding: 6px 10px;
	font-size: 12.5px;
}

.btn.danger {
	color: var(--danger);
	border-color: var(--border-2);
}

.btn.danger:hover {
	background: var(--danger-wash);
	border-color: #f3c6c1;
}

.btn.test.ok {
	color: var(--success);
	border-color: #bfe3cf;
}

.btn[disabled] {
	opacity: .5;
	cursor: not-allowed;
}

.linkbtn {
	background: 0;
	border: 0;
	color: var(--accent);
	font-family: inherit;
	font-size: 13px;
	font-weight: 550;
	cursor: pointer;
	padding: 0;
}

.linkbtn:hover {
	text-decoration: underline;
}

/* ---------- 徽章 / 药丸 ---------- */
.chip {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	font-size: 11.5px;
	font-weight: 600;
	letter-spacing: .01em;
	padding: 3px 9px;
	border-radius: 999px;
	background: var(--surface-2);
	border: 1px solid var(--border);
	color: var(--fg-2);
}

.chip .led {
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: var(--faint);
}

.chip.mono {
	font-family: var(--mono);
	font-weight: 500;
	letter-spacing: 0;
}

.chip.run {
	background: var(--success-wash);
	border-color: #bfe3cf;
	color: var(--success);
}

.chip.run .led {
	background: var(--success);
	box-shadow: 0 0 0 3px rgba(24, 121, 78, .16);
}

.chip.stop {
	background: var(--surface-2);
	color: var(--muted);
}

.chip.stop .led {
	background: var(--faint);
}

.chip.warn {
	background: var(--warn-wash);
	border-color: #ecd9a6;
	color: var(--warn);
}

.led.oa {
	background: #10a37f;
}

.led.an {
	background: #cc785c;
}

.led.ds {
	background: #4d6bfe;
}

.led.cp {
	background: #8a93a3;
}

/* ---------- 服务商 / MCP 列表卡 ---------- */
.lcard {
	background: var(--surface);
	border: 1px solid var(--border);
	border-radius: var(--r-lg);
	padding: 16px 18px;
	transition: border-color .14s, box-shadow .14s;
}

.lcard+.lcard {
	margin-top: 11px;
}

.lcard:hover {
	border-color: var(--border-2);
	box-shadow: 0 2px 8px -4px rgba(20, 23, 28, .14);
}

.lcard.off {
	opacity: .72;
}

.lc-top {
	display: flex;
	align-items: center;
	gap: 12px;
}

.lc-ico {
	width: 38px;
	height: 38px;
	flex: none;
	border-radius: 9px;
	background: var(--surface-2);
	border: 1px solid var(--border);
	display: grid;
	place-items: center;
	color: var(--fg-2);
}

.lc-ico :deep(svg),
.lc-ico .settings-icon {
	width: 19px;
	height: 19px;
}

.lc-name {
	font-size: 14px;
	font-weight: 600;
	display: flex;
	align-items: center;
	gap: 9px;
	flex-wrap: wrap;
}

.lc-meta {
	font-size: 12px;
	color: var(--muted);
	margin-top: 2px;
	display: flex;
	align-items: center;
	gap: 8px;
	flex-wrap: wrap;
}

.lc-meta .mono {
	font-family: var(--mono);
	font-size: 11.5px;
}

.lc-grow {
	flex: 1;
	min-width: 0;
}

.lc-act {
	display: flex;
	align-items: center;
	gap: 8px;
	flex: none;
}

.lc-body {
	margin-top: 14px;
	padding-top: 14px;
	border-top: 1px solid var(--border);
}

.kv {
	display: flex;
	align-items: center;
	gap: 10px;
	padding: 6px 0;
	font-size: 12.5px;
}

.kv .k {
	width: 96px;
	flex: none;
	color: var(--muted);
	font-weight: 500;
}

.kv .v {
	flex: 1;
	min-width: 0;
	font-family: var(--mono);
	font-size: 12px;
	color: var(--fg-2);
	word-break: break-all;
}

.cmd {
	font-family: var(--mono);
	font-size: 12px;
	line-height: 1.7;
	color: var(--fg-2);
	background: var(--code-bg);
	border: 1px solid var(--border);
	border-radius: 8px;
	padding: 10px 12px;
	white-space: pre-wrap;
	word-break: break-all;
}

.cmd .tok {
	color: var(--accent-ink);
}

.cmd .flag {
	color: var(--warn);
}

/* ---------- 模型表 ---------- */
.table {
	width: 100%;
	border: 1px solid var(--border);
	border-radius: var(--r-lg);
	overflow: hidden;
	background: var(--surface);
}

.thead,
.trow {
	display: grid;
	grid-template-columns: 1.5fr 1.4fr 1fr .9fr 64px;
	align-items: center;
	gap: 14px;
}

.thead {
	background: var(--surface-2);
	padding: 10px 18px;
	font-size: 11px;
	font-weight: 600;
	letter-spacing: .05em;
	text-transform: uppercase;
	color: var(--faint);
	border-bottom: 1px solid var(--border);
}

.trow {
	padding: 13px 18px;
	border-top: 1px solid var(--border);
	cursor: pointer;
	transition: background .12s;
}

.trow:first-child {
	border-top: 0;
}

.trow:hover {
	background: var(--surface-2);
}

.trow .nm {
	font-size: 13.5px;
	font-weight: 600;
	display: flex;
	align-items: center;
	gap: 8px;
}

.trow .nm .star {
	color: var(--warn);
	width: 14px;
	height: 14px;
}

.trow .mdl {
	font-family: var(--mono);
	font-size: 12px;
	color: var(--fg-2);
}

.trow .sub {
	font-size: 12px;
	color: var(--muted);
}

.trow .ar {
	justify-self: end;
	color: var(--faint);
	display: flex;
	align-items: center;
}

.trow .ar :deep(svg),
.trow .ar .settings-icon {
	width: 16px;
	height: 16px;
}

.table-note {
	font-size: 12px;
	color: var(--faint);
	margin-top: 12px;
	display: flex;
	align-items: center;
	gap: 4px;
	flex-wrap: wrap;
}

.inline-star {
	width: 12px;
	height: 12px;
	color: var(--warn);
}

/* ---------- 右侧抽屉 ---------- */
.scrim {
	position: fixed;
	inset: 0;
	background: rgba(17, 20, 26, .36);
	opacity: 0;
	visibility: hidden;
	transition: opacity .2s var(--ease), visibility .2s;
	z-index: 60;
	backdrop-filter: blur(1.5px);
}

.scrim.open {
	opacity: 1;
	visibility: visible;
}

.drawer {
	position: fixed;
	top: 0;
	right: 0;
	height: 100%;
	width: min(460px, 94vw);
	background: var(--surface);
	border-left: 1px solid var(--border);
	box-shadow: -18px 0 50px -20px rgba(20, 23, 28, .4);
	transform: translateX(100%);
	transition: transform .26s var(--ease);
	z-index: 61;
	display: flex;
	flex-direction: column;
}

.drawer.open {
	transform: none;
}

.dr-head {
	padding: 18px 22px;
	border-bottom: 1px solid var(--border);
	display: flex;
	align-items: flex-start;
	gap: 12px;
	flex: none;
}

.dr-head h3 {
	font-size: 16px;
	font-weight: 650;
	letter-spacing: -.01em;
}

.dr-head p {
	font-size: 12.5px;
	color: var(--muted);
	margin-top: 2px;
}

.dr-head .grow {
	flex: 1;
	min-width: 0;
}

.dr-body {
	flex: 1;
	overflow-y: auto;
	padding: 20px 22px;
}

.dr-body::-webkit-scrollbar {
	width: 10px;
}

.dr-body::-webkit-scrollbar-thumb {
	background: #dcdfe5;
	border-radius: 10px;
	border: 3px solid var(--surface);
}

.dr-foot {
	flex: none;
	padding: 14px 22px;
	border-top: 1px solid var(--border);
	display: flex;
	gap: 10px;
	justify-content: flex-end;
	background: var(--surface-2);
}

.dr-sec {
	font-size: 11px;
	font-weight: 600;
	letter-spacing: .06em;
	text-transform: uppercase;
	color: var(--faint);
	margin: 20px 0 12px;
}

.dr-sec:first-child {
	margin-top: 0;
}

.field-2 {
	display: grid;
	grid-template-columns: 1fr 1fr;
	gap: 12px;
}

/* 采样滑块 */
.slider-row {
	display: flex;
	align-items: center;
	gap: 14px;
	margin-bottom: 14px;
}

.slider-row .sl-lab {
	width: 104px;
	flex: none;
	font-size: 12.5px;
	font-weight: 550;
	color: var(--fg-2);
}

.slider-row input[type=range] {
	flex: 1;
	accent-color: var(--accent);
	height: 4px;
	padding: 0;
}

.slider-row .sl-val {
	width: 48px;
	flex: none;
	text-align: right;
	font-family: var(--mono);
	font-size: 12.5px;
	color: var(--accent-ink);
	font-weight: 600;
}

/* env 键值行 */
.env-row {
	display: grid;
	grid-template-columns: 1fr 1fr 30px;
	gap: 8px;
	margin-bottom: 8px;
	align-items: center;
}

.env-row input {
	font-family: var(--mono);
	font-size: 12px;
	padding: 7px 9px;
}

/* ---------- 未保存条 ---------- */
.savebar {
	position: absolute;
	left: 0;
	right: 0;
	bottom: 0;
	display: flex;
	align-items: center;
	gap: 14px;
	padding: 12px 24px;
	background: var(--surface);
	border-top: 1px solid var(--border);
	box-shadow: 0 -8px 24px -16px rgba(20, 23, 28, .3);
	transform: translateY(120%);
	transition: transform .24s var(--ease);
	z-index: 20;
}

.savebar.show {
	transform: none;
}

.savebar .sv-msg {
	flex: 1;
	font-size: 13px;
	color: var(--fg-2);
	display: flex;
	align-items: center;
	gap: 9px;
}

.savebar .sv-msg .pulse {
	width: 7px;
	height: 7px;
	border-radius: 50%;
	background: var(--warn);
}

/* ---------- Toast ---------- */
.toasts {
	position: fixed;
	bottom: 22px;
	left: 50%;
	transform: translateX(-50%);
	z-index: 80;
	display: flex;
	flex-direction: column;
	gap: 8px;
	align-items: center;
}

.toast {
	display: flex;
	align-items: center;
	gap: 9px;
	background: var(--pill);
	color: #fff;
	font-size: 13px;
	font-weight: 500;
	padding: 9px 15px;
	border-radius: 9px;
	box-shadow: 0 8px 24px -6px rgba(20, 23, 28, .5);
	animation: settings-tin .26s var(--ease);
}

.toast .tg {
	color: #5fd39a;
	display: inline-flex;
	width: 15px;
	height: 15px;
}

@keyframes settings-tin {
	from {
		opacity: 0;
		transform: translateY(10px);
	}

	to {
		opacity: 1;
		transform: none;
	}
}

/* about */
.about-hero {
	display: flex;
	align-items: center;
	gap: 16px;
	margin-bottom: 8px;
}

.about-mark {
	width: 54px;
	height: 54px;
	border-radius: 13px;
	background: var(--pill);
	display: grid;
	place-items: center;
	color: #fff;
	flex: none;
}

.about-mark :deep(svg),
.about-mark .settings-icon {
	width: 28px;
	height: 28px;
}

.about-hero h2 {
	font-size: 20px;
	font-weight: 650;
}

.about-hero .ver {
	font-family: var(--mono);
	font-size: 12px;
	color: var(--muted);
	margin-top: 3px;
}

.meta-grid {
	display: grid;
	grid-template-columns: 1fr 1fr;
	gap: 0 28px;
	margin-top: 6px;
}

.meta-grid .kv {
	border-top: 1px solid var(--border);
}

@keyframes settings-spin {
	to {
		transform: rotate(360deg);
	}
}

@media (max-width:920px) {
	.settings-root {
		--sb-w: 228px;
	}

	.page {
		padding: 28px 26px 90px;
	}

	.thead,
	.trow {
		grid-template-columns: 1.4fr 1.3fr 60px;
	}

	.thead .c-prov,
	.trow .c-prov,
	.thead .c-fmt,
	.trow .c-fmt {
		display: none;
	}
}
</style>
