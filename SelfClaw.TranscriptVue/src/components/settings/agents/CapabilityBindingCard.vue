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
	<section class="cap-card">
		<div class="cap-card-head">
			<div>
				<h3 class="card-title">能力绑定矩阵</h3>
			</div>
		</div>

		<div class="rows-container">
			<button
				v-for="section in sections"
				:key="section.key"
				type="button"
				class="cap-row"
				:disabled="!section.isBasic && !section.items.length"
				@click="emit('open', section)"
			>
				<span class="cap-icon" aria-hidden="true">
					<component :is="section.icon" :size="17" :stroke-width="2" />
				</span>
				<div class="cap-main">
					<div class="cap-titleline">
						<span class="cap-name">{{ section.title }}</span>
					</div>
				</div>
				<span v-if="!section.isBasic" class="count-pill" :class="{ off: boundCount(section) === 0 }">
					<span class="count-led" v-if="boundCount(section) > 0"></span>
					{{ boundCount(section) }} / {{ section.items.length }}
				</span>
				<ChevronRight :size="16" :stroke-width="2.2" class="cap-chev" aria-hidden="true" />
			</button>
		</div>
	</section>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.cap-card {
	overflow: hidden;
	border: 1px solid var(--sc-line);
	border-radius: 14px;
	background: var(--sc-panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.04);
	transition: all 0.2s var(--sc-ease-out);
}

.cap-card:hover {
	border-color: var(--sc-line-2);
	box-shadow: 0 8px 24px rgba(var(--shadow-ink), 0.06);
}

.cap-card-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	padding: 18px 22px;
	border-bottom: 1px solid var(--sc-line);
	background: linear-gradient(to bottom, var(--sc-raise), transparent);
}

.card-kicker {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.18em;
	margin-bottom: 3px;
}

.card-title {
	margin: 0;
	font-family: var(--sc-display);
	font-size: var(--fs-16, 16px);
	font-weight: 650;
	color: var(--sc-text);
}

.card-sub-tag {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.12em;
}

.rows-container {
	display: flex;
	flex-direction: column;
}

.cap-row {
	display: flex;
	align-items: center;
	width: 100%;
	gap: 14px;
	padding: 14px 22px;
	border: 0;
	border-bottom: 1px solid var(--sc-line);
	background: transparent;
	color: inherit;
	font: inherit;
	text-align: left;
	cursor: pointer;
	transition: all 0.16s var(--sc-ease-out);
}

.cap-row:last-child {
	border-bottom: 0;
}

.cap-row:hover:not(:disabled) {
	background: color-mix(in srgb, var(--sc-acid) 4%, var(--sc-panel));
}

.cap-row:disabled {
	cursor: not-allowed;
	opacity: 0.5;
}

.cap-icon {
	display: grid;
	width: 40px;
	height: 40px;
	flex: 0 0 auto;
	place-items: center;
	border: 1px solid var(--sc-line-2);
	border-radius: 10px;
	background: var(--sc-raise);
	color: var(--sc-soft);
	transition: all 0.2s var(--sc-ease-spring);
}

.cap-row:hover:not(:disabled) .cap-icon {
	border-color: var(--sc-acid);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	transform: translateY(-1px) scale(1.05);
	box-shadow: 0 4px 12px color-mix(in srgb, var(--sc-acid) 18%, transparent);
}

.cap-main {
	display: flex;
	flex-direction: column;
	gap: 3px;
	min-width: 0;
	flex: 1;
}

.cap-titleline {
	display: flex;
	align-items: center;
	gap: 8px;
}

.cap-name {
	font-size: var(--fs-135);
	font-weight: 620;
	color: var(--sc-text);
}

.cap-kicker {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 600;
	letter-spacing: 0.14em;
}

.cap-hint {
	overflow: hidden;
	color: var(--sc-mute);
	font-size: var(--fs-12);
	text-overflow: ellipsis;
	white-space: nowrap;
}

.count-pill {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	padding: 3px 9px;
	border: 1px solid color-mix(in srgb, var(--sc-acid) 35%, transparent);
	border-radius: 99px;
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-size: var(--fs-105);
	font-weight: 650;
	letter-spacing: 0.04em;
	transition: all 0.2s var(--sc-ease-spring);
}

.count-led {
	width: 5px;
	height: 5px;
	border-radius: 50%;
	background: var(--sc-acid);
}

.count-pill.off {
	border-color: var(--sc-line-2);
	background: var(--sc-raise);
	color: var(--sc-mute);
}

.cap-chev {
	color: var(--sc-faint);
	transition: all 0.18s var(--sc-ease-out);
}

.cap-row:hover:not(:disabled) .cap-chev {
	color: var(--sc-acid);
	transform: translateX(3px);
}
</style>
