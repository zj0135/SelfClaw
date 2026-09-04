<script setup>
import { computed, onMounted, reactive, ref, watch } from 'vue';
import { Check, ChevronDown, LoaderCircle, X } from 'lucide-vue-next';
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
		<div v-if="open" class="overlay" @click="$emit('close')">
			<div class="dialog basic-dialog" @click.stop>
				<header class="dialog-head">
					<div>
						<div class="dh-kicker">BASIC CAPABILITY</div>
						<h2>基本能力</h2>
					</div>
					<button class="m-icon close-btn" type="button" title="关闭" aria-label="关闭" @click="$emit('close')">
						<X :size="18" :stroke-width="2" />
					</button>
				</header>

				<div class="dialog-body">
					<p class="hint">配置子代理的基本信息和系统指令</p>

					<div class="form-grid">
						<div class="field">
							<label class="fl" for="subagent-basic-name">名称</label>
							<input id="subagent-basic-name" v-model="form.name" class="input" type="text" maxlength="120" />
						</div>
						<div class="field">
							<label class="fl" for="subagent-basic-model">模型配置</label>
							<div class="select">
								<select id="subagent-basic-model" v-model="form.modelProfileId" aria-label="模型配置">
									<option value="">继承父级 Agent 的模型</option>
									<option v-for="model in enabledModels" :key="model.modelProfileId"
										:value="model.modelProfileId">
										{{ model.name }} · {{ model.providerName }}
									</option>
								</select>
								<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
							</div>
						</div>
						<div class="field span-2">
							<label class="fl" for="subagent-basic-desc">描述</label>
							<input id="subagent-basic-desc" v-model="form.description" class="input" type="text"
								placeholder="一句话说明该子代理的职责" />
						</div>
						<div class="field">
							<label class="fl" for="subagent-basic-tools">工具策略</label>
							<div class="select">
								<select id="subagent-basic-tools" v-model="form.toolPolicy" aria-label="工具策略">
									<option value="none">none — 不注入工作区工具</option>
									<option value="read-only">read-only — 仅只读工具</option>
									<option value="system">system — 完整系统工具</option>
								</select>
								<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
							</div>
						</div>
						<div class="field">
							<label class="fl" for="subagent-basic-max-run">最长运行时间（秒）</label>
							<input id="subagent-basic-max-run" v-model="form.maxRunSeconds" class="input mono" type="number"
								min="30" max="3600" step="30" />
							<p v-if="!maxRunValid" class="field-error">取值需在 30 到 3600 秒之间。</p>
						</div>
						<div class="field span-2">
							<label class="fl" for="subagent-basic-instructions">系统指令（Instructions）</label>
							<textarea id="subagent-basic-instructions" v-model="form.instructions" class="input mono instructions"
								rows="8" placeholder="写入该子代理执行任务时使用的系统提示"></textarea>
						</div>
					</div>
				</div>

				<footer class="dialog-foot">
					<button class="btn secondary" type="button" @click="$emit('close')">取消</button>
					<button class="btn primary" type="button" :disabled="!canSubmit" @click="submit">
						<LoaderCircle v-if="saving" :size="14" :stroke-width="2.2" class="spin-ico" aria-hidden="true" />
						<Check v-else :size="14" :stroke-width="2.4" aria-hidden="true" />
						{{ saving ? '保存中…' : '保存' }}
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
	z-index: 1000;
	display: flex;
	align-items: center;
	justify-content: center;
	background: color-mix(in srgb, var(--sc-base) 40%, transparent);
	backdrop-filter: blur(12px);
	animation: sc-fade-in 0.18s var(--sc-ease-out);
}

.dialog {
	position: relative;
	display: flex;
	flex-direction: column;
	width: min(640px, calc(100vw - 32px));
	max-height: calc(100vh - 64px);
	border: 1px solid rgba(19, 27, 45, 0.14);
	border-radius: 16px;
	background: #ffffff;
	box-shadow:
		0 0 0 1px rgba(0, 0, 0, 0.12),
		0 20px 80px rgba(23, 26, 31, 0.28);
	overflow: hidden;
	animation: sc-slide-up 0.22s cubic-bezier(0.22, 1, 0.36, 1);
}

