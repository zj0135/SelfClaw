<script setup>
import { Puzzle, Settings2 } from 'lucide-vue-next';

defineProps({
	open: { type: Boolean, default: false },
	panels: { type: Array, default: () => [] },
	openKeys: { type: Array, default: () => [] },
});

defineEmits(['close', 'select', 'manage']);
</script>

<template>
	<div v-if="open" class="launcher-backdrop" @click.self="$emit('close')">
		<div class="launcher" role="dialog" aria-label="打开插件面板">
			<header>
				<Puzzle :size="14" :stroke-width="1.8" />
				<span>插件面板</span>
			</header>

			<!-- 已打开的条目照样可点：面板被隐藏时，重新选它就是把右栏叫回来的那条路。
				 若在这里 disabled，面板全部打开又全部隐藏时右栏就再也回不来了。 -->
			<ul v-if="panels.length" class="panel-list">
				<li v-for="panel in panels" :key="panel.key">
					<button type="button" @click="$emit('select', panel.key)">
						<span class="panel-title">{{ panel.title }}</span>
						<code>{{ panel.pluginId }}</code>
						<span v-if="openKeys.includes(panel.key)" class="panel-state">已打开</span>
					</button>
				</li>
			</ul>
			<p v-else class="empty">还没有启用任何提供面板的插件。</p>

			<footer>
				<button type="button" @click="$emit('manage')">
					<Settings2 :size="13" :stroke-width="1.8" />管理插件
				</button>
			</footer>
		</div>
	</div>
</template>

<style scoped>
.launcher-backdrop {
	position: fixed;
	inset: 0;
	z-index: 500;
	display: flex;
	align-items: center;
	justify-content: center;
	padding: 24px;
	background: var(--overlay);
	backdrop-filter: blur(3px);
}

.launcher {
	width: min(420px, 100%);
	max-height: min(60vh, 520px);
	display: flex;
	flex-direction: column;
	overflow: hidden;
	border: 1px solid var(--border-strong);
	border-radius: 14px;
	background: var(--panel);
	box-shadow: 0 24px 70px rgba(var(--shadow-ink), 0.22);
	animation: launcher-pop 180ms cubic-bezier(0.22, 1, 0.36, 1);
}

@keyframes launcher-pop {
	from {
		opacity: 0;
		transform: translateY(8px) scale(0.98);
	}
}

header {
	display: flex;
	align-items: center;
	gap: 8px;
	flex: none;
	padding: 14px 16px 12px;
	border-bottom: 1px solid var(--border);
	color: var(--muted);
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.04em;
}

.panel-list {
	min-height: 0;
	flex: 1 1 auto;
	margin: 0;
	padding: 6px;
	overflow-y: auto;
	list-style: none;
}

.panel-list button {
	display: flex;
	align-items: baseline;
	width: 100%;
	gap: 10px;
	padding: 9px 10px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--text);
	font-size: var(--fs-13);
	text-align: left;
	transition: background 0.12s;
}

.panel-list button:hover {
	background: var(--panel-muted);
}

.panel-title {
	flex: 1 1 auto;
	overflow: hidden;
	font-weight: 560;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.panel-list code {
	flex: none;
	color: var(--faint);
	font-family: var(--font-mono);
	font-size: var(--fs-10);
}

.panel-state {
	flex: none;
	color: var(--accent);
	font-size: var(--fs-10);
}

.empty {
	margin: 0;
	padding: 26px 16px;
	color: var(--faint);
	font-size: var(--fs-12);
	text-align: center;
}

footer {
	flex: none;
	padding: 8px;
	border-top: 1px solid var(--border);
}

footer button {
	display: flex;
	align-items: center;
	justify-content: center;
	width: 100%;
	gap: 7px;
	height: 32px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: var(--muted);
	font-size: var(--fs-12);
	font-weight: 560;
	transition: background 0.12s, color 0.12s;
}

footer button:hover {
	background: var(--panel-muted);
	color: var(--text);
}
</style>
