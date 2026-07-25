<script setup>
import { computed, reactive, ref } from 'vue';
import {
	Puzzle,
	Network,
	ShieldCheck,
	Plus,
	Search,
	X,
	Eye,
	Check,
	UploadCloud,
} from 'lucide-vue-next';

// 图标放在 reactive 之外，避免组件被包成响应式代理。
const catIcons = {
	plugin: Puzzle,
	mcp: Network,
	skill: ShieldCheck,
};

// ── 三类扩展数据 ────────────────────────────────────────────────
// 实际接入时用 sendHostMessage('get-extensions') 拉取，这里给出结构与示例。
const categories = reactive({
	plugin: {
		name: '插件',
		en: 'PLUGINS',
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
		en: 'MCP SERVERS',
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
		en: 'SKILLS',
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
	if (searchTerm.value.trim()) return `MATCH ${filteredItems.value.length}/${total}`;
	return `TOTAL ${total} · ON ${enabledCount(activeCat.value)}`;
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
</script>

<template>
	<main class="plugins-view sc-root sc-stage scroll">
		<div class="panel">
			<header class="pg-hero sc-rise" style="--i: 0">
				<div class="pg-kicker">EXTENSION REGISTRY</div>
				<h1 class="pg-title">插件</h1>
				<p class="pg-sub">能力包、MCP 服务器与技能工作流的挂载舱。</p>
			</header>

			<!-- tab 切换 + 右侧功能按钮 -->
			<div class="tab-bar sc-rise" style="--i: 1">
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
						<component :is="catIcons[cat]" :size="15" :stroke-width="1.9" class="tab-ico" aria-hidden="true" />
						{{ categories[cat].name }}
						<span class="tab-n">{{ String(categories[cat].items.length).padStart(2, '0') }}</span>
					</button>
				</div>

				<button type="button" class="add-btn" @click="onAdd">
					<Plus :size="15" :stroke-width="2.2" />
					{{ addLabel }}
				</button>
			</div>

			<!-- 查询框 + 计数 -->
			<div class="tool-row sc-rise" style="--i: 2">
				<div class="search">
					<Search :size="14" :stroke-width="2" class="search-ico" aria-hidden="true" />
					<input v-model="searchTerm" type="text" placeholder="搜索名称、id 或描述…" aria-label="搜索当前类型" />
					<button v-show="searchTerm" type="button" class="search-clear" aria-label="清除搜索" @click="clearSearch">
						<X :size="14" :stroke-width="2" />
					</button>
				</div>
				<div class="tool-count">{{ countText }}</div>
			</div>

			<!-- 当前 tab 的项，按列表铺开 -->
			<div class="list sc-rise" style="--i: 3" role="tabpanel">
				<template v-if="filteredItems.length">
					<div v-for="(it, ii) in filteredItems" :key="it.id" class="item" :class="{ off: !it.on }">
						<span class="item-index">{{ String(ii + 1).padStart(2, '0') }}</span>
						<span class="item-logo" aria-hidden="true">
							<component :is="catIcons[activeCat]" :size="16" :stroke-width="1.8" />
						</span>
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
								<Eye :size="15" :stroke-width="1.9" />
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
					<div class="dh-ico" aria-hidden="true"><Network :size="17" :stroke-width="1.9" /></div>
					<div>
						<div class="dlg-kicker">MCP SERVER</div>
						<h3 id="mcp-title">新增 MCP 服务器</h3>
						<p>配置一个 Model Context Protocol 连接</p>
					</div>
					<button class="icon-act dialog-close" aria-label="关闭" @click="mcpDialog.open = false">
						<X :size="15" :stroke-width="2" />
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
					<div class="dh-ico" aria-hidden="true">
						<ShieldCheck v-if="importIsSkill" :size="17" :stroke-width="1.9" />
						<Puzzle v-else :size="17" :stroke-width="1.9" />
					</div>
					<div>
						<div class="dlg-kicker">{{ importIsSkill ? 'SKILL IMPORT' : 'PLUGIN IMPORT' }}</div>
						<h3 id="import-title">{{ importIsSkill ? '导入技能' : '导入插件' }}</h3>
						<p>{{ importIsSkill ? '从本地文件安装技能' : '从本地文件安装插件' }}</p>
					</div>
					<button class="icon-act dialog-close" aria-label="关闭" @click="importDialog.open = false">
						<X :size="15" :stroke-width="2" />
					</button>
				</div>
				<div class="dialog-body">
					<div class="field">
						<label>选择文件<span class="req">*</span></label>
						<label class="dropzone" :class="{ 'has-file': importDialog.fileName }" @dragover.prevent @drop.prevent="onFileDrop">
							<input type="file" hidden @change="onFilePick" />
							<UploadCloud :size="26" :stroke-width="1.6" class="dz-ico" aria-hidden="true" />
							<span class="dz-title">点击选择，或拖拽文件到此处</span>
							<span class="dz-sub">{{ importIsSkill ? '支持 .zip / .odskill 包或 SKILL.md' : '支持 .zip / .odplugin 包' }}</span>
							<span class="dz-file">
								<Check :size="14" :stroke-width="2.4" />
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
						<div class="dr-logo" aria-hidden="true">
							<component :is="catIcons[drawer.cat]" :size="22" :stroke-width="1.8" />
						</div>
						<div class="dr-meta">
							<div class="dlg-kicker">{{ categories[drawer.cat].en }}</div>
							<h3 id="drawer-name">{{ drawer.item.name }}</h3>
							<div class="dr-sub">
								{{ categories[drawer.cat].name }} · {{ drawer.cat === 'mcp' ? drawer.item.version : 'v' + drawer.item.version }}
							</div>
						</div>
						<button class="icon-act" aria-label="关闭" @click="closeDrawer">
							<X :size="16" :stroke-width="2" />
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
						<button class="btn primary" @click="toggleItem(drawer.item)">{{ drawer.item.on ? '停用' : '启用' }}</button>
					</div>
				</template>
			</aside>
		</div>

		<!-- toast -->
		<div class="toast" :class="{ show: toastState.visible }" role="status" aria-live="polite">
			<Check :size="15" :stroke-width="2.4" class="toast-ico" />
			<span>{{ toastState.text }}</span>
		</div>
	</main>
</template>

<style scoped>
@import './settings-console.css';

.plugins-view {
	height: 100%;
	overflow-y: auto;
	color: var(--sc-text);
	font-family: var(--sc-sans);
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
	scrollbar-color: var(--sc-faint) transparent;
}
.scroll::-webkit-scrollbar {
	width: 9px;
	height: 9px;
}
.scroll::-webkit-scrollbar-thumb {
	background: var(--sc-raise);
	background-clip: padding-box;
	border: 2px solid transparent;
	border-radius: 99px;
}
.scroll::-webkit-scrollbar-thumb:hover {
	background: var(--sc-faint);
}

.panel {
	max-width: 920px;
	padding: 48px 40px 72px;
}

/* ── hero ───────────────────────────────────────────────────── */
.pg-hero {
	margin-bottom: 30px;
	padding-bottom: 24px;
	border-bottom: 1px solid var(--sc-line);
}

.pg-kicker,
.dlg-kicker {
	margin-bottom: 10px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 10px;
	font-weight: 600;
	letter-spacing: 0.24em;
}

.pg-title {
	margin: 0;
	font-family: var(--sc-display);
	font-size: 44px;
	font-weight: 660;
	letter-spacing: 0.01em;
	line-height: 1.05;
}

.pg-sub {
	margin: 10px 0 0;
	color: var(--sc-mute);
	font-size: 13px;
}

/* ── tab 切换栏 ─────────────────────────────────────────────── */
.tab-bar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 16px;
	margin-bottom: 16px;
}

.tab-strip {
	display: inline-flex;
	align-items: center;
	gap: 3px;
	padding: 4px;
	border: 1px solid var(--sc-line);
	border-radius: 11px;
	background: var(--sc-panel);
}

.tab-btn {
	display: inline-flex;
	align-items: center;
	gap: 8px;
	padding: 8px 15px;
	border: 1px solid transparent;
	border-radius: 8px;
	background: transparent;
	color: var(--sc-mute);
	font-size: 13px;
	font-weight: 540;
	transition:
		background 0.16s,
		border-color 0.16s,
		color 0.16s;
}

.tab-ico {
	flex: none;
}

.tab-n {
	display: inline-grid;
	place-items: center;
	height: 17px;
	padding: 0 6px;
	border-radius: 99px;
	background: var(--sc-raise);
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: 10px;
	font-weight: 600;
	letter-spacing: 0.04em;
}

.tab-btn:hover {
	color: var(--sc-text);
}

.tab-btn.active {
	border-color: var(--sc-line-2);
	background: var(--sc-raise);
	color: var(--sc-text);
	box-shadow: 0 4px 16px rgba(23, 26, 31, 0.06);
}

.tab-btn.active .tab-ico {
	color: var(--sc-acid);
}

.tab-btn.active .tab-n {
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
}

.tab-btn:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 2px;
}

.add-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 7px;
	flex: 0 0 auto;
	min-height: 38px;
	padding: 8px 16px;
	border: 1px solid var(--sc-acid);
	border-radius: 9px;
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
	font-size: 13px;
	font-weight: 640;
	line-height: 1.2;
	white-space: nowrap;
	transition:
		transform 0.12s var(--sc-ease-spring),
		box-shadow 0.16s;
}

