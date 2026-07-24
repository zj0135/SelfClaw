<script setup>
import { computed, defineExpose, onMounted, onUnmounted, reactive, ref } from 'vue';
import { RefreshCw, ChevronDown, Check, Zap, TerminalSquare, TriangleAlert } from 'lucide-vue-next';
import claudeIcon from '../../../assets/agents-icons/claude.svg';
import codexIcon from '../../../assets/agents-icons/codex.svg';
import opencodeIcon from '../../../assets/agents-icons/opencode.svg';

const defaultModel = 'Default (CLI config)';
const isLoading = ref(false);
const isRescanning = ref(false);
const scanError = ref('');
const selectedCliId = ref('');
// Tracks the newest test request per CLI so late/stale replies from the host bridge are ignored.
const activeTestRequests = new Map();
let scanRequestId = 0;
let activeScanRequestId = null;
let fallbackTimer = null;

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

function postToHost(message) {
	window.chrome?.webview?.postMessage(message);
}

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

function testCli(cli) {
	if (cli.testing) {
		return;
	}

	const requestId = `cli-test-${Date.now()}-${++scanRequestId}`;
	cli.testing = true;
	cli.showToast = false;
	cli.testError = '';
	activeTestRequests.set(requestId, cli.id);

	postToHost({
		type: 'test-programming-cli',
		requestId,
		cliId: cli.id,
	});

	// No WebView2 host in dev — fake a plausible success so the UI can still be exercised in a browser.
	if (!window.chrome?.webview) {
		window.setTimeout(() => {
			handleTestResult({
				type: 'programming-cli-test-result',
				requestId,
				cliId: cli.id,
				success: true,
				version: cli.version || '',
				error: null,
			});
		}, 400);
	}
}

