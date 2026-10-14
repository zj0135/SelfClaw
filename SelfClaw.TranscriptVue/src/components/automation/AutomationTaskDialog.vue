<script setup>
import { computed, ref, watch } from 'vue';
import { X } from 'lucide-vue-next';
import AutomationScheduleFields from './AutomationScheduleFields.vue';
import AutomationTaskCapabilities from './AutomationTaskCapabilities.vue';
import { describeNextRun, validateTaskForm, wireSchedule } from '../../renderers/automationEvents';

const props = defineProps({
	open: { type: Boolean, default: false },
	form: { type: Object, required: true },
	timeZone: { type: String, default: '' },
	preview: { type: Function, required: true },
	saving: { type: Boolean, default: false },
});

const emit = defineEmits(['close', 'submit']);

const previewState = ref({ text: '', errorCode: null });
const previewing = ref(false);

const hasPrompt = computed(() => Boolean(String(props.form.prompt ?? '').trim()));
const validation = computed(() => validateTaskForm(props.form, { previewErrorCode: previewState.value.errorCode }));
const canSubmit = computed(() => validation.value.ok && !props.saving && !previewing.value);

async function runPreview() {
	previewState.value = { text: '', errorCode: null };
	if (props.form.kind === 'none') return;
	previewing.value = true;
	try {
		const response = await props.preview(props.form.kind, wireSchedule(props.form, props.timeZone));
		if (response?.ok === false) {
			previewState.value = { text: '', errorCode: response.errorCode || 'InvalidSchedule' };
			return;
		}
		previewState.value = {
			text: describeNextRun(response?.nextRunAtUtc, { timeZone: props.timeZone }).text,
			errorCode: null,
		};
	} catch (_) {
		previewState.value = { text: '', errorCode: 'InvalidSchedule' };
	} finally {
		previewing.value = false;
	}
}

watch(() => props.open, (open) => { if (open) runPreview(); });
watch(
	() => [props.form.kind, props.form.schedule?.time, props.form.schedule?.runAtLocal,
		props.form.schedule?.cron, props.form.schedule?.dayOfMonth, (props.form.schedule?.weekdays || []).join(',')],
	() => { if (props.open) runPreview(); }
);

function submit() {
	if (!canSubmit.value) return;
	emit('submit', props.form);
}
</script>

<template>
	<div v-if="open" class="ac-dialog-backdrop" @click.self="emit('close')">
		<div class="ac-dialog" role="dialog" aria-modal="true" aria-label="自动化任务">
			<header class="ac-dialog-head">
				<div>
					<span class="ac-dialog-kicker">{{ form.id ? '编辑任务' : '新建任务' }}</span>
					<h2>{{ form.title || '未命名任务' }}</h2>
				</div>
				<button type="button" class="ac-dialog-close" title="关闭" @click="emit('close')">
					<X aria-hidden="true" />
				</button>
			</header>

			<div class="ac-dialog-body">
				<section class="ac-section">
					<label class="ac-field">
						<span class="ac-field-label">标题 <em>必填</em></span>
						<input v-model="form.title" type="text" maxlength="120" placeholder="输入任务标题…" />
					</label>
					<label class="ac-field">
						<span class="ac-field-label">备注</span>
						<textarea v-model="form.notes" rows="2" placeholder="添加备注…"></textarea>
					</label>
				</section>

				<section class="ac-section">
					<AutomationScheduleFields :form="form" />
					<p class="ac-preview" :class="{ 'is-error': previewState.errorCode }" role="status">
						<template v-if="form.kind === 'none'">手动触发：不自动运行</template>
						<template v-else-if="previewState.errorCode">{{ previewState.errorCode === 'IntervalTooShort' ? '触发间隔不能小于 5 分钟' : '触发器配置无效' }}</template>
						<template v-else-if="previewState.text">下次触发：{{ previewState.text }}</template>
						<template v-else>正在计算下次触发…</template>
					</p>
				</section>

				<section class="ac-section">
					<label class="ac-field">
						<span class="ac-field-label">提示词 <em v-if="form.kind !== 'none'">必填</em></span>
						<textarea v-model="form.prompt" rows="5" placeholder="输入任务提示词…"></textarea>
					</label>
					<label v-if="form.kind === 'none'" class="ac-field">
						<span class="ac-field-label">截止日期</span>
						<input v-model="form.dueAtLocal" type="date" />
					</label>
					<label v-if="hasPrompt" class="ac-confirm">
						<input v-model="form.fullAccessConfirmed" type="checkbox" />
						<span>允许该任务在无人值守时读写文件并执行命令。</span>
					</label>
				</section>

				<section class="ac-section">
					<span class="ac-field-label">能力配置</span>
					<AutomationTaskCapabilities :form="form" />
				</section>

				<label class="ac-field">
					<span class="ac-field-label">自定义指令</span>
					<textarea v-model="form.instructions" rows="2" placeholder="自定义指令…"></textarea>
				</label>
			</div>

			<footer class="ac-dialog-foot">
				<p v-if="!validation.ok" class="ac-dialog-error">{{ validation.message }}</p>
				<div class="ac-dialog-actions">
					<button type="button" class="ac-secondary" @click="emit('close')">取消</button>
					<button type="button" class="ac-primary" :disabled="!canSubmit" @click="submit">
						{{ saving ? '保存中…' : '保存任务' }}
					</button>
				</div>
			</footer>
		</div>
	</div>
</template>

