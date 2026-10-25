<script setup>
import { computed } from 'vue';
import { Terminal, SquareTerminal, ChevronRight, GitBranch, Check } from 'lucide-vue-next';

const props = defineProps({
	shellKind: { type: String, default: 'WindowsPowerShell' },
	shells: { type: Array, default: () => [] },
});

const emit = defineEmits(['select']);

const icons = {
	WindowsPowerShell: Terminal,
	PowerShellSeven: SquareTerminal,
	CommandPrompt: ChevronRight,
	GitBash: GitBranch,
};

// 可用性完全由宿主回显决定：shells 里缺失或 available !== true 一律禁用，前端不做本地猜测。
const cards = computed(() => {
	const definitions = [
		{ kind: 'WindowsPowerShell', label: 'Windows PowerShell', en: 'POWERSHELL 5.1' },
		{ kind: 'PowerShellSeven', label: 'PowerShell 7', en: 'PWSH 7+' },
		{ kind: 'CommandPrompt', label: '命令提示符', en: 'CMD.EXE' },
		{ kind: 'GitBash', label: 'Git Bash', en: 'BASH' },
	];
	return definitions.map((definition) => {
		const shell = props.shells.find((item) => item.kind === definition.kind) || {};
		return {
			...definition,
			icon: icons[definition.kind],
			available: shell.available === true,
			resolvedPath: typeof shell.resolvedPath === 'string' ? shell.resolvedPath : '',
			active: props.shellKind === definition.kind,
		};
	});
});
</script>

<template>
	<div class="shell-grid">
		<button v-for="(card, index) in cards" :key="card.kind" type="button" class="shell-card sc-rise"
			:style="{ '--i': index }" :data-active="card.active ? 'true' : 'false'"
			:data-available="card.available ? 'true' : 'false'"
			:aria-pressed="card.active ? 'true' : 'false'" :disabled="!card.available"
			:title="card.available && card.resolvedPath ? card.resolvedPath : undefined"
			@click="card.available && emit('select', card.kind)">
			<span class="sh-meta">
				<span class="sh-head">
					<component :is="card.icon" :size="15" :stroke-width="1.9" class="sh-ico" aria-hidden="true" />
					<span class="sh-label">{{ card.label }}</span>
					<span class="sh-en">{{ card.en }}</span>
				</span>
				<span class="sh-path" :data-present="card.available && card.resolvedPath ? 'true' : 'false'">
					{{ card.available ? card.resolvedPath || '系统内置' : '未检测到' }}
				</span>
			</span>

			<span v-if="card.active" class="sh-check" aria-hidden="true">
				<Check :size="11" :stroke-width="3" />
			</span>
		</button>
	</div>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.shell-grid {
	display: grid;
	grid-template-columns: repeat(auto-fit, minmax(232px, 1fr));
	gap: 12px;
}

.shell-card {
	position: relative;
	display: flex;
	flex-direction: column;
	gap: 12px;
	padding: 13px 13px 15px;
	overflow: hidden;
	border: 1px solid var(--sc-line);
	border-radius: 13px;
	background: var(--sc-panel);
	color: inherit;
	font: inherit;
	text-align: left;
	cursor: pointer;
	transition:
		border-color 0.18s var(--sc-ease-out),
		transform 0.18s var(--sc-ease-out),
		box-shadow 0.18s var(--sc-ease-out);

	&:hover {
		border-color: var(--sc-line-2);
		transform: translateY(-2px);
		box-shadow: 0 14px 34px rgba(var(--shadow-ink), 0.09);
	}

	&:focus-visible {
		outline: none;
		border-color: var(--sc-acid);
		box-shadow: 0 0 0 3px var(--sc-acid-soft);
	}

	&[data-active='true'] {
		border-color: color-mix(in srgb, var(--sc-acid) 55%, transparent);
		background:
			radial-gradient(240px 140px at 12% 0%, var(--sc-acid-soft), transparent 70%),
			var(--sc-panel);
	}

	&[data-available='false'] {
		cursor: not-allowed;
		opacity: 0.55;

		&:hover {
			border-color: var(--sc-line);
			transform: none;
			box-shadow: none;
		}
	}
}

.sh-meta {
	display: flex;
	flex-direction: column;
	gap: 6px;
	min-width: 0;
}

.sh-head {
	display: flex;
	align-items: center;
	gap: 8px;
	min-width: 0;
}

.sh-ico {
	flex: none;
	color: var(--sc-mute);
	transition: color 0.16s;
}

.shell-card[data-active='true'] .sh-ico {
	color: var(--sc-acid);
}

.sh-label {
	color: var(--sc-text);
	font-size: var(--fs-135);
	font-weight: 620;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.sh-en {
	margin-left: auto;
	flex: none;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	font-weight: 600;
	letter-spacing: 0.18em;
}

.sh-path {
	overflow: hidden;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	letter-spacing: 0.04em;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.shell-card[data-available='false'] .sh-path {
	color: var(--sc-err);
}

.shell-card[data-active='true'] .sh-path {
	color: var(--sc-mute);
}

.sh-check {
	position: absolute;
	top: 13px;
	right: 13px;
	display: grid;
	place-items: center;
	width: 18px;
	height: 18px;
	border-radius: 50%;
	background: var(--sc-acid);
	color: var(--sc-acid-ink);
	box-shadow: 0 0 0 3px color-mix(in srgb, var(--sc-panel) 80%, transparent);
	animation: sc-pop 0.24s var(--sc-ease-spring) both;
}
</style>
