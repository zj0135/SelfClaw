<script setup>
import { computed } from 'vue';
import { Activity, SquareTerminal } from 'lucide-vue-next';
import { useSideDock } from '../../composables/useSideDock.js';

const dock = useSideDock();
const state = dock.state;
const TOOL_ICONS = { terminal: SquareTerminal, subagents: Activity };
const isActive = (toolId) => state.open && state.activeTab === toolId;
// 角点只表达"有子代理任务"，不显示数字；运行中时缓慢呼吸，安静即可。
const subagentPresent = computed(() => state.subagents.total > 0);
const subagentRunning = computed(() => state.subagents.running > 0);
</script>

<template>
	<nav class="dock-rail" aria-label="功能面板快捷入口">
		<button v-for="tool in dock.tools" :key="tool.id" class="dock-rail-button" :class="{ active: isActive(tool.id) }"
			type="button" :title="tool.label" :aria-label="tool.label" :aria-pressed="isActive(tool.id)" :data-tool="tool.id"
			@click="dock.toggleTool(tool.id)">
			<component :is="TOOL_ICONS[tool.id]" :size="16" :stroke-width="1.7" />
			<span v-if="tool.id === 'subagents' && subagentPresent" class="dock-rail-dot" :class="{ running: subagentRunning }"
				aria-hidden="true"></span>
		</button>
	</nav>
</template>

<style scoped>
.dock-rail {
	position: absolute;
	top: 14px;
	right: 18px;
	z-index: 6;
	display: flex;
	flex-direction: column;
	gap: 4px;
	padding: 4px;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--panel);
	box-shadow: var(--card-shadow);
}

.dock-rail-button {
	position: relative;
	display: grid;
	place-items: center;
	width: 30px;
	height: 30px;
	border: 0;
	border-radius: 7px;
	background: transparent;
	color: var(--muted);
	transition: background 0.14s, color 0.14s;
}

.dock-rail-button:hover {
	background: var(--panel-hover);
	color: var(--text);
}

.dock-rail-button.active {
	background: var(--accent-soft);
	color: var(--accent);
}

.dock-rail-dot {
	position: absolute;
	top: 5px;
	right: 5px;
	width: 6px;
	height: 6px;
	border-radius: 50%;
	background: var(--accent);
	box-shadow: 0 0 0 2px var(--panel), 0 0 6px color-mix(in srgb, var(--accent) 70%, transparent);
}

.dock-rail-dot.running {
	animation: dock-rail-breathe 2.2s ease-in-out infinite;
}

@keyframes dock-rail-breathe {
	0%,
	100% {
		box-shadow: 0 0 0 2px var(--panel), 0 0 4px color-mix(in srgb, var(--accent) 45%, transparent);
	}

	50% {
		box-shadow: 0 0 0 2px var(--panel), 0 0 9px color-mix(in srgb, var(--accent) 90%, transparent);
	}
}

@media (prefers-reduced-motion: reduce) {
	.dock-rail-dot.running {
		animation: none;
	}
}
</style>