function handleTestResult(payload) {
	const requestId = typeof payload?.requestId === 'string' ? payload.requestId : '';
	const cliId = activeTestRequests.get(requestId) || payload?.cliId;
	activeTestRequests.delete(requestId);
	if (!cliId) {
		return;
	}

	const cli = cliTools.find((tool) => tool.id === cliId);
	if (!cli) {
		return;
	}

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

function requestProgrammingAssistantSettings({ refresh = false } = {}) {
	if (isLoading.value || isRescanning.value) {
		return;
	}

	const requestId = `cli-scan-${Date.now()}-${++scanRequestId}`;
	activeScanRequestId = requestId;
	isLoading.value = !refresh;
	isRescanning.value = refresh;
	scanError.value = '';

	postToHost({
		type: refresh ? 'scan-programming-clis' : 'get-programming-assistant-settings',
		requestId,
	});

	if (!window.chrome?.webview) {
		fallbackTimer = window.setTimeout(() => {
			applySettingsResult({ tools: [], selectedCliId: null }, requestId);
		}, 250);
	}
}

function rescanCliTools() {
	requestProgrammingAssistantSettings({ refresh: true });
}

function selectCli(cli) {
	if (!cli?.id || selectedCliId.value === cli.id || isLoading.value || isRescanning.value) {
		return;
	}

	const requestId = `cli-select-${Date.now()}-${++scanRequestId}`;
	activeScanRequestId = requestId;
	selectedCliId.value = cli.id;
	scanError.value = '';

	postToHost({
		type: 'select-programming-cli',
		requestId,
		cliId: cli.id,
	});

	if (!window.chrome?.webview) {
		fallbackTimer = window.setTimeout(() => {
			applySettingsResult({ tools: cliTools, selectedCliId: cli.id }, requestId);
		}, 250);
	}
}

function applySettingsResult(payload, requestId) {
	if (requestId && activeScanRequestId && requestId !== activeScanRequestId) {
		return;
	}

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
	isLoading.value = false;
	isRescanning.value = false;
	activeScanRequestId = null;

	if (fallbackTimer) {
		window.clearTimeout(fallbackTimer);
		fallbackTimer = null;
	}
}

function normalizeSelectedCliId(tools, value) {
	const normalized = typeof value === 'string' ? value.trim().toLowerCase() : '';
	if (normalized && tools.some((tool) => tool?.id === normalized)) {
		return normalized;
	}

	return tools[0]?.id || '';
}

function handleMessage(payload) {
	if (payload?.type === 'programming-cli-test-result') {
		handleTestResult(payload);
		return;
	}

	if (payload?.type !== 'programming-assistant-settings') {
		return;
	}

	applySettingsResult(payload, payload.requestId);
}

defineExpose({
	handleMessage,
});

onMounted(() => {
	requestProgrammingAssistantSettings({ refresh: false });
});

onUnmounted(() => {
	activeTestRequests.clear();

	if (fallbackTimer) {
		window.clearTimeout(fallbackTimer);
	}

	if (isLoading.value || isRescanning.value) {
		isLoading.value = false;
		isRescanning.value = false;
		activeScanRequestId = null;
	}
});
</script>

<template>
	<section class="programming-assistant sc-root sc-stage">
		<main class="assistant-main">
			<header class="pa-hero sc-rise" style="--i: 0">
				<div class="pa-hero-left">
					<div class="pa-kicker">
						<TerminalSquare :size="13" :stroke-width="2" aria-hidden="true" />
						LOCAL CLI RUNTIMES
					</div>
					<h1 class="pa-title">编程助手</h1>
					<p class="pa-sub">检测本机安装的智能体 CLI，选择默认运行时与模型。</p>
				</div>
				<button class="pa-btn scan-btn" :class="{ 'is-rescanning': isRescanning }" type="button" :disabled="isRescanning"
					@click="rescanCliTools">
					<RefreshCw :size="14" :stroke-width="2" class="scan-icon" aria-hidden="true" />
					{{ isRescanning ? '扫描中…' : '重新扫描' }}
				</button>
			</header>

			<div class="section-bar sc-rise" style="--i: 1">
				<h3>本地 CLI <span class="count">[{{ String(cliTools.length).padStart(2, '0') }}]</span></h3>
				<span class="section-line" aria-hidden="true"></span>
			</div>

			<div class="cli-list">
				<div v-if="scanStatusText" class="scan-state sc-rise" style="--i: 2" :class="{ 'scan-state--error': scanError }">
					<TriangleAlert v-if="scanError" :size="15" :stroke-width="2" aria-hidden="true" />
					{{ scanStatusText }}
				</div>

				<article v-for="(cli, ci) in cliTools" :key="cli.id" class="cli-card sc-rise" :style="{ '--i': ci + 2 }" :class="{ 'is-open': cli.isOpen }">
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
											<option v-for="level in cli.reasoningLevels" :key="level" :value="level">{{ level }}
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

								<button v-if="selectedCliId !== cli.id" class="pa-btn pa-btn--acid cli-select" type="button"
									:disabled="isLoading || isRescanning" @click="selectCli(cli)">
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
@import './settings-console.css';

.programming-assistant {
	min-height: 100%;
	color: var(--sc-text);
	font-family: var(--sc-sans);
	font-size: 15px;
	line-height: 1.55;
	-webkit-font-smoothing: antialiased;
	-moz-osx-font-smoothing: grayscale;
}

.programming-assistant *,
.programming-assistant *::before,
.programming-assistant *::after {
	box-sizing: border-box;
}

.assistant-main {
	max-width: 940px;
	margin: 0 auto;
	padding: 52px 56px 72px;
}

/* ── hero ───────────────────────────────────────────────────── */
.pa-hero {
	display: flex;
	align-items: flex-end;
	justify-content: space-between;
	gap: 24px;
	margin-bottom: 34px;
	padding-bottom: 26px;
	border-bottom: 1px solid var(--sc-line);
}

.pa-kicker {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	margin-bottom: 14px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 10px;
	font-weight: 600;
	letter-spacing: 0.24em;
}

.pa-kicker svg {
	color: var(--sc-acid);
}

.pa-title {
	margin: 0;
	font-family: var(--sc-display);
	font-size: 46px;
	font-weight: 660;
	letter-spacing: 0.01em;
	line-height: 1.05;
}

.pa-sub {
	margin: 10px 0 0;
	color: var(--sc-mute);
	font-size: 13px;
}

/* ── section bar ────────────────────────────────────────────── */
.section-bar {
	display: flex;
	align-items: center;
	gap: 16px;
	margin-bottom: 18px;
}

.section-bar h3 {
	display: flex;
	align-items: baseline;
	gap: 8px;
	margin: 0;
	color: var(--sc-soft);
	font-size: 13px;
	font-weight: 600;
	letter-spacing: 0.04em;
	white-space: nowrap;
}

.count {
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-size: 12px;
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
	font-size: 13px;
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
	box-shadow: 0 8px 22px rgba(59, 91, 253, 0.18);
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
	background: var(--sc-panel);
	color: var(--sc-mute);
	font-size: 13.5px;
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
	box-shadow: 0 16px 40px rgba(23, 26, 31, 0.08);
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
	color: #6b7280;
	font-family: var(--sc-mono);
	font-size: 13px;
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
	font-size: 17px;
	font-weight: 630;
	letter-spacing: 0.01em;
}

.cli-vendor {
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: 11px;
	font-weight: 500;
	letter-spacing: 0.03em;
}

.cli-meta {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 10px;
	margin-top: 6px;
	font-size: 12px;
}

.label {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: 9.5px;
	font-weight: 600;
	letter-spacing: 0.2em;
}

.ver,
.model-name {
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: 12px;
}

.ver {
	padding: 2px 7px;
	border: 1px solid var(--sc-line);
	border-radius: 5px;
	background: var(--sc-raise);
	font-size: 10.5px;
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
	font-size: 11px;
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
	font-size: 9.5px;
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
	font-size: 13px;
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
	font-size: 12.5px;
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
	font-size: 13.5px;
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
	font-size: 12.5px;
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
	font-size: 12px;
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
	.assistant-main {
		padding: 32px 22px 52px;
	}

	.pa-hero {
		align-items: flex-start;
		flex-direction: column;
	}

	.cli-config {
		padding-left: 22px;
	}
}
</style>
