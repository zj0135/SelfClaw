<script setup>
import { ref } from 'vue';
import { RotateCcw, Palette, Type, Code2, Eye, Shrink, ChevronDown } from 'lucide-vue-next';
import AppearancePreview from './appearance/AppearancePreview.vue';
import CodeSection from './appearance/CodeSection.vue';
import ThemeModeSelector from './appearance/ThemeModeSelector.vue';
import TypographySection from './appearance/TypographySection.vue';
import Compression from './compression/Compression.vue';
import { useAppearance } from '../../composables/useAppearance.js';

const { state, resolvedTheme, setMode, resetAll } = useAppearance();

const groups = [
	{ id: 'theme', label: '主题', en: 'THEME', icon: Palette },
	{ id: 'typography', label: '界面排版', en: 'TYPOGRAPHY', icon: Type },
	{ id: 'compression', label: '上下文压缩', en: 'COMPRESSION', icon: Shrink },
];

const openGroups = ref({
	theme: false,
	typography: false,
	compression: false,
});

function isOpen(id) {
	return Boolean(openGroups.value[id]);
}

function toggleGroup(id) {
	openGroups.value[id] = !openGroups.value[id];
}
</script>

<template>
	<main class="sc-root sc-stage sc-page appearance-page">
		<header class="sc-page-head sc-rise" style="--i: 0">
			<div>
				<h1 class="sc-page-title">系统设置</h1>
			</div>

			<button type="button" class="reset-all" title="把全部外观设置恢复为默认" @click="resetAll">
				<RotateCcw :size="13" :stroke-width="2" aria-hidden="true" />
				<span>全部恢复默认</span>
			</button>
		</header>

		<div class="sc-page-body">
			<section v-for="(group, index) in groups" :key="group.id" class="group sc-rise"
				:class="{ 'is-open': isOpen(group.id) }" :style="{ '--i': index + 1 }">
				<button type="button" class="group-head" :class="{ 'is-open': isOpen(group.id) }"
					:aria-expanded="isOpen(group.id)" :aria-controls="`group-body-${group.id}`"
					@click="toggleGroup(group.id)">
					<component :is="group.icon" :size="15" :stroke-width="1.9" class="gh-ico" aria-hidden="true" />
					<span class="gh-label">{{ group.label }}</span>
					<ChevronDown :size="15" :stroke-width="2" class="gh-chev" aria-hidden="true" />
				</button>

				<div v-show="isOpen(group.id)" :id="`group-body-${group.id}`" class="group-body">
					<ThemeModeSelector v-if="group.id === 'theme'" :mode="state.mode" :resolved="resolvedTheme"
						@select="setMode" />
					<div v-else-if="group.id === 'typography'" class="typography-composite">
						<TypographySection />
						<div class="subgroup-divider">
							<Code2 :size="13" :stroke-width="2" class="subgroup-ico" aria-hidden="true" />
							<span class="subgroup-title">代码块</span>
						</div>
						<CodeSection />
						<div class="subgroup-divider">
							<Eye :size="13" :stroke-width="2" class="subgroup-ico" aria-hidden="true" />
							<span class="subgroup-title">预览</span>
						</div>
						<AppearancePreview />
					</div>
					<Compression v-else-if="group.id === 'compression'" />
				</div>
			</section>
		</div>
	</main>
</template>

<style scoped>
@import '../../styles/settings-console.css';

.appearance-page .sc-page-body {
	display: flex;
	flex-direction: column;
	gap: 16px;
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

.group {
	display: flex;
	flex-direction: column;
	border-radius: 8px;
}

.group-head {
	display: flex;
	align-items: center;
	gap: 10px;
	width: 100%;
	padding: 10px 10px 12px;
	border: 0;
	border-bottom: 1px solid var(--sc-line-2);
	border-radius: 8px 8px 0 0;
	background: transparent;
	color: inherit;
	font-family: inherit;
	text-align: left;
	cursor: pointer;
	user-select: none;
	transition:
		background 0.16s var(--sc-ease-out),
		border-color 0.16s var(--sc-ease-out);
}

.group-head:hover {
	background: var(--sc-hover);
	border-bottom-color: var(--sc-acid);
}

.group-head:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: -1px;
}

.group-head.is-open {
	border-bottom-color: color-mix(in srgb, var(--sc-acid) 65%, var(--sc-line-2));
}

.gh-ico {
	flex: none;
	color: var(--sc-acid);
	transition: transform 0.2s var(--sc-ease-spring);
}

.group-head:hover .gh-ico {
	transform: scale(1.12);
}

.gh-label {
	color: var(--sc-text);
	font-size: var(--fs-135);
	font-weight: 650;
	letter-spacing: 0.01em;
}

.gh-en {
	margin-left: auto;
	flex: none;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 600;
	letter-spacing: 0.2em;
	transition: color 0.16s;
}

.group-head:hover .gh-en {
	color: var(--sc-mute);
}

.gh-chev {
	flex: none;
	margin-left: 8px;
	color: var(--sc-faint);
	transition:
		transform 0.24s var(--sc-ease-spring),
		color 0.16s;
}

.group-head:hover .gh-chev {
	color: var(--sc-text);
}

.group-head.is-open .gh-chev {
	transform: rotate(180deg);
	color: var(--sc-acid);
}

.group-body {
	padding-top: 14px;
	animation: group-expand 0.2s cubic-bezier(0.16, 1, 0.3, 1) both;
	transform-origin: top;
}

.typography-composite {
	display: flex;
	flex-direction: column;
}

.subgroup-divider {
	display: flex;
	align-items: center;
	gap: 8px;
	margin: 22px 0 6px;
	padding-bottom: 8px;
	border-bottom: 1px solid var(--sc-line-2);
}

.subgroup-ico {
	flex: none;
	color: var(--sc-acid);
}

.subgroup-title {
	color: var(--sc-text);
	font-size: var(--fs-12);
	font-weight: 650;
	letter-spacing: 0.02em;
}

.subgroup-en {
	margin-left: auto;
	flex: none;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 600;
	letter-spacing: 0.18em;
}

@keyframes group-expand {
	from {
		opacity: 0;
		transform: translateY(-6px);
	}
	to {
		opacity: 1;
		transform: translateY(0);
	}
}
</style>