.add-btn:hover {
	transform: translateY(-1px);
	box-shadow: 0 10px 26px rgba(59, 91, 253, 0.2);
}

.add-btn:active {
	transform: translateY(0);
}

/* ── 查询框 + 计数行 ────────────────────────────────────────── */
.tool-row {
	display: flex;
	align-items: center;
	gap: 14px;
	margin-bottom: 16px;
}

.search {
	position: relative;
	display: flex;
	align-items: center;
	flex: 1;
	min-width: 0;
}

.search-ico {
	position: absolute;
	left: 12px;
	color: var(--sc-faint);
	pointer-events: none;
}

.search input {
	width: 100%;
	padding: 10px 36px 10px 35px;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: 13px;
	transition:
		border-color 0.16s,
		box-shadow 0.16s,
		background 0.16s;
}

.search input::placeholder {
	color: var(--sc-faint);
}

.search input:focus {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	outline: none;
	background: var(--sc-panel);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.search-clear {
	position: absolute;
	right: 7px;
	display: grid;
	place-items: center;
	width: 24px;
	height: 24px;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: var(--sc-mute);
	transition:
		background 0.14s,
		color 0.14s;
}

.search-clear:hover {
	background: var(--sc-hover);
	color: var(--sc-text);
}

.tool-count {
	flex: 0 0 auto;
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: 10.5px;
	font-weight: 500;
	letter-spacing: 0.14em;
	white-space: nowrap;
}

/* ── 列表 ───────────────────────────────────────────────────── */
.list {
	overflow: hidden;
	border: 1px solid var(--sc-line);
	border-radius: 14px;
	background: var(--sc-panel);
}

.item {
	display: flex;
	align-items: center;
	gap: 13px;
	padding: 14px 18px;
	border-bottom: 1px solid var(--sc-line);
	transition:
		background 0.15s,
		transform 0.15s var(--sc-ease-out);
}

.item:hover {
	background: var(--sc-hover);
}

.item:last-child {
	border-bottom: 0;
}

.item.off .item-logo,
.item.off .item-main,
.item.off .item-index {
	opacity: 0.45;
}

.item-index {
	width: 20px;
	flex: 0 0 auto;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 10px;
	letter-spacing: 0.06em;
}

.item-logo {
	display: grid;
	place-items: center;
	width: 36px;
	height: 36px;
	flex: 0 0 auto;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	background: var(--sc-raise);
	color: var(--sc-acid);
}

.item-main {
	flex: 1;
	min-width: 0;
}

.item-title {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 9px;
}

.item-name {
	font-size: 14px;
	font-weight: 600;
}

.item-tag {
	padding: 2px 7px;
	border: 1px solid var(--sc-line);
	border-radius: 5px;
	background: var(--sc-raise);
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: 10px;
	font-weight: 500;
	letter-spacing: 0.03em;
}

.item-tag.type {
	border-color: color-mix(in srgb, var(--sc-acid) 35%, transparent);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
}

.item-desc {
	margin-top: 4px;
	overflow: hidden;
	max-width: 52ch;
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: 11.5px;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.item-actions {
	display: flex;
	align-items: center;
	gap: 11px;
	flex: 0 0 auto;
}

.status-pill {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	padding: 3px 10px;
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
	border: 1px solid color-mix(in srgb, var(--sc-ok) 30%, transparent);
	background: var(--sc-ok-soft);
	color: var(--sc-ok);
}

.status-pill.on .d {
	background: var(--sc-ok);
	box-shadow: 0 0 7px rgba(15, 157, 99, 0.4);
}

.status-pill.off {
	border: 1px solid var(--sc-line);
	background: var(--sc-raise);
	color: var(--sc-mute);
}

.status-pill.off .d {
	background: var(--sc-faint);
}

.icon-act {
	display: grid;
	place-items: center;
	width: 30px;
	height: 30px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--sc-mute);
	transition:
		background 0.14s,
		color 0.14s,
		transform 0.14s var(--sc-ease-spring);
}

.icon-act:hover {
	background: var(--sc-hover);
	color: var(--sc-text);
	transform: translateY(-1px);
}

/* ── switch ─────────────────────────────────────────────────── */
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
	border: 1px solid var(--sc-line-2);
	border-radius: 99px;
	background: var(--sc-raise);
	transition: background 0.2s, border-color 0.2s;
}

