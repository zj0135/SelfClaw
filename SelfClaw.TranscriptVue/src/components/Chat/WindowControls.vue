<script setup>
import { computed } from 'vue';
import { Layers, PanelRight, SquareTerminal, Minus, Square, Copy, X } from 'lucide-vue-next';

const props = defineProps({
	isMaximized: {
		type: Boolean,
		default: false,
	},
	// 右栏当前是否可见。按下时是「收起」，抬起时是「展开」，同一个按钮两个方向。
	panelVisible: {
		type: Boolean,
		default: false,
	},
	// 悬浮层是否被用户收起。它与右栏分开：插件盖住整个界面时，这颗按钮是宿主侧最直接的那条退路。
	floatingVisible: {
		type: Boolean,
		default: true,
	},
	// 两颗插件按钮各自是否有可显隐的内容。没有已打开的视图时它们是禁用态，而不是退回启动器：
	// 打开视图只有一个入口（左侧「插件」），标题栏只负责显隐。
	panelAvailable: {
		type: Boolean,
		default: false,
	},
	// 悬浮层是否被用户收起。它与右栏分开：插件盖住主对话区时，这颗按钮是宿主侧最直接的那条退路。
	floatingVisible: {
		type: Boolean,
		default: true,
	},
	floatingAvailable: {
		type: Boolean,
		default: false,
	},
});

const emit = defineEmits(['action']);

const panelTitle = computed(() => {
	if (!props.panelAvailable) return '插件面板：暂无已打开的视图';
	return props.panelVisible ? '隐藏插件面板' : '显示插件面板';
});

const floatingTitle = computed(() => {
	if (!props.floatingAvailable) return '悬浮视图：暂无已打开的视图';
	return props.floatingVisible ? '隐藏悬浮视图' : '显示悬浮视图';
});

function send(action) {
	emit('action', action);
}
</script>

<template>
	<div class="window-controls" aria-label="Window controls">
		<div class="window-tool-group" aria-label="Workspace tools">
			<button class="chrome-button tool-button" type="button" title="命令行" aria-label="Terminal"
				@click="send('terminal')">
				<SquareTerminal :size="15" :stroke-width="1.7" />
			</button>
			<button class="chrome-button tool-button" :class="{ on: props.panelVisible }" type="button"
				:title="panelTitle" :aria-label="panelTitle" :aria-pressed="props.panelAvailable ? props.panelVisible : undefined"
				:disabled="!props.panelAvailable" @click="send('toggle-panel')">
				<PanelRight :size="15" :stroke-width="1.7" />
			</button>
			<button class="chrome-button tool-button" :class="{ on: props.floatingAvailable && props.floatingVisible }"
				type="button" :title="floatingTitle" :aria-label="floatingTitle"
				:aria-pressed="props.floatingAvailable ? props.floatingVisible : undefined"
				:disabled="!props.floatingAvailable" @click="send('toggle-floating')">
				<Layers :size="15" :stroke-width="1.7" />
			</button>
		</div>

		<div class="caption-group" aria-label="Window actions">
			<button class="chrome-button caption-button" type="button" title="最小化" aria-label="Minimize"
				@click="send('minimize')">
				<Minus :size="15" :stroke-width="1.8" />
			</button>
			<button class="chrome-button caption-button" type="button" :title="props.isMaximized ? '还原' : '最大化'"
				:aria-label="props.isMaximized ? '还原' : '最大化'" @click="send('toggle-maximize')">
				<Copy v-if="props.isMaximized" :size="13" :stroke-width="1.8" />
				<Square v-else :size="13" :stroke-width="1.8" />
			</button>
			<button class="chrome-button caption-button close-button" type="button" title="关闭" aria-label="Close"
				@click="send('close')">
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
	transition: background 0.14s, color 0.14s;
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

/* 右栏开着的时候按钮自己就是状态指示，不用等 hover。 */
.tool-button.on {
	background: color-mix(in srgb, var(--accent) 10%, transparent);
	color: var(--accent);
}

.tool-button.on:hover {
	background: color-mix(in srgb, var(--accent) 16%, transparent);
}

/* 没有可显隐的内容：按钮仍然占位（用户得知道这颗按钮存在，也知道去左侧「插件」打开），
   但不吃点击。 */
.tool-button:disabled {
	cursor: default;
	color: color-mix(in srgb, var(--text-soft) 45%, transparent);
}

.tool-button:disabled:hover {
	background: transparent;
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
