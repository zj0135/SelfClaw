<script setup>
import { computed, onMounted, reactive, ref } from 'vue';
import { RefreshCw, ChevronDown, Check, Zap, TriangleAlert } from 'lucide-vue-next';
import claudeIcon from '@lobehub/icons-static-png/light/claude-color.png';
import codexIcon from '@lobehub/icons-static-png/light/openai.png';
import opencodeIcon from '@lobehub/icons-static-png/light/opencode.png';
import { useHostBridge, isSuperseded } from '../../composables/hostBridge.js';

const { request, requestLatest } = useHostBridge();

const defaultModel = 'Default (CLI config)';
const isLoading = ref(false);
const isRescanning = ref(false);
const scanError = ref('');
const selectedCliId = ref('');

// iconBackground 刻意不跟主题：lobehub 品牌 PNG 是近黑描线或彩色 logo，浅色底板
// 在两个主题下都认得出来。换成 var(--panel) 会让深色下的 opencode 图标糊掉。
const cliRegistry = {
	claude: {
		id: 'claude',
		name: 'Claude Code',
		vendor: 'Anthropic official CLI',
		iconSrc: claudeIcon,
		iconBackground: '#ffffff',
		models: [defaultModel],
	},
	codex: {
		id: 'codex',
		name: 'Codex CLI',
		vendor: 'OpenAI official CLI',
		iconSrc: codexIcon,
		iconBackground: '#ffffff',
		models: [defaultModel],
		reasoningLevels: [defaultModel, 'Low', 'Medium', 'High'],
	},
	opencode: {
		id: 'opencode',
		name: 'OpenCode',
		vendor: 'Open-source agent CLI',
		iconSrc: opencodeIcon,
		iconBackground: '#ffffff',
		models: [defaultModel],
	},
};

const cliTools = reactive([]);
const hasCliTools = computed(() => cliTools.length > 0);
const scanStatusText = computed(() => {
	// The initial-load and rescan spinners live on the "重新扫描" button; showing the same status
	// as a bar above the CLI list would just duplicate the affordance, so we stay silent then.
	if (isLoading.value || isRescanning.value) {
		return '';
	}

	if (scanError.value) {
		return scanError.value;
	}

	return hasCliTools.value ? '' : '还没有检测到 Claude Code、Codex CLI 或 OpenCode。';
});

function createCliTool(rawTool, index) {
	const base = cliRegistry[rawTool?.id] || {};
	const models = normalizeList(rawTool?.models, base.models || [defaultModel]);
	const reasoningLevels = normalizeOptionalList(rawTool?.reasoningLevels, base.reasoningLevels || []);
	const version = typeof rawTool?.version === 'string' ? rawTool.version.trim() : '';

	return {
		...base,
		...rawTool,
		name: rawTool?.name || base.name || rawTool?.id || 'CLI',
		vendor: rawTool?.vendor || base.vendor || '',
		version,
		iconSrc: base.iconSrc,
		iconBackground: base.iconBackground || '#ffffff',
		iconFallback: getCliInitials(rawTool?.name || base.name || rawTool?.id || 'CLI'),
		models,
		selectedModel: models[0] || defaultModel,
		reasoningLevels,
		selectedReasoningLevel: reasoningLevels[0] || '',
		testMessage: version ? `连接正常 · CLI 版本 ${version}` : '连接正常 · 已检测到 CLI',
		isOpen: index === 0,
		testing: false,
		showToast: false,
	};
}

function normalizeList(value, fallback) {
	const values = Array.isArray(value) ? value : fallback;
	const normalized = values
		.filter((item) => typeof item === 'string' && item.trim().length > 0)
		.map((item) => item.trim());

	return normalized.length > 0 ? [...new Set(normalized)] : [defaultModel];
}

function normalizeOptionalList(value, fallback) {
	const values = Array.isArray(value) ? value : fallback;
	const normalized = values
		.filter((item) => typeof item === 'string' && item.trim().length > 0)
		.map((item) => item.trim());

	return [...new Set(normalized)];
}

function getCliInitials(value) {
	return String(value || 'CLI')
		.split(/\s+/)
		.filter(Boolean)
		.slice(0, 2)
		.map((part) => part.charAt(0).toUpperCase())
		.join('') || 'CLI';
}

function toggleOpen(cli) {
	const shouldOpen = !cli.isOpen;

	cliTools.forEach((item) => {
		item.isOpen = item.id === cli.id ? shouldOpen : false;
	});
}

function onModelChange(cli) {
	cli.showToast = false;
}

