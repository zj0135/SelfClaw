<script setup>
defineProps({ draft: { type: Object, required: true } });
const prices = [
	{ key: 'priceInPerMTok', label: '输入 Token', en: 'INPUT TOKENS' },
	{ key: 'priceOutPerMTok', label: '输出 Token', en: 'OUTPUT TOKENS' },
	{ key: 'priceCacheReadPerMTok', label: '缓存命中读取', en: 'CACHE READ' },
	{ key: 'priceCacheWritePerMTok', label: '缓存写入创建', en: 'CACHE WRITE' },
];
</script>

<template>
	<div class="meta-stack">
		<!-- Identity -->
		<section class="param-card">
			<div class="card-head">
				<h4 class="card-title">基本标识</h4>
			</div>
			<div class="field-item">
				<label class="field-label" for="meta-name">
					<span class="label-text">显示名称</span>
				</label>
				<div class="input-shell">
					<input id="meta-name" v-model.trim="draft.name" required maxlength="256" placeholder="例如 DeepSeek V4.1 Flash" />
				</div>
			</div>
		</section>

		<!-- Economics -->
		<section class="param-card">
			<div class="card-head">
				<h4 class="card-title">计费基准</h4>
			</div>

			<div class="pricing-matrix">
				<div v-for="p in prices" :key="p.key" class="price-cell">
					<div class="price-cell-head">
						<span class="price-label">{{ p.label }}</span>
					</div>
					<div class="currency-input-wrap">
						<span class="currency-symbol">$</span>
						<input
							v-model.number="draft[p.key]"
							type="number"
							min="0"
							step="any"
							placeholder="0.00"
							:aria-label="p.label"
							class="currency-input"
						/>
					</div>
				</div>
			</div>
		</section>

		<!-- Notes -->
		<section class="param-card">
			<div class="card-head">
				<h4 class="card-title">备注与描述</h4>
			</div>
			<div class="field-item">
				<textarea
					v-model="draft.description"
					rows="3"
					maxlength="4000"
					placeholder="输入针对此模型的特有说明、用量建议或限制..."
					class="note-textarea"
				></textarea>
			</div>
		</section>
	</div>
</template>

<style scoped>
@import '../../styles/model-configuration-fields.css';

.meta-stack {
	display: flex;
	flex-direction: column;
	gap: 16px;
}

.param-card {
	position: relative;
	padding: 16px 18px;
	border: 1px solid var(--sc-line);
	border-radius: 12px;
	background: color-mix(in srgb, var(--sc-panel) 60%, var(--sc-raise));
	transition: border-color 0.18s;
}

.param-card:hover {
	border-color: var(--sc-line-2);
}

.card-head {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
	margin-bottom: 14px;
	padding-bottom: 10px;
	border-bottom: 1px solid var(--sc-line);
}

.card-kicker {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.18em;
}

.card-title {
	display: flex;
	align-items: center;
	gap: 8px;
	margin: 0;
	color: var(--sc-soft);
	font-size: var(--fs-12);
	font-weight: 600;
}

.rate-note {
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.1em;
}

.field-item {
	display: flex;
	flex-direction: column;
	gap: 6px;
}

.field-label {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
	color: var(--sc-text);
	font-size: var(--fs-12);
	font-weight: 600;
}

.label-en {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.12em;
}

.input-shell input {
	width: 100%;
	height: 38px;
	padding: 0 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-13);
	outline: none;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.input-shell input:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

.pricing-matrix {
	display: grid;
	grid-template-columns: repeat(2, minmax(0, 1fr));
	gap: 12px;
}

.price-cell {
	display: flex;
	flex-direction: column;
	gap: 6px;
	padding: 10px 12px;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
}

.price-cell-head {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
}

.price-label {
	font-size: var(--fs-115);
	font-weight: 600;
	color: var(--sc-soft);
}

.price-en {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-85, 9px);
	letter-spacing: 0.1em;
}

.currency-input-wrap {
	position: relative;
	display: flex;
	align-items: center;
}

.currency-symbol {
	position: absolute;
	left: 10px;
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-weight: 600;
	font-size: var(--fs-13);
	pointer-events: none;
}

.currency-input {
	width: 100%;
	height: 34px;
	padding-left: 24px;
	padding-right: 10px;
	border: 1px solid var(--sc-line-2);
	border-radius: 6px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: var(--sc-mono);
	font-size: var(--fs-125);
	outline: none;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.currency-input:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

.note-textarea {
	width: 100%;
	padding: 10px 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-125);
	line-height: 1.5;
	outline: none;
	resize: vertical;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.note-textarea:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

@media (max-width: 540px) {
	.pricing-matrix {
		grid-template-columns: 1fr;
	}
}
</style>
