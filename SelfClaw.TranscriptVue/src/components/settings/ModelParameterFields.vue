<script setup>
defineProps({ draft: { type: Object, required: true } });
const levels = ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];

const tempPresets = [
	{ label: '0.2 严谨', val: 0.2 },
	{ label: '0.7 平衡', val: 0.7 },
	{ label: '1.2 发散', val: 1.2 },
];

const topPPresets = [
	{ label: '0.5', val: 0.5 },
	{ label: '0.8', val: 0.8 },
	{ label: '0.95', val: 0.95 },
	{ label: '1.0', val: 1.0 },
];

const ctxPresets = [
	{ label: '32K', val: 32768 },
	{ label: '128K', val: 131072 },
	{ label: '200K', val: 204800 },
	{ label: '1M', val: 1048576 },
	{ label: '2M', val: 2097152 },
];

const outPresets = [
	{ label: '4K', val: 4096 },
	{ label: '8K', val: 8192 },
	{ label: '16K', val: 16384 },
	{ label: '64K', val: 65536 },
	{ label: '128K', val: 131072 },
];

function setTemp(draft, val) {
	draft.sampling.temperatureEnabled = true;
	draft.sampling.temperature = val;
}

function setTopP(draft, val) {
	draft.sampling.topPEnabled = true;
	draft.sampling.topP = val;
}
</script>

