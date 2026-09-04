<script setup>
import { computed, reactive, watch } from 'vue';
import { X, LoaderCircle, Check } from 'lucide-vue-next';

const props = defineProps({
	open: { type: Boolean, default: false },
	mode: { type: String, default: 'create-agent' }, // 'create-agent', 'create-subagent', or 'edit'
	agent: { type: Object, default: null },
	saving: { type: Boolean, default: false },
});

const emit = defineEmits(['close', 'submit']);

const form = reactive({
	id: '',
	name: '',
	description: '',
	mode: 'direct',
	instructions: '',
});

const dialogTitle = computed(() => {
	if (props.mode === 'create-agent') return '新增代理';
	if (props.mode === 'create-subagent') return '新增子代理';
	return '编辑代理';
});

const dialogKicker = computed(() => {
	if (props.mode === 'create-agent') return 'NEW AGENT';
	if (props.mode === 'create-subagent') return 'NEW SUBAGENT';
	return 'EDIT AGENT';
});

watch(
	() => props.open,
	(isOpen) => {
		if (isOpen) {
			if (props.mode === 'edit' && props.agent) {
				form.id = props.agent.id;
				form.name = props.agent.name;
				form.description = props.agent.description;
				form.mode = props.agent.mode;
				form.instructions = props.agent.instructions;
			} else {
				form.id = '';
				form.name = '';
				form.description = '';
				form.mode = 'direct';
				form.instructions = '';
			}
		}
	},
	{ immediate: true },
);

const isValid = computed(() => {
	if (props.mode === 'create-agent' || props.mode === 'create-subagent') {
		return form.id.trim() && form.name.trim();
	}
	return form.name.trim();
});

function handleSubmit() {
	if (!isValid.value || props.saving) return;

	emit('submit', {
		id: form.id.trim(),
		name: form.name.trim(),
		description: form.description.trim(),
		mode: form.mode,
		instructions: form.instructions,
	});
}

function handleClose() {
	if (!props.saving) {
		emit('close');
	}
}
</script>

