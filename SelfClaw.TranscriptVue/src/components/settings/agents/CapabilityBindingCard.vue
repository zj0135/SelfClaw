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
				:disabled="!section.items.length" @click="emit('open', section)">
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
				<span class="count-pill" :class="{ off: boundCount(section) === 0 }">
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
}

.card-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 14px 18px;
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
	font-size: var(--fs-17);
	font-weight: 630;
}

.card-count {
	margin-top: 3px;
	color: var(--sc-mute);
	font-size: var(--fs-12);
}

.rows {
	border-top: 1px solid var(--sc-line);
}

.cap-row {
	display: flex;
	align-items: center;
	width: 100%;
	gap: 12px;
	padding: 9px 18px;
	border: 0;
	border-bottom: 1px solid var(--sc-line);
	background: transparent;
	color: inherit;
	font: inherit;
	text-align: left;
	cursor: pointer;
	transition: background 0.15s;
}

.cap-row:last-child {
	border-bottom: 0;
}

.cap-row:hover:not(:disabled) {
	background: color-mix(in srgb, var(--text) 2.5%, transparent);
}

.cap-row:disabled {
	cursor: default;
	opacity: 0.6;
}

.cap-icon {
	display: grid;
	width: 30px;
	height: 30px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-soft);
	transition: color 0.15s, border-color 0.15s;
}

.cap-row:hover:not(:disabled) .cap-icon {
	border-color: color-mix(in srgb, var(--sc-acid) 35%, transparent);
	color: var(--sc-acid);
}

.cap-main {
	display: grid;
	min-width: 0;
	flex: 1;
	gap: 2px;
}

.cap-title {
	display: flex;
	align-items: center;
	gap: 9px;
	font-size: var(--fs-135);
	font-weight: 600;
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
	padding: 3px 10px;
	border: 1px solid color-mix(in srgb, var(--sc-acid) 35%, transparent);
	border-radius: 99px;
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-size: var(--fs-11);
	font-weight: 600;
	letter-spacing: 0.04em;
	transition: transform 0.14s var(--sc-ease-spring), box-shadow 0.15s;
}

.cap-row:hover:not(:disabled) .count-pill {
	transform: translateY(-1px);
	box-shadow: 0 6px 16px color-mix(in srgb, var(--accent) 16%, transparent);
}

.count-pill.off {
	border-color: var(--sc-line);
	background: var(--sc-raise);
	color: var(--sc-mute);
}

.cap-chev {
	flex: 0 0 auto;
	color: var(--sc-faint);
	transition: color 0.15s, transform 0.15s var(--sc-ease-out);
}

.cap-row:hover:not(:disabled) .cap-chev {
	color: var(--sc-acid);
	transform: translateX(2px);
}
</style>
