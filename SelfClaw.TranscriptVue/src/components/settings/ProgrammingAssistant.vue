<script setup>
import { onUnmounted, reactive, ref } from 'vue';
import claudeIcon from '../../../assets/agents-icons/claude.svg';
import codexIcon from '../../../assets/agents-icons/codex.svg';
import opencodeIcon from '../../../assets/agents-icons/opencode.svg';

const isRescanning = ref(false);
const testTimers = new Map();
let rescanTimer = null;

const cliTools = reactive([
	{
		id: 'claude',
		name: 'Claude Code',
		vendor: 'Anthropic official CLI',
		version: '2.1.195',
		iconSrc: claudeIcon,
		iconBackground: '#ffffff',
		models: ['Default (CLI config)', 'claude-sonnet-4.5', 'claude-opus-4.1', 'claude-haiku-4'],
		selectedModel: 'Default (CLI config)',
		testMessage: '连接正常 · CLI 版本 2.1.195',
		isOpen: true,
		testing: false,
		showToast: false,
	},
	{
		id: 'codex',
		name: 'Codex CLI',
		vendor: 'OpenAI official CLI',
		version: 'codex-cli 0.142.3',
		iconSrc: codexIcon,
		iconBackground: '#ffffff',
		models: ['Default (CLI config)', 'gpt-5.1-codex', 'gpt-5.1', 'o4-mini'],
		selectedModel: 'Default (CLI config)',
		reasoningLevels: ['Default (CLI config)', 'Low', 'Medium', 'High'],
		selectedReasoningLevel: 'Default (CLI config)',
		testMessage: '连接正常 · CLI 版本 0.142.3',
		isOpen: false,
		testing: false,
		showToast: false,
	},
	{
		id: 'opencode',
		name: 'OpenCode',
		vendor: 'Open-source agent CLI',
		version: '1.17.11',
		iconSrc: opencodeIcon,
		iconBackground: '#ffffff',
		models: ['Default (CLI config)', 'claude-sonnet-4.5', 'gpt-5.1-codex', 'deepseek-v4', 'qwen3-coder-480b'],
		selectedModel: 'Default (CLI config)',
		testMessage: '连接正常 · 已检测到 4 个可用模型',
		isOpen: false,
		testing: false,
		showToast: false,
	},
]);

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

	const currentTimer = testTimers.get(cli.id);
	if (currentTimer) {
		window.clearTimeout(currentTimer);
	}

	cli.testing = true;
	cli.showToast = false;

	const timer = window.setTimeout(() => {
		cli.testing = false;
		cli.showToast = true;
		testTimers.delete(cli.id);
	}, 850);

	testTimers.set(cli.id, timer);
}

function rescanCliTools() {
	if (isRescanning.value) {
		return;
	}

	isRescanning.value = true;
	rescanTimer = window.setTimeout(() => {
		isRescanning.value = false;
		rescanTimer = null;
	}, 750);
}

onUnmounted(() => {
	testTimers.forEach((timer) => window.clearTimeout(timer));
	testTimers.clear();

	if (rescanTimer) {
		window.clearTimeout(rescanTimer);
	}
});
</script>