async function testCli(cli) {
	if (cli.testing) {
		return;
	}

	cli.testing = true;
	cli.showToast = false;
	cli.testError = '';

	try {
		// per-CLI latest-wins：同一 CLI 连点测试时只认最新一次回包。
		const payload = await requestLatest(`cli-test:${cli.id}`, 'test-programming-cli', { cliId: cli.id });
		applyTestResult(cli, payload);
	} catch (error) {
		if (isSuperseded(error)) return;
		applyTestResult(cli, { success: false, error: error?.message });
	}
}

function applyTestResult(cli, payload) {
	cli.testing = false;
	if (payload?.success) {
		const version = typeof payload.version === 'string' ? payload.version.trim() : '';
		cli.testMessage = version ? `连接正常 · ${version}` : '连接正常 · 已检测到 CLI';
		cli.testError = '';
	} else {
		const detail = typeof payload?.error === 'string' && payload.error.trim().length > 0
			? payload.error.trim()
			: '未能连接到该 CLI';
		cli.testMessage = `连接失败 · ${detail}`;
		cli.testError = detail;
	}
	cli.showToast = true;
}

async function requestProgrammingAssistantSettings({ refresh = false } = {}) {
	if (isLoading.value || isRescanning.value) {
		return;
	}

	isLoading.value = !refresh;
	isRescanning.value = refresh;
	scanError.value = '';

	try {
		// scan / get / select 共用 cli-scan 这个 key：三者都会重写整份 CLI 列表，
		// 连续触发时只有最新一次的回包生效。
		const payload = await requestLatest(
			'cli-scan',
			refresh ? 'scan-programming-clis' : 'get-programming-assistant-settings',
		);
		applySettingsResult(payload);
	} catch (error) {
		if (isSuperseded(error)) return;
		scanError.value = `本地 CLI 设置同步失败：${error?.message || error}`;
	} finally {
		isLoading.value = false;
		isRescanning.value = false;
	}
}

function rescanCliTools() {
	requestProgrammingAssistantSettings({ refresh: true });
}

async function selectCli(cli) {
	if (!cli?.id || selectedCliId.value === cli.id || isLoading.value || isRescanning.value) {
		return;
	}

	selectedCliId.value = cli.id;
	scanError.value = '';

	try {
		const payload = await requestLatest('cli-scan', 'select-programming-cli', { cliId: cli.id });
		applySettingsResult(payload);
	} catch (error) {
		if (isSuperseded(error)) return;
		scanError.value = `本地 CLI 设置同步失败：${error?.message || error}`;
	}
}

function applySettingsResult(payload) {
	const rawTools = Array.isArray(payload?.tools) ? payload.tools : [];
	const normalizedSelected = normalizeSelectedCliId(rawTools, payload?.selectedCliId);
	const nextTools = rawTools
		.map(createCliTool)
		.filter((tool) => Boolean(tool.id));

	nextTools.forEach((tool, index) => {
		tool.isOpen = normalizedSelected ? tool.id === normalizedSelected : index === 0;
	});

	cliTools.splice(0, cliTools.length, ...nextTools);
	selectedCliId.value = normalizedSelected || '';
	scanError.value = payload?.error ? `本地 CLI 设置同步失败：${payload.error}` : '';
}

function normalizeSelectedCliId(tools, value) {
	const normalized = typeof value === 'string' ? value.trim().toLowerCase() : '';
	if (normalized && tools.some((tool) => tool?.id === normalized)) {
		return normalized;
	}

	return tools[0]?.id || '';
}

onMounted(() => {
	requestProgrammingAssistantSettings({ refresh: false });
});
</script>

