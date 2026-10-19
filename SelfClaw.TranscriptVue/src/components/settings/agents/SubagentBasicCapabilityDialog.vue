<script setup>
import { computed, onMounted, reactive, ref, watch } from 'vue';
import { Check, ChevronDown, LoaderCircle, X, Workflow, Sparkles } from 'lucide-vue-next';
import { useHostBridge } from '../../../composables/hostBridge.js';

const props = defineProps({
	open: { type: Boolean, default: false },
	subagent: { type: Object, default: null },
	saving: { type: Boolean, default: false },
});

const emit = defineEmits(['close', 'save']);

const { request } = useHostBridge();

const form = reactive({
	name: '',
	description: '',
	modelProfileId: '',
	toolPolicy: 'read-only',
	maxRunSeconds: 900,
	instructions: '',
});

const enabledModels = ref([]);

const runTimePresets = [
	{ label: '5m (300s)', val: 300 },
	{ label: '15m (900s)', val: 900 },
	{ label: '30m (1800s)', val: 1800 },
	{ label: '60m (3600s)', val: 3600 },
];

onMounted(async () => {
	try {
		const payload = await request('ai-providers/list-enabled-models');
		enabledModels.value = payload.models || [];
	} catch {
		enabledModels.value = [];
	}
});

watch(
	() => props.subagent,
	(subagent) => {
		if (subagent) {
			form.name = subagent.name;
			form.description = subagent.description;
			form.modelProfileId = subagent.modelProfileId || '';
			form.toolPolicy = subagent.toolPolicy;
			form.maxRunSeconds = subagent.maxRunSeconds;
			form.instructions = subagent.instructions;
		}
	},
	{ immediate: true, deep: true }
);

const isDirty = computed(() => {
	if (!props.subagent) return false;
	return (
		form.name !== props.subagent.name ||
		form.description !== props.subagent.description ||
		form.modelProfileId !== (props.subagent.modelProfileId || '') ||
		form.toolPolicy !== props.subagent.toolPolicy ||
		Number(form.maxRunSeconds) !== props.subagent.maxRunSeconds ||
		form.instructions !== props.subagent.instructions
	);
});

const maxRunValid = computed(() => {
	const value = Number(form.maxRunSeconds);
	return Number.isInteger(value) && value >= 30 && value <= 3600;
});

const canSubmit = computed(
	() =>
		isDirty.value &&
		!props.saving &&
		form.name.trim().length > 0 &&
		form.description.trim().length > 0 &&
		form.instructions.trim().length > 0 &&
		maxRunValid.value,
);

function submit() {
	if (!canSubmit.value) return;
	emit('save', {
		name: form.name.trim(),
		description: form.description.trim(),
		modelProfileId: form.modelProfileId || null,
		toolPolicy: form.toolPolicy,
		maxRunSeconds: Number(form.maxRunSeconds),
		instructions: form.instructions,
	});
}
</script>

