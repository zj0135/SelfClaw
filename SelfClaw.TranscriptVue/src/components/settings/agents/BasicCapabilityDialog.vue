<script setup>
import { computed, reactive, watch } from 'vue';
import { Check, ChevronDown, LoaderCircle, X } from 'lucide-vue-next';

const props = defineProps({
	open: { type: Boolean, default: false },
	agent: { type: Object, default: null },
	saving: { type: Boolean, default: false },
});

const emit = defineEmits(['close', 'save']);

const form = reactive({ name: '', description: '', mode: 'direct', instructions: '' });

watch(
	() => props.agent,
	(agent) => {
		if (agent) {
			form.name = agent.name;
			form.description = agent.description;
			form.mode = agent.mode;
			form.instructions = agent.instructions;
		}
	},
	{ immediate: true, deep: true }
);

const isDirty = computed(() => {
	if (!props.agent) return false;
	return (
		form.name !== props.agent.name ||
		form.description !== props.agent.description ||
		form.mode !== props.agent.mode ||
		form.instructions !== props.agent.instructions
	);
});

function submit() {
	if (!isDirty.value || props.saving) return;
	emit('save', {
		name: form.name.trim(),
		description: form.description.trim(),
		mode: form.mode,
		instructions: form.instructions,
	});
}
</script>

<template>
	<Teleport to="body">
		<div v-if="open" class="overlay sc-root" @click="$emit('close')">
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
					<div class="form-grid">
						<div class="field">
							<label class="fl" for="basic-name">名称</label>
							<input id="basic-name" v-model="form.name" class="input" type="text" maxlength="120" />
						</div>
						<div class="field">
							<label class="fl" for="basic-mode">运行模式</label>
							<div class="select">
								<select id="basic-mode" v-model="form.mode" aria-label="运行模式">
									<option value="direct">Direct</option>
									<option value="cli">CLI</option>
								</select>
								<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
							</div>
						</div>
						<div class="field span-2">
							<label class="fl" for="basic-desc">描述</label>
							<input id="basic-desc" v-model="form.description" class="input" type="text"
								placeholder="代理职责描述" />
						</div>
						<div class="field span-2">
							<label class="fl" for="basic-instructions">系统指令</label>
							<textarea id="basic-instructions" v-model="form.instructions" class="input mono instructions"
								rows="8" placeholder="输入系统指令..."></textarea>
						</div>
					</div>
				</div>

				<footer class="dialog-foot">
					<button class="btn secondary" type="button" @click="$emit('close')">取消</button>
					<button class="btn primary" type="button" :disabled="!isDirty || saving" @click="submit">
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
	padding: 24px;
	background: var(--overlay);
	backdrop-filter: blur(8px);
	animation: sc-fade-in 0.18s var(--sc-ease-out);
	font-family: var(--sc-sans);
}

.dialog {
	position: relative;
	display: flex;
	flex-direction: column;
	width: min(640px, calc(100vw - 32px));
	max-height: min(720px, calc(100vh - 48px));
	border: 1px solid var(--sc-line-2);
	border-radius: 16px;
	background: var(--sc-panel, var(--panel, #ffffff));
	color: var(--sc-text);
	box-shadow: 0 32px 90px rgba(var(--shadow-ink), 0.25);
	overflow: hidden;
	animation: sc-slide-up 0.22s cubic-bezier(0.22, 1, 0.36, 1);
}

.dialog-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 1rem;
	padding: 24px 28px 20px;
	border-bottom: 1px solid var(--sc-line);
}

.dh-kicker {
	margin-bottom: 6px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	font-weight: 600;
	letter-spacing: 0.22em;
}

.dialog-head h2 {
	margin: 0;
	font-size: var(--fs-18);
	font-weight: 640;
	letter-spacing: -0.015em;
	color: var(--sc-text);
}

.close-btn {
	flex-shrink: 0;
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

.dialog-body {
	flex: 1;
	min-height: 0;
	padding: 24px 28px;
	overflow-y: auto;
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

.input {
	width: 100%;
	box-sizing: border-box;
	padding: 10px 12px;
	font-family: var(--sc-sans);
	font-size: var(--fs-135);
	color: var(--sc-text);
	background: var(--sc-raise);
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	outline: none;
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

.input::placeholder {
	color: var(--sc-faint);
}

.input:focus {
	border-color: var(--sc-acid);
	background: var(--sc-panel);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
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
	width: 100%;
	box-sizing: border-box;
	padding: 10px 34px 10px 12px;
	font-family: var(--sc-sans);
	font-size: var(--fs-135);
	color: var(--sc-text);
	background: var(--sc-raise);
	border: 1px solid var(--sc-line-2);
	border-radius: 8px;
	outline: none;
	appearance: none;
	cursor: pointer;
	transition: border-color 0.16s, box-shadow 0.16s, background 0.16s;
}

.select select:focus {
	border-color: var(--sc-acid);
	background: var(--sc-panel);
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.select .chev {
	position: absolute;
	right: 12px;
	color: var(--sc-mute);
	pointer-events: none;
}

.dialog-foot {
	display: flex;
	align-items: center;
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
	font-family: var(--sc-sans);
	font-size: var(--fs-13);
	font-weight: 600;
	border: 1px solid transparent;
	border-radius: 8px;
	cursor: pointer;
	transition: all 0.16s;
	outline: none;
}

.btn.secondary {
	border-color: var(--sc-line-2);
	background: var(--sc-panel);
	color: var(--sc-mute);
}

.btn.secondary:hover:not(:disabled) {
	border-color: var(--sc-faint);
	background: var(--sc-hover);
	color: var(--sc-text);
}

.btn.primary {
	border-color: var(--sc-acid);
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
}

.btn.primary:hover:not(:disabled) {
	background: color-mix(in srgb, var(--accent) 86%, var(--ink-1));
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
