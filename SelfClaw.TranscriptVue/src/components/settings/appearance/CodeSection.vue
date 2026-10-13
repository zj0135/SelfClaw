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
		<AppearanceRow label="代码块字体" :index="0">
			<FontSelect :value="state.codeFontFamily" :choices="CODE_FONT_CHOICES"
				@change="setCodeFontFamily" />
		</AppearanceRow>

		<AppearanceRow label="代码块字号" :index="1">
			<ScaleSteps :value="state.codeFontScale" @select="setCodeFontScale" />
		</AppearanceRow>

		<AppearanceRow label="代码块背景" :index="2">
			<ColorField :value="state.codeSurface" fallback-token="--code-surface" :revision="revision"
				@change="setCodeSurface" @reset="setCodeSurface('')" />
		</AppearanceRow>

		<AppearanceRow label="代码块前景" :index="3">
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
