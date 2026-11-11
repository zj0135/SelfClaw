<script setup>
import {
	Layers,
	ShieldCheck,
	FlaskConical,
	Sparkles,
	FileCode2,
	Terminal,
	Boxes,
	ArrowUpRight,
} from 'lucide-vue-next';

const emit = defineEmits(['select-preset', 'trigger-mention']);

const STARTER_PRESETS = [
	{
		id: 'architecture',
		title: '探索工程架构',
		desc: '梳理当前工作区目录结构与核心业务逻辑',
		prompt: '请分析当前工作区的工程架构，梳理主要目录结构与核心业务逻辑。',
		icon: Layers,
	},
	{
		id: 'review',
		title: '代码审查分析',
		desc: '检查当前代码质量、潜在缺陷并提出优化建议',
		prompt: '请审查当前工作区代码，指出潜在的代码质量问题、逻辑隐患并提出改进建议。',
		icon: ShieldCheck,
	},
	{
		id: 'tests',
		title: '编写单元测试',
		desc: '为核心模块与关键逻辑生成严谨的测试用例',
		prompt: '请帮我梳理当前项目的核心逻辑，并为关键模块编写针对性的单元测试用例。',
		icon: FlaskConical,
	},
	{
		id: 'feature',
		title: '功能设计拆解',
		desc: '拆解新需求场景并规划模块技术实施步骤',
		prompt: '我想在当前项目中实现一个新功能，请先协助我进行方案设计与分步实施计划。',
		icon: Sparkles,
	},
];

const MENTION_HINTS = [
	{ id: 'file', label: '@ 引用文件', icon: FileCode2, token: '@' },
	{ id: 'command', label: '/ 快捷指令', icon: Terminal, token: '/' },
	{ id: 'skill', label: '$ 技能扩展', icon: Boxes, token: '$' },
];

function onPresetClick(preset) {
	emit('select-preset', preset.prompt);
}

function onMentionClick(mention) {
	emit('trigger-mention', mention.token);
}
</script>

<template>
	<section class="empty-starter-stage" aria-label="新对话引导">
		<div class="hero-block">
			<h1 class="hero-title">想构建什么？</h1>
			<p class="hero-subtitle">随意提问，或选择以下高频场景快速开始</p>
			
			<div class="mention-pills-row" aria-label="快捷输入指引">
				<button
					v-for="hint in MENTION_HINTS"
					:key="hint.id"
					class="mention-pill"
					type="button"
					:title="`在输入框中输入 ${hint.token} 唤起快速面板`"
					@click="onMentionClick(hint)"
				>
					<component :is="hint.icon" :size="12" :stroke-width="1.9" aria-hidden="true" />
					<span>{{ hint.label }}</span>
				</button>
			</div>
		</div>

		<div class="starter-grid" role="list" aria-label="常用开发场景推荐">
			<button
				v-for="(preset, index) in STARTER_PRESETS"
				:key="preset.id"
				class="starter-card"
				type="button"
				role="listitem"
				:style="{ '--card-idx': index }"
				@click="onPresetClick(preset)"
			>
				<div class="card-icon-wrap" aria-hidden="true">
					<component :is="preset.icon" :size="17" :stroke-width="1.8" />
				</div>
				<div class="card-body">
					<div class="card-head">
						<span class="card-title">{{ preset.title }}</span>
						<ArrowUpRight :size="13" class="card-arrow" aria-hidden="true" />
					</div>
					<p class="card-desc">{{ preset.desc }}</p>
				</div>
			</button>
		</div>
	</section>
</template>

<style scoped>
.empty-starter-stage {
	width: 100%;
	max-width: min(calc(100% - var(--chat-inline-inset)), calc(var(--chat-content-width) * 0.93));
	margin: 0 auto;
	display: flex;
	flex-direction: column;
	align-items: center;
	justify-content: flex-end;
	padding: 0 0 24px;
	background: transparent;
	user-select: none;
}

.hero-block {
	text-align: center;
	margin-bottom: 24px;
	animation: hero-fade-in 0.5s var(--ease-out) both;
}

@keyframes hero-fade-in {
	from {
		opacity: 0;
		transform: translateY(12px);
	}
	to {
		opacity: 1;
		transform: translateY(0);
	}
}