html[data-theme='dark'] .dialog {
	background: #16191f;
	border-color: rgba(255, 255, 255, 0.08);
	box-shadow:
		0 0 0 1px rgba(255, 255, 255, 0.1),
		0 20px 80px rgba(0, 0, 0, 0.6);
}

.dialog-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 1rem;
	padding: 24px 28px 20px;
	border-bottom: 1px solid rgba(19, 27, 45, 0.08);
}

html[data-theme='dark'] .dialog-head {
	border-bottom-color: rgba(255, 255, 255, 0.06);
}

.dh-kicker {
	margin-bottom: 6px;
	color: #9aa1ad;
	font-family: 'JetBrains Mono', 'SF Mono', 'Cascadia Code', ui-monospace, Menlo, Consolas, monospace;
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0.22em;
}

html[data-theme='dark'] .dh-kicker {
	color: #666e7d;
}

.dialog-head h2 {
	font-size: var(--fs-18);
	font-weight: 640;
	letter-spacing: -0.015em;
	color: #171a1f;
	margin: 0;
}

html[data-theme='dark'] .dialog-head h2 {
	color: #e9ebf0;
}

.close-btn {
	flex-shrink: 0;
	display: grid;
	width: 32px;
	height: 32px;
	place-items: center;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: #b9c0cc;
	cursor: pointer;
	transition: background 0.15s, color 0.15s;
}

html[data-theme='dark'] .close-btn {
	color: #b9c0cc;
}

.close-btn:hover:not(:disabled) {
	background: #eef0f4;
	color: #171a1f;
}

html[data-theme='dark'] .close-btn:hover:not(:disabled) {
	background: #1c2027;
	color: #e9ebf0;
}

.dialog-body {
	flex: 1;
	padding: 24px 28px;
	overflow-y: auto;
	scrollbar-width: thin;
	scrollbar-color: #9aa1ad transparent;
}

html[data-theme='dark'] .dialog-body {
	scrollbar-color: #666e7d transparent;
}

.dialog-body::-webkit-scrollbar {
	width: 9px;
}

.dialog-body::-webkit-scrollbar-thumb {
	background: #f1f3f6;
	background-clip: padding-box;
	border: 2px solid transparent;
	border-radius: 99px;
}

html[data-theme='dark'] .dialog-body::-webkit-scrollbar-thumb {
	background: #22262e;
}

.dialog-body::-webkit-scrollbar-thumb:hover {
	background: #9aa1ad;
	background-clip: padding-box;
}

html[data-theme='dark'] .dialog-body::-webkit-scrollbar-thumb:hover {
	background: #666e7d;
}

.hint {
	font-size: var(--fs-13);
	color: #6b7280;
	margin: 0 0 1.5rem;
	line-height: 1.5;
}

html[data-theme='dark'] .hint {
	color: #8d95a4;
}

.form-grid {
	display: grid;
	grid-template-columns: 1fr 1fr;
	gap: 18px;
}

.field {
	display: flex;
	flex-direction: column;
	gap: 8px;
}

.field.span-2 {
	grid-column: span 2;
}

.fl {
	color: #b9c0cc;
	font-size: var(--fs-13);
	font-weight: 560;
}

html[data-theme='dark'] .fl {
	color: #b9c0cc;
}

