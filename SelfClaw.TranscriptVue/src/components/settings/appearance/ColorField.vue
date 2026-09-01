<script setup>
import { computed, onMounted, ref, watch } from 'vue';
import { RotateCcw } from 'lucide-vue-next';

const props = defineProps({
	// 用户设定值。空串表示「未覆盖」，此时展示主题默认色。
	value: { type: String, default: '' },
	// 未覆盖时从哪个 token 读出当前生效色，用于给取色器一个正确的起点。
	fallbackToken: { type: String, required: true },
	revision: { type: Number, default: 0 },
});

const emit = defineEmits(['change', 'reset']);

const themeColor = ref('#000000');

// <input type="color"> 只认 #rrggbb。token 可能是 rgb()/color-mix() 之类，
// 所以借一个离屏元素让浏览器算成 rgb() 再转成十六进制。
function toHex(raw) {
	if (!raw) {
		return '#000000';
	}

	if (/^#[0-9a-f]{6}$/i.test(raw)) {
		return raw.toLowerCase();
	}

	const probe = document.createElement('span');
	probe.style.color = raw;
	probe.style.display = 'none';
	document.body.appendChild(probe);
	const computed = getComputedStyle(probe).color;
	probe.remove();

	const parts = computed.match(/\d+(\.\d+)?/g);
	if (!parts || parts.length < 3) {
		return '#000000';
	}

	return `#${parts
		.slice(0, 3)
		.map((part) => Math.round(Number(part)).toString(16).padStart(2, '0'))
		.join('')}`;
}

function readThemeColor() {
	const raw = getComputedStyle(document.documentElement).getPropertyValue(props.fallbackToken).trim();
	themeColor.value = toHex(raw);
}

const effective = computed(() => (props.value ? toHex(props.value) : themeColor.value));
const isCustom = computed(() => Boolean(props.value));

onMounted(readThemeColor);

// 主题切换后默认色变了，取色器的起点也得跟着变。
watch(() => props.revision, readThemeColor);
</script>

<template>
	<div class="color-field">
		<label class="swatch" :style="{ '--swatch': effective }">
			<input type="color" :value="effective" @input="emit('change', $event.target.value)" />
			<span class="swatch-fill" aria-hidden="true"></span>
		</label>

		<span class="swatch-code">{{ effective.toUpperCase() }}</span>
		<span class="swatch-tag" :data-custom="isCustom ? 'true' : 'false'">{{ isCustom ? '自定义' : '主题默认' }}</span>

		<button v-if="isCustom" type="button" class="swatch-reset" title="恢复主题默认色" @click="emit('reset')">
			<RotateCcw :size="12" :stroke-width="2" aria-hidden="true" />
		</button>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.color-field {
	display: inline-flex;
	align-items: center;
	gap: 9px;
}

.swatch {
	position: relative;
	display: grid;
	place-items: center;
	width: 32px;
	height: 32px;
	overflow: hidden;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	cursor: pointer;
	transition: border-color 0.16s, transform 0.14s var(--sc-ease-spring);
}

.swatch:hover {
	border-color: var(--sc-faint);
	transform: translateY(-1px);
}

.swatch:focus-within {
	outline: none;
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

/* 原生取色器控件铺满整块并透明化：外观完全由 .swatch-fill 决定，
   但点击与键盘可达性仍然来自真正的 input。 */
.swatch input {
	position: absolute;
	inset: 0;
	width: 100%;
	height: 100%;
	padding: 0;
	border: 0;
	opacity: 0;
	cursor: pointer;
}

.swatch-fill {
	width: 22px;
	height: 22px;
	border-radius: 5px;
	background: var(--swatch);
	box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--sc-text) 12%, transparent);
	pointer-events: none;
}

.swatch-code {
	min-width: 62px;
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: var(--fs-11);
	letter-spacing: 0.03em;
}

.swatch-tag {
	padding: 2px 7px;
	border: 1px solid var(--sc-line-2);
	border-radius: 999px;
	color: var(--sc-faint);
	font-size: var(--fs-95);
	font-weight: 600;
	white-space: nowrap;
}

.swatch-tag[data-custom='true'] {
	border-color: color-mix(in srgb, var(--sc-acid) 40%, transparent);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
}

.swatch-reset {
	display: grid;
	place-items: center;
	width: 26px;
	height: 26px;
	border: 1px solid var(--sc-line-2);
	border-radius: 7px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	cursor: pointer;
	transition: border-color 0.16s, color 0.16s, background 0.16s;
}

.swatch-reset:hover {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
	color: var(--sc-text);
}

.swatch-reset:focus-visible {
	outline: 2px solid var(--sc-acid);
	outline-offset: 1px;
}
</style>