<template>
	<section class="programming-assistant sc-root sc-stage sc-page">
		<header class="sc-page-head sc-rise" style="--i: 0">
			<span class="sc-page-ghost" aria-hidden="true">Assistant</span>
			<div>
				<span class="sc-page-kicker">LOCAL CLI RUNTIMES</span>
				<h1 class="sc-page-title">编程助手</h1>
				<p class="sc-page-sub">检测本机安装的智能体 CLI，选择默认运行时与模型。</p>
			</div>
			<button class="pa-btn scan-btn" :class="{ 'is-rescanning': isRescanning }" type="button"
				:disabled="isRescanning" @click="rescanCliTools">
				<RefreshCw :size="14" :stroke-width="2" class="scan-icon" aria-hidden="true" />
				{{ isRescanning ? '扫描中…' : '重新扫描' }}
			</button>
		</header>

		<main class="sc-page-body">
			<div class="section-bar sc-rise" style="--i: 1">
				<h3>本地 CLI <span class="count">[{{ String(cliTools.length).padStart(2, '0') }}]</span></h3>
				<span class="section-line" aria-hidden="true"></span>
			</div>

			<div class="cli-list">
				<div v-if="scanStatusText" class="scan-state sc-rise" style="--i: 2"
					:class="{ 'scan-state--error': scanError }">
					<TriangleAlert v-if="scanError" :size="15" :stroke-width="2" aria-hidden="true" />
					{{ scanStatusText }}
				</div>

				<article v-for="(cli, ci) in cliTools" :key="cli.id" class="cli-card sc-rise" :style="{ '--i': ci + 2 }"
					:class="{ 'is-open': cli.isOpen }">
					<div class="cli-row" role="button" tabindex="0" :aria-expanded="cli.isOpen ? 'true' : 'false'"
						@click="toggleOpen(cli)" @keydown.enter.prevent="toggleOpen(cli)"
						@keydown.space.prevent="toggleOpen(cli)">
						<div class="cli-icon" :style="{ background: cli.iconBackground }">
							<img v-if="cli.iconSrc" class="cli-svg" :src="cli.iconSrc" alt="" aria-hidden="true" />
							<span v-else class="cli-initials" aria-hidden="true">{{ cli.iconFallback }}</span>
						</div>

						<div class="cli-body">
							<div class="cli-titleline">
								<span class="cli-name">{{ cli.name }}</span>
								<span v-if="cli.vendor" class="cli-vendor">{{ cli.vendor }}</span>
								<span v-if="selectedCliId === cli.id" class="badge badge--selected">
									<Check :size="11" :stroke-width="3" aria-hidden="true" />
									已选择
								</span>
							</div>

							<div class="cli-meta">
								<span v-if="cli.version" class="ver">{{ cli.version }}</span>
								<span class="label">MODEL</span>
								<span class="model-name">{{ cli.selectedModel }}</span>
							</div>
						</div>

						<button class="cli-expand" type="button" :aria-label="cli.isOpen ? '收起' : '展开'"
							@click.stop="toggleOpen(cli)">
							<ChevronDown :size="17" :stroke-width="2" aria-hidden="true" />
						</button>
					</div>

					<div class="cli-config">
						<div class="cli-config__inner">
							<div class="cli-config__label">
								模型
								<span class="badge badge--live">LIVE · 来自 CLI 的实时列表</span>
							</div>

							<div class="cli-config__controls">
								<div class="select-wrap">
									<select v-model="cli.selectedModel" :aria-label="`${cli.name} 模型选择`"
										@change="onModelChange(cli)">
										<option v-for="model in cli.models" :key="model" :value="model">{{ model }}
										</option>
									</select>
									<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
								</div>

								<div v-if="cli.reasoningLevels.length" class="select-field">
									<label class="select-field__label" :for="`reasoning-${cli.id}`">推理等级</label>
									<div class="select-wrap select-wrap--reasoning">
										<select :id="`reasoning-${cli.id}`" v-model="cli.selectedReasoningLevel"
											:aria-label="`${cli.name} 推理等级选择`" @change="onModelChange(cli)">
											<option v-for="level in cli.reasoningLevels" :key="level" :value="level">{{
												level }}
											</option>
										</select>
										<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
									</div>
								</div>

								<button class="pa-btn pa-btn--ghost cli-test" type="button" :disabled="cli.testing"
									@click="testCli(cli)">
									<Zap :size="13" :stroke-width="2" aria-hidden="true" />
									{{ cli.testing ? '测试中…' : '测试' }}
								</button>

								<button v-if="selectedCliId !== cli.id" class="pa-btn pa-btn--acid cli-select"
									type="button" :disabled="isLoading || isRescanning" @click="selectCli(cli)">
									设为默认
								</button>
							</div>

							<p class="cli-config__hint">
								模型列表来自这个 CLI；选 <span class="em">“默认”</span> 会沿用 CLI 自己的设置。
							</p>

							<div class="test-toast" :class="{ show: cli.showToast, err: cli.testError }">
								<span class="tt-led" aria-hidden="true"></span>
								{{ cli.testMessage }}
							</div>
						</div>
					</div>
				</article>
			</div>
		</main>
	</section>
</template>

<style scoped>
@import '../../styles/settings-console.css';

.programming-assistant *,
.programming-assistant *::before,
.programming-assistant *::after {
	box-sizing: border-box;
}

.programming-assistant .sc-page-body {
	font-size: var(--fs-15);
	line-height: 1.55;
	-webkit-font-smoothing: antialiased;
	-moz-osx-font-smoothing: grayscale;
}