.hero-title {
	margin: 0;
	color: var(--text);
	font-family: var(--font-display);
	font-size: 38px;
	font-weight: 700;
	line-height: 1.15;
	letter-spacing: -0.022em;
}

.hero-subtitle {
	margin: 8px 0 14px;
	color: var(--muted);
	font-size: var(--fs-135);
	line-height: 1.5;
	font-weight: 480;
}

.mention-pills-row {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 8px;
	flex-wrap: wrap;
}

.mention-pill {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 3px 9px;
	border: 1px solid color-mix(in srgb, var(--text) 8%, transparent);
	border-radius: 999px;
	background: color-mix(in srgb, var(--panel) 85%, transparent);
	color: var(--muted-soft);
	font-size: var(--fs-115);
	font-weight: 520;
	cursor: pointer;
	transition:
		border-color 0.15s,
		color 0.15s,
		background 0.15s,
		transform 0.12s var(--ease-spring);
}

.mention-pill:hover {
	border-color: color-mix(in srgb, var(--accent) 35%, transparent);
	background: color-mix(in srgb, var(--accent) 6%, var(--panel));
	color: var(--accent);
	transform: translateY(-1px);
}

.mention-pill:active {
	transform: translateY(0);
}

/* 4 格建议卡片网格：响应式 2x2 */
.starter-grid {
	width: 100%;
	display: grid;
	grid-template-columns: repeat(2, minmax(0, 1fr));
	gap: 10px;
}

.starter-card {
	position: relative;
	display: flex;
	align-items: flex-start;
	gap: 12px;
	padding: 12px 14px;
	border: 1px solid color-mix(in srgb, var(--text) 9%, transparent);
	border-radius: 12px;
	background: color-mix(in srgb, var(--panel) 90%, transparent);
	text-align: left;
	cursor: pointer;
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.02);
	backdrop-filter: blur(10px);
	transition:
		border-color 0.18s,
		box-shadow 0.18s,
		background 0.18s,
		transform 0.18s var(--ease-out);
	animation: card-stagger-in 0.5s var(--ease-out) both;
	animation-delay: calc(var(--card-idx, 0) * 45ms + 80ms);
}

@keyframes card-stagger-in {
	from {
		opacity: 0;
		transform: translateY(10px);
	}
	to {
		opacity: 1;
		transform: translateY(0);
	}
}

.starter-card:hover {
	border-color: color-mix(in srgb, var(--accent) 32%, transparent);
	background: color-mix(in srgb, var(--accent) 3%, var(--panel));
	box-shadow:
		0 2px 4px rgba(var(--shadow-ink), 0.03),
		0 8px 20px -3px rgba(var(--shadow-ink), 0.05);
	transform: translateY(-2px);
}

.starter-card:active {
	transform: translateY(0);
}

.card-icon-wrap {
	display: grid;
	place-items: center;
	width: 32px;
	height: 32px;
	flex: none;
	border-radius: 8px;
	border: 1px solid color-mix(in srgb, var(--text) 7%, transparent);
	background: color-mix(in srgb, var(--text) 4%, transparent);
	color: var(--muted);
	transition:
		border-color 0.18s,
		background 0.18s,
		color 0.18s;
}

.starter-card:hover .card-icon-wrap {
	border-color: color-mix(in srgb, var(--accent) 25%, transparent);
	background: color-mix(in srgb, var(--accent) 10%, transparent);
	color: var(--accent);
}

.card-body {
	flex: 1;
	min-width: 0;
}

.card-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 6px;
	margin-bottom: 2px;
}

.card-title {
	color: var(--text);
	font-size: var(--fs-13);
	font-weight: 600;
	line-height: 1.3;
}

.card-arrow {
	color: var(--faint);
	opacity: 0;
	transform: translate(-2px, 2px);
	transition: opacity 0.16s, transform 0.16s, color 0.16s;
}

.starter-card:hover .card-arrow {
	opacity: 1;
	color: var(--accent);
	transform: translate(0, 0);
}

.card-desc {
	margin: 0;
	color: var(--muted);
	font-size: var(--fs-115);
	line-height: 1.45;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

@media (max-width: 760px) {
	.starter-grid {
		grid-template-columns: 1fr;
	}
	.hero-title {
		font-size: 30px;
	}
}

@media (prefers-reduced-motion: reduce) {
	.hero-block,
	.starter-card {
		animation: none;
	}
	.starter-card:hover {
		transform: none;
	}
}
</style>
