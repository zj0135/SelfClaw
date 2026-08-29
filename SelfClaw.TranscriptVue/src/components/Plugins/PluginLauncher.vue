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

			<ul v-if="panels.length" class="panel-list">
				<li v-for="panel in panels" :key="panel.key">
					<button type="button" :disabled="openKeys.includes(panel.key)" @click="$emit('select', panel.key)">
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
	background: rgba(23, 26, 31, 0.28);
	backdrop-filter: blur(3px);
}

.launcher {
	width: min(420px, 100%);
	max-height: min(60vh, 520px);
	display: flex;
	flex-direction: column;
	overflow: hidden;
	border: 1px solid var(--border-strong, #d8dde5);
	border-radius: 14px;
	background: #ffffff;
	box-shadow: 0 24px 70px rgba(23, 26, 31, 0.22);
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
	border-bottom: 1px solid var(--border, #e5e7eb);
	color: #6b7280;
	font-size: 11px;
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
	color: #171a1f;
	font-size: 13px;
	text-align: left;
	transition: background 0.12s;
}

.panel-list button:hover:not(:disabled) {
	background: #f1f3f6;
}

.panel-list button:disabled {
	cursor: default;
	opacity: 0.55;
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
	color: #9aa1ad;
	font-family: var(--font-mono);
	font-size: 10px;
}

.panel-state {
	flex: none;
	color: var(--accent, #3b5bfd);
	font-size: 10px;
}

.empty {
	margin: 0;
	padding: 26px 16px;
	color: #9aa1ad;
	font-size: 12px;
	text-align: center;
}

footer {
	flex: none;
	padding: 8px;
	border-top: 1px solid var(--border, #e5e7eb);
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
	color: #6b7280;
	font-size: 12px;
	font-weight: 560;
	transition: background 0.12s, color 0.12s;
}

footer button:hover {
	background: #f1f3f6;
	color: #171a1f;
}
</style>