.knob {
	position: absolute;
	top: 3px;
	left: 3px;
	width: 18px;
	height: 18px;
	border-radius: 50%;
	background: #fff;
	box-shadow: 0 1px 3px rgba(23, 26, 31, 0.22);
	transition:
		transform 0.22s var(--sc-ease-spring),
		background 0.2s;
}

.switch input:checked + .track {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
}

.switch input:checked + .track + .knob {
	transform: translateX(18px);
	background: #fff;
}

.empty {
	padding: 40px;
	color: var(--sc-mute);
	font-size: 13px;
	text-align: center;
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
	background: rgba(23, 26, 31, 0.28);
	backdrop-filter: blur(4px);
	animation: sc-fade 0.16s ease-out;
}

.dialog {
	display: flex;
	flex-direction: column;
	width: 100%;
	max-width: 480px;
	max-height: calc(100vh - 48px);
	overflow: hidden;
	border: 1px solid var(--sc-line-2);
	border-radius: 16px;
	background: var(--sc-panel);
	box-shadow: 0 32px 90px rgba(23, 26, 31, 0.2);
	animation: sc-pop 0.24s var(--sc-ease-out);
}

.dialog-head {
	display: flex;
	align-items: center;
	gap: 13px;
	padding: 20px 22px 16px;
	border-bottom: 1px solid var(--sc-line);
}

