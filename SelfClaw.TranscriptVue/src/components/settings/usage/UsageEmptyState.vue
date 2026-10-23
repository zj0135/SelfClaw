<script setup>
import { ChartColumn } from 'lucide-vue-next';
</script>

<template>
	<section class="usage-card empty-card" aria-live="polite">
		<div class="empty-icon">
			<ChartColumn :size="18" :stroke-width="1.8" aria-hidden="true" />
		</div>
		<div class="empty-copy">

			<h3 class="usage-card-title">还没有用量记录</h3>
			<p class="empty-note">
				开始一次对话后，这里会显示终身摘要、52 周热力图、每日趋势与模型排行。
			</p>
			<span class="empty-status">统计来自本地 turn_usage · 未上报的 CLI 回合不计入</span>
		</div>
	</section>
</template>

<style scoped>
@import '../../../styles/usage-dashboard.css';

.empty-card {
	display: flex;
	align-items: flex-start;
	gap: 14px;
}

.empty-icon {
	display: grid;
	place-items: center;
	width: 34px;
	height: 34px;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	background: var(--sc-raise);
	color: var(--sc-acid);
}

.empty-copy {
	display: flex;
	flex-direction: column;
	gap: 6px;
	min-width: 0;
}

.empty-note {
	margin: 0;
	max-width: 54ch;
	color: var(--sc-mute);
	font-size: var(--fs-12);
	line-height: 1.65;
}

.empty-status {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.04em;
}
</style>