<template>
	<section class="programming-assistant">
		<main class="assistant-main">
			<div class="section-bar">
				<h3>本地 CLI <span class="count">({{ cliTools.length }})</span></h3>
				<button class="pa-btn" :class="{ 'is-rescanning': isRescanning }" type="button" :disabled="isRescanning"
					@click="rescanCliTools">
					<svg class="scan-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"
						stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
						<path d="M3 12a9 9 0 0 1 15-6.7L21 8" />
						<path d="M21 3v5h-5" />
						<path d="M21 12a9 9 0 0 1-15 6.7L3 16" />
						<path d="M3 21v-5h5" />
					</svg>
					重新扫描
				</button>
			</div>

			<div class="cli-list">
				<article v-for="cli in cliTools" :key="cli.id" class="cli-card" :class="{ 'is-open': cli.isOpen }">
					<div class="cli-row" role="button" tabindex="0" :aria-expanded="cli.isOpen ? 'true' : 'false'"
						@click="toggleOpen(cli)" @keydown.enter.prevent="toggleOpen(cli)"
						@keydown.space.prevent="toggleOpen(cli)">
						<div class="cli-icon" :style="{ background: cli.iconBackground }">
							<img class="cli-svg" :src="cli.iconSrc" alt="" aria-hidden="true" />
						</div>

						<div class="cli-body">
							<div class="cli-titleline">
								<span class="cli-name">{{ cli.name }}</span>
								<span v-if="cli.vendor" class="cli-vendor">· {{ cli.vendor }}</span>
							</div>

							<div class="cli-meta">
								<template v-if="cli.version">
									<span class="ver">{{ cli.version }}</span>
									<span class="label">·</span>
								</template>
								<span class="label">模型</span>
								<span class="model-name">{{ cli.selectedModel }}</span>
							</div>
						</div>

						<button class="cli-expand" type="button" :aria-label="cli.isOpen ? '收起' : '展开'"
							@click.stop="toggleOpen(cli)">
							<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
								stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
								<path d="m6 9 6 6 6-6" />
							</svg>
						</button>
					</div>

					<div class="cli-config">
						<div class="cli-config__inner">
							<div class="cli-config__label">
								模型
								<span class="badge badge--live">来自 CLI 的实时列表</span>
							</div>

							<div class="cli-config__controls">
								<div class="select-wrap">
									<select v-model="cli.selectedModel" :aria-label="`${cli.name} 模型选择`"
										@change="onModelChange(cli)">
										<option v-for="model in cli.models" :key="model" :value="model">{{ model }}
										</option>
									</select>
									<svg class="chev" viewBox="0 0 24 24" fill="none" stroke="currentColor"
										stroke-width="2" stroke-linecap="round" stroke-linejoin="round"
										aria-hidden="true">
										<path d="m6 9 6 6 6-6" />
									</svg>
								</div>

								<div v-if="cli.reasoningLevels" class="select-field">
									<label class="select-field__label" :for="`reasoning-${cli.id}`">推理等级</label>
									<div class="select-wrap select-wrap--reasoning">
										<select :id="`reasoning-${cli.id}`" v-model="cli.selectedReasoningLevel"
											:aria-label="`${cli.name} 推理等级选择`" @change="onModelChange(cli)">
											<option v-for="level in cli.reasoningLevels" :key="level" :value="level">{{ level }}
											</option>
										</select>
										<svg class="chev" viewBox="0 0 24 24" fill="none" stroke="currentColor"
											stroke-width="2" stroke-linecap="round" stroke-linejoin="round"
											aria-hidden="true">
											<path d="m6 9 6 6 6-6" />
										</svg>
									</div>
								</div>

								<button class="pa-btn pa-btn--ghost cli-test" type="button" :disabled="cli.testing"
									@click="testCli(cli)">
									{{ cli.testing ? '测试中…' : '测试' }}
								</button>
							</div>

							<p class="cli-config__hint">
								模型列表来自这个 CLI；选 <span class="em">“默认”</span> 会沿用 CLI 自己的设置。
							</p>

							<div class="test-toast" :class="{ show: cli.showToast }">
								<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2"
									stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
									<path d="M20 6 9 17l-5-5" />
								</svg>
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
.programming-assistant {
	--pa-bg: #ffffff;
	--pa-surface: oklch(100% 0 0);
	--pa-surface-2: oklch(98.6% 0.003 250);
	--pa-fg: oklch(22% 0.02 255);
	--pa-fg-soft: oklch(38% 0.018 255);
	--pa-muted: oklch(56% 0.014 255);
	--pa-faint: oklch(70% 0.012 255);
	--pa-border: oklch(91% 0.006 255);
	--pa-border-2: oklch(86% 0.008 255);
	--pa-accent: oklch(54% 0.13 250);
	--pa-accent-weak: oklch(95% 0.03 250);
	--pa-ok: oklch(55% 0.12 155);
	--pa-ok-bg: oklch(95.5% 0.04 155);
	--pa-font: -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Hiragino Sans GB", "Microsoft YaHei",
		"Source Han Sans SC", system-ui, sans-serif;
	--pa-mono: "SF Mono", "JetBrains Mono", "Cascadia Code", ui-monospace, "Roboto Mono", Menlo, Consolas, monospace;
	min-height: 100%;
	background: var(--pa-bg);
	color: var(--pa-fg);
	font-family: var(--pa-font);
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
	max-width: 920px;
	margin: 0 auto;
	padding: 44px 56px 64px;
}