.dialog-head .dlg-kicker {
	margin-bottom: 4px;
}

.dh-ico {
	display: grid;
	place-items: center;
	width: 38px;
	height: 38px;
	flex: 0 0 auto;
	border: 1px solid color-mix(in srgb, var(--sc-acid) 35%, transparent);
	border-radius: 10px;
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
}

.dialog-head h3 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: 16.5px;
	font-weight: 640;
}

.dialog-head p {
	margin: 3px 0 0;
	color: var(--sc-mute);
	font-size: 12px;
}

.dialog-close {
	margin-left: auto;
}

.dialog-body {
	padding: 20px 22px;
	overflow-y: auto;
}

.field {
	margin-bottom: 17px;
}

.field:last-child {
	margin-bottom: 0;
}

.field label {
	display: block;
	margin-bottom: 7px;
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: 10.5px;
	font-weight: 600;
	letter-spacing: 0.16em;
	text-transform: uppercase;
}

.req {
	color: var(--sc-err);
	margin-left: 2px;
}

.hint {
	margin: 7px 0 0;
	color: var(--sc-mute);
	font-size: 11.5px;
}

.input,
.textarea {
	width: 100%;
	padding: 10px 12px;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: 13px;
	transition:
		border-color 0.16s,
		box-shadow 0.16s;
}