.input {
	width: 100%;
	padding: 10px 12px;
	font-family: var(--sc-sans);
	font-size: var(--fs-135);
	color: #171a1f;
	background: #f1f3f6;
	border: 1px solid rgba(19, 27, 45, 0.08);
	border-radius: 8px;
	outline: none;
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

html[data-theme='dark'] .input {
	border-color: rgba(255, 255, 255, 0.06);
	background: #22262e;
	color: #e9ebf0;
}

.input::placeholder {
	color: #9aa1ad;
}

html[data-theme='dark'] .input::placeholder {
	color: #666e7d;
}

.input:focus {
	border-color: rgba(59, 91, 253, 0.55);
	background: #ffffff;
	box-shadow: 0 0 0 3px rgba(59, 91, 253, 0.08);
}

html[data-theme='dark'] .input:focus {
	border-color: rgba(59, 91, 253, 0.55);
	background: #16191f;
	box-shadow: 0 0 0 3px rgba(59, 91, 253, 0.08);
}

.input.mono {
	font-family: var(--sc-mono);
	font-size: var(--fs-125);
	letter-spacing: 0.01em;
}

.input.instructions {
	resize: vertical;
	min-height: 120px;
	line-height: 1.6;
}

.select {
	position: relative;
	display: flex;
	align-items: center;
}

.select select {
	flex: 1;
	padding: 10px 34px 10px 12px;
	font-family: var(--sc-sans);
	font-size: var(--fs-135);
	color: #171a1f;
	background: #f1f3f6;
	border: 1px solid rgba(19, 27, 45, 0.08);
	border-radius: 8px;
	outline: none;
	appearance: none;
	cursor: pointer;
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

html[data-theme='dark'] .select select {
	border-color: rgba(255, 255, 255, 0.06);
	background: #22262e;
	color: #e9ebf0;
}

.select select:focus {
	border-color: rgba(59, 91, 253, 0.55);
	background: #ffffff;
	box-shadow: 0 0 0 3px rgba(59, 91, 253, 0.08);
}

html[data-theme='dark'] .select select:focus {
	border-color: rgba(59, 91, 253, 0.55);
	background: #16191f;
	box-shadow: 0 0 0 3px rgba(59, 91, 253, 0.08);
}

.select .chev {
	position: absolute;
	right: 12px;
	color: #9aa1ad;
	pointer-events: none;
}

html[data-theme='dark'] .select .chev {
	color: #666e7d;
}

.field-error {
	margin: 0;
	color: #dc4545;
	font-size: var(--fs-12);
}

html[data-theme='dark'] .field-error {
	color: #f2777a;
}

.dialog-foot {
	display: flex;
	align-items: center;
	justify-content: flex-end;
	gap: 10px;
	padding: 18px 28px;
	border-top: 1px solid rgba(19, 27, 45, 0.08);
}

html[data-theme='dark'] .dialog-foot {
	border-top-color: rgba(255, 255, 255, 0.06);
}

.btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 6px;
	height: 38px;
	padding: 0 18px;
	font-family: var(--sc-sans);
	font-size: var(--fs-135);
	font-weight: 560;
	border: none;
	border-radius: 999px;
	cursor: pointer;
	transition: all 0.16s;
	outline: none;
}

.btn.secondary {
	background: transparent;
	color: #b9c0cc;
}

html[data-theme='dark'] .btn.secondary {
	color: #b9c0cc;
}

.btn.secondary:hover:not(:disabled) {
	background: #eef0f4;
	color: #171a1f;
}

html[data-theme='dark'] .btn.secondary:hover:not(:disabled) {
	background: #1c2027;
	color: #e9ebf0;
}

.btn.primary {
	background: #3b5bfd;
	color: #ffffff;
	font-weight: 600;
}

html[data-theme='dark'] .btn.primary {
	background: #3b5bfd;
	color: #ffffff;
}

.btn.primary:hover:not(:disabled) {
	background: #2f49d1;
}

html[data-theme='dark'] .btn.primary:hover:not(:disabled) {
	background: #4a6aff;
}

.btn:disabled {
	opacity: 0.4;
	cursor: not-allowed;
}

.spin-ico {
	animation: spin 0.8s linear infinite;
}

@keyframes sc-fade-in {
	from {
		opacity: 0;
	}
	to {
		opacity: 1;
	}
}

@keyframes sc-slide-up {
	from {
		opacity: 0;
		transform: translateY(20px);
	}
	to {
		opacity: 1;
		transform: translateY(0);
	}
}

@keyframes spin {
	from {
		transform: rotate(0deg);
	}
	to {
		transform: rotate(360deg);
	}
}
</style>
