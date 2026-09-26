<script setup>
import { computed, ref } from 'vue';
import { ChevronRight, RefreshCw, ScrollText } from 'lucide-vue-next';

const props = defineProps({
	// PluginHookExecutionEntry[] pulled on demand; entries never carry payloads or stdout.
	entries: { type: Array, default: () => [] },
	loading: { type: Boolean, default: false },
});
defineEmits(['refresh']);

const expanded = ref(new Set());

const rows = computed(() =>
	(props.entries || []).map((entry) => ({
		...entry,
		time: new Date(entry.timestampUtc).toLocaleTimeString(),
		duration: entry.durationMs >= 1000 ? `${(entry.durationMs / 1000).toFixed(1)}s` : `${Math.round(entry.durationMs)}ms`,
		tone: resolveTone(entry.outcome),
	})),
);

function resolveTone(outcome) {
	switch (outcome) {
		case 'blocked':
			return 'blocked';
		case 'denied':
			return 'denied';
		case 'failed':
			return 'failed';
		case 'timedOut':
			return 'timedout';
		case 'dropped':
			return 'dropped';
		default:
			return 'observed';
	}
}

function toggle(id) {
	const next = new Set(expanded.value);
	if (next.has(id)) next.delete(id);
	else next.add(id);
	expanded.value = next;
}
</script>

<template>
	<section class="hook-log" aria-label="Hook 执行日志">
		<header>
			<h3><ScrollText :size="13" aria-hidden="true" />执行日志</h3>
			<button type="button" :disabled="loading" title="刷新执行日志" @click="$emit('refresh')">
				<RefreshCw :size="13" :class="{ spin: loading }" aria-hidden="true" />刷新
			</button>
		</header>
		<p v-if="!rows.length" class="empty">暂无执行记录（重启后清空）</p>
		<ul v-else class="rows">
			<li v-for="(entry, index) in rows" :key="`${entry.timestampUtc}-${index}`" class="entry">
				<div class="entry-main">
					<span class="entry-time">{{ entry.time }}</span>
					<code class="entry-hook">{{ entry.hookId }}</code>
					<span class="entry-event">{{ entry.event }}</span>
					<span class="entry-outcome" :class="entry.tone">{{ entry.outcome }}</span>
					<span class="entry-duration">{{ entry.duration }}</span>
					<span v-if="entry.exitCode !== null && entry.exitCode !== undefined" class="entry-exit">
						{{ entry.exitCode }}
					</span>
				</div>
				<div v-if="entry.detail" class="entry-detail">{{ entry.detail }}</div>
				<button v-if="entry.stderrTail" type="button" class="stderr-toggle"
					:aria-expanded="expanded.has(`${entry.timestampUtc}-${index}`) ? 'true' : 'false'"
					@click="toggle(`${entry.timestampUtc}-${index}`)">
					<ChevronRight :size="12" :class="{ open: expanded.has(`${entry.timestampUtc}-${index}`) }" aria-hidden="true" />
					stderr
				</button>
				<pre v-if="entry.stderrTail && expanded.has(`${entry.timestampUtc}-${index}`)" class="stderr">{{ entry.stderrTail }}</pre>
			</li>
		</ul>
	</section>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.hook-log {
	display: grid;
	gap: 9px;
	margin: 0 0 18px;
}

header {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 8px;
}

h3 {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	margin: 0;
	color: var(--sc-text);
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.06em;
	text-transform: uppercase;
}

header button {
	display: inline-flex;
	align-items: center;
	gap: 5px;
	height: 26px;
	padding: 0 9px;
	border: 1px solid var(--sc-line-2);
	border-radius: 5px;
	background: transparent;
	color: var(--sc-mute);
	font-size: var(--fs-10);
}

header button:disabled {
	cursor: wait;
	opacity: 0.55;
}

.empty {
	margin: 0;
	color: var(--sc-faint);
	font-size: var(--fs-11);
}

.rows {
	display: grid;
	gap: 4px;
	margin: 0;
	padding: 0;
	max-height: 320px;
	overflow-y: auto;
	list-style: none;
}

.entry {
	display: grid;
	gap: 4px;
	padding: 6px 8px;
	border: 1px solid var(--sc-line);
	border-radius: 5px;
	background: var(--sc-panel);
}

.entry-main {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 7px;
}

.entry-time {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.entry-hook {
	color: var(--sc-text);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
}

.entry-event {
	color: var(--sc-mute);
	font-size: var(--fs-10);
}

.entry-outcome {
	padding: 1px 6px;
	border-radius: 4px;
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	background: var(--sc-raise);
	color: var(--sc-soft);
}

.entry-outcome.blocked,
.entry-outcome.denied,
.entry-outcome.failed {
	background: var(--sc-err-soft);
	color: var(--sc-err);
}

.entry-outcome.timedout,
.entry-outcome.dropped {
	background: var(--caution-tint);
	color: var(--caution-icon);
}

.entry-duration,
.entry-exit {
	margin-left: auto;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.entry-exit {
	margin-left: 0;
}

.entry-detail {
	color: var(--sc-mute);
	font-size: var(--fs-10);
	line-height: 1.5;
	overflow-wrap: anywhere;
}

.stderr-toggle {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	justify-self: start;
	padding: 0;
	border: 0;
	background: transparent;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.stderr-toggle svg {
	transition: transform 140ms ease;
}

.stderr-toggle svg.open {
	transform: rotate(90deg);
}

.stderr {
	max-height: 180px;
	margin: 0;
	padding: 7px 8px;
	border-radius: 4px;
	background: var(--sc-raise);
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
	line-height: 1.5;
	overflow: auto;
	white-space: pre-wrap;
	overflow-wrap: anywhere;
}

.spin {
	animation: sc-spin 0.8s linear infinite;
}
</style>