<template>
	<Teleport to="body">
		<div v-if="open" class="dialog-overlay" @click.self="handleClose">
			<div class="dialog" role="dialog" aria-modal="true" :aria-labelledby="'dialog-title-' + mode">
				<div class="dialog-head">
					<div class="dialog-kicker">{{ dialogKicker }}</div>
					<h2 :id="'dialog-title-' + mode">
						{{ dialogTitle }}
					</h2>
					<button
						type="button"
						class="close-btn"
						:disabled="saving"
						@click="handleClose"
						aria-label="关闭对话框"
					>
						<X :size="18" :stroke-width="2" />
					</button>
				</div>

				<div class="dialog-body">
					<div class="form-grid">
						<div v-if="mode === 'create-agent' || mode === 'create-subagent'" class="field span-2">
							<label class="fl" for="new-agent-id">
								ID <span class="required">*</span>
							</label>
							<input
								id="new-agent-id"
								v-model="form.id"
								class="input"
								type="text"
								placeholder="例如：my-agent"
								maxlength="120"
								:disabled="saving"
							/>
							<p class="field-hint">仅限字母、数字、下划线和短横线，创建后不可修改</p>
						</div>

						<div class="field span-2">
							<label class="fl" for="new-agent-name">
								名称 <span class="required">*</span>
							</label>
							<input
								id="new-agent-name"
								v-model="form.name"
								class="input"
								type="text"
								placeholder="代理的显示名称"
								maxlength="120"
								:disabled="saving"
							/>
						</div>

						<div class="field span-2">
							<label class="fl" for="new-agent-mode">运行模式</label>
							<div class="select">
								<select id="new-agent-mode" v-model="form.mode" :disabled="saving" aria-label="运行模式">
									<option value="direct">Direct — 进程内调用 AI 服务商</option>
									<option value="cli">CLI — 运行本地编程 CLI 子进程</option>
								</select>
								<svg class="chev" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
									<polyline points="6 9 12 15 18 9"></polyline>
								</svg>
							</div>
						</div>

						<div class="field span-2">
							<label class="fl" for="new-agent-desc">描述</label>
							<input
								id="new-agent-desc"
								v-model="form.description"
								class="input"
								type="text"
								placeholder="一句话说明该代理的职责"
								:disabled="saving"
							/>
						</div>

						<div class="field span-2">
							<label class="fl" for="new-agent-instructions">系统指令（Instructions）</label>
							<textarea
								id="new-agent-instructions"
								v-model="form.instructions"
								class="input mono instructions"
								rows="6"
								placeholder="写入该代理的系统提示，Direct 模式注入系统消息，CLI 模式作为附加系统提示"
								:disabled="saving"
							></textarea>
						</div>
					</div>
				</div>

				<div class="dialog-foot">
					<button type="button" class="btn btn-ghost" :disabled="saving" @click="handleClose">
						取消
					</button>
					<button type="button" class="btn btn-primary" :disabled="!isValid || saving" @click="handleSubmit">
						<LoaderCircle v-if="saving" :size="14" :stroke-width="2.2" class="spin-ico" aria-hidden="true" />
						<Check v-else :size="14" :stroke-width="2.4" aria-hidden="true" />
						{{ mode === 'create-agent' || mode === 'create-subagent' ? '创建' : '保存' }}
					</button>
				</div>
			</div>
		</div>
	</Teleport>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.dialog-overlay {
	position: fixed;
	top: 0;
	left: 0;
	z-index: 9999;
	display: grid;
	width: 100%;
	height: 100%;
	place-items: center;
	background: color-mix(in srgb, var(--sc-base) 40%, transparent);
	backdrop-filter: blur(12px);
	animation: sc-fade-in 0.18s var(--sc-ease-out);
}

.dialog {
	display: flex;
	width: min(620px, calc(100vw - 32px));
	max-height: calc(100vh - 64px);
	flex-direction: column;
	border: 1px solid rgba(19, 27, 45, 0.14);
	border-radius: 16px;
	background: #ffffff;
	box-shadow:
		0 0 0 1px rgba(0, 0, 0, 0.12),
		0 20px 80px rgba(23, 26, 31, 0.28);
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
	position: relative;
	padding: 24px 28px 20px;
	border-bottom: 1px solid rgba(19, 27, 45, 0.08);
}

html[data-theme='dark'] .dialog-head {
	border-bottom-color: rgba(255, 255, 255, 0.06);
}

.dialog-kicker {
	margin-bottom: 6px;
	color: #9aa1ad;
	font-family: 'JetBrains Mono', 'SF Mono', 'Cascadia Code', ui-monospace, Menlo, Consolas, monospace;
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0.22em;
}

html[data-theme='dark'] .dialog-kicker {
	color: #666e7d;
}

.dialog-head h2 {
	margin: 0;
	color: #171a1f;
	font-size: var(--fs-18);
	font-weight: 640;
	letter-spacing: -0.015em;
}

html[data-theme='dark'] .dialog-head h2 {
	color: #e9ebf0;
}

