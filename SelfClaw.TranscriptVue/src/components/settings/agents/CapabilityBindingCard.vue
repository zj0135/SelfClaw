<script setup>
import { ChevronRight } from 'lucide-vue-next';

// 能力绑定汇总卡：每个绑定类型一行，点击数量标签或行打开 BindingDialog 维护。
// section: { key, kicker, title, hint, icon, items }
defineProps({
	sections: { type: Array, default: () => [] },
});

const emit = defineEmits(['open']);

const boundCount = (section) => section.items.filter((item) => item.bound).length;
</script>

<template>
	<section class="card">
		<div class="card-head">
			<div>
				<div class="card-kicker">CAPABILITIES</div>
				<h3>能力绑定</h3>
			</div>
		</div>

		<div class="rows">
			<button v-for="section in sections" :key="section.key" type="button" class="cap-row"
				:disabled="!section.isBasic && !section.items.length" @click="emit('open', section)">
				<span class="cap-icon" aria-hidden="true">
					<component :is="section.icon" :size="16" :stroke-width="1.9" />
				</span>
				<span class="cap-main">
					<span class="cap-title">
						{{ section.title }}
						<span class="cap-kicker">{{ section.kicker }}</span>
					</span>
					<span class="cap-hint">{{ section.hint }}</span>
				</span>
				<span v-if="!section.isBasic" class="count-pill" :class="{ off: boundCount(section) === 0 }">
					{{ boundCount(section) }} / {{ section.items.length }}
				</span>
				<ChevronRight :size="15" :stroke-width="2" class="cap-chev" aria-hidden="true" />
			</button>
		</div>
	</section>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.card {
	overflow: hidden;
	border: 1px solid var(--sc-line);
	border-radius: 14px;
	background: var(--sc-panel);
	box-shadow: 0 2px 8px rgba(0, 0, 0, 0.04);
}

.card-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 20px 24px;
	border-bottom: 1px solid var(--sc-line);
	background: linear-gradient(to bottom, var(--sc-panel), color-mix(in srgb, var(--sc-panel) 98%, var(--sc-surface-0)));
}

.card-kicker {
	margin-bottom: 5px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-95);
	font-weight: 500;
	letter-spacing: 0.24em;
}

.card-head h3 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: var(--fs-18);
	font-weight: 640;
	letter-spacing: -0.01em;
}

.card-count {
	margin-top: 4px;
	color: var(--sc-mute);
	font-size: var(--fs-12);
}

.rows {
	display: flex;
	flex-direction: column;
}

.cap-row {
	display: flex;
	align-items: center;
	width: 100%;
	gap: 14px;
	padding: 16px 24px;
	border: 0;
	border-bottom: 1px solid var(--sc-line);
	background: transparent;
	color: inherit;
	font: inherit;
	text-align: left;
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s;
}

.cap-row:last-child {
	border-bottom: 0;
}

.cap-row:hover:not(:disabled) {
	background: color-mix(in srgb, var(--sc-acid) 3%, transparent);
	border-color: color-mix(in srgb, var(--sc-acid) 12%, transparent);
}

.cap-row:disabled {
	cursor: not-allowed;
	opacity: 0.5;
}

.cap-icon {
	display: grid;
	width: 38px;
	height: 38px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line);
	border-radius: 10px;
	background: var(--sc-surface-0);
	color: var(--sc-soft);
	transition: all 0.2s var(--sc-ease-spring);
}

.cap-row:hover:not(:disabled) .cap-icon {
	border-color: color-mix(in srgb, var(--sc-acid) 45%, transparent);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	transform: translateY(-2px) scale(1.05);
	box-shadow: 0 4px 12px color-mix(in srgb, var(--sc-acid) 18%, transparent);
}

.cap-main {
	display: grid;
	min-width: 0;
	flex: 1;
	gap: 4px;
}

.cap-title {
	display: flex;
	align-items: center;
	gap: 10px;
	font-size: var(--fs-145);
	font-weight: 600;
	letter-spacing: -0.01em;
}

.cap-kicker {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-95);
	font-weight: 500;
	letter-spacing: 0.2em;
}

.cap-hint {
	overflow: hidden;
	color: var(--sc-mute);
	font-size: var(--fs-12);
	line-height: 1.5;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.count-pill {
	flex: 0 0 auto;
	padding: 4px 11px;
	border: 1px solid color-mix(in srgb, var(--sc-acid) 35%, transparent);
	border-radius: 99px;
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.04em;
	transition: all 0.2s var(--sc-ease-spring);
}

.cap-row:hover:not(:disabled) .count-pill {
	transform: translateY(-1px) scale(1.05);
	box-shadow: 0 4px 12px color-mix(in srgb, var(--sc-acid) 20%, transparent);
}

.count-pill.off {
	border-color: var(--sc-line-2);
	background: var(--sc-raise);
	color: var(--sc-mute);
}

.cap-chev {
	flex: 0 0 auto;
	color: var(--sc-faint);
	transition: all 0.2s var(--sc-ease-out);
}

.cap-row:hover:not(:disabled) .cap-chev {
	color: var(--sc-acid);
	transform: translateX(3px);
}
</style>
