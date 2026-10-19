<script setup>
import { computed, reactive, watch } from 'vue';
import { Check, ChevronDown, LoaderCircle, X, Bot } from 'lucide-vue-next';

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
		<div v-if="open" class="overlay sc-root" @click.self="$emit('close')">
			<div class="dialog-frame" @click.stop>
				<header class="dialog-head">
					<div class="dh-icon-box">
						<Bot :size="20" :stroke-width="2" />
					</div>
					<div class="dh-meta">
						<h2>基本能力配置</h2>
					</div>
					<button class="close-btn" type="button" title="关闭" aria-label="关闭" @click="$emit('close')">
						<X :size="16" :stroke-width="2" />
					</button>
				</header>

				<div class="dialog-body scroll">
					<div class="form-grid">
						<div class="field">
							<label class="fl" for="basic-name">
								<span>代理名称</span>
							</label>
							<input id="basic-name" v-model="form.name" class="input" type="text" maxlength="120" />
						</div>

						<div class="field">
							<label class="fl" for="basic-mode">
								<span>运行模式</span>
							</label>
							<div class="select-wrap">
								<select id="basic-mode" v-model="form.mode" aria-label="运行模式">
									<option value="direct">Direct (进程内模型直接推理)</option>
									<option value="cli">CLI (独立外部编程环境)</option>
								</select>
								<ChevronDown :size="15" :stroke-width="2" class="chev" aria-hidden="true" />
							</div>
						</div>

						<div class="field span-2">
							<label class="fl" for="basic-desc">
								<span>职责描述</span>
							</label>
							<input
								id="basic-desc"
								v-model="form.description"
								class="input"
								type="text"
								placeholder="简要概括该代理的专长或任务分工..."
							/>
						</div>

						<div class="field span-2">
							<label class="fl" for="basic-instructions">
								<span>系统指令 (Prompt)</span>
							</label>
							<textarea
								id="basic-instructions"
								v-model="form.instructions"
								class="input mono instructions"
								rows="8"
								placeholder="输入系统指令与行为设定..."
							></textarea>
						</div>
					</div>
				</div>

				<footer class="dialog-foot">
					<button class="btn btn-secondary" type="button" @click="$emit('close')">取消</button>
					<button class="btn btn-primary" type="button" :disabled="!isDirty || saving" @click="submit">
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

.instructions {
	height: auto;
	min-height: 160px;
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

.btn-secondary:hover {
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