.section-bar h3 {
	display: flex;
	align-items: baseline;
	gap: 8px;
	margin: 0;
	color: var(--sc-soft);
	font-size: var(--fs-13);
	font-weight: 600;
	letter-spacing: 0.04em;
	white-space: nowrap;
}

.count {
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-size: var(--fs-12);
	font-weight: 500;
	letter-spacing: 0.08em;
}

.section-line {
	flex: 1;
	height: 1px;
	background: linear-gradient(90deg, var(--sc-line-2), transparent);
}

/* ── buttons ────────────────────────────────────────────────── */
.pa-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 7px;
	min-height: 38px;
	padding: 8px 16px;
	border: 1px solid var(--sc-line-2);
	border-radius: 9px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-13);
	font-weight: 560;
	line-height: 1.2;
	cursor: pointer;
	transition:
		background 0.16s,
		border-color 0.16s,
		color 0.16s,
		transform 0.12s var(--sc-ease-spring),
		opacity 0.16s;
}

.pa-btn:hover {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
	transform: translateY(-1px);
}

.pa-btn:active {
	transform: translateY(0);
}

.pa-btn:disabled {
	cursor: default;
	opacity: 0.55;
	transform: none;
}

.pa-btn--ghost {
	border-color: var(--sc-line);
	background: transparent;
	color: var(--sc-acid);
}

.pa-btn--ghost:hover {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	background: var(--sc-acid-soft);
}

.pa-btn--acid {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
	font-weight: 640;
}

.pa-btn--acid:hover {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	box-shadow: 0 8px 22px color-mix(in srgb, var(--accent) 18%, transparent);
}

.is-rescanning .scan-icon {
	animation: sc-spin 0.8s linear infinite;
}

/* ── cli cards ──────────────────────────────────────────────── */
.cli-list {
	display: flex;
	flex-direction: column;
	gap: 14px;
}

.scan-state {
	display: flex;
	align-items: center;
	gap: 9px;
	min-height: 64px;
	padding: 16px 18px;
	border: 1px dashed var(--sc-line-2);
	border-radius: 13px;
	background: var(--sc-raise);
	color: var(--sc-mute);
	font-size: var(--fs-135);
}

.scan-state--error {
	border-color: color-mix(in srgb, var(--sc-err) 40%, transparent);
	color: var(--sc-err);
}

.cli-card {
	position: relative;
	overflow: hidden;
	border: 1px solid var(--sc-line);
	border-radius: 15px;
	background: var(--sc-panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.05);
	transition:
		border-color 0.18s,
		transform 0.18s var(--sc-ease-out),
		box-shadow 0.18s;
}

/* crosshair corner ticks */
.cli-card::before,
.cli-card::after {
	position: absolute;
	z-index: 2;
	width: 9px;
	height: 9px;
	border: 0 solid var(--sc-faint);
	content: '';
	transition: border-color 0.18s;
	pointer-events: none;
}

.cli-card::before {
	top: 6px;
	left: 6px;
	border-top-width: 1px;
	border-left-width: 1px;
}

.cli-card::after {
	right: 6px;
	bottom: 6px;
	border-right-width: 1px;
	border-bottom-width: 1px;
}

.cli-card:hover {
	border-color: var(--sc-line-2);
	transform: translateY(-2px);
	box-shadow: 0 16px 40px rgba(var(--shadow-ink), 0.08);
}

.cli-card.is-open {
	border-color: var(--sc-line-2);
}

.cli-card.is-open::before,
.cli-card.is-open::after {
	border-color: var(--sc-acid);
}

.cli-row {
	display: flex;
	align-items: flex-start;
	gap: 16px;
	padding: 20px 22px;
	cursor: pointer;
	user-select: none;
}

.cli-row:focus-visible {
	border-radius: 15px;
	outline: 2px solid var(--sc-acid);
	outline-offset: -2px;
}

.cli-icon {
	display: grid;
	width: 48px;
	height: 48px;
	flex-shrink: 0;
	place-items: center;
	overflow: hidden;
	border: 1px solid var(--sc-line);
	border-radius: 12px;
	font-weight: 700;
}

.cli-svg {
	display: block;
	width: 26px;
	height: 26px;
	object-fit: contain;
}

.cli-initials {
	color: var(--muted);
	font-family: var(--sc-mono);
	font-size: var(--fs-13);
	font-weight: 700;
}

.cli-body {
	flex: 1;
	min-width: 0;
}

.cli-titleline {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 8px 12px;
}

.cli-name {
	font-family: var(--sc-display);
	font-size: var(--fs-17);
	font-weight: 630;
	letter-spacing: 0.01em;
}

.cli-vendor {
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-11);
	font-weight: 500;
	letter-spacing: 0.03em;
}

