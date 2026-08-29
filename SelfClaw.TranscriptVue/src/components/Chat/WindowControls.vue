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
	<div class="window-controls" aria-label="Window controls">
		<div class="window-tool-group" aria-label="Workspace tools">
			<button class="chrome-button tool-button" type="button" title="Terminal" aria-label="Terminal" @click="send('terminal')">
				<SquareTerminal :size="15" :stroke-width="1.7" />
			</button>
		</div>

		<div class="caption-group" aria-label="Window actions">
			<button class="chrome-button caption-button" type="button" title="Minimize" aria-label="Minimize" @click="send('minimize')">
				<Minus :size="15" :stroke-width="1.8" />
			</button>
			<button
				class="chrome-button caption-button"
				type="button"
				:title="props.isMaximized ? 'Restore' : 'Maximize'"
				:aria-label="props.isMaximized ? 'Restore' : 'Maximize'"
				@click="send('toggle-maximize')"
			>
				<Copy v-if="props.isMaximized" :size="13" :stroke-width="1.8" />
				<Square v-else :size="13" :stroke-width="1.8" />
			</button>
			<button class="chrome-button caption-button close-button" type="button" title="Close" aria-label="Close" @click="send('close')">
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
	color: #171a1f;
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
	border-right: 1px solid rgba(19, 27, 45, 0.12);
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
	background: #eef0f4;
}

.chrome-button:active {
	background: #e3e6ec;
}

.tool-button {
	width: 34px;
	color: #454c59;
}

.tool-button:hover {
	color: var(--accent, #3b5bfd);
}

.caption-button {
	width: 44px;
	color: #171a1f;
}

.close-button:hover {
	background: #e81123;
	color: #ffffff;
}

.close-button:active {
	background: #c50f1f;
}
</style>
