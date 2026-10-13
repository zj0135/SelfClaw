<script setup>
import { computed, reactive, watch } from 'vue';
import { X, LoaderCircle, Check, ChevronDown } from 'lucide-vue-next';

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
		<div v-if="open" class="dialog-overlay sc-root" @click.self="handleClose">
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
								placeholder="显示名称"
								maxlength="120"
								:disabled="saving"
							/>
						</div>

						<div class="field span-2">
							<label class="fl" for="new-agent-mode">运行模式</label>
							<div class="select">
								<select id="new-agent-mode" v-model="form.mode" :disabled="saving" aria-label="运行模式">
									<option value="direct">Direct</option>
									<option value="cli">CLI</option>
								</select>
								<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
							</div>
						</div>

						<div class="field span-2">
							<label class="fl" for="new-agent-desc">描述</label>
							<input
								id="new-agent-desc"
								v-model="form.description"
								class="input"
								type="text"
								placeholder="职责描述"
								:disabled="saving"
							/>
						</div>

						<div class="field span-2">
							<label class="fl" for="new-agent-instructions">系统指令</label>
							<textarea
								id="new-agent-instructions"
								v-model="form.instructions"
								class="input mono instructions"
								rows="6"
								placeholder="输入系统指令..."
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
	z-index: 1200;
	display: grid;
	width: 100%;
	height: 100%;
	place-items: center;
	padding: 24px;
	background: var(--overlay);
	backdrop-filter: blur(8px);
	animation: sc-fade-in 0.18s var(--sc-ease-out);
	font-family: var(--sc-sans);
}

.dialog {
	display: flex;
	width: min(620px, 100%);
	max-height: min(680px, calc(100vh - 48px));
	flex-direction: column;
	border: 1px solid var(--sc-line-2);
	border-radius: 16px;
	background: var(--sc-panel, var(--panel, #ffffff));
	color: var(--sc-text);
	box-shadow: 0 32px 90px rgba(var(--shadow-ink), 0.25);
	animation: sc-slide-up 0.22s cubic-bezier(0.22, 1, 0.36, 1);
	overflow: hidden;
}

.dialog-head {
	position: relative;
	padding: 24px 28px 20px;
	border-bottom: 1px solid var(--sc-line);
}

.dialog-kicker {
	margin-bottom: 6px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0.22em;
}

.dialog-head h2 {
	margin: 0;
	color: var(--sc-text);
	font-size: var(--fs-18);
	font-weight: 640;
	letter-spacing: -0.015em;
}

.close-btn {
	position: absolute;
	top: 24px;
	right: 24px;
	display: grid;
	width: 32px;
	height: 32px;
	place-items: center;
	border: 1px solid var(--sc-line);
	border-radius: 8px;
	background: transparent;
	color: var(--sc-mute);
	cursor: pointer;
	transition: background 0.15s, color 0.15s, border-color 0.15s;
}

.close-btn:hover:not(:disabled) {
	border-color: var(--sc-line-2);
	background: var(--sc-hover);
	color: var(--sc-text);
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
	scrollbar-color: var(--sc-faint) transparent;
}

.dialog-body::-webkit-scrollbar {
	width: 9px;
}

.dialog-body::-webkit-scrollbar-thumb {
	background: var(--sc-raise);
	background-clip: padding-box;
	border: 2px solid transparent;
	border-radius: 99px;
}

.dialog-body::-webkit-scrollbar-thumb:hover {
	background: var(--sc-faint);
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
	color: var(--sc-soft);
	font-size: var(--fs-13);
	font-weight: 600;
}

.required {
	color: var(--sc-err);
}

.input {
	width: 100%;
	box-sizing: border-box;
	padding: 10px 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-raise);
	color: var(--sc-text);
	font: inherit;
	font-size: var(--fs-135);
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

.input.mono {
	font-family: var(--sc-mono);
	font-size: var(--fs-125);
	line-height: 1.6;
	letter-spacing: 0.01em;
}

.input.instructions {
	resize: vertical;
	min-height: 120px;
}

.input::placeholder {
	color: var(--sc-faint);
}

.input:focus {
	border-color: var(--sc-acid);
	outline: none;
	background: var(--sc-panel);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
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
	box-sizing: border-box;
	padding: 10px 34px 10px 12px;
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	background: var(--sc-raise);
	color: var(--sc-text);
	cursor: pointer;
	font: inherit;
	font-size: var(--fs-135);
	appearance: none;
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

.select select:focus {
	border-color: var(--sc-acid);
	outline: none;
	background: var(--sc-panel);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.select select:disabled {
	opacity: 0.5;
	cursor: not-allowed;
}

.select .chev {
	position: absolute;
	right: 12px;
	color: var(--sc-mute);
	pointer-events: none;
}

.dialog-foot {
	display: flex;
	justify-content: flex-end;
	gap: 10px;
	padding: 18px 28px;
	border-top: 1px solid var(--sc-line);
}

.btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	gap: 6px;
	height: 38px;
	padding: 0 18px;
	border: 1px solid transparent;
	border-radius: 8px;
	cursor: pointer;
	font: inherit;
	font-size: var(--fs-13);
	font-weight: 600;
	transition: background 0.16s, color 0.16s, border-color 0.16s, opacity 0.16s;
}

.btn-ghost {
	border-color: var(--sc-line-2);
	background: var(--sc-panel);
	color: var(--sc-mute);
}

.btn-ghost:hover:not(:disabled) {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
	color: var(--sc-text);
}

.btn-primary {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
}

.btn-primary:hover:not(:disabled) {
	background: color-mix(in srgb, var(--accent) 86%, var(--ink-1));
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