<template>
	<Teleport to="body">
		<div v-if="open" class="overlay sc-root" @click.self="$emit('close')">
			<div class="dialog-frame" @click.stop>
				<header class="dialog-head">
					<div class="dh-icon-box">
						<Workflow :size="20" :stroke-width="2" />
					</div>
					<div class="dh-meta">
						<h2>子代理基本能力配置</h2>
					</div>
					<button class="close-btn" type="button" title="关闭" aria-label="关闭" @click="$emit('close')">
						<X :size="16" :stroke-width="2" />
					</button>
				</header>

				<div class="dialog-body scroll">
					<div class="form-grid">
						<div class="field">
							<label class="fl" for="subagent-basic-name">
								<span>子代理名称</span>
							</label>
							<input id="subagent-basic-name" v-model="form.name" class="input" type="text" maxlength="120" />
						</div>

						<div class="field">
							<label class="fl" for="subagent-basic-model">
								<span>模型配置</span>
							</label>
							<div class="select-wrap">
								<select id="subagent-basic-model" v-model="form.modelProfileId" aria-label="模型配置">
									<option value="">继承父级主对话模型 (Inherit)</option>
									<option v-for="model in enabledModels" :key="model.modelProfileId" :value="model.modelProfileId">
										{{ model.name }} · {{ model.providerName }}
									</option>
								</select>
								<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
							</div>
						</div>

						<div class="field span-2">
							<label class="fl" for="subagent-basic-desc">
								<span>职责描述</span>
							</label>
							<input
								id="subagent-basic-desc"
								v-model="form.description"
								class="input"
								type="text"
								placeholder="子代理职责定位与任务分派指引..."
							/>
						</div>

						<div class="field">
							<label class="fl" for="subagent-basic-tools">
								<span>工具执行策略</span>
							</label>
							<div class="select-wrap">
								<select id="subagent-basic-tools" v-model="form.toolPolicy" aria-label="工具策略">
									<option value="none">无工具 (none) - 仅纯文本推理</option>
									<option value="read-only">只读工具 (read-only) - 探索调研</option>
									<option value="system">系统工具 (system) - 全量读写执行</option>
								</select>
								<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
							</div>
						</div>

						<div class="field">
							<label class="fl" for="subagent-basic-maxrun">
								<span>单次最大执行秒数 (30~3600s)</span>
							</label>
							<div class="input-wrap">
								<input
									id="subagent-basic-maxrun"
									v-model.number="form.maxRunSeconds"
									class="input mono"
									type="number"
									min="30"
									max="3600"
									step="1"
								/>
							</div>
							<div class="preset-pills">
								<button
									v-for="p in runTimePresets"
									:key="p.val"
									type="button"
									class="preset-pill"
									:class="{ active: form.maxRunSeconds === p.val }"
									@click="form.maxRunSeconds = p.val"
								>
									{{ p.label }}
								</button>
							</div>
						</div>

						<div class="field span-2">
							<label class="fl" for="subagent-basic-instructions">
								<span>系统指令 (Prompt)</span>
							</label>
							<textarea
								id="subagent-basic-instructions"
								v-model="form.instructions"
								class="input mono instructions"
								rows="7"
								placeholder="设定子代理的任务指引、边界约束及工具调用规范..."
							></textarea>
						</div>
					</div>
				</div>

				<footer class="dialog-foot">
					<button class="btn btn-secondary" type="button" @click="$emit('close')">取消</button>
					<button class="btn btn-primary" type="button" :disabled="!canSubmit || saving" @click="submit">
						<LoaderCircle v-if="saving" :size="14" :stroke-width="2.2" class="spin-ico" aria-hidden="true" />
						<Check v-else :size="14" :stroke-width="2.4" aria-hidden="true" />
						<span>{{ saving ? '保存中…' : '保存变更' }}</span>
					</button>
				</footer>
			</div>
		</div>
	</Teleport>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.overlay {
	position: fixed;
	inset: 0;
	z-index: 1200;
	display: grid;
	place-items: center;
	padding: 24px;
	background: var(--overlay-heavy);
	backdrop-filter: blur(12px);
	animation: sc-fade 0.18s var(--sc-ease-out);
	font-family: var(--sc-sans);
}

.dialog-frame {
	position: relative;
	display: flex;
	flex-direction: column;
	width: min(640px, calc(100vw - 32px));
	max-height: min(720px, calc(100vh - 48px));
	border: 1px solid var(--sc-line-2);
	border-radius: 16px;
	background: var(--sc-panel);
	color: var(--sc-text);
	box-shadow: 0 32px 100px rgba(var(--shadow-ink), 0.35), 0 4px 20px rgba(var(--shadow-ink), 0.1);
	overflow: hidden;
	animation: sc-pop 0.22s var(--sc-ease-spring);
}

.dialog-head {
	display: flex;
	align-items: center;
	gap: 14px;
	padding: 22px 24px 18px;
	border-bottom: 1px solid var(--sc-line);
	background: linear-gradient(to bottom, var(--sc-raise), transparent);
}

.dh-icon-box {
	display: grid;
	place-items: center;
	width: 42px;
	height: 42px;
	border: 1px solid var(--sc-line-2);
	border-radius: 10px;
	background: var(--sc-panel);
	color: var(--sc-acid);
	box-shadow: 0 4px 12px color-mix(in srgb, var(--sc-acid) 12%, transparent);
}

.dh-meta {
	flex: 1;
	min-width: 0;
}

.dh-kicker {
	margin-bottom: 3px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 650;
	letter-spacing: 0.2em;
}

