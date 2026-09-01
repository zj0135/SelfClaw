<script setup>
import AppearanceRow from './AppearanceRow.vue';
import ColorField from './ColorField.vue';
import FontSelect from './FontSelect.vue';
import ScaleSteps from './ScaleSteps.vue';
import { CODE_FONT_CHOICES, useAppearance } from '../../../composables/useAppearance.js';

const {
	state,
	revision,
	setCodeFontFamily,
	setCodeFontScale,
	setCodeSurface,
	setCodeInk,
} = useAppearance();
</script>

<template>
	<div class="section">
		<AppearanceRow label="代码块字体" hint="同时作用于行内代码与内置终端" :index="0">
			<FontSelect :value="state.codeFontFamily" :choices="CODE_FONT_CHOICES"
				@change="setCodeFontFamily" />
		</AppearanceRow>

		<AppearanceRow label="代码块字号" hint="与界面字号相互独立" :index="1">
			<ScaleSteps :value="state.codeFontScale" @select="setCodeFontScale" />
		</AppearanceRow>

		<AppearanceRow label="代码块背景" hint="默认在两个主题下都是深色，语法高亮按深底配色" :index="2">
			<ColorField :value="state.codeSurface" fallback-token="--code-surface" :revision="revision"
				@change="setCodeSurface" @reset="setCodeSurface('')" />
		</AppearanceRow>

		<AppearanceRow label="代码块前景" hint="未被语法高亮命中的普通字符用这个颜色" :index="3">
			<ColorField :value="state.codeInk" fallback-token="--code-ink" :revision="revision"
				@change="setCodeInk" @reset="setCodeInk('')" />
		</AppearanceRow>
	</div>
</template>

<style scoped>
.section {
	display: flex;
	flex-direction: column;
}
</style>