.section-bar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 16px;
	margin-bottom: 16px;
}

.section-bar h3 {
	display: flex;
	align-items: baseline;
	gap: 8px;
	margin: 0;
	color: var(--pa-fg-soft);
	font-size: 14px;
	font-weight: 600;
	line-height: 1.3;
}

.count {
	color: var(--pa-muted);
	font-family: var(--pa-mono);
	font-size: 12.5px;
	font-weight: 500;
}

.pa-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 7px;
	min-height: 36px;
	padding: 8px 14px;
	border: 1px solid var(--pa-border-2);
	border-radius: 9px;
	background: var(--pa-surface);
	color: var(--pa-fg-soft);
	font-family: inherit;
	font-size: 13.5px;
	font-weight: 540;
	line-height: 1.2;
	cursor: pointer;
	transition:
		background 0.14s,
		border-color 0.14s,
		transform 0.06s,
		opacity 0.14s;
}

.pa-btn:hover {
	border-color: var(--pa-muted);
	background: var(--pa-surface-2);
}

.pa-btn:active {
	transform: translateY(1px);
}

.pa-btn:disabled {
	cursor: default;
	opacity: 0.72;
}

.pa-btn svg {
	width: 15px;
	height: 15px;
}

.pa-btn--ghost {
	border-color: var(--pa-border);
	background: transparent;
	color: var(--pa-accent);
	padding: 8px 16px;
}

.pa-btn--ghost:hover {
	border-color: var(--pa-accent);
	background: var(--pa-accent-weak);
}

.scan-icon {
	transform-origin: center;
}

.is-rescanning .scan-icon {
	animation: scan-rotate 0.7s ease-in-out;
}

.cli-list {
	display: flex;
	max-width: 720px;
	flex-direction: column;
	gap: 12px;
}

.cli-card {
	position: relative;
	overflow: hidden;
	border: 1px solid var(--pa-border);
	border-radius: 14px;
	background: var(--pa-surface);
	transition:
		border-color 0.15s,
		box-shadow 0.15s;
}

.cli-card:hover {
	border-color: var(--pa-border-2);
}

.cli-card.is-open {
	border-color: var(--pa-border-2);
}

.cli-card.is-open::before {
	position: absolute;
	z-index: 2;
	top: 25%;
	bottom: 25%;
	left: 0;
	width: 3px;
	border-radius: 0 3px 3px 0;
	background: var(--pa-accent);
	content: "";
}

.cli-row {
	display: flex;
	align-items: flex-start;
	gap: 15px;
	padding: 18px 20px;
	cursor: pointer;
	user-select: none;
}

.cli-row:focus-visible {
	border-radius: 14px;
	outline: 2px solid var(--pa-accent);
	outline-offset: -2px;
}

.cli-icon {
	display: grid;
	width: 46px;
	height: 46px;
	flex-shrink: 0;
	place-items: center;
	overflow: hidden;
	border-radius: 11px;
	font-weight: 700;
}

.cli-svg {
	display: block;
	width: 26px;
	height: 26px;
	object-fit: contain;
}

.cli-body {
	flex: 1;
	min-width: 0;
}

.cli-titleline {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 8px 10px;
}

.cli-name {
	color: var(--pa-fg);
	font-size: 16px;
	font-weight: 620;
	letter-spacing: 0;
}