<style scoped>
.ac-dialog-backdrop {
	position: absolute;
	inset: 0;
	z-index: 320;
	display: flex;
	align-items: center;
	justify-content: center;
	padding: 24px;
	background: var(--overlay, rgba(0, 0, 0, 0.45));
	backdrop-filter: blur(8px);
	-webkit-backdrop-filter: blur(8px);
	animation: ac-fade-in 0.18s ease-out;
}

.ac-dialog {
	display: flex;
	width: min(680px, 100%);
	max-height: 100%;
	flex-direction: column;
	overflow: hidden;
	border: 1px solid var(--line-1);
	border-radius: 16px;
	background: var(--panel, #ffffff);
	color: var(--text);
	box-shadow: 0 28px 80px rgba(var(--shadow-ink), 0.26);
	animation: ac-slide-up 0.22s cubic-bezier(0.16, 1, 0.3, 1);
}

.ac-dialog-head {
	display: flex;
	align-items: flex-start;
	justify-content: space-between;
	gap: 12px;
	padding: 16px 20px 14px;
	border-bottom: 1px solid var(--line-1);
}

.ac-dialog-kicker {
	color: var(--muted);
	font-family: var(--font-code, monospace);
	font-size: var(--fs-10);
	font-weight: 650;
	letter-spacing: 0.18em;
	text-transform: uppercase;
}

.ac-dialog-head h2 {
	margin: 4px 0 0;
	font-family: var(--font-display);
	font-size: var(--fs-18);
	font-weight: 650;
	letter-spacing: -0.015em;
	color: var(--text);
}

.ac-dialog-close {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 28px;
	height: 28px;
	flex: none;
	border: 1px solid var(--line-1);
	border-radius: 8px;
	background: var(--panel-soft);
	color: var(--muted);
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s, color 0.15s, transform 0.15s var(--ease-spring);
}

.ac-dialog-close:hover {
	background: var(--panel-hover);
	color: var(--text);
	border-color: var(--line-2);
}

.ac-dialog-close:active {
	transform: scale(0.92);
}

.ac-dialog-close svg {
	width: 14px;
	height: 14px;
}

.ac-dialog-body {
	display: flex;
	min-height: 0;
	flex: 1 1 auto;
	flex-direction: column;
	gap: 16px;
	padding: 18px 20px;
	overflow-y: auto;
}

.ac-section {
	display: flex;
	flex-direction: column;
	gap: 10px;
}

.ac-field {
	display: flex;
	min-width: 0;
	flex-direction: column;
	gap: 5px;
}

.ac-field-label {
	color: var(--muted);
	font-family: var(--font-code, monospace);
	font-size: var(--fs-10);
	font-weight: 650;
	letter-spacing: 0.08em;
	text-transform: uppercase;
}

.ac-field-label em {
	color: var(--danger);
	font-style: normal;
}

.ac-field input[type='text'],
.ac-field input[type='date'],
.ac-field textarea {
	width: 100%;
	padding: 8px 10px;
	border: 1px solid var(--line-1);
	border-radius: 8px;
	background: var(--panel-soft);
	color: var(--text);
	font-family: inherit;
	font-size: var(--fs-12);
	transition: border-color 0.15s, box-shadow 0.15s;
}

.ac-field input:focus,
.ac-field textarea:focus {
	border-color: var(--accent);
	box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 15%, transparent);
	outline: none;
}

.ac-field textarea {
	resize: vertical;
	line-height: 1.55;
}

.ac-preview {
	margin: 0;
	color: var(--muted);
	font-family: var(--font-code, monospace);
	font-size: 11px;
	font-variant-numeric: tabular-nums;
}

.ac-preview.is-error {
	color: var(--danger);
}

.ac-confirm {
	display: flex;
	align-items: flex-start;
	gap: 8px;
	color: var(--text);
	font-size: var(--fs-12);
	line-height: 1.5;
	cursor: pointer;
}

.ac-dialog-foot {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
	padding: 12px 20px 16px;
	border-top: 1px solid var(--line-1);
}

.ac-dialog-error {
	margin: 0;
	color: var(--danger);
	font-size: var(--fs-12);
}

.ac-dialog-actions {
	display: flex;
	flex: none;
	gap: 8px;
	margin-left: auto;
}

.ac-secondary,
.ac-primary {
	padding: 7px 16px;
	border-radius: 8px;
	font-size: var(--fs-12);
	font-weight: 600;
	cursor: pointer;
	transition: filter 0.15s, background 0.15s, border-color 0.15s, transform 0.15s var(--ease-spring);
}

.ac-secondary {
	border: 1px solid var(--line-1);
	background: var(--panel-soft);
	color: var(--text);
}

.ac-secondary:hover {
	background: var(--panel-hover);
	border-color: var(--line-2);
}

.ac-secondary:active {
	transform: scale(0.96);
}

.ac-primary {
	border: 0;
	background: var(--accent);
	color: var(--accent-ink-on, #fff);
	box-shadow: 0 2px 8px color-mix(in srgb, var(--accent) 30%, transparent);
}

.ac-primary:hover:not(:disabled) {
	filter: brightness(1.05);
	box-shadow: 0 4px 12px color-mix(in srgb, var(--accent) 42%, transparent);
}

.ac-primary:active:not(:disabled) {
	transform: scale(0.96);
}

.ac-primary:disabled {
	opacity: 0.5;
	cursor: not-allowed;
}

@keyframes ac-fade-in {
	from { opacity: 0; }
	to { opacity: 1; }
}

@keyframes ac-slide-up {
	from {
		opacity: 0;
		transform: translateY(12px) scale(0.98);
	}
	to {
		opacity: 1;
		transform: translateY(0) scale(1);
	}
}
</style>