<template>
	<div class="fields-stack">
		<!-- Section 1: Sampling & Creativity -->
		<section class="param-card">
			<div class="card-head">
				<h4 class="card-title">采样与随机性</h4>
			</div>

			<div class="sampling-group">
				<!-- Temperature -->
				<div class="sampling-item" :class="{ 'is-active': draft.sampling.temperatureEnabled }">
					<div class="sampling-head">
						<div class="sampling-meta">
							<span class="param-name">温度</span>
						</div>
						<div class="sampling-toggle">
							<label class="custom-switch" title="启用温度自定义">
								<input v-model="draft.sampling.temperatureEnabled" type="checkbox" />
								<span class="switch-slider"></span>
							</label>
						</div>
					</div>

					<div class="slider-controls" :class="{ disabled: !draft.sampling.temperatureEnabled }">
						<div class="slider-row">
							<div class="slider-track-wrap">
								<input
									v-model.number="draft.sampling.temperature"
									type="range"
									min="0"
									max="2"
									step="0.01"
									aria-label="温度滑块"
									:disabled="!draft.sampling.temperatureEnabled"
									class="cyber-range"
								/>
							</div>
							<div class="num-box">
								<input
									v-model.number="draft.sampling.temperature"
									type="number"
									min="0"
									max="2"
									step="0.01"
									required
									aria-label="温度"
									:disabled="!draft.sampling.temperatureEnabled"
									class="num-input"
								/>
							</div>
						</div>
						<div class="preset-pills">
							<button
								v-for="p in tempPresets"
								:key="p.val"
								type="button"
								class="preset-pill"
								:class="{ active: draft.sampling.temperature === p.val && draft.sampling.temperatureEnabled }"
								@click="setTemp(draft, p.val)"
							>
								{{ p.label }}
							</button>
						</div>
					</div>
				</div>

				<!-- Top P -->
				<div class="sampling-item" :class="{ 'is-active': draft.sampling.topPEnabled }">
					<div class="sampling-head">
						<div class="sampling-meta">
							<span class="param-name">核采样</span>
						</div>
						<div class="sampling-toggle">
							<label class="custom-switch" title="启用核采样自定义">
								<input v-model="draft.sampling.topPEnabled" type="checkbox" />
								<span class="switch-slider"></span>
							</label>
						</div>
					</div>

					<div class="slider-controls" :class="{ disabled: !draft.sampling.topPEnabled }">
						<div class="slider-row">
							<div class="slider-track-wrap">
								<input
									v-model.number="draft.sampling.topP"
									type="range"
									min="0"
									max="1"
									step="0.01"
									aria-label="Top P 滑块"
									:disabled="!draft.sampling.topPEnabled"
									class="cyber-range"
								/>
							</div>
							<div class="num-box">
								<input
									v-model.number="draft.sampling.topP"
									type="number"
									min="0"
									max="1"
									step="0.01"
									required
									aria-label="Top P"
									:disabled="!draft.sampling.topPEnabled"
									class="num-input"
								/>
							</div>
						</div>
						<div class="preset-pills">
							<button
								v-for="p in topPPresets"
								:key="p.val"
								type="button"
								class="preset-pill"
								:class="{ active: draft.sampling.topP === p.val && draft.sampling.topPEnabled }"
								@click="setTopP(draft, p.val)"
							>
								{{ p.label }}
							</button>
						</div>
					</div>
				</div>
			</div>
		</section>

		<!-- Section 2: Reasoning & Capabilities -->
		<section class="param-card">
			<div class="card-head">
				<h4 class="card-title">模型能力与扩展</h4>
			</div>

			<div class="dual-grid">
				<div class="field-item">
					<label class="field-label" for="param-reasoning">
						<span class="label-text">扩展思考等级</span>
					</label>
					<div class="select-shell">
						<select id="param-reasoning" v-model="draft.reasoningEffort">
							<option value="">提供商默认 (Default)</option>
							<option v-for="level in levels" :key="level" :value="level">
								{{ level.toUpperCase() }}
							</option>
						</select>
					</div>
				</div>

				<div class="field-item">
					<label class="field-label" for="param-multimodal">
						<span class="label-text">多模态 · 图像输入</span>
					</label>
					<div class="select-shell">
						<select id="param-multimodal" v-model="draft.isMultimodal">
							<option :value="null">未标记 (Unspecified)</option>
							<option :value="true">支持 (Enabled)</option>
							<option :value="false">不支持 (Disabled)</option>
						</select>
					</div>
				</div>
			</div>
		</section>

		<!-- Section 3: Token Limits -->
		<section class="param-card">
			<div class="card-head">
				<h4 class="card-title">上下文与输出限制</h4>
			</div>

			<div class="dual-grid">
				<div class="field-item">
					<label class="field-label" for="param-ctx">
						<span class="label-text">上下文长度</span>
					</label>
					<div class="input-shell">
						<input
							id="param-ctx"
							v-model.number="draft.contextLength"
							type="number"
							min="1"
							max="2147483647"
							step="1"
							placeholder="未设置 (默认)"
						/>
					</div>
					<div class="preset-pills micro">
						<button
							v-for="p in ctxPresets"
							:key="p.val"
							type="button"
							class="preset-pill"
							:class="{ active: draft.contextLength === p.val }"
							@click="draft.contextLength = p.val"
						>
							{{ p.label }}
						</button>
					</div>
				</div>

				<div class="field-item">
					<label class="field-label" for="param-maxout">
						<span class="label-text">最大输出</span>
					</label>
					<div class="input-shell">
						<input
							id="param-maxout"
							v-model.number="draft.maxOutputTokens"
							type="number"
							min="1"
							max="2147483647"
							step="1"
							placeholder="未设置 (默认)"
						/>
					</div>
					<div class="preset-pills micro">
						<button
							v-for="p in outPresets"
							:key="p.val"
							type="button"
							class="preset-pill"
							:class="{ active: draft.maxOutputTokens === p.val }"
							@click="draft.maxOutputTokens = p.val"
						>
							{{ p.label }}
						</button>
					</div>
				</div>
			</div>
		</section>
	</div>
</template>

<style scoped>
@import '../../styles/model-configuration-fields.css';

.fields-stack {
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
	transition: border-color 0.18s, box-shadow 0.18s;
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
	margin: 0;
	color: var(--sc-soft);
	font-size: var(--fs-12);
	font-weight: 600;
}

.sampling-group {
	display: flex;
	flex-direction: column;
	gap: 14px;
}

.sampling-item {
	padding: 12px 14px;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	background: var(--sc-panel);
	transition: all 0.2s var(--sc-ease-out);
}

.sampling-item.is-active {
	border-color: color-mix(in srgb, var(--sc-acid) 35%, transparent);
	box-shadow: 0 4px 16px color-mix(in srgb, var(--sc-acid) 5%, transparent);
}