.cli-meta {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 10px;
	margin-top: 6px;
	font-size: var(--fs-12);
}

.label {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-95);
	font-weight: 600;
	letter-spacing: 0.2em;
}

.ver,
.model-name {
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: var(--fs-12);
}

.ver {
	padding: 2px 7px;
	border: 1px solid var(--sc-line);
	border-radius: 5px;
	background: var(--sc-raise);
	font-size: var(--fs-105);
}

.cli-expand {
	display: grid;
	width: 30px;
	height: 30px;
	flex-shrink: 0;
	place-items: center;
	margin-top: 8px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--sc-mute);
	cursor: pointer;
	transition:
		background 0.15s,
		color 0.15s;
}

.cli-expand:hover {
	background: var(--sc-hover);
	color: var(--sc-text);
}

.cli-expand svg {
	transition: transform 0.25s var(--sc-ease-out);
}

.cli-card.is-open .cli-expand svg {
	transform: rotate(180deg);
}

/* ── badges ─────────────────────────────────────────────────── */
.badge {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 2px 9px;
	border-radius: 999px;
	font-size: var(--fs-11);
	font-weight: 600;
	line-height: 1.7;
	white-space: nowrap;
}

.badge--selected {
	border: 1px solid color-mix(in srgb, var(--sc-acid) 45%, transparent);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
}

.badge--live {
	border: 1px solid color-mix(in srgb, var(--sc-ok) 30%, transparent);
	background: var(--sc-ok-soft);
	color: var(--sc-ok);
	font-family: var(--sc-mono);
	font-size: var(--fs-95);
	font-weight: 600;
	letter-spacing: 0.1em;
}

/* ── config area ────────────────────────────────────────────── */
.cli-config {
	display: none;
	padding: 0 22px 20px 86px;
}

.cli-card.is-open .cli-config {
	display: block;
	animation: sc-rise 0.35s var(--sc-ease-out) both;
}

.cli-config__inner {
	padding-top: 18px;
	border-top: 1px dashed var(--sc-line-2);
}

.cli-config__label {
	display: flex;
	align-items: center;
	gap: 10px;
	margin-bottom: 11px;
	color: var(--sc-soft);
	font-size: var(--fs-13);
	font-weight: 560;
}

.cli-config__controls {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 12px;
}

.select-wrap {
	position: relative;
	flex: 1;
	min-width: 240px;
	max-width: 420px;
}

.select-field {
	display: flex;
	align-items: center;
	gap: 9px;
}

.select-field__label {
	color: var(--sc-soft);
	font-size: var(--fs-125);
	font-weight: 560;
	white-space: nowrap;
}

.select-wrap--reasoning {
	flex: 0 0 150px;
	min-width: 150px;
	max-width: 170px;
}

.select-wrap select {
	width: 100%;
	padding: 11px 38px 11px 13px;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	appearance: none;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-135);
	cursor: pointer;
	transition:
		border-color 0.15s,
		box-shadow 0.15s;
}

.select-wrap select:hover {
	border-color: var(--sc-line-2);
}

.select-wrap select:focus {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	outline: none;
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.select-wrap option {
	background: var(--sc-panel);
	color: var(--sc-text);
}

.chev {
	position: absolute;
	top: 50%;
	right: 12px;
	color: var(--sc-mute);
	pointer-events: none;
	transform: translateY(-50%);
}

.cli-config__hint {
	margin: 10px 0 0;
	color: var(--sc-mute);
	font-size: var(--fs-125);
}

.em {
	color: var(--sc-soft);
}

.test-toast {
	display: none;
	align-items: center;
	gap: 9px;
	margin-top: 14px;
	padding: 9px 13px;
	border: 1px solid color-mix(in srgb, var(--sc-ok) 30%, transparent);
	border-radius: 9px;
	background: var(--sc-ok-soft);
	color: var(--sc-ok);
	font-family: var(--sc-mono);
	font-size: var(--fs-12);
	letter-spacing: 0.02em;
}

.test-toast.show {
	display: flex;
	animation: sc-rise 0.3s var(--sc-ease-out) both;
}

.test-toast.err {
	border-color: color-mix(in srgb, var(--sc-err) 35%, transparent);
	background: var(--sc-err-soft);
	color: var(--sc-err);
}

.tt-led {
	width: 7px;
	height: 7px;
	flex: 0 0 auto;
	border-radius: 50%;
	background: currentColor;
	box-shadow: 0 0 9px currentColor;
	animation: sc-blink 1.8s ease-in-out infinite;
}

@media (max-width: 880px) {
	.cli-config {
		padding-left: 22px;
	}
}
</style>
