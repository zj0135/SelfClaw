<script setup>
import { Sun, Moon, MonitorSmartphone, Check } from 'lucide-vue-next';

const props = defineProps({
	mode: { type: String, default: 'system' },
	resolved: { type: String, default: 'light' },
});

const emit = defineEmits(['select']);

// preview 的取值决定卡片缩略图用哪套色：'system' 卡片要同时露出两半，
// 所以它不是一个主题名，而是一个专门的拼接态。
const modes = [
	{ id: 'light', label: '浅色', en: 'LIGHT', icon: Sun, note: '始终使用浅色界面' },
	{ id: 'dark', label: '暗色', en: 'DARK', icon: Moon, note: '始终使用深色界面' },
	{ id: 'system', label: '跟随系统', en: 'AUTO', icon: MonitorSmartphone, note: '随系统外观自动切换' },
];
</script>

<template>
	<div class="mode-grid">
		<button v-for="(item, index) in modes" :key="item.id" type="button" class="mode-card sc-rise"
			:style="{ '--i': index }" :data-active="props.mode === item.id ? 'true' : 'false'"
			:aria-pressed="props.mode === item.id ? 'true' : 'false'" @click="emit('select', item.id)">
			<span class="mc-thumb" :data-preview="item.id" aria-hidden="true">
				<span class="mc-thumb-bar"></span>
				<span class="mc-thumb-body">
					<span class="mc-thumb-line long"></span>
					<span class="mc-thumb-line"></span>
					<span class="mc-thumb-dot"></span>
				</span>
			</span>

			<span class="mc-meta">
				<span class="mc-head">
					<component :is="item.icon" :size="15" :stroke-width="1.9" class="mc-ico" aria-hidden="true" />
					<span class="mc-label">{{ item.label }}</span>
					<span class="mc-en">{{ item.en }}</span>
				</span>
				<span class="mc-note">{{ item.note }}</span>
				<span v-if="item.id === 'system'" class="mc-resolved">
					当前解析为 {{ props.resolved === 'dark' ? '暗色' : '浅色' }}
				</span>
			</span>

			<span v-if="props.mode === item.id" class="mc-check" aria-hidden="true">
				<Check :size="11" :stroke-width="3" />
			</span>
		</button>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.mode-grid {
	display: grid;
	grid-template-columns: repeat(auto-fit, minmax(232px, 1fr));
	gap: 12px;
}

.mode-card {
	position: relative;
	display: flex;
	flex-direction: column;
	gap: 12px;
	padding: 13px 13px 15px;
	overflow: hidden;
	border: 1px solid var(--sc-line);
	border-radius: 13px;
	background: var(--sc-panel);
	color: inherit;
	font: inherit;
	text-align: left;
	cursor: pointer;
	transition:
		border-color 0.18s var(--sc-ease-out),
		transform 0.18s var(--sc-ease-out),
		box-shadow 0.18s var(--sc-ease-out);
}

.mode-card:hover {
	border-color: var(--sc-line-2);
	transform: translateY(-2px);
	box-shadow: 0 14px 34px rgba(var(--shadow-ink), 0.09);
}

.mode-card:focus-visible {
	outline: none;
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.mode-card[data-active='true'] {
	border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
	background:
		radial-gradient(240px 140px at 12% 0%, var(--sc-acid-soft), transparent 70%),
		var(--sc-panel);
}

/*
 * 缩略图是自成一体的一小块「界面」：它必须画出目标主题的颜色，而不是当前主题的，
 * 所以这里的色值是写死的字面量 —— 用 token 就会跟着当前主题一起变，
 * 三张卡片会长得一模一样，失去选择的意义。
 */
.mc-thumb {
	position: relative;
	display: block;
	height: 74px;
	overflow: hidden;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
}

.mc-thumb-bar {
	position: absolute;
	inset: 0 0 auto;
	height: 14px;
}

.mc-thumb-body {
	position: absolute;
	inset: 22px 10px 10px;
	display: flex;
	flex-direction: column;
	gap: 6px;
}

.mc-thumb-line {
	height: 5px;
	width: 58%;
	border-radius: 3px;
}

.mc-thumb-line.long {
	width: 84%;
}

.mc-thumb-dot {
	width: 16px;
	height: 5px;
	border-radius: 3px;
}

.mc-thumb[data-preview='light'] {
	background: #ffffff;
}

.mc-thumb[data-preview='light'] .mc-thumb-bar {
	background: #f5f6f8;
	border-bottom: 1px solid #e5e7eb;
}

.mc-thumb[data-preview='light'] .mc-thumb-line {
	background: #d8dde5;
}

.mc-thumb[data-preview='light'] .mc-thumb-dot {
	background: #3b5bfd;
}

.mc-thumb[data-preview='dark'] {
	background: #16191f;
}

.mc-thumb[data-preview='dark'] .mc-thumb-bar {
	background: #0f1115;
	border-bottom: 1px solid #262b34;
}

.mc-thumb[data-preview='dark'] .mc-thumb-line {
	background: #343a45;
}

.mc-thumb[data-preview='dark'] .mc-thumb-dot {
	background: #6d86ff;
}

/* 跟随系统：一条对角线把两套色拼在一张图里。 */
.mc-thumb[data-preview='system'] {
	background: linear-gradient(115deg, #ffffff 0 49.6%, #262b34 49.6% 50.4%, #16191f 50.4% 100%);
}

.mc-thumb[data-preview='system'] .mc-thumb-bar {
	background: linear-gradient(115deg, #f5f6f8 0 49.6%, #262b34 49.6% 50.4%, #0f1115 50.4% 100%);
}

.mc-thumb[data-preview='system'] .mc-thumb-line {
	background: linear-gradient(105deg, #d8dde5 0 46%, #343a45 46% 100%);
}

.mc-thumb[data-preview='system'] .mc-thumb-dot {
	background: linear-gradient(105deg, #3b5bfd 0 50%, #6d86ff 50% 100%);
}

.mc-meta {
	display: flex;
	flex-direction: column;
	gap: 4px;
	min-width: 0;
}

.mc-head {
	display: flex;
	align-items: center;
	gap: 8px;
	min-width: 0;
}

.mc-ico {
	flex: none;
	color: var(--sc-mute);
	transition: color 0.16s;
}

.mode-card[data-active='true'] .mc-ico {
	color: var(--sc-acid);
}

.mc-label {
	color: var(--sc-text);
	font-size: var(--fs-135);
	font-weight: 620;
}

.mc-en {
	margin-left: auto;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 600;
	letter-spacing: 0.18em;
}

.mc-note {
	color: var(--sc-mute);
	font-size: var(--fs-12);
	line-height: 1.5;
}

.mc-resolved {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-95);
	letter-spacing: 0.04em;
}

.mc-check {
	position: absolute;
	top: 21px;
	right: 21px;
	display: grid;
	place-items: center;
	width: 18px;
	height: 18px;
	border-radius: 50%;
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
	box-shadow: 0 0 0 3px color-mix(in srgb, var(--sc-panel) 80%, transparent);
	animation: sc-pop 0.24s var(--sc-ease-spring) both;
}
</style>