.cli-vendor {
	color: var(--pa-muted);
	font-size: 13px;
	font-weight: 450;
}

.cli-meta {
	display: flex;
	align-items: center;
	gap: 10px;
	margin-top: 4px;
	color: var(--pa-muted);
	font-size: 13px;
}

.label {
	color: var(--pa-faint);
}

.ver,
.model-name {
	color: var(--pa-fg-soft);
	font-family: var(--pa-mono);
	font-size: 12.5px;
}

.cli-expand {
	display: grid;
	width: 30px;
	height: 30px;
	flex-shrink: 0;
	place-items: center;
	margin-top: 6px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--pa-muted);
	cursor: pointer;
	transition:
		background 0.14s,
		color 0.14s,
		transform 0.18s ease;
}

.cli-expand:hover {
	background: var(--pa-surface-2);
	color: var(--pa-fg-soft);
}

.cli-expand svg {
	width: 18px;
	height: 18px;
	transition: transform 0.2s ease;
}

.cli-card.is-open .cli-expand svg {
	transform: rotate(180deg);
}

.badge {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	padding: 2px 9px;
	border-radius: 999px;
	font-size: 11.5px;
	font-weight: 560;
	line-height: 1.7;
	white-space: nowrap;
}

.badge--live {
	background: var(--pa-ok-bg);
	color: var(--pa-ok);
	font-family: var(--pa-mono);
	font-size: 11px;
	letter-spacing: 0;
}

.cli-config {
	display: none;
	padding: 0 20px 18px 81px;
}

.cli-card.is-open .cli-config {
	display: block;
}

.cli-config__inner {
	padding-top: 16px;
	border-top: 1px dashed var(--pa-border-2);
}

.cli-config__label {
	display: flex;
	align-items: center;
	gap: 9px;
	margin-bottom: 9px;
	color: var(--pa-fg-soft);
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
	gap: 8px;
}

.select-field__label {
	color: var(--pa-fg-soft);
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
	padding: 10px 38px 10px 13px;
	border: 1px solid var(--pa-border-2);
	border-radius: 9px;
	appearance: none;
	background: var(--pa-surface-2);
	color: var(--pa-fg);
	font-family: inherit;
	font-size: 14px;
	cursor: pointer;
	transition:
		border-color 0.14s,
		background 0.14s;
}

.select-wrap select:hover {
	border-color: var(--pa-muted);
}

.select-wrap select:focus {
	border-color: var(--pa-accent);
	outline: none;
	background: var(--pa-surface);
	box-shadow: 0 0 0 3px var(--pa-accent-weak);
}

.chev {
	position: absolute;
	top: 50%;
	right: 12px;
	width: 16px;
	height: 16px;
	color: var(--pa-muted);
	pointer-events: none;
	transform: translateY(-50%);
}

.cli-config__hint {
	margin: 9px 0 0;
	color: var(--pa-muted);
	font-size: 12.5px;
}

.em {
	color: var(--pa-fg-soft);
}

.test-toast {
	display: none;
	align-items: center;
	gap: 8px;
	margin-top: 12px;
	padding: 8px 12px;
	border-radius: 8px;
	background: var(--pa-ok-bg);
	color: var(--pa-ok);
	font-size: 13px;
}

.test-toast.show {
	display: flex;
}

.test-toast svg {
	width: 15px;
	height: 15px;
}

@keyframes scan-rotate {
	from {
		transform: rotate(0deg);
	}

	to {
		transform: rotate(360deg);
	}
}

@media (max-width: 880px) {
	.assistant-main {
		padding: 28px 22px 48px;
	}

	.cli-config {
		padding-left: 20px;
	}
}

@media (prefers-reduced-motion: reduce) {

	.programming-assistant *,
	.programming-assistant *::before,
	.programming-assistant *::after {
		animation-duration: 0.001ms !important;
		transition-duration: 0.001ms !important;
	}
}
</style>
