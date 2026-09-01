<script setup>
import AppearanceRow from './AppearanceRow.vue';
import ColorField from './ColorField.vue';
import FontSelect from './FontSelect.vue';
import ScaleSteps from './ScaleSteps.vue';
import { UI_FONT_CHOICES, useAppearance } from '../../../composables/useAppearance.js';

// useAppearance 是模块级单例，各组件各自取即可，不必从页面层层往下传。
const { state, revision, setUiFontFamily, setUiFontScale, setTextColor } = useAppearance();
</script>

<template>
	<div class="section">
		<AppearanceRow label="界面字体" hint="所选字族会插到默认字体栈最前面，缺字时自动回落" :index="0">
			<FontSelect :value="state.uiFontFamily" :choices="UI_FONT_CHOICES"
				@change="setUiFontFamily" />
		</AppearanceRow>

		<AppearanceRow label="界面字号" hint="按档位整体缩放，装饰性大字号不参与" :index="1">
			<ScaleSteps :value="state.uiFontScale" @select="setUiFontScale" />
		</AppearanceRow>

		<AppearanceRow label="正文颜色" hint="仅覆盖主文本色，次级与辅助文字仍跟随主题" :index="2">
			<ColorField :value="state.textColor" fallback-token="--ink-1" :revision="revision"
				@change="setTextColor" @reset="setTextColor('')" />
		</AppearanceRow>
	</div>
</template>

<style scoped>
.section {
	display: flex;
	flex-direction: column;
}
</style>
