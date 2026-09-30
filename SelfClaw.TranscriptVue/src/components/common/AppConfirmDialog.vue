<script setup>
import { nextTick, onMounted, onUnmounted, ref, watch } from 'vue';
import { AlertTriangle, CircleHelp } from 'lucide-vue-next';
import { useConfirm } from '../../composables/useConfirm';

const { confirmState, resolveConfirm } = useConfirm();

const confirmButton = ref(null);
const cancelButton = ref(null);
let restoreFocus = null;

watch(
	() => confirmState.open,
	async (open) => {
		if (open) {
			restoreFocus = document.activeElement;
			await nextTick();
			// 危险操作把初始焦点给取消：回车不该顺手完成一个破坏性动作。
			(confirmState.danger ? cancelButton.value : confirmButton.value)?.focus();
			return;
		}

		// 同一个 tick 内被新确认框顶掉时，原焦点元素可能已随上一个弹框移除，不再恢复。
		if (restoreFocus?.isConnected) {
			restoreFocus.focus();
		}

		restoreFocus = null;
	},
);

// Esc 取消。监听挂在 document 上：焦点可能停在按钮之外（例如刚打开尚未聚焦）。
function onKeydown(event) {
	if (!confirmState.open || event.key !== 'Escape') {
		return;
	}

	event.preventDefault();
	resolveConfirm(false);
}

// 只有两个按钮，Tab 在它们之间循环，焦点不会跑到背景页面上。
function onTabKeydown(event) {
	if (event.key !== 'Tab') {
		return;
	}

	const order = [cancelButton.value, confirmButton.value].filter(Boolean);
	const first = order[0];
	const last = order[order.length - 1];
	if (!first || !last) {
		return;
	}

	if (event.shiftKey && document.activeElement === first) {
		event.preventDefault();
		last.focus();
	} else if (!event.shiftKey && document.activeElement === last) {
		event.preventDefault();
		first.focus();
	}
}

onMounted(() => document.addEventListener('keydown', onKeydown));
onUnmounted(() => document.removeEventListener('keydown', onKeydown));
</script>

<template>
	<!-- Teleport 到 body：与 AppToast 同理，避免被设置页对话框的毛玻璃遮罩裁剪。 -->
	<Teleport to="body">
		<Transition name="confirm-fade">
			<div v-if="confirmState.open" class="confirm-backdrop sc-root" @mousedown.self="resolveConfirm(false)">
				<section class="confirm" :class="{ danger: confirmState.danger }" role="dialog" aria-modal="true"
					aria-labelledby="app-confirm-title"
					:aria-describedby="confirmState.message ? 'app-confirm-message' : undefined"
					@keydown="onTabKeydown">
					<header class="confirm-head">
						<span class="confirm-mark" aria-hidden="true">
							<AlertTriangle v-if="confirmState.danger" :size="17" :stroke-width="2.2" />
							<CircleHelp v-else :size="17" :stroke-width="2.2" />
						</span>
						<div class="confirm-copy">
							<span class="confirm-kicker">{{ confirmState.danger ? 'DANGER' : 'CONFIRM' }}</span>
							<h2 id="app-confirm-title">{{ confirmState.title }}</h2>
						</div>
					</header>

					<p v-if="confirmState.message" id="app-confirm-message" class="confirm-message">{{
						confirmState.message }}</p>

					<footer class="confirm-actions">
						<button ref="cancelButton" type="button" class="confirm-btn ghost" @click="resolveConfirm(false)">
							{{ confirmState.cancelText }}
						</button>
						<button ref="confirmButton" type="button" class="confirm-btn primary"
							:class="{ danger: confirmState.danger }" @click="resolveConfirm(true)">
							{{ confirmState.confirmText }}
						</button>
					</footer>
				</section>
			</div>
		</Transition>
	</Teleport>
</template>

<style scoped>
@import '../../styles/settings-console.css';

.confirm-backdrop {
	position: fixed;
	inset: 0;
	/* 高于设置页对话框（1200）、低于 AppToast（1300）：确认期间弹出的错误提示仍能看见。 */
	z-index: 1250;
	display: grid;
	place-items: center;
	padding: 24px;
	background: var(--overlay);
	backdrop-filter: blur(4px);
	font-family: var(--sc-sans);
}

