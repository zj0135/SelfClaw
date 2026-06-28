<script setup>
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
				<svg viewBox="0 0 20 20" aria-hidden="true">
					<path d="M3 4.5h14v11H3z"></path>
					<path d="M6 7.2 8.4 10 6 12.8"></path>
					<path d="M10.2 12.8h3.6"></path>
				</svg>
			</button>
			<button class="chrome-button tool-button" type="button" title="Files" aria-label="Files" @click="send('files')">
				<svg viewBox="0 0 20 20" aria-hidden="true">
					<path d="M3 6.2h4.5l1.4 1.6H17v7.3a1.4 1.4 0 0 1-1.4 1.4H4.4A1.4 1.4 0 0 1 3 15.1z"></path>
					<path d="M3 6.2V4.8a1.3 1.3 0 0 1 1.3-1.3h4.1l1.4 1.7H17v2.6"></path>
				</svg>
			</button>
			<button class="chrome-button tool-button" type="button" title="Browser" aria-label="Browser" @click="send('browser')">
				<svg viewBox="0 0 20 20" aria-hidden="true">
					<circle cx="10" cy="10" r="7"></circle>
					<path d="M3.3 10h13.4"></path>
					<path d="M10 3.2c1.4 1.7 2.1 4 2.1 6.8s-.7 5.1-2.1 6.8"></path>
					<path d="M10 3.2c-1.4 1.7-2.1 4-2.1 6.8s.7 5.1 2.1 6.8"></path>
				</svg>
			</button>
		</div>

		<div class="caption-group" aria-label="Window actions">
			<button class="chrome-button caption-button" type="button" title="Minimize" aria-label="Minimize" @click="send('minimize')">
				<svg viewBox="0 0 20 20" aria-hidden="true">
					<path d="M5 11h10"></path>
				</svg>
			</button>
			<button
				class="chrome-button caption-button"
				type="button"
				:title="props.isMaximized ? 'Restore' : 'Maximize'"
				:aria-label="props.isMaximized ? 'Restore' : 'Maximize'"
				@click="send('toggle-maximize')"
			>
				<svg v-if="props.isMaximized" viewBox="0 0 20 20" aria-hidden="true">
					<path d="M7 5h8v8"></path>
					<path d="M5 7h8v8H5z"></path>
				</svg>
				<svg v-else viewBox="0 0 20 20" aria-hidden="true">
					<path d="M5 5h10v10H5z"></path>
				</svg>
			</button>
			<button class="chrome-button caption-button close-button" type="button" title="Close" aria-label="Close" @click="send('close')">
				<svg viewBox="0 0 20 20" aria-hidden="true">
					<path d="M6 6 14 14"></path>
					<path d="M14 6 6 14"></path>
				</svg>
			</button>
		</div>
	</div>
</template>

<style scoped>
.window-controls {
	position: fixed;
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
	border-right: 1px solid rgba(23, 26, 31, 0.12);
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
}

.chrome-button:hover {
	background: #f1f3f6;
}

.chrome-button:active {
	background: #e5e7eb;
}

.tool-button {
	width: 34px;
	color: #252b34;
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

.chrome-button svg {
	width: 16px;
	height: 16px;
	fill: none;
	stroke: currentColor;
	stroke-width: 1.55;
	stroke-linecap: round;
	stroke-linejoin: round;
}

.caption-button svg {
	width: 14px;
	height: 14px;
}
</style>
