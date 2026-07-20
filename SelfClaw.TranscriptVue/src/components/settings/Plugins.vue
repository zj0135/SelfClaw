<script setup>
import { computed, reactive, ref } from 'vue';

// ── 图标：完整 SVG 字符串，模板用 v-html 注入（照 AppSidebar.vue 的写法）。
// v-html 是运行时指令，不经过模板编译器，Vite 工程下可直接渲染。
const iconMap = {
	plugin: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.85" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg>`,
	mcp: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.85" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="6" cy="6" r="2.5"/><circle cx="6" cy="18" r="2.5"/><circle cx="18" cy="12" r="2.5"/><path d="M8.5 6H13a2.5 2.5 0 0 1 2.5 2.5v1"/><path d="M8.5 18H13a2.5 2.5 0 0 0 2.5-2.5v-1"/></svg>`,
	skill: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.85" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 2 4 6v6c0 5 3.5 8 8 10 4.5-2 8-5 8-10V6z"/><path d="m9 12 2 2 4-4"/></svg>`,
};

function getIcon(name) {
	return iconMap[name] || '';
}

// ── 三类扩展数据 ────────────────────────────────────────────────
// 实际接入时用 sendHostMessage('get-extensions') 拉取，这里给出结构与示例。
const categories = reactive({
	plugin: {
		name: '插件',
		icon: 'plugin',
		sub: '扩展应用功能的能力包',
		addType: 'import',
		items: [
			{
				id: 'od-default',
				name: '默认设计路由器',
				version: '0.1.0',
				on: true,
				desc: '为自由输入的提示分流任务类型，再进入对应的设计工作流。',
				detail: { source: 'plugins/_official/od-default', kind: '任务路由', perms: ['读写工作区文件', '调用生成能力'] },
			},
			{
				id: 'guizang-ppt',
				name: '规藏 PPT',
				version: '1.4.2',
				on: true,
				desc: '固定画布幻灯片框架，内置缩放适配、导航与 PDF 导出。',
				detail: { source: 'plugins/guizang-ppt', kind: '幻灯片', perms: ['读写工作区文件'] },
			},
			{
				id: 'brand-extract',
				name: '品牌规范提取',
				version: '0.3.0',
				on: false,
				desc: '从站点或截图中提取配色与字体，生成 brand-spec.md。',
				detail: { source: 'plugins/brand-extract', kind: '资产分析', perms: ['访问网络', '读取附件'] },
			},
		],
	},
	mcp: {
		name: 'MCP',
		icon: 'mcp',
		sub: 'Model Context Protocol 服务器连接',
		addType: 'mcp',
		items: [
			{
				id: 'filesystem',
				name: 'filesystem',
				version: 'stdio',
				on: true,
				desc: 'npx -y @modelcontextprotocol/server-filesystem ./workspace',
				detail: {
					transport: 'stdio',
					command: 'npx',
					args: '-y @modelcontextprotocol/server-filesystem ./workspace',
					env: '—',
					tools: ['read_file', 'write_file', 'list_directory'],
				},
			},
			{
				id: 'git',
				name: 'git',
				version: 'stdio',
				on: true,
				desc: 'uvx mcp-server-git --repository .',
				detail: {
					transport: 'stdio',
					command: 'uvx',
					args: 'mcp-server-git --repository .',
					env: '—',
					tools: ['git_status', 'git_diff', 'git_log', 'git_commit'],
				},
			},
			{
				id: 'context7',
				name: 'context7',
				version: 'sse',
				on: false,
				desc: 'https://mcp.context7.com/sse',
				detail: {
					transport: 'sse',
					url: 'https://mcp.context7.com/sse',
					env: 'CONTEXT7_API_KEY=••••',
					tools: ['resolve-library-id', 'get-library-docs'],
				},
			},
		],
	},
	skill: {
		name: 'SKILL',
		icon: 'skill',
		sub: '可复用的专业技能与工作流',
		addType: 'import',
		items: [
			{
				id: 'simple-deck',
				name: 'simple-deck',
				version: '2.0.1',
				on: true,
				desc: '八种成品幻灯片版式，含密度规则与 P0/P1/P2 自检清单。',
				detail: {
					source: 'skills/simple-deck',
					triggers: ['deck', 'presentation', '幻灯片'],
					files: ['template.html', 'layouts.md', 'checklist.md'],
				},
			},
			{
				id: 'xaml-reader',
				name: 'xaml-layout-reader',
				version: '0.2.0',
				on: true,
				desc: '解析 WPF XAML 布局，输出组件树与栅格结构说明。',
				detail: { source: 'skills/xaml-reader', triggers: ['xaml', 'wpf', '布局'], files: ['SKILL.md', 'parser.md'] },
			},
			{
				id: 'anti-slop',
				name: 'anti-ai-slop',
				version: '1.1.0',
				on: false,
				desc: '对产出的 HTML 做反 AI 套路检查：配色、字距、占位文案。',
				detail: { source: 'skills/anti-slop', triggers: ['review', '自检'], files: ['SKILL.md', 'rules.md'] },
			},
		],
	},
});

const catOrder = ['plugin', 'mcp', 'skill'];

const activeCat = ref('plugin');
const searchTerm = ref('');
const toastState = reactive({ visible: false, text: '' });
let toastTimer = null;

// ── 计算属性 ────────────────────────────────────────────────────
const activeCategory = computed(() => categories[activeCat.value]);

const enabledCount = (cat) => categories[cat].items.filter((i) => i.on).length;

const filteredItems = computed(() => {
	const term = searchTerm.value.trim().toLowerCase();
	return activeCategory.value.items.filter(
		(it) => !term || it.name.toLowerCase().includes(term) || it.id.toLowerCase().includes(term) || (it.desc || '').toLowerCase().includes(term)
	);
});

const addLabel = computed(() => (activeCategory.value.addType === 'mcp' ? '新增服务器' : '导入'));

const countText = computed(() => {
	const total = activeCategory.value.items.length;
	if (searchTerm.value.trim()) return `匹配 ${filteredItems.value.length} / ${total} 项`;
	return `共 ${total} 项 · ${enabledCount(activeCat.value)} 已启用`;
});

// ── tab 切换 ────────────────────────────────────────────────────
function selectTab(cat) {
	if (categories[cat]) activeCat.value = cat;
}

function onTabKey(event, index) {
	if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
	event.preventDefault();
	const next = event.key === 'ArrowRight' ? (index + 1) % catOrder.length : (index - 1 + catOrder.length) % catOrder.length;
	selectTab(catOrder[next]);
	const buttons = document.querySelectorAll('.plugins-view .tab-btn');
	buttons[next]?.focus();
}

function clearSearch() {
	searchTerm.value = '';
}

// ── 启用 / 停用 ─────────────────────────────────────────────────
function toggleItem(it) {
	it.on = !it.on;
	if (drawer.item && drawer.item.id === it.id) drawer.item = it;
	toast(it.on ? `已启用 ${it.name}` : `已停用 ${it.name}`);
}

// ── 查看抽屉 ────────────────────────────────────────────────────
const drawer = reactive({ open: false, cat: 'plugin', item: null });

function openDrawer(cat, it) {
	drawer.cat = cat;
	drawer.item = it;
	drawer.open = true;
}

function closeDrawer() {
	drawer.open = false;
}

// ── 新增：MCP 配置弹框 ───────────────────────────────────────────
const mcpDialog = reactive({ open: false, transport: 'stdio', name: '', command: '', args: '', url: '', env: '' });

function openMcpDialog() {
	Object.assign(mcpDialog, { open: true, transport: 'stdio', name: '', command: '', args: '', url: '', env: '' });
}

function saveMcp() {
	const name = mcpDialog.name.trim();
	if (!name) return;
	const isSse = mcpDialog.transport === 'sse';
	categories.mcp.items.push({
		id: `${name}-${Date.now()}`,
		name,
		version: mcpDialog.transport,
		on: true,
		desc: isSse ? mcpDialog.url.trim() || '（未填写地址）' : `${mcpDialog.command.trim() || '—'} ${mcpDialog.args.trim()}`.trim(),
		detail: isSse
			? { transport: 'sse', url: mcpDialog.url.trim() || '—', env: mcpDialog.env.trim() || '—', tools: [] }
			: {
					transport: 'stdio',
					command: mcpDialog.command.trim() || '—',
					args: mcpDialog.args.trim() || '—',
					env: mcpDialog.env.trim() || '—',
					tools: [],
				},
	});
	mcpDialog.open = false;
	activeCat.value = 'mcp';
	searchTerm.value = '';
	toast(`已添加 MCP 服务器 ${name}`);
}

// ── 新增：插件 / SKILL 文件导入弹框 ──────────────────────────────
const importDialog = reactive({ open: false, cat: 'plugin', fileName: '' });
let importFile = null;

const importIsSkill = computed(() => importDialog.cat === 'skill');

function openImportDialog(cat) {
	Object.assign(importDialog, { open: true, cat, fileName: '' });
	importFile = null;
}

function onFilePick(event) {
	const file = event.target.files?.[0];
	if (file) {
		importFile = file;
		importDialog.fileName = file.name;
	}
}

function onFileDrop(event) {
	const file = event.dataTransfer.files?.[0];
	if (file) {
		importFile = file;
		importDialog.fileName = file.name;
	}
}

function saveImport() {
	if (!importFile) return;
	const cat = importDialog.cat;
	const base = importFile.name.replace(/\.[^.]+$/, '');
	categories[cat].items.push({
		id: `${base}-${Date.now()}`,
		name: base,
		version: '1.0.0',
		on: true,
		desc: `由 ${importFile.name} 导入`,
		detail:
			cat === 'skill'
				? { source: `skills/${base}`, triggers: ['—'], files: [importFile.name] }
				: { kind: '导入的插件', source: `plugins/${base}`, perms: ['待审核'] },
	});
	importDialog.open = false;
	activeCat.value = cat;
	searchTerm.value = '';
	toast(`已导入 ${cat === 'skill' ? '技能' : '插件'} ${base}`);
}

// ── 新增分发 ────────────────────────────────────────────────────
function onAdd() {
	if (activeCategory.value.addType === 'mcp') openMcpDialog();
	else openImportDialog(activeCat.value);
}

// ── toast ───────────────────────────────────────────────────────
function toast(text) {
	toastState.text = text;
	toastState.visible = true;
	clearTimeout(toastTimer);
	toastTimer = setTimeout(() => {
		toastState.visible = false;
	}, 1900);
}

defineExpose({
	handleMessage() {
		// 预留：接入宿主返回的扩展列表 / 启用状态时在此处理。
	},
});
</script>

<template>
	<main class="plugins-view settings-content scroll">
		<div class="panel">
			<!-- tab 切换 + 右侧功能按钮 -->
			<div class="tab-bar">
				<div class="tab-strip" role="tablist" aria-label="扩展类型">
					<button
						v-for="(cat, index) in catOrder"
						:key="cat"
						type="button"
						class="tab-btn"
						:class="{ active: activeCat === cat }"
						role="tab"
						:aria-selected="activeCat === cat ? 'true' : 'false'"
						:tabindex="activeCat === cat ? 0 : -1"
						@click="selectTab(cat)"
						@keydown="onTabKey($event, index)"
					>
						<span class="tab-ico" aria-hidden="true" v-html="getIcon(categories[cat].icon)"></span>
						{{ categories[cat].name }}
						<span class="tab-n">{{ categories[cat].items.length }}</span>
					</button>
				</div>

				<button type="button" class="add-btn" @click="onAdd">
					<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
						<path d="M12 5v14M5 12h14" />
					</svg>
					{{ addLabel }}
				</button>
			</div>

			<!-- 查询框 + 计数 -->
			<div class="tool-row">
				<div class="search">
					<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
						<circle cx="11" cy="11" r="8" />
						<path d="m21 21-4.3-4.3" />
					</svg>
					<input v-model="searchTerm" type="text" placeholder="搜索…" aria-label="搜索当前类型" />
					<button v-show="searchTerm" type="button" class="search-clear" aria-label="清除搜索" @click="clearSearch">
						<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
							<path d="M18 6 6 18M6 6l12 12" />
						</svg>
					</button>
				</div>
				<div class="tool-count">{{ countText }}</div>
			</div>

			<!-- 当前 tab 的项，按列表铺开 -->
			<div class="list" role="tabpanel">
				<template v-if="filteredItems.length">
					<div v-for="it in filteredItems" :key="it.id" class="item" :class="{ off: !it.on }">
						<span class="item-logo" aria-hidden="true" v-html="getIcon(activeCategory.icon)"></span>
						<div class="item-main">
							<div class="item-title">
								<span class="item-name">{{ it.name }}</span>
								<span class="item-tag" :class="{ type: activeCat === 'mcp' }">
									{{ activeCat === 'mcp' ? it.version.toUpperCase() : 'v' + it.version }}
								</span>
							</div>
							<div class="item-desc" :title="it.desc">{{ it.desc }}</div>
						</div>
						<div class="item-actions">
							<span class="status-pill" :class="it.on ? 'on' : 'off'"><span class="d" />{{ it.on ? '已启用' : '已停用' }}</span>
							<button class="icon-act" type="button" aria-label="查看详情" title="查看" @click="openDrawer(activeCat, it)">
								<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round">
									<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z" />
									<circle cx="12" cy="12" r="3" />
								</svg>
							</button>
							<label class="switch" title="启用/停用">
								<input type="checkbox" :checked="it.on" :aria-label="`启用 ${it.name}`" @change="toggleItem(it)" />
								<span class="track" /><span class="knob" />
							</label>
						</div>
					</div>
				</template>
				<div v-else class="empty">
					{{ searchTerm.trim() ? `没有匹配「${searchTerm.trim()}」的项` : `暂无内容，点击右上角「${addLabel}」添加。` }}
				</div>
			</div>
		</div>

		<!-- ── MCP 配置弹框 ─────────────────────────────────────────── -->
		<div v-if="mcpDialog.open" class="overlay show" @click.self="mcpDialog.open = false">
			<div class="dialog" role="dialog" aria-modal="true" aria-labelledby="mcp-title">
				<div class="dialog-head">
					<div class="dh-ico" aria-hidden="true" v-html="getIcon('mcp')"></div>
					<div>
						<h3 id="mcp-title">新增 MCP 服务器</h3>
						<p>配置一个 Model Context Protocol 连接</p>
					</div>
					<button class="icon-act dialog-close" aria-label="关闭" @click="mcpDialog.open = false">
						<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
							<path d="M18 6 6 18M6 6l12 12" />
						</svg>
					</button>
				</div>
				<div class="dialog-body scroll">
					<div class="field">
						<label>连接方式</label>
						<div class="seg">
							<button type="button" :class="{ active: mcpDialog.transport === 'stdio' }" @click="mcpDialog.transport = 'stdio'">
								本地进程 (stdio)
							</button>
							<button type="button" :class="{ active: mcpDialog.transport === 'sse' }" @click="mcpDialog.transport = 'sse'">远程 (SSE / HTTP)</button>
						</div>
					</div>
					<div class="field">
						<label>名称<span class="req">*</span></label>
						<input v-model="mcpDialog.name" class="input" type="text" placeholder="例如：filesystem" />
					</div>

					<template v-if="mcpDialog.transport === 'stdio'">
						<div class="field">
							<label>启动命令<span class="req">*</span></label>
							<input v-model="mcpDialog.command" class="input mono" type="text" placeholder="npx" />
						</div>
						<div class="field">
							<label>启动参数</label>
							<input v-model="mcpDialog.args" class="input mono" type="text" placeholder="-y @modelcontextprotocol/server-filesystem ./workspace" />
							<p class="hint">以空格分隔，每个参数会独立传入。</p>
						</div>
					</template>

					<div v-else class="field">
						<label>服务器地址<span class="req">*</span></label>
						<input v-model="mcpDialog.url" class="input mono" type="text" placeholder="https://mcp.example.com/sse" />
					</div>

					<div class="field">
						<label>环境变量</label>
						<textarea v-model="mcpDialog.env" class="textarea mono" placeholder="每行一个，KEY=VALUE&#10;GITHUB_TOKEN=ghp_xxx"></textarea>
						<p class="hint">用于向服务器进程注入密钥等配置，留空则不设置。</p>
					</div>
				</div>
				<div class="dialog-foot">
					<button class="btn" @click="mcpDialog.open = false">取消</button>
					<button class="btn primary" :disabled="!mcpDialog.name.trim()" @click="saveMcp">保存并连接</button>
				</div>
			</div>
		</div>

		<!-- ── 文件导入弹框（插件 / SKILL 复用） ───────────────────────── -->
		<div v-if="importDialog.open" class="overlay show" @click.self="importDialog.open = false">
			<div class="dialog" role="dialog" aria-modal="true" aria-labelledby="import-title">
				<div class="dialog-head">
					<div class="dh-ico" aria-hidden="true" v-html="getIcon(importIsSkill ? 'skill' : 'plugin')"></div>
					<div>
						<h3 id="import-title">{{ importIsSkill ? '导入技能' : '导入插件' }}</h3>
						<p>{{ importIsSkill ? '从本地文件安装技能' : '从本地文件安装插件' }}</p>
					</div>
					<button class="icon-act dialog-close" aria-label="关闭" @click="importDialog.open = false">
						<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
							<path d="M18 6 6 18M6 6l12 12" />
						</svg>
					</button>
				</div>
				<div class="dialog-body">
					<div class="field">
						<label>选择文件<span class="req">*</span></label>
						<label class="dropzone" :class="{ 'has-file': importDialog.fileName }" @dragover.prevent @drop.prevent="onFileDrop">
							<input type="file" hidden @change="onFilePick" />
							<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round">
								<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
								<polyline points="17 8 12 3 7 8" />
								<line x1="12" y1="3" x2="12" y2="15" />
							</svg>
							<span class="dz-title">点击选择，或拖拽文件到此处</span>
							<span class="dz-sub">{{ importIsSkill ? '支持 .zip / .odskill 包或 SKILL.md' : '支持 .zip / .odplugin 包' }}</span>
							<span class="dz-file">
								<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
									<path d="M20 6 9 17l-5-5" />
								</svg>
								{{ importDialog.fileName }}
							</span>
						</label>
						<p class="hint">
							{{ importIsSkill ? '导入后可在列表中启用，技能会复制到应用技能目录。' : '导入后可在列表中启用，插件会复制到应用扩展目录。' }}
						</p>
					</div>
				</div>
				<div class="dialog-foot">
					<button class="btn" @click="importDialog.open = false">取消</button>
					<button class="btn primary" :disabled="!importDialog.fileName" @click="saveImport">导入</button>
				</div>
			</div>
		</div>

		<!-- ── 查看抽屉 ───────────────────────────────────────────────── -->
		<div class="drawer-wrap" :class="{ show: drawer.open }">
			<div class="drawer-scrim" @click="closeDrawer" />
			<aside class="drawer" role="dialog" aria-modal="true" aria-labelledby="drawer-name">
				<template v-if="drawer.item">
					<div class="drawer-head">
						<div class="dr-logo" aria-hidden="true" v-html="getIcon(categories[drawer.cat].icon)"></div>
						<div class="dr-meta">
							<h3 id="drawer-name">{{ drawer.item.name }}</h3>
							<div class="dr-sub">
								{{ categories[drawer.cat].name }} · {{ drawer.cat === 'mcp' ? drawer.item.version : 'v' + drawer.item.version }}
							</div>
						</div>
						<button class="icon-act" aria-label="关闭" @click="closeDrawer">
							<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
								<path d="M18 6 6 18M6 6l12 12" />
							</svg>
						</button>
					</div>
					<div class="drawer-body scroll">
						<div class="dr-section">
							<h4>说明</h4>
							<p class="dr-desc">{{ drawer.item.desc }}</p>
						</div>

						<template v-if="drawer.cat === 'mcp'">
							<div class="dr-section">
								<h4>连接配置</h4>
								<dl class="kv">
									<template v-if="drawer.item.detail.transport === 'sse'">
										<dt>连接方式</dt>
										<dd>SSE / HTTP</dd>
										<dt>服务器地址</dt>
										<dd>{{ drawer.item.detail.url }}</dd>
									</template>
									<template v-else>
										<dt>连接方式</dt>
										<dd>本地进程 (stdio)</dd>
										<dt>启动命令</dt>
										<dd>{{ drawer.item.detail.command }}</dd>
										<dt>启动参数</dt>
										<dd>{{ drawer.item.detail.args }}</dd>
									</template>
									<dt>环境变量</dt>
									<dd>{{ drawer.item.detail.env }}</dd>
								</dl>
							</div>
							<div class="dr-section">
								<h4>提供的工具</h4>
								<div class="perm-list">
									<span v-for="t in drawer.item.detail.tools" :key="t" class="perm">{{ t }}</span>
									<span v-if="!drawer.item.detail.tools.length" class="perm">—</span>
								</div>
							</div>
						</template>

						<template v-else-if="drawer.cat === 'skill'">
							<div class="dr-section">
								<h4>触发词</h4>
								<div class="perm-list">
									<span v-for="t in drawer.item.detail.triggers" :key="t" class="perm">{{ t }}</span>
								</div>
							</div>
							<div class="dr-section">
								<h4>包含文件</h4>
								<div class="perm-list">
									<span v-for="f in drawer.item.detail.files" :key="f" class="perm">{{ f }}</span>
								</div>
							</div>
							<div class="dr-section">
								<h4>安装位置</h4>
								<dl class="kv">
									<dt>路径</dt>
									<dd>{{ drawer.item.detail.source }}</dd>
								</dl>
							</div>
						</template>

						<template v-else>
							<div class="dr-section">
								<h4>信息</h4>
								<dl class="kv">
									<dt>类型</dt>
									<dd>{{ drawer.item.detail.kind }}</dd>
									<dt>安装位置</dt>
									<dd>{{ drawer.item.detail.source }}</dd>
								</dl>
							</div>
							<div class="dr-section">
								<h4>权限</h4>
								<div class="perm-list">
									<span v-for="p in drawer.item.detail.perms" :key="p" class="perm">{{ p }}</span>
								</div>
							</div>
						</template>
					</div>
					<div class="drawer-foot">
						<span class="status-pill grow" :class="drawer.item.on ? 'on' : 'off'"><span class="d" />{{ drawer.item.on ? '已启用' : '已停用' }}</span>
						<button class="btn" @click="toggleItem(drawer.item)">{{ drawer.item.on ? '停用' : '启用' }}</button>
					</div>
				</template>
			</aside>
		</div>

		<!-- toast -->
		<div class="toast" :class="{ show: toastState.visible }" role="status" aria-live="polite">
			<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5" /></svg>
			<span>{{ toastState.text }}</span>
		</div>
	</main>
</template>

<style scoped>
.plugins-view {
	--panel: #ffffff;
	--panel-soft: #f7f8fa;
	--panel-muted: #f1f3f6;
	--border: #e5e7eb;
	--border-strong: #d8dde5;
	--text: #171a1f;
	--muted: #6b7280;
	--muted-soft: #8a929e;
	--accent: #4f73c8;
	--accent-2: #375fae;
	--accent-soft: #eef2fb;
	--pill: #1f232b;
	--pill-fg: #ffffff;
	--ok: #2f9e5f;
	--ok-soft: #e8f6ee;
	--danger: #c2483c;
	--font-display: 'Segoe UI Variable Display', 'Segoe UI', sans-serif;
	--font-mono: 'Cascadia Code', 'SF Mono', 'JetBrains Mono', ui-monospace, Consolas, monospace;
	--radius-sm: 7px;
	--radius-md: 10px;
	--radius-lg: 13px;
	--shadow-sm: 0 1px 2px rgba(23, 26, 31, 0.06);
	--shadow-md: 0 8px 28px rgba(23, 26, 31, 0.12), 0 2px 6px rgba(23, 26, 31, 0.06);

	height: 100%;
	overflow-y: auto;
	background: #fff;
	color: var(--text);
}
.plugins-view * {
	box-sizing: border-box;
}
.plugins-view button {
	cursor: pointer;
	font: inherit;
}

.scroll {
	scrollbar-width: thin;
	scrollbar-color: var(--border-strong) transparent;
}
.scroll::-webkit-scrollbar {
	width: 9px;
	height: 9px;
}
.scroll::-webkit-scrollbar-thumb {
	background: var(--border-strong);
	background-clip: padding-box;
	border: 2px solid transparent;
	border-radius: 99px;
}
.scroll::-webkit-scrollbar-thumb:hover {
	background: var(--muted-soft);
}

.panel {
	max-width: 880px;
	padding: 30px 34px 64px;
}

/* ── tab 切换栏 ─────────────────────────────────────────────── */
.tab-bar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 16px;
	margin-bottom: 14px;
}
.tab-strip {
	display: inline-flex;
	align-items: center;
	gap: 2px;
	padding: 4px;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--panel-soft);
}
.tab-btn {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	padding: 6px 14px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--muted);
	font-size: 12.5px;
	font-weight: 500;
	transition:
		background 0.14s,
		color 0.14s,
		box-shadow 0.14s;
}
.tab-ico {
	display: inline-flex;
	align-items: center;
	justify-content: center;
}
.tab-btn :deep(svg) {
	display: block;
	width: 15px;
	height: 15px;
}
.tab-n {
	font-family: var(--font-mono);
	font-size: 11px;
	font-weight: 560;
	padding: 0 6px;
	height: 16px;
	display: inline-grid;
	place-items: center;
	border-radius: 99px;
	background: var(--panel-muted);
	color: var(--muted-soft);
}
.tab-btn:hover {
	color: var(--text);
}
.tab-btn.active {
	background: var(--panel);
	color: var(--text);
	font-weight: 600;
	box-shadow:
		var(--shadow-sm),
		0 0 0 1px rgba(23, 26, 31, 0.04);
}
.tab-btn.active .tab-n {
	background: var(--accent-soft);
	color: var(--accent-2);
}
.tab-btn:focus-visible {
	outline: 2px solid var(--accent);
	outline-offset: 2px;
}

.add-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 7px;
	flex: 0 0 auto;
	min-height: 34px;
	padding: 6px 13px;
	border-radius: 7px;
	border: 1px solid var(--border-strong);
	background: var(--panel);
	color: var(--text);
	font-size: 12.5px;
	font-weight: 600;
	line-height: 1.2;
	white-space: nowrap;
	transition:
		border-color 0.14s,
		background 0.14s,
		color 0.14s,
		transform 0.08s;
}
.add-btn svg {
	width: 15px;
	height: 15px;
	stroke-width: 2;
	color: var(--muted);
	transition: color 0.14s;
}
.add-btn:hover {
	border-color: #cfd5df;
	background: var(--panel-soft);
}
.add-btn:hover svg {
	color: var(--accent-2);
}
.add-btn:active {
	transform: translateY(1px);
}

/* ── 查询框 + 计数行 ────────────────────────────────────────── */
.tool-row {
	display: flex;
	align-items: center;
	gap: 12px;
	margin-bottom: 14px;
}
.search {
	position: relative;
	display: flex;
	align-items: center;
	flex: 1;
	min-width: 0;
}
.search > svg {
	position: absolute;
	left: 11px;
	width: 15px;
	height: 15px;
	color: var(--muted-soft);
	stroke-width: 1.9;
	pointer-events: none;
}
.search input {
	width: 100%;
	padding: 8px 34px 8px 33px;
	border: 1px solid var(--border-strong);
	border-radius: var(--radius-sm);
	background: var(--panel);
	color: var(--text);
	font: inherit;
	font-size: 13px;
	transition:
		border-color 0.14s,
		box-shadow 0.14s;
}
.search input::placeholder {
	color: var(--muted-soft);
}
.search input:focus {
	border-color: var(--accent);
	outline: none;
	box-shadow: 0 0 0 3px var(--accent-soft);
}
.search-clear {
	position: absolute;
	right: 6px;
	display: grid;
	place-items: center;
	width: 24px;
	height: 24px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--muted-soft);
	transition:
		background 0.13s,
		color 0.13s;
}
.search-clear svg {
	width: 15px;
	height: 15px;
}
.search-clear:hover {
	background: var(--panel-soft);
	color: var(--text);
}
.tool-count {
	flex: 0 0 auto;
	font-size: 12px;
	color: var(--muted);
	font-family: var(--font-mono);
	white-space: nowrap;
}

/* ── 列表 ───────────────────────────────────────────────────── */
.list {
	border: 1px solid var(--border);
	border-radius: var(--radius-lg);
	background: var(--panel);
	overflow: hidden;
}
.item {
	display: flex;
	align-items: center;
	gap: 13px;
	padding: 13px 16px;
	border-bottom: 1px solid var(--border);
	background: var(--panel);
	transition: background 0.13s;
}
.item:hover {
	background: var(--panel-soft);
}
.item:last-child {
	border-bottom: 0;
}
.item.off .item-logo,
.item.off .item-main {
	opacity: 0.6;
}
.item-logo {
	display: grid;
	place-items: center;
	width: 32px;
	height: 32px;
	flex: 0 0 auto;
	border-radius: 8px;
	border: 1px solid var(--border);
	background: var(--panel-soft);
	color: var(--accent-2);
}
.item-logo :deep(svg) {
	width: 17px;
	height: 17px;
	stroke-width: 1.8;
}
.item-main {
	flex: 1;
	min-width: 0;
}
.item-title {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 8px;
}
.item-name {
	font-size: 13.5px;
	font-weight: 600;
}
.item-tag {
	padding: 2px 7px;
	border-radius: 5px;
	border: 1px solid var(--border);
	background: var(--panel-soft);
	color: var(--muted);
	font-size: 10.5px;
	font-family: var(--font-mono);
	font-weight: 500;
}
.item-tag.type {
	color: var(--accent-2);
	background: var(--accent-soft);
	border-color: transparent;
}
.item-desc {
	margin-top: 3px;
	color: var(--muted);
	font-size: 12px;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
	max-width: 46ch;
}
.item-actions {
	display: flex;
	align-items: center;
	gap: 10px;
	flex: 0 0 auto;
}

.status-pill {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 3px 9px;
	border-radius: 99px;
	font-size: 11px;
	font-weight: 560;
	letter-spacing: 0.01em;
}
.status-pill .d {
	width: 6px;
	height: 6px;
	border-radius: 50%;
}
.status-pill.on {
	background: var(--ok-soft);
	color: #1f7a45;
}
.status-pill.on .d {
	background: var(--ok);
	box-shadow: 0 0 0 3px rgba(47, 158, 95, 0.15);
}
.status-pill.off {
	background: var(--panel-muted);
	color: var(--muted);
}
.status-pill.off .d {
	background: var(--muted-soft);
}

.icon-act {
	display: grid;
	place-items: center;
	width: 30px;
	height: 30px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--muted-soft);
	transition:
		background 0.13s,
		color 0.13s;
}
.icon-act svg {
	width: 17px;
	height: 17px;
	stroke-width: 1.8;
}
.icon-act:hover {
	background: var(--panel-soft);
	color: var(--text);
}

.switch {
	position: relative;
	width: 42px;
	height: 24px;
	flex: 0 0 auto;
}
.switch input {
	position: absolute;
	z-index: 2;
	inset: 0;
	width: 100%;
	height: 100%;
	margin: 0;
	opacity: 0;
	cursor: pointer;
}
.track {
	position: absolute;
	inset: 0;
	border-radius: 99px;
	background: var(--border-strong);
	transition: background 0.18s;
}
.knob {
	position: absolute;
	top: 3px;
	left: 3px;
	width: 18px;
	height: 18px;
	border-radius: 50%;
	background: #fff;
	box-shadow: 0 1px 3px rgba(0, 0, 0, 0.25);
	transition: transform 0.18s;
}
.switch input:checked + .track {
	background: var(--pill);
}
.switch input:checked + .track + .knob {
	transform: translateX(18px);
}

.empty {
	padding: 34px;
	text-align: center;
	color: var(--muted);
	font-size: 13px;
}

/* ── 弹框 ───────────────────────────────────────────────────── */
.overlay {
	position: fixed;
	inset: 0;
	z-index: 100;
	display: flex;
	align-items: center;
	justify-content: center;
	padding: 24px;
	background: rgba(23, 26, 31, 0.32);
	backdrop-filter: blur(2px);
}
@keyframes pop {
	from {
		opacity: 0;
		transform: translateY(8px) scale(0.98);
	}
	to {
		opacity: 1;
		transform: none;
	}
}
.dialog {
	width: 100%;
	max-width: 460px;
	max-height: calc(100vh - 48px);
	display: flex;
	flex-direction: column;
	overflow: hidden;
	background: var(--panel);
	border: 1px solid var(--border);
	border-radius: var(--radius-lg);
	box-shadow: var(--shadow-md);
	animation: pop 0.2s cubic-bezier(0.2, 0.7, 0.3, 1);
}
.dialog-head {
	display: flex;
	align-items: center;
	gap: 12px;
	padding: 18px 20px 14px;
	border-bottom: 1px solid var(--border);
}
.dh-ico {
	display: grid;
	place-items: center;
	width: 34px;
	height: 34px;
	border-radius: 9px;
	background: var(--accent-soft);
	color: var(--accent-2);
	flex: 0 0 auto;
}
.dh-ico :deep(svg) {
	width: 18px;
	height: 18px;
	stroke-width: 1.9;
}
.dialog-head h3 {
	margin: 0;
	font-size: 15.5px;
	font-weight: 640;
}
.dialog-head p {
	margin: 1px 0 0;
	font-size: 12px;
	color: var(--muted);
}
.dialog-close {
	margin-left: auto;
}
.dialog-body {
	padding: 18px 20px;
	overflow-y: auto;
}
.field {
	margin-bottom: 16px;
}
.field:last-child {
	margin-bottom: 0;
}
.field label {
	display: block;
	margin-bottom: 6px;
	font-size: 12.5px;
	font-weight: 600;
}
.req {
	color: var(--danger);
	margin-left: 2px;
}
.hint {
	margin: 6px 0 0;
	font-size: 11.5px;
	color: var(--muted);
}
.input,
.textarea {
	width: 100%;
	padding: 9px 12px;
	border: 1px solid var(--border-strong);
	border-radius: var(--radius-sm);
	background: var(--panel);
	color: var(--text);
	font: inherit;
	font-size: 13px;
	transition:
		border-color 0.14s,
		box-shadow 0.14s;
}
.input.mono,
.textarea.mono {
	font-family: var(--font-mono);
	font-size: 12.5px;
	letter-spacing: 0.01em;
}
.textarea {
	resize: vertical;
	min-height: 72px;
}
.input:focus,
.textarea:focus {
	border-color: var(--accent);
	outline: none;
	box-shadow: 0 0 0 3px var(--accent-soft);
}
.seg {
	display: flex;
	gap: 6px;
}
.seg button {
	flex: 1;
	padding: 8px;
	border: 1px solid var(--border-strong);
	border-radius: var(--radius-sm);
	background: var(--panel);
	color: var(--muted);
	font-size: 12.5px;
	font-weight: 560;
	transition: all 0.13s;
}
.seg button.active {
	background: var(--pill);
	border-color: var(--pill);
	color: #fff;
}

.dropzone {
	display: flex;
	flex-direction: column;
	align-items: center;
	gap: 8px;
	padding: 26px 18px;
	border: 1.5px dashed var(--border-strong);
	border-radius: var(--radius-md);
	background: var(--panel-soft);
	text-align: center;
	cursor: pointer;
	transition:
		border-color 0.14s,
		background 0.14s;
}
.dropzone:hover {
	border-color: var(--accent);
	background: var(--accent-soft);
}
.dropzone > svg {
	width: 26px;
	height: 26px;
	color: var(--accent-2);
	stroke-width: 1.7;
}
.dz-title {
	font-size: 13px;
	font-weight: 560;
	color: var(--text);
}
.dz-sub {
	font-size: 11.5px;
	color: var(--muted);
}
.dz-file {
	display: none;
	align-items: center;
	gap: 8px;
	margin-top: 4px;
	padding: 6px 12px;
	border-radius: 7px;
	background: var(--panel);
	border: 1px solid var(--border);
	font-family: var(--font-mono);
	font-size: 12px;
	color: var(--text);
}
.dz-file svg {
	width: 15px;
	height: 15px;
	color: var(--ok);
}
.dropzone.has-file .dz-file {
	display: inline-flex;
}
.dropzone.has-file .dz-title,
.dropzone.has-file .dz-sub,
.dropzone.has-file > svg {
	display: none;
}

.dialog-foot {
	display: flex;
	justify-content: flex-end;
	gap: 9px;
	padding: 14px 20px;
	border-top: 1px solid var(--border);
	background: var(--panel-soft);
}
.btn {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	height: 36px;
	padding: 0 18px;
	border-radius: var(--radius-sm);
	border: 1px solid var(--border-strong);
	background: var(--panel);
	color: var(--text);
	font-size: 13px;
	font-weight: 560;
	transition:
		background 0.14s,
		border-color 0.14s,
		color 0.14s,
		opacity 0.14s;
}
.btn:hover {
	background: var(--panel-soft);
	border-color: var(--muted-soft);
}
.btn.primary {
	background: var(--pill);
	border-color: var(--pill);
	color: #fff;
}
.btn.primary:hover {
	background: #2b303a;
}
.btn:disabled {
	opacity: 0.5;
	cursor: default;
}

/* ── 查看抽屉 ───────────────────────────────────────────────── */
.drawer-wrap {
	position: fixed;
	inset: 0;
	z-index: 110;
	display: none;
}
.drawer-wrap.show {
	display: block;
}
.drawer-scrim {
	position: absolute;
	inset: 0;
	background: rgba(23, 26, 31, 0.32);
	backdrop-filter: blur(2px);
	opacity: 0;
	transition: opacity 0.24s;
}
.drawer-wrap.show .drawer-scrim {
	opacity: 1;
}
.drawer {
	position: absolute;
	top: 0;
	right: 0;
	height: 100%;
	width: 420px;
	max-width: 92vw;
	display: flex;
	flex-direction: column;
	background: var(--panel);
	border-left: 1px solid var(--border);
	box-shadow: -12px 0 32px rgba(23, 26, 31, 0.12);
	transform: translateX(100%);
	transition: transform 0.28s cubic-bezier(0.2, 0.7, 0.3, 1);
}
.drawer-wrap.show .drawer {
	transform: none;
}
.drawer-head {
	display: flex;
	align-items: flex-start;
	gap: 13px;
	padding: 22px 22px 18px;
	border-bottom: 1px solid var(--border);
}
.dr-logo {
	display: grid;
	place-items: center;
	width: 44px;
	height: 44px;
	border-radius: 11px;
	border: 1px solid var(--border);
	background: var(--panel-soft);
	color: var(--accent-2);
	flex: 0 0 auto;
}
.dr-logo :deep(svg) {
	width: 24px;
	height: 24px;
	stroke-width: 1.8;
}
.dr-meta {
	flex: 1;
	min-width: 0;
}
.drawer-head h3 {
	margin: 0;
	font-size: 17px;
	font-weight: 650;
	letter-spacing: -0.01em;
}
.dr-sub {
	margin-top: 3px;
	font-size: 12.5px;
	color: var(--muted);
	font-family: var(--font-mono);
}
.drawer-body {
	flex: 1;
	overflow-y: auto;
	padding: 20px 22px 30px;
}
.dr-section {
	margin-bottom: 22px;
}
.dr-section h4 {
	margin: 0 0 9px;
	font-size: 11px;
	font-weight: 600;
	letter-spacing: 0.07em;
	text-transform: uppercase;
	color: var(--muted-soft);
}
.dr-desc {
	font-size: 13px;
	color: var(--text);
	line-height: 1.6;
}
.kv {
	display: grid;
	grid-template-columns: 96px 1fr;
	gap: 8px 14px;
	font-size: 12.5px;
	margin: 0;
}
.kv dt {
	color: var(--muted);
}
.kv dd {
	margin: 0;
	color: var(--text);
	font-family: var(--font-mono);
	font-size: 12px;
	word-break: break-all;
}
.perm-list {
	display: flex;
	flex-wrap: wrap;
	gap: 6px;
}
.perm {
	padding: 3px 9px;
	border-radius: 6px;
	background: var(--panel-muted);
	color: var(--muted);
	font-size: 11.5px;
	font-family: var(--font-mono);
}
.drawer-foot {
	display: flex;
	align-items: center;
	gap: 10px;
	padding: 14px 22px;
	border-top: 1px solid var(--border);
	background: var(--panel-soft);
}
.drawer-foot .grow {
	flex: 1;
}

/* toast */
.toast {
	position: fixed;
	left: 50%;
	bottom: 28px;
	z-index: 200;
	display: flex;
	align-items: center;
	gap: 9px;
	padding: 10px 18px;
	transform: translateX(-50%) translateY(20px);
	border-radius: 10px;
	background: var(--pill);
	color: #fff;
	box-shadow: var(--shadow-md);
	font-size: 13px;
	font-weight: 500;
	opacity: 0;
	pointer-events: none;
	transition:
		opacity 0.2s,
		transform 0.2s;
}
.toast.show {
	transform: translateX(-50%) translateY(0);
	opacity: 1;
}
.toast svg {
	width: 16px;
	height: 16px;
	color: #6fe0a0;
	stroke-width: 2.2;
}

@media (prefers-reduced-motion: reduce) {
	.plugins-view *,
	.plugins-view *::before,
	.plugins-view *::after {
		transition-duration: 0.001ms !important;
		animation-duration: 0.001ms !important;
	}
}
</style>