.confirm {
	width: min(420px, 100%);
	overflow: hidden;
	border: 1px solid var(--sc-line-2);
	border-radius: 14px;
	background: var(--sc-panel);
	box-shadow: 0 32px 90px rgba(var(--shadow-ink), 0.22);
	color: var(--sc-text);
	animation: sc-pop 0.24s var(--sc-ease-out);
}

.confirm-head {
	display: flex;
	align-items: flex-start;
	gap: 13px;
	padding: 22px 22px 0;
}

.confirm-mark {
	display: grid;
	width: 34px;
	height: 34px;
	flex: 0 0 auto;
	place-items: center;
	border-radius: 10px;
	background: color-mix(in srgb, var(--sc-acid) 12%, transparent);
	color: var(--sc-acid);
}

.confirm.danger .confirm-mark {
	background: color-mix(in srgb, var(--danger) 12%, transparent);
	color: var(--danger);
}

.confirm-copy {
	min-width: 0;
}

.confirm-kicker {
	display: block;
	margin: 1px 0 4px;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-95);
	font-weight: 650;
	letter-spacing: 0.22em;
}

.confirm.danger .confirm-kicker {
	color: color-mix(in srgb, var(--danger) 70%, var(--sc-faint));
}

h2 {
	margin: 0;
	overflow-wrap: anywhere;
	font-family: var(--sc-display);
	font-size: var(--fs-17);
	font-weight: 640;
	line-height: 1.32;
}

.confirm-message {
	margin: 12px 22px 0;
	color: var(--sc-mute);
	font-size: var(--fs-125);
	line-height: 1.65;
	white-space: pre-line;
	overflow-wrap: anywhere;
}

.confirm-actions {
	display: flex;
	justify-content: flex-end;
	gap: 8px;
	padding: 20px 22px 18px;
}

.confirm-btn {
	display: inline-flex;
	align-items: center;
	justify-content: center;
	min-width: 84px;
	height: 36px;
	padding: 0 16px;
	border: 1px solid transparent;
	border-radius: 9px;
	font-family: inherit;
	font-size: var(--fs-13);
	font-weight: 600;
	cursor: pointer;
	transition: background 0.15s, border-color 0.15s, color 0.15s, transform 0.12s;
}

.confirm-btn:focus-visible {
	outline: none;
	box-shadow: 0 0 0 3px var(--sc-acid-soft);
}

.confirm-btn:active {
	transform: translateY(1px);
}

.confirm-btn.ghost {
	border-color: var(--sc-line-2);
	background: var(--sc-panel);
	color: var(--sc-mute);
}

.confirm-btn.ghost:hover {
	background: var(--sc-hover);
	color: var(--sc-text);
}

.confirm-btn.primary {
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
}

.confirm-btn.primary:hover {
	background: color-mix(in srgb, var(--accent) 86%, var(--ink-1));
}

/* 危险按钮的填充用 --danger，文字用会随主题翻转的 --accent-ink：
   浅色下红底白字，深色下浅红底深字，两种主题都能读。 */
.confirm-btn.primary.danger {
	background: var(--danger);
	color: var(--accent-ink);
}

.confirm-btn.primary.danger:hover {
	background: color-mix(in srgb, var(--danger) 86%, var(--ink-1));
}

.confirm-btn.primary.danger:focus-visible {
	box-shadow: 0 0 0 3px color-mix(in srgb, var(--danger) 20%, transparent);
}

.confirm-fade-enter-active,
.confirm-fade-leave-active {
	transition: opacity 0.16s ease;
}

.confirm-fade-enter-from,
.confirm-fade-leave-to {
	opacity: 0;
}

.confirm-fade-leave-active .confirm {
	animation: none;
	transform: translateY(6px) scale(0.985);
	opacity: 0;
	transition: transform 0.13s ease, opacity 0.13s ease;
}

@media (prefers-reduced-motion: reduce) {

	/* settings-console 的降级规则只覆盖 .sc-root 的后代，过渡类在根元素上，这里补一条。 */
	.confirm-fade-enter-active,
	.confirm-fade-leave-active {
		transition-duration: 0.001ms;
	}
}
</style>
