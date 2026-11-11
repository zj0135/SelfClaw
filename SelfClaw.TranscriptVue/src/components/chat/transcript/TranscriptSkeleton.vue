<script setup>
import { Loader2 } from 'lucide-vue-next';
</script>

<template>
	<div class="transcript-skeleton-wrap" aria-label="正在加载对话历史" role="status" aria-live="polite">
		<!-- 居中微型状态提示 -->
		<div class="skeleton-status-pill">
			<Loader2 :size="13" class="spin-icon" aria-hidden="true" />
			<span>正在载入历史对话…</span>
		</div>

		<!-- 骨架消息流列表 -->
		<div class="skeleton-content">
			<!-- 模拟助手回复气泡 -->
			<div class="skeleton-row assistant">
				<div class="skeleton-avatar" />
				<div class="skeleton-lines">
					<div class="skeleton-bar" style="width: 78%;" />
					<div class="skeleton-bar" style="width: 92%;" />
					<div class="skeleton-bar" style="width: 45%;" />
				</div>
			</div>

			<!-- 模拟用户消息气泡 -->
			<div class="skeleton-row user">
				<div class="skeleton-lines right">
					<div class="skeleton-bar user-bubble" style="width: 220px;" />
				</div>
			</div>

			<!-- 模拟后续助手回复与工具块 -->
			<div class="skeleton-row assistant">
				<div class="skeleton-avatar" />
				<div class="skeleton-lines">
					<div class="skeleton-card" />
					<div class="skeleton-bar" style="width: 86%;" />
					<div class="skeleton-bar" style="width: 60%;" />
				</div>
			</div>
		</div>
	</div>
</template>

<style scoped>
.transcript-skeleton-wrap {
	flex: 1 1 auto;
	min-height: 0;
	display: flex;
	flex-direction: column;
	align-items: center;
	overflow: hidden;
	padding: 36px calc((var(--chat-inline-inset) - 2 * var(--scrollbar-size)) / 2) 24px;
	animation: skeleton-fade-in 0.24s cubic-bezier(0.22, 1, 0.36, 1) both;
	user-select: none;
	pointer-events: none;
}

@keyframes skeleton-fade-in {
	from {
		opacity: 0;
		transform: translateY(6px);
	}
	to {
		opacity: 1;
		transform: none;
	}
}

.skeleton-status-pill {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	padding: 4px 12px;
	margin-bottom: 28px;
	border: 1px solid color-mix(in srgb, var(--text) 8%, transparent);
	border-radius: 999px;
	background: color-mix(in srgb, var(--panel) 80%, transparent);
	color: var(--muted);
	font-size: var(--fs-115);
	font-weight: 500;
	box-shadow: 0 1px 3px rgba(var(--shadow-ink), 0.03);
}

.spin-icon {
	animation: skeleton-spin 1.1s linear infinite;
	color: var(--accent);
}

@keyframes skeleton-spin {
	to {
		transform: rotate(360deg);
	}
}

.skeleton-content {
	width: min(100%, var(--chat-content-width));
	margin-inline: auto;
	display: flex;
	flex-direction: column;
	gap: 28px;
}

.skeleton-row {
	display: flex;
	align-items: flex-start;
	gap: 12px;
	width: 100%;
}

.skeleton-row.user {
	justify-content: flex-end;
}

.skeleton-avatar {
	width: 28px;
	height: 28px;
	border-radius: 8px;
	flex: none;
	background: color-mix(in srgb, var(--text) 7%, transparent);
}

.skeleton-lines {
	flex: 1;
	min-width: 0;
	display: flex;
	flex-direction: column;
	gap: 10px;
}

.skeleton-lines.right {
	flex: none;
	align-items: flex-end;
}

/* 核心 Shimmer 动效条 */
.skeleton-bar {
	height: 15px;
	border-radius: 6px;
	background: linear-gradient(
		90deg,
		color-mix(in srgb, var(--text) 5%, transparent) 0%,
		color-mix(in srgb, var(--text) 11%, transparent) 50%,
		color-mix(in srgb, var(--text) 5%, transparent) 100%
	);
	background-size: 200% 100%;
	animation: shimmer-sweep 1.8s ease-in-out infinite;
}

.skeleton-bar.user-bubble {
	height: 34px;
	border-radius: 12px;
	background: linear-gradient(
		90deg,
		color-mix(in srgb, var(--accent) 10%, var(--panel)) 0%,
		color-mix(in srgb, var(--accent) 22%, var(--panel)) 50%,
		color-mix(in srgb, var(--accent) 10%, var(--panel)) 100%
	);
	background-size: 200% 100%;
}

.skeleton-card {
	width: 100%;
	height: 52px;
	border-radius: 10px;
	border: 1px solid color-mix(in srgb, var(--text) 6%, transparent);
	background: linear-gradient(
		90deg,
		color-mix(in srgb, var(--text) 3%, transparent) 0%,
		color-mix(in srgb, var(--text) 7%, transparent) 50%,
		color-mix(in srgb, var(--text) 3%, transparent) 100%
	);
	background-size: 200% 100%;
	animation: shimmer-sweep 1.8s ease-in-out infinite;
}

@keyframes shimmer-sweep {
	0% {
		background-position: 200% 0;
	}
	100% {
		background-position: -200% 0;
	}
}

@media (prefers-reduced-motion: reduce) {
	.spin-icon,
	.skeleton-bar,
	.skeleton-card {
		animation: none;
	}
}
</style>
