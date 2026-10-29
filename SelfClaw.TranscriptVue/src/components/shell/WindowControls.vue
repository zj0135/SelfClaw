<script setup>
import { SquareTerminal, Minus, Square, Copy, X } from 'lucide-vue-next';

const props = defineProps({
	isMaximized: {
		type: Boolean,
		default: false,
	},
});

const emit = defineEmits(['action']);

function send(action) {
	emit('action', action);
}
</script>

<template>
	<div
		class="window-controls"
		:class="{ 'is-maximized': props.isMaximized }"
		aria-label="Window controls"
	>
		<div class="window-tool-group" aria-label="Workspace tools">
			<button class="chrome-button tool-button" type="button" title="命令行" aria-label="Terminal" @click="send('terminal')">
				<SquareTerminal :size="15" :stroke-width="1.7" />
			</button>
		</div>

		<div class="caption-group" aria-label="Window actions">
			<button class="chrome-button caption-button" type="button" title="最小化" aria-label="Minimize" @click="send('minimize')">
				<Minus :size="15" :stroke-width="1.8" />
			</button>
			<button
				class="chrome-button caption-button"
				type="button"
				:title="props.isMaximized ? '还原' : '最大化'"
				:aria-label="props.isMaximized ? '还原' : '最大化'"
				@click="send('toggle-maximize')"
			>
				<Copy v-if="props.isMaximized" :size="13" :stroke-width="1.8" />
				<Square v-else :size="13" :stroke-width="1.8" />
			</button>
			<button
				class="chrome-button caption-button close-button"
				type="button"
				title="关闭"
				aria-label="Minimize to tray"
				@click="send('minimize-to-tray')"
			>
				<X :size="16" :stroke-width="1.8" />
			</button>
		</div>
	</div>
</template>

<style scoped>
.window-controls {
	position: absolute;
	top: 6px;
	right: 8px;
	z-index: 120;
	display: inline-flex;
	align-items: center;
	gap: 8px;
	height: 34px;
	color: var(--text);
	user-select: none;
}

/* 窗口最大化（全屏）适配：
   Windows 在 SingleBorderWindow 最大化时，系统 DWM 会将窗口向屏幕外扩展约 8px，
   使得工具栏在屏幕内的有效可见高度为 38px（46px - 8px）。
   未最大化时居中 top 为 6px（46 - 34 = 12px，上下各 6px）；
   最大化时若保持 top: 6px，顶端位于屏幕边缘 0px（甚至裁切 2px），底边到工具栏底线留空 6px，视觉上明显偏上。
   下移 4px（top: 10px）使得在 38px 可见工具栏中上下各留 2px（按钮距两端各留 3px），实现完美垂直居中。
   同时在按钮顶部扩展 3px 点击感应区，确保鼠标甩到屏幕最顶边缘时依然能直接命中。 */
.window-controls.is-maximized {
	top: 10px;
}

.window-controls.is-maximized .caption-button {
	position: relative;
}

.window-controls.is-maximized .caption-button::before {
	content: '';
	position: absolute;
	top: -3px;
	left: 0;
	right: 0;
	bottom: -3px;
}

.window-tool-group,
.caption-group {
	display: inline-flex;
	align-items: center;
	gap: 2px;
	height: 34px;
}

.window-tool-group {
	padding-right: 4px;
	border-right: 1px solid color-mix(in srgb, var(--text) 12%, transparent);
}

.chrome-button {
	display: inline-grid;
	place-items: center;
	height: 32px;
	padding: 0;
	border: 0;
	border-radius: 6px;
	background: transparent;
	color: inherit;
	transition:
		background 0.14s,
		color 0.14s;
}

.chrome-button:hover {
	background: var(--panel-hover);
}

.chrome-button:active {
	background: var(--card-border);
}

.tool-button {
	width: 34px;
	color: var(--text-soft);
}

.tool-button:hover {
	color: var(--accent);
}

.caption-button {
	width: 44px;
	color: var(--text);
}

/* 关闭键的红是 Windows 的窗口按钮约定色，两个主题下都一样，不进 token。
   白色描线同理：底色恒为这个红，跟着主题翻成深色反而看不清。 */
.close-button:hover {
	background: #e81123;
	color: #ffffff;
}

.close-button:active {
	background: #c50f1f;
}
</style>
