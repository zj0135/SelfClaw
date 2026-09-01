<script setup>
import { RotateCcw, Palette, Type, Code2, Eye } from 'lucide-vue-next';
import AppearancePreview from './appearance/AppearancePreview.vue';
import CodeSection from './appearance/CodeSection.vue';
import ThemeModeSelector from './appearance/ThemeModeSelector.vue';
import TypographySection from './appearance/TypographySection.vue';
import { useAppearance } from '../../composables/useAppearance.js';

const { state, resolvedTheme, setMode, resetAll } = useAppearance();

const groups = [
	{ id: 'theme', label: '主题', en: 'THEME', icon: Palette, hint: '整套调色板的明暗基调' },
	{ id: 'typography', label: '界面排版', en: 'TYPOGRAPHY', icon: Type, hint: '字族、字号与正文颜色' },
	{ id: 'code', label: '代码块', en: 'CODE', icon: Code2, hint: '代码与终端的独立排版设置' },
	{ id: 'preview', label: '预览', en: 'PREVIEW', icon: Eye, hint: '' },
];
</script>

<template>
	<main class="sc-root sc-stage sc-page appearance-page">
		<header class="sc-page-head sc-rise" style="--i: 0">
			<span class="sc-page-ghost" aria-hidden="true">General</span>
			<div>
				<span class="sc-page-kicker">GENERAL · APPEARANCE</span>
				<h1 class="sc-page-title">系统设置</h1>
				<p class="sc-page-sub">主题、字体与代码块外观</p>
			</div>

			<button type="button" class="reset-all" title="把全部外观设置恢复为默认" @click="resetAll">
				<RotateCcw :size="13" :stroke-width="2" aria-hidden="true" />
				<span>全部恢复默认</span>
			</button>
		</header>

		<div class="sc-page-body">
			<section v-for="(group, index) in groups" :key="group.id" class="group sc-rise"
				:style="{ '--i': index + 1 }">
				<div class="group-head">
					<component :is="group.icon" :size="15" :stroke-width="1.9" class="gh-ico" aria-hidden="true" />
					<span class="gh-label">{{ group.label }}</span>
					<span v-if="group.hint" class="gh-hint">{{ group.hint }}</span>
					<span class="gh-en">{{ group.en }}</span>
				</div>

				<ThemeModeSelector v-if="group.id === 'theme'" :mode="state.mode" :resolved="resolvedTheme"
					@select="setMode" />
				<TypographySection v-else-if="group.id === 'typography'" />
				<CodeSection v-else-if="group.id === 'code'" />
				<AppearancePreview v-else />
			</section>
		</div>
	</main>
</template>

<style scoped>
@import '../../styles/settings-console.css';

.appearance-page .sc-page-body {
	display: flex;
	flex-direction: column;
	gap: 22px;
}

.reset-all {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	min-height: 34px;
	padding: 7px 13px;
	border: 1px solid var(--sc-line-2);
	border-radius: 9px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	font-family: inherit;
	font-size: var(--fs-12);
	font-weight: 600;
	cursor: pointer;
	white-space: nowrap;
	transition:
		border-color 0.16s,
		background 0.16s,
		color 0.16s,
		transform 0.12s var(--sc-ease-spring);
}

.reset-all:hover {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
	color: var(--sc-text);
	transform: translateY(-1px);
}

.reset-all:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 2px;
}

.group-head {
	display: flex;
	align-items: center;
	gap: 9px;
	padding-bottom: 11px;
	margin-bottom: 3px;
	border-bottom: 1px solid var(--sc-line-2);
}

.gh-ico {
	flex: none;
	color: var(--sc-acid);
}

.gh-label {
	color: var(--sc-text);
	font-size: var(--fs-135);
	font-weight: 650;
	letter-spacing: 0.01em;
}

.gh-hint {
	min-width: 0;
	overflow: hidden;
	color: var(--sc-mute);
	font-size: var(--fs-115);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.gh-en {
	margin-left: auto;
	flex: none;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 600;
	letter-spacing: 0.2em;
}
</style>