.input::placeholder,
.textarea::placeholder {
	color: var(--sc-faint);
}

.input.mono,
.textarea.mono {
	font-family: var(--sc-mono);
	font-size: 12px;
	letter-spacing: 0.02em;
}

.textarea {
	min-height: 76px;
	resize: vertical;
}

.input:focus,
.textarea:focus {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	outline: none;
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.seg {
	display: flex;
	gap: 7px;
}

.seg button {
	flex: 1;
	padding: 9px;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	font-size: 12.5px;
	font-weight: 560;
	transition: all 0.15s;
}

.seg button:hover {
	color: var(--sc-text);
	border-color: var(--sc-line-2);
}

.seg button.active {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
}

.dropzone {
	display: flex;
	flex-direction: column;
	align-items: center;
	gap: 9px;
	padding: 30px 18px;
	border: 1.5px dashed var(--sc-line-2);
	border-radius: 12px;
	background: var(--sc-raise);
	text-align: center;
	cursor: pointer;
	transition:
		border-color 0.16s,
		background 0.16s;
}

.dropzone:hover {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	background: var(--sc-acid-soft);
}

.dz-ico {
	color: var(--sc-acid);
}

.dz-title {
	color: var(--sc-text);
	font-size: 13px;
	font-weight: 560;
}

.dz-sub {
	color: var(--sc-mute);
	font-size: 11.5px;
}

.dz-file {
	display: none;
	align-items: center;
	gap: 8px;
	margin-top: 4px;
	padding: 7px 13px;
	border: 1px solid color-mix(in srgb, var(--sc-ok) 35%, transparent);
	border-radius: 8px;
	background: var(--sc-ok-soft);
	color: var(--sc-ok);
	font-family: var(--sc-mono);
	font-size: 12px;
}

.dropzone.has-file .dz-file {
	display: inline-flex;
}

.dropzone.has-file .dz-title,
.dropzone.has-file .dz-sub,
.dropzone.has-file .dz-ico {
	display: none;
}

.dialog-foot {
	display: flex;
	justify-content: flex-end;
	gap: 9px;
	padding: 15px 22px;
	border-top: 1px solid var(--sc-line);
	background: var(--sc-raise);
}

.btn {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	height: 38px;
	padding: 0 18px;
	border: 1px solid var(--sc-line-2);
	border-radius: 9px;
	background: var(--sc-panel);
	color: var(--sc-soft);
	font-size: 13px;
	font-weight: 560;
	transition:
		background 0.15s,
		border-color 0.15s,
		color 0.15s,
		opacity 0.15s,
		transform 0.12s var(--sc-ease-spring);
}

.btn:hover {
	background: var(--sc-hover);
	color: var(--sc-text);
}

.btn.primary {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
	font-weight: 640;
}

.btn.primary:hover:not(:disabled) {
	color: var(--sc-acid-ink);
	transform: translateY(-1px);
	box-shadow: 0 8px 22px rgba(59, 91, 253, 0.18);
}

.btn:disabled {
	opacity: 0.45;
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
	background: rgba(23, 26, 31, 0.28);
	backdrop-filter: blur(4px);
	opacity: 0;
	transition: opacity 0.26s;
}

.drawer-wrap.show .drawer-scrim {
	opacity: 1;
}

.drawer {
	position: absolute;
	top: 0;
	right: 0;
	display: flex;
	flex-direction: column;
	width: 440px;
	max-width: 92vw;
	height: 100%;
	border-left: 1px solid var(--sc-line-2);
	background: var(--sc-panel);
	box-shadow: -24px 0 60px rgba(23, 26, 31, 0.12);
	transform: translateX(100%);
	transition: transform 0.32s var(--sc-ease-out);
}

.drawer-wrap.show .drawer {
	transform: none;
}

.drawer-head {
	display: flex;
	align-items: flex-start;
	gap: 14px;
	padding: 24px 24px 20px;
	border-bottom: 1px solid var(--sc-line);
}

.dr-logo {
	display: grid;
	place-items: center;
	width: 48px;
	height: 48px;
	flex: 0 0 auto;
	border: 1px solid color-mix(in srgb, var(--sc-acid) 35%, transparent);
	border-radius: 12px;
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
}

.dr-meta {
	flex: 1;
	min-width: 0;
}

.dr-meta .dlg-kicker {
	margin-bottom: 5px;
}

.drawer-head h3 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: 19px;
	font-weight: 650;
	letter-spacing: 0.01em;
}