.dialog-head h2 {
	margin: 0;
	font-family: var(--sc-display);
	font-size: var(--fs-18);
	font-weight: 650;
	color: var(--sc-text);
}

.close-btn {
	display: grid;
	place-items: center;
	width: 32px;
	height: 32px;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-mute);
	cursor: pointer;
	transition: all 0.16s var(--sc-ease-out);
}

.close-btn:hover {
	border-color: var(--sc-line-2);
	background: var(--sc-hover);
	color: var(--sc-text);
	transform: scale(1.05);
}

.dialog-body {
	flex: 1;
	min-height: 0;
	overflow-y: auto;
	padding: 22px 24px;
}

.scroll::-webkit-scrollbar {
	width: 7px;
}

.scroll::-webkit-scrollbar-thumb {
	background: var(--sc-line-2);
	border-radius: 99px;
}

.form-grid {
	display: grid;
	grid-template-columns: 1fr 1fr;
	gap: 16px;
}

.field {
	display: flex;
	flex-direction: column;
	gap: 6px;
}

.field.span-2 {
	grid-column: span 2;
}

.fl {
	display: flex;
	align-items: baseline;
	justify-content: space-between;
	color: var(--sc-text);
	font-size: var(--fs-12);
	font-weight: 600;
}

.sub-tag {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.12em;
}

.input {
	width: 100%;
	padding: 0 12px;
	height: 38px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-panel);
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-13);
	outline: none;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.input.mono {
	font-family: var(--sc-mono);
	font-size: var(--fs-12);
}

.input:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

.preset-pills {
	display: flex;
	gap: 6px;
	flex-wrap: wrap;
	margin-top: 4px;
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
	cursor: pointer;
	transition: all 0.15s;
}

.preset-pill:hover {
	border-color: var(--sc-acid);
	color: var(--sc-text);
}

.preset-pill.active {
	border-color: var(--sc-acid);
	background: var(--sc-acid-soft);
	color: var(--sc-acid);
	font-weight: 600;
}

.instructions {
	height: auto;
	min-height: 150px;
	padding: 10px 12px;
	line-height: 1.6;
	resize: vertical;
}

.select-wrap {
	position: relative;
	width: 100%;
}

.select-wrap select {
	width: 100%;
	height: 38px;
	padding: 0 34px 0 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	appearance: none;
	background: var(--sc-panel);
	color: var(--sc-text);
	font-family: inherit;
	font-size: var(--fs-13);
	cursor: pointer;
	outline: none;
	transition: border-color 0.16s, box-shadow 0.16s;
}

.select-wrap select:focus {
	border-color: var(--sc-acid);
	box-shadow: 0 0 0 2px var(--sc-acid-soft);
}

.chev {
	position: absolute;
	top: 50%;
	right: 12px;
	color: var(--sc-faint);
	pointer-events: none;
	transform: translateY(-50%);
}

.dialog-foot {
	display: flex;
	align-items: center;
	justify-content: flex-end;
	gap: 10px;
	padding: 16px 24px;
	border-top: 1px solid var(--sc-line);
	background: var(--sc-raise);
}

.btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 7px;
	height: 38px;
	padding: 0 18px;
	border: 1px solid transparent;
	border-radius: 9px;
	font-family: inherit;
	font-size: var(--fs-13);
	font-weight: 600;
	cursor: pointer;
	transition: all 0.18s var(--sc-ease-spring);
}

.btn-secondary {
	border-color: var(--sc-line-2);
	background: var(--sc-panel);
	color: var(--sc-soft);
}

.btn-secondary:hover:not(:disabled) {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
	color: var(--sc-text);
}

.btn-primary {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.08);
}

.btn-primary:hover:not(:disabled) {
	transform: translateY(-1px);
	box-shadow: 0 3px 8px rgba(var(--shadow-ink), 0.12);
}

.btn-primary:disabled {
	opacity: 0.5;
	cursor: not-allowed;
	transform: none;
}

.spin-ico {
	animation: sc-spin 0.8s linear infinite;
}

@media (max-width: 600px) {
	.form-grid {
		grid-template-columns: 1fr;
	}
	.field.span-2 {
		grid-column: span 1;
	}
}
</style>
