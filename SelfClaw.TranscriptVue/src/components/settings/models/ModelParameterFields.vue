<script setup>
import { computed, ref } from 'vue';
import { ChevronDown, ChevronUp } from 'lucide-vue-next';

const props = defineProps({
	draft: { type: Object, required: true },
	provider: { type: Object, default: null },
});

const levels = ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];

const formatNames = {
	0: 'OpenAI Chat Completions (/v1/chat/completions)',
	1: 'OpenAI Responses (/v1/responses)',
	2: 'Anthropic Messages (/v1/messages)',
};

const availableFormats = computed(() => {
	if (props.provider?.supportedFormats && props.provider.supportedFormats.length > 0) {
		return props.provider.supportedFormats;
	}
	if (props.draft?.apiFormat === 2) return [2];
	return [0, 1];
});

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

const samplingExpanded = ref(
	Boolean(props.draft?.sampling?.temperatureEnabled || props.draft?.sampling?.topPEnabled)
);
</script>

<template>
	<div class="fields-stack">
		<!-- Section 1: Protocol & Capabilities -->
		<section class="param-card">
			<div class="card-head">
				<h4 class="card-title">模型协议与能力</h4>
			</div>

			<div class="dual-grid">
				<!-- API Format: Only show if draft has a provider profile or available formats -->
				<div v-if="draft.hasProfile || availableFormats.length > 1" class="field-item format-field">
					<label class="field-label" for="param-api-format">
						<span class="label-text">API 协议格式</span>
						<span class="label-en">WIRE FORMAT</span>
					</label>
					<div class="select-shell">
						<select id="param-api-format" v-model.number="draft.apiFormat">
							<option v-for="fmt in availableFormats" :key="fmt" :value="fmt">
								{{ formatNames[fmt] || `协议标准 ${fmt}` }}
							</option>
						</select>
					</div>
				</div>

				<div class="field-item">
					<label class="field-label" for="param-reasoning">
						<span class="label-text">扩展思考等级</span>
						<span class="label-en">REASONING</span>
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
						<span class="label-en">VISION</span>
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

		<!-- Section 2: Token Limits -->
		<section class="param-card">
			<div class="card-head">
				<h4 class="card-title">上下文与输出限制</h4>
			</div>

			<div class="dual-grid">
				<div class="field-item">
					<label class="field-label" for="param-ctx">
						<span class="label-text">上下文长度</span>
						<span class="label-en">CONTEXT WINDOW</span>
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
						<span class="label-en">MAX OUTPUT</span>
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

		<!-- Section 3: Advanced Sampling (Collapsible & Compact) -->
		<section class="param-card sampling-accordion" :class="{ 'is-expanded': samplingExpanded }">
			<header
				class="card-head accordion-toggle"
				role="button"
				tabindex="0"
				:aria-expanded="samplingExpanded"
				@click="samplingExpanded = !samplingExpanded"
				@keydown.enter.space.prevent="samplingExpanded = !samplingExpanded"
			>
				<div class="head-lead">
					<h4 class="card-title">高级采样与随机性</h4>
					<span
						class="sampling-badge"
						:class="{ active: draft.sampling.temperatureEnabled || draft.sampling.topPEnabled }"
					>
						{{ (draft.sampling.temperatureEnabled || draft.sampling.topPEnabled) ? '已自定义' : '默认 (未自定义)' }}
					</span>
				</div>
				<button type="button" class="expand-btn" :aria-label="samplingExpanded ? '收起采样参数' : '展开采样参数'">
					<ChevronUp v-if="samplingExpanded" :size="16" :stroke-width="2.2" />
					<ChevronDown v-else :size="16" :stroke-width="2.2" />
				</button>
			</header>

			<div v-show="samplingExpanded" class="sampling-grid">
				<!-- Temperature -->
				<div class="sampling-compact-item" :class="{ 'is-active': draft.sampling.temperatureEnabled }">
					<div class="compact-head">
						<div class="compact-meta">
							<span class="param-name">温度</span>
							<span v-if="draft.sampling.temperatureEnabled" class="param-val">{{ draft.sampling.temperature }}</span>
						</div>
						<label class="custom-switch" title="启用温度自定义">
							<input v-model="draft.sampling.temperatureEnabled" type="checkbox" />
							<span class="switch-slider"></span>
						</label>
					</div>

					<div v-if="draft.sampling.temperatureEnabled" class="slider-controls">
						<div class="slider-row">
							<div class="slider-track-wrap">
								<input
									v-model.number="draft.sampling.temperature"
									type="range"
									min="0"
									max="2"
									step="0.01"
									aria-label="温度滑块"
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
									class="num-input"
								/>
							</div>
						</div>
					</div>
					<div v-else class="compact-hint">使用服务商预设 (默认)</div>
				</div>

				<!-- Top P -->
				<div class="sampling-compact-item" :class="{ 'is-active': draft.sampling.topPEnabled }">
					<div class="compact-head">
						<div class="compact-meta">
							<span class="param-name">核采样 (Top P)</span>
							<span v-if="draft.sampling.topPEnabled" class="param-val">{{ draft.sampling.topP }}</span>
						</div>
						<label class="custom-switch" title="启用核采样自定义">
							<input v-model="draft.sampling.topPEnabled" type="checkbox" />
							<span class="switch-slider"></span>
						</label>
					</div>

					<div v-if="draft.sampling.topPEnabled" class="slider-controls">
						<div class="slider-row">
							<div class="slider-track-wrap">
								<input
									v-model.number="draft.sampling.topP"
									type="range"
									min="0"
									max="1"
									step="0.01"
									aria-label="Top P 滑块"
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
									class="num-input"
								/>
							</div>
						</div>
					</div>
					<div v-else class="compact-hint">使用服务商预设 (默认)</div>
				</div>
			</div>
		</section>
	</div>
