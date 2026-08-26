<script setup>
import { Check } from 'lucide-vue-next';
import { useToast } from '../../composables/useToast';

const { toastState } = useToast();
</script>

<template>
	<!-- Teleport 到 body：避免被 Teleport 对话框（z-index 1200）的毛玻璃遮罩压住 -->
	<Teleport to="body">
		<div class="app-toast sc-root" :class="{ show: toastState.visible }" role="status" aria-live="polite">
			<Check :size="15" :stroke-width="2.4" class="app-toast-ico" aria-hidden="true" />
			<span>{{ toastState.text }}</span>
		</div>
	</Teleport>
</template>

<style scoped>
@import '../../styles/settings-console.css';

.app-toast {
	position: fixed;
	left: 50%;
	bottom: 26px;
	z-index: 1300;
	display: flex;
	align-items: center;
	gap: 9px;
	padding: 11px 18px;
	transform: translateX(-50%) translateY(24px);
	border: 1px solid var(--sc-line-2);
	border-radius: 10px;
	background: var(--sc-panel);
	color: var(--sc-text);
	box-shadow: 0 18px 48px rgba(23, 26, 31, 0.16);
	font-family: var(--sc-sans);
	font-size: 13px;
	font-weight: 500;
	opacity: 0;
	pointer-events: none;
	transition:
		opacity 0.22s,
		transform 0.28s var(--sc-ease-spring);
}

.app-toast-ico {
	color: var(--sc-ok);
}

.app-toast.show {
	transform: translateX(-50%) translateY(0);
	opacity: 1;
}
</style>