.dr-sub {
	margin-top: 4px;
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: 11px;
	letter-spacing: 0.03em;
}

.drawer-body {
	flex: 1;
	overflow-y: auto;
	padding: 22px 24px 32px;
}

.dr-section {
	margin-bottom: 24px;
}

.dr-section h4 {
	margin: 0 0 10px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 9.5px;
	font-weight: 600;
	letter-spacing: 0.22em;
	text-transform: uppercase;
}

.dr-desc {
	color: var(--sc-text);
	font-size: 13px;
	line-height: 1.65;
}

.kv {
	display: grid;
	grid-template-columns: 88px 1fr;
	gap: 9px 14px;
	margin: 0;
	font-size: 12.5px;
}

.kv dt {
	color: var(--sc-mute);
}

.kv dd {
	margin: 0;
	color: var(--sc-text);
	font-family: var(--sc-mono);
	font-size: 11.5px;
	word-break: break-all;
}

.perm-list {
	display: flex;
	flex-wrap: wrap;
	gap: 7px;
}

.perm {
	padding: 4px 10px;
	border: 1px solid var(--sc-line);
	border-radius: 7px;
	background: var(--sc-raise);
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: 11px;
}

.drawer-foot {
	display: flex;
	align-items: center;
	gap: 10px;
	padding: 15px 24px;
	border-top: 1px solid var(--sc-line);
	background: var(--sc-raise);
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
	padding: 11px 18px;
	transform: translateX(-50%) translateY(24px);
	border: 1px solid var(--sc-line-2);
	border-radius: 10px;
	background: var(--sc-panel);
	color: var(--sc-text);
	box-shadow: 0 18px 48px rgba(23, 26, 31, 0.16);
	font-size: 13px;
	font-weight: 500;
	opacity: 0;
	pointer-events: none;
	transition:
		opacity 0.22s,
		transform 0.28s var(--sc-ease-spring);
}

.toast-ico {
	color: var(--sc-ok);
}

.toast.show {
	transform: translateX(-50%) translateY(0);
	opacity: 1;
}

@media (max-width: 760px) {
	.panel {
		padding: 32px 20px 56px;
	}

	.tab-bar {
		align-items: stretch;
		flex-direction: column;
	}

	.item {
		align-items: flex-start;
		flex-direction: column;
	}

	.item-actions {
		align-self: flex-end;
	}
}
</style>