</template>

<style scoped>
@import '../../../styles/model-configuration-fields.css';

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
	align-items: center;
	justify-content: space-between;
	margin-bottom: 14px;
	padding-bottom: 10px;
	border-bottom: 1px solid var(--sc-line);
}

.card-title {
	margin: 0;
	color: var(--sc-soft);
	font-size: var(--fs-12);
	font-weight: 600;
}

/* Accordion */
.sampling-accordion .card-head.accordion-toggle {
	cursor: pointer;
	margin-bottom: 0;
	padding-bottom: 0;
	border-bottom: none;
	user-select: none;
}

.sampling-accordion.is-expanded .card-head.accordion-toggle {
	margin-bottom: 14px;
	padding-bottom: 10px;
	border-bottom: 1px solid var(--sc-line);
}

.head-lead {
	display: flex;
	align-items: center;
	gap: 10px;
}

.sampling-badge {
	display: inline-flex;
	align-items: center;
	padding: 2px 8px;
	border-radius: 99px;
	background: var(--sc-raise);
	border: 1px solid var(--sc-line);
	color: var(--sc-mute);
	font-size: var(--fs-10);
	font-family: var(--sc-mono);
}

.sampling-badge.active {
	background: var(--sc-acid-soft);
	border-color: var(--sc-acid);
	color: var(--sc-acid);
	font-weight: 600;
}

.expand-btn {
	display: flex;
	align-items: center;
	justify-content: center;
	background: transparent;
	border: none;
	color: var(--sc-mute);
	padding: 4px;
	border-radius: 6px;
	cursor: pointer;
	transition: color 0.16s;
}

.expand-btn:hover {
	color: var(--sc-text);
}

.sampling-grid {
	display: grid;
	grid-template-columns: repeat(2, minmax(0, 1fr));
	gap: 14px;
}

.sampling-compact-item {
	padding: 12px 14px;
	border: 1px solid var(--sc-line);
	border-radius: 9px;
	background: var(--sc-panel);
	transition: all 0.2s var(--sc-ease-out);
	display: flex;
	flex-direction: column;
	gap: 8px;
}

.sampling-compact-item.is-active {
	border-color: color-mix(in srgb, var(--sc-acid) 35%, transparent);
	box-shadow: 0 4px 16px color-mix(in srgb, var(--sc-acid) 5%, transparent);
}

.compact-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
}

.compact-meta {
	display: flex;
	align-items: baseline;
	gap: 8px;
}

.param-name {
	font-size: var(--fs-13);
	font-weight: 600;
	color: var(--sc-text);
}

.param-val {
	font-family: var(--sc-mono);
	font-size: var(--fs-12);
	font-weight: 600;
	color: var(--sc-acid);
}

.compact-hint {
	font-size: var(--fs-11);
	color: var(--sc-faint);
	padding: 4px 0 2px;
}

/* Custom switch */
.custom-switch {
	position: relative;
	display: inline-block;
	width: 36px;
	height: 18px;
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
	height: 12px;
	width: 12px;
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
	gap: 8px;
}

.slider-row {
	display: grid;
	grid-template-columns: minmax(0, 1fr) 64px;
	align-items: center;
	gap: 12px;
}

.slider-track-wrap {
	display: flex;
	align-items: center;
	width: 100%;
}

.cyber-range {
	width: 100%;
	height: 5px;
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
	width: 14px;
	height: 14px;
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
	height: 28px;
	padding: 0 6px;
	border: 1px solid var(--sc-line-2);
	border-radius: 6px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: var(--sc-mono);
	font-size: var(--fs-12);
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
	padding: 2px 7px;
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

.format-field {
	grid-column: 1 / -1;
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
	.dual-grid,
	.sampling-grid {
		grid-template-columns: 1fr;
	}
}
</style>
