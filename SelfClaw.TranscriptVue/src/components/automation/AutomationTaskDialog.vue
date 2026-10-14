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
						<input v-model="form.title" type="text" maxlength="120" placeholder="例如：每日代码审查" />
					</label>
					<label class="ac-field">
						<span class="ac-field-label">备注</span>
						<textarea v-model="form.notes" rows="2" placeholder="可选说明"></textarea>
					</label>
				</section>

				<section class="ac-section">
					<AutomationScheduleFields :form="form" />
					<p class="ac-preview" :class="{ 'is-error': previewState.errorCode }" role="status">
						<template v-if="form.kind === 'none'">手动触发：不自动运行。</template>
						<template v-else-if="previewState.errorCode">{{ previewState.errorCode === 'IntervalTooShort' ? '触发间隔不能小于 5 分钟。' : '触发器配置无效。' }}</template>
						<template v-else-if="previewState.text">下次触发：{{ previewState.text }}</template>
						<template v-else>正在计算下次触发…</template>
					</p>
				</section>

				<section class="ac-section">
					<label class="ac-field">
						<span class="ac-field-label">提示词 <em v-if="form.kind !== 'none'">必填</em></span>
						<textarea v-model="form.prompt" rows="5" placeholder="留空则只是待办，不会调用模型。"></textarea>
					</label>
					<label v-if="form.kind === 'none'" class="ac-field">
						<span class="ac-field-label">截止日期（待办，可选）</span>
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
					<textarea v-model="form.instructions" rows="3" placeholder="可选的系统提示词"></textarea>
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
	background: color-mix(in srgb, var(--bg) 46%, transparent);
	backdrop-filter: blur(3px);
}

.ac-dialog {
	display: flex;
	width: min(680px, 100%);
	max-height: 100%;
	flex-direction: column;
	overflow: hidden;
	border: 1px solid var(--border);
	border-radius: 16px;
	background: var(--panel);
	box-shadow: 0 26px 80px var(--overlay-shadow);
}

.ac-dialog-head {
	display: flex;
	align-items: flex-start;
	justify-content: space-between;
	gap: 12px;
	padding: 16px 20px 12px;
	border-bottom: 1px solid var(--border);
}

.ac-dialog-kicker {
	color: var(--muted);
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.08em;
	text-transform: uppercase;
}

.ac-dialog-head h2 {
	margin: 4px 0 0;
	font-family: var(--font-display);
	font-size: var(--fs-18);
}

.ac-dialog-close {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 28px;
	height: 28px;
	flex: none;
	border: 1px solid var(--border);
	border-radius: 8px;
	background: var(--panel-soft);
	color: var(--muted);
}

.ac-dialog-close svg {
	width: 15px;
	height: 15px;
}

.ac-dialog-body {
	display: flex;
	min-height: 0;
	flex: 1 1 auto;
	flex-direction: column;
	gap: 16px;
	padding: 16px 20px;
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
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.04em;
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
	border: 1px solid var(--border);
	border-radius: 9px;
	background: var(--panel-soft);
	color: var(--text);
	font-family: inherit;
	font-size: var(--fs-13);
}

.ac-field textarea {
	resize: vertical;
	line-height: 1.6;
}

.ac-preview {
	margin: 0;
	color: var(--muted);
	font-size: var(--fs-12);
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
}

.ac-dialog-foot {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
	padding: 12px 20px 16px;
	border-top: 1px solid var(--border);
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
	padding: 8px 16px;
	border-radius: 9px;
	font-size: var(--fs-13);
	font-weight: 650;
}

.ac-secondary {
	border: 1px solid var(--border);
	background: var(--panel-soft);
	color: var(--text);
}

.ac-primary {
	border: 0;
	background: var(--accent);
	color: #fff;
}

.ac-primary:disabled {
	opacity: 0.55;
}
</style>