.sampling-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	margin-bottom: 10px;
}

.sampling-meta {
	display: flex;
	align-items: baseline;
	gap: 8px;
}

.param-name {
	font-size: var(--fs-13);
	font-weight: 600;
	color: var(--sc-text);
}

.param-en {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-95);
	letter-spacing: 0.14em;
}

/* Custom switch */
.custom-switch {
	position: relative;
	display: inline-block;
	width: 38px;
	height: 20px;
	cursor: pointer;
}

.custom-switch input {
	opacity: 0;
	width: 0;
	height: 0;
}

.switch-slider {
	position: absolute;
	inset: 0;
	background: var(--sc-raise);
	border: 1px solid var(--sc-line-2);
	border-radius: 99px;
	transition: all 0.22s var(--sc-ease-out);
}

.switch-slider::before {
	position: absolute;
	content: '';
	height: 14px;
	width: 14px;
	left: 2px;
	top: 2px;
	background: var(--sc-mute);
	border-radius: 50%;
	transition: all 0.22s var(--sc-ease-spring);
}

.custom-switch input:checked + .switch-slider {
	background: var(--sc-acid);
	border-color: var(--sc-acid);
}

.custom-switch input:checked + .switch-slider::before {
	transform: translateX(18px);
	background: var(--sc-acid-ink);
}

.slider-controls {
	display: flex;
	flex-direction: column;
	gap: 10px;
	transition: opacity 0.2s, filter 0.2s;
}

.slider-controls.disabled {
	opacity: 0.45;
	filter: grayscale(0.6);
	pointer-events: none;
}

.slider-row {
	display: grid;
	grid-template-columns: minmax(0, 1fr) 80px;
	align-items: center;
	gap: 16px;
}

.slider-track-wrap {
	display: flex;
	align-items: center;
	width: 100%;
}

.cyber-range {
	width: 100%;
	height: 6px;
	border-radius: 6px;
	background: var(--sc-line-2);
	outline: none;
	-webkit-appearance: none;
	appearance: none;
	cursor: pointer;
	transition: background 0.18s;
}

.cyber-range::-webkit-slider-thumb {
	-webkit-appearance: none;
	appearance: none;
	width: 16px;
	height: 16px;
	border-radius: 50%;
	background: var(--sc-acid);
	border: 2px solid var(--sc-panel);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.25);
	cursor: grab;
	transition: transform 0.14s var(--sc-ease-spring), box-shadow 0.14s;
}

.cyber-range::-webkit-slider-thumb:hover {
	transform: scale(1.15);
	box-shadow: 0 2px 6px rgba(var(--shadow-ink), 0.35);
}

.num-box {
	position: relative;
}

.num-input {
	width: 100%;
	height: 32px;
	padding: 0 8px;
	border: 1px solid var(--sc-line-2);
	border-radius: 6px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: var(--sc-mono);
	font-size: var(--fs-125);
	font-weight: 600;
	text-align: center;
	outline: none;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.num-input:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

.preset-pills {
	display: flex;
	flex-wrap: wrap;
	gap: 6px;
}

.preset-pills.micro {
	margin-top: 6px;
}

.preset-pill {
	display: inline-flex;
	align-items: center;
	padding: 3px 8px;
	border: 1px solid var(--sc-line);
	border-radius: 99px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-weight: 500;
	cursor: pointer;
	transition: all 0.15s var(--sc-ease-out);
}

.preset-pill:hover {
	border-color: var(--sc-acid);
	color: var(--sc-text);
	background: var(--sc-hover);
}

.preset-pill.active {
	border-color: var(--sc-acid);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	font-weight: 600;
}

/* Dual grid */
.dual-grid {
	display: grid;
	grid-template-columns: repeat(2, minmax(0, 1fr));
	gap: 16px;
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

.label-en,
.unit-tag {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.12em;
}

.select-shell select,
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

.select-shell select:focus,
.input-shell input:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

@media (max-width: 600px) {
	.dual-grid {
		grid-template-columns: 1fr;
	}
}
</style>
