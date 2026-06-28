<script setup>
import { computed, ref } from 'vue';

const props = defineProps({
	disabled: {
		type: Boolean,
		default: false,
	},
});

const emit = defineEmits(['submit']);

const composerText = ref('');
const shellRef = ref(null);

const canSend = computed(() => composerText.value.trim().length > 0 && !props.disabled);

function submit() {
	const prompt = composerText.value.trim();
	if (!prompt || props.disabled) {
		return;
	}

	emit('submit', prompt);
	composerText.value = '';
}

function onKeydown(event) {
	if (event.key !== 'Enter' || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) {
		return;
	}

	event.preventDefault();
	submit();
}

defineExpose({
	getShellEl: () => shellRef.value,
});
</script>

<template>
	<section ref="shellRef" class="composer-shell" aria-label="消息输入">
		<div class="composer-grip" aria-hidden="true"></div>
		<textarea
			v-model="composerText"
			class="composer-input"
			rows="3"
			placeholder="让助手帮你处理项目..."
			:disabled="props.disabled"
			@keydown="onKeydown"
		></textarea>
		<div class="composer-toolbar">
			<div class="composer-tools-left">
				<button class="composer-model" type="button" title="模型选择">
					<span class="model-dot" aria-hidden="true"></span>
					<span>Kimi K2.6 Code Preview</span>
				</button>
				<button class="icon-btn" type="button" title="功能" aria-label="功能">
					<svg viewBox="0 0 20 20" aria-hidden="true">
						<path d="M3 7h3M10 7h7" />
						<circle cx="8" cy="7" r="2" />
						<path d="M3 13h7M14 13h3" />
						<circle cx="12" cy="13" r="2" />
					</svg>
				</button>
				<button class="icon-btn" type="button" title="文件夹" aria-label="文件夹">
					<svg viewBox="0 0 20 20" aria-hidden="true">
						<path d="M3 7h4.5l1.5 1.5H17v7a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 3 14.5V7Z" />
						<path d="M3 7V5a1.5 1.5 0 0 1 1.5-1.5H8l1.5 1.5H15a1 1 0 0 1 1 1V7" />
					</svg>
				</button>
			</div>
			<div class="composer-tools-right">
				<button class="send-btn" type="button" :disabled="!canSend" title="发送" aria-label="发送" @click="submit">
					<svg viewBox="0 0 20 20" aria-hidden="true">
						<path d="M5 10h10M10 5l5 5-5 5" />
					</svg>
				</button>
			</div>
		</div>
	</section>
</template>

<style>
.composer-shell {
	position: relative;
	width: min(calc(100% - 72px), 728px);
	min-height: 138px;
	margin: 0 auto 16px;
	display: grid;
	grid-template-rows: 1fr auto;
	padding: 22px 18px 12px;
	border: 1px solid #e2e5eb;
	border-radius: 16px;
	background: #ffffff;
	box-shadow:
		0 1px 2px rgba(23, 26, 31, 0.05),
		0 8px 24px rgba(23, 26, 31, 0.04);
	will-change: transform;
}

.empty-workspace .composer-shell {
	width: min(calc(100% - 72px), 680px);
	min-height: 132px;
	margin-bottom: 0;
}

.composer-grip {
	position: absolute;
	top: 6px;
	left: 50%;
	width: 36px;
	height: 3.5px;
	border-radius: 99px;
	background: #dde1e7;
	transform: translateX(-50%);
}

.composer-input {
	width: 100%;
	min-height: 64px;
	resize: none;
	padding: 0 2px;
	border: 0;
	outline: none;
	background: transparent;
	color: #20242a;
	font: 14px/1.65 var(--font-ui);
}

.composer-input::placeholder {
	color: #8f9aab;
}

.composer-input:disabled {
	opacity: 0.68;
}

.composer-toolbar {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
	min-height: 40px;
	padding-top: 4px;
}

.composer-tools-left,
.composer-tools-right {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	min-width: 0;
}

.composer-model {
	display: inline-flex;
	align-items: center;
	gap: 7px;
	padding: 5px 10px 5px 8px;
	border: 1px solid #e5e7eb;
	border-radius: 8px;
	background: transparent;
	color: #161a20;
	font-size: 12px;
	font-weight: 500;
	letter-spacing: 0.02em;
	white-space: nowrap;
	cursor: pointer;
	transition: background 0.15s;
}

.composer-model:hover {
	background: #f7f8fa;
}

.model-dot {
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: #17a34a;
}

.icon-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 32px;
	height: 32px;
	padding: 0;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: #6b7280;
	cursor: pointer;
	transition:
		background 0.15s,
		color 0.15s;
}

.icon-btn:hover {
	background: #f3f4f6;
	color: #171a1f;
}

.icon-btn svg {
	width: 18px;
	height: 18px;
	fill: none;
	stroke: currentColor;
	stroke-width: 1.7;
	stroke-linecap: round;
	stroke-linejoin: round;
}

.send-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	width: 36px;
	height: 36px;
	border: 0;
	border-radius: 50%;
	background: #171a1f;
	color: #ffffff;
	cursor: pointer;
	transition:
		opacity 0.15s,
		transform 0.1s;
}

.send-btn:hover {
	opacity: 0.85;
}

.send-btn:active {
	transform: scale(0.94);
}

.send-btn:disabled {
	opacity: 0.28;
	cursor: default;
	transform: none;
}

.send-btn svg {
	width: 16px;
	height: 16px;
	fill: none;
	stroke: currentColor;
	stroke-width: 2;
	stroke-linecap: round;
	stroke-linejoin: round;
}

@media (max-width: 960px) {
	.composer-shell {
		width: calc(100% - 28px);
	}

	.empty-workspace .composer-shell {
		width: calc(100% - 28px);
	}
}
</style>