.close-btn {
	position: absolute;
	top: 24px;
	right: 24px;
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

.close-btn:disabled {
	opacity: 0.4;
	cursor: not-allowed;
}

.dialog-body {
	min-height: 0;
	flex: 1;
	overflow-y: auto;
	padding: 24px 28px;
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

.required {
	color: #dc4545;
}

html[data-theme='dark'] .required {
	color: #f2777a;
}

.input {
	width: 100%;
	padding: 10px 12px;
	border: 1px solid rgba(19, 27, 45, 0.08);
	border-radius: 8px;
	background: #f1f3f6;
	color: #171a1f;
	font: inherit;
	font-size: var(--fs-135);
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

html[data-theme='dark'] .input {
	border-color: rgba(255, 255, 255, 0.06);
	background: #22262e;
	color: #e9ebf0;
}

.input.mono {
	font-family: 'JetBrains Mono', 'SF Mono', 'Cascadia Code', ui-monospace, Menlo, Consolas, monospace;
	font-size: var(--fs-125);
	line-height: 1.6;
	letter-spacing: 0.01em;
}

.input.instructions {
	resize: vertical;
	min-height: 120px;
}

.input::placeholder {
	color: #9aa1ad;
}

html[data-theme='dark'] .input::placeholder {
	color: #666e7d;
}

.input:focus {
	border-color: rgba(59, 91, 253, 0.55);
	outline: none;
	background: #ffffff;
	box-shadow: 0 0 0 3px rgba(59, 91, 253, 0.08);
}

html[data-theme='dark'] .input:focus {
	border-color: rgba(59, 91, 253, 0.55);
	background: #16191f;
	box-shadow: 0 0 0 3px rgba(59, 91, 253, 0.08);
}

.input:disabled {
	opacity: 0.5;
	cursor: not-allowed;
}

.select {
	position: relative;
	display: flex;
	align-items: center;
}

.select select {
	width: 100%;
	padding: 10px 34px 10px 12px;
	border: 1px solid rgba(19, 27, 45, 0.08);
	border-radius: 8px;
	background: #f1f3f6;
	color: #171a1f;
	cursor: pointer;
	font: inherit;
	font-size: var(--fs-135);
	appearance: none;
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

html[data-theme='dark'] .select select {
	border-color: rgba(255, 255, 255, 0.06);
	background: #22262e;
	color: #e9ebf0;
}

.select select:focus {
	border-color: rgba(59, 91, 253, 0.55);
	outline: none;
	background: #ffffff;
	box-shadow: 0 0 0 3px rgba(59, 91, 253, 0.08);
}

html[data-theme='dark'] .select select:focus {
	border-color: rgba(59, 91, 253, 0.55);
	background: #16191f;
	box-shadow: 0 0 0 3px rgba(59, 91, 253, 0.08);
}

.select select:disabled {
	opacity: 0.5;
	cursor: not-allowed;
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

.field-hint {
	margin: -2px 0 0;
	color: #6b7280;
	font-size: var(--fs-115);
}

html[data-theme='dark'] .field-hint {
	color: #8d95a4;
}

.dialog-foot {
	display: flex;
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
	border: 0;
	border-radius: 999px;
	color: #171a1f;
	cursor: pointer;
	font: inherit;
	font-size: var(--fs-135);
	font-weight: 560;
	transition: background 0.16s, color 0.16s, opacity 0.16s;
}

html[data-theme='dark'] .btn {
	color: #e9ebf0;
}

.btn-ghost {
	background: transparent;
	color: #b9c0cc;
}

html[data-theme='dark'] .btn-ghost {
	color: #b9c0cc;
}

.btn-ghost:hover:not(:disabled) {
	background: #eef0f4;
	color: #171a1f;
}

html[data-theme='dark'] .btn-ghost:hover:not(:disabled) {
	background: #1c2027;
	color: #e9ebf0;
}

.btn-primary {
	background: #3b5bfd;
	color: #ffffff;
	font-weight: 600;
}

html[data-theme='dark'] .btn-primary {
	background: #3b5bfd;
	color: #ffffff;
}

.btn-primary:hover:not(:disabled) {
	background: #2f49d1;
}

html[data-theme='dark'] .btn-primary:hover:not(:disabled) {
	background: #4a6aff;
}

.btn:disabled {
	opacity: 0.4;
	cursor: not-allowed;
}

.spin-ico {
	animation: sc-spin 0.8s linear infinite;
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

@keyframes sc-spin {
	from {
		transform: rotate(0deg);
	}
	to {
		transform: rotate(360deg);
	}
}
</style>
