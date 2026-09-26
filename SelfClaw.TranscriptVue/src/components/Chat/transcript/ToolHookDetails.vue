<script setup>
import { computed } from 'vue';
import { ShieldX, Pencil, MessageSquare, AlertTriangle, UserCheck } from 'lucide-vue-next';

const props = defineProps({
	// TranscriptToolHookView：intervention details recorded for one tool run.
	hook: { type: Object, required: true },
});

const feedback = computed(() => props.hook.feedback || []);
const ignoredFailures = computed(() => props.hook.ignoredFailures || []);
const argumentsModifiedBy = computed(() => props.hook.argumentsModifiedBy || []);
const approvalRequiredBy = computed(() => props.hook.approvalRequiredBy || []);
const hasBlock = computed(() => Boolean(props.hook.blockedBy));
const hasRewrite = computed(() => argumentsModifiedBy.value.length > 0);
const hasApproval = computed(() => approvalRequiredBy.value.length > 0);
const visible = computed(
	() => hasBlock.value || hasRewrite.value || hasApproval.value || feedback.value.length > 0 || ignoredFailures.value.length > 0,
);
</script>

<template>
	<section v-if="visible" class="tool-hook">
		<div v-if="hasBlock" class="hook-note blocked">
			<ShieldX class="hook-icon" :size="13" :stroke-width="1.9" aria-hidden="true" />
			<div class="hook-note-body">
				<span class="hook-note-title">被 <code>{{ hook.blockedBy }}</code> 拦截</span>
				<p v-if="hook.blockReason" class="hook-note-reason">{{ hook.blockReason }}</p>
			</div>
		</div>

		<div v-if="hasRewrite" class="hook-arguments">
			<div class="hook-arguments-head">
				<span class="hook-note-title">
					<Pencil :size="12" :stroke-width="1.9" aria-hidden="true" />
					参数已被修改
				</span>
				<span class="hook-modifiers">
					由 <code v-for="source in argumentsModifiedBy" :key="source">{{ source }}</code>
				</span>
			</div>
			<div class="hook-arguments-grid">
				<figure class="hook-arguments-column">
					<figcaption>原始参数</figcaption>
					<pre><code>{{ hook.originalArgumentsText }}</code></pre>
				</figure>
				<figure class="hook-arguments-column effective">
					<figcaption>实际执行参数</figcaption>
					<pre><code>{{ hook.effectiveArgumentsText }}</code></pre>
				</figure>
			</div>
		</div>

		<div v-if="hasApproval" class="hook-note approval">
			<UserCheck class="hook-icon" :size="13" :stroke-width="1.9" aria-hidden="true" />
			<div class="hook-note-body">
				<span class="hook-note-title">由 <code>{{ approvalRequiredBy.join(', ') }}</code> 要求确认</span>
			</div>
		</div>

		<ul v-if="feedback.length" class="hook-list feedback">
			<li v-for="(note, index) in feedback" :key="`feedback-${index}`">
				<MessageSquare class="hook-icon" :size="12" :stroke-width="1.9" aria-hidden="true" />
				<code class="hook-source">{{ note.source }}</code>
				<span class="hook-text">{{ note.text }}</span>
			</li>
		</ul>

		<ul v-if="ignoredFailures.length" class="hook-list ignored">
			<li v-for="(note, index) in ignoredFailures" :key="`ignored-${index}`">
				<AlertTriangle class="hook-icon" :size="12" :stroke-width="1.9" aria-hidden="true" />
				<span class="hook-text">
					<code class="hook-source">{{ note.source }}</code> 失败（{{ note.text }}），已忽略
				</span>
			</li>
		</ul>
	</section>
</template>

<style scoped>
.tool-hook {
	display: grid;
	gap: 8px;
	margin-top: 8px;
	padding-top: 8px;
	border-top: 1px dashed var(--card-line);
}

.hook-note {
	display: flex;
	align-items: flex-start;
	gap: 7px;
	padding: 7px 9px;
	border-left: 2px solid var(--card-line);
	border-radius: 5px;
	background: var(--panel-soft);
	font-size: var(--fs-115);
	line-height: 1.5;
}

.hook-note.blocked {
	border-left-color: var(--danger);
}

.hook-note.approval {
	border-left-color: var(--caution);
}

.hook-icon {
	flex: none;
	margin-top: 2px;
	color: var(--muted-soft);
}

.hook-note.blocked .hook-icon {
	color: var(--danger);
}

.hook-note.approval .hook-icon {
	color: var(--caution-icon);
}

.hook-note-body {
	display: grid;
	gap: 2px;
	min-width: 0;
}

.hook-note-title {
	display: inline-flex;
	align-items: center;
	gap: 4px;
	color: var(--text-strong);
	font-weight: 600;
	overflow-wrap: anywhere;
}

.hook-note-reason {
	margin: 0;
	color: var(--muted);
	overflow-wrap: anywhere;
}

code {
	font-family: var(--font-mono);
	font-size: var(--fs-10);
}

.hook-arguments {
	display: grid;
	gap: 6px;
}

.hook-arguments-head {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 8px;
	color: var(--muted);
	font-size: var(--fs-11);
}

.hook-modifiers {
	display: inline-flex;
	flex-wrap: wrap;
	gap: 5px;
	color: var(--muted-soft);
}

.hook-modifiers code {
	padding: 1px 5px;
	border: 1px solid var(--card-line);
	border-radius: 4px;
	background: var(--panel-soft);
}

.hook-arguments-grid {
	display: grid;
	grid-template-columns: repeat(auto-fit, minmax(0, 1fr));
	gap: 8px;
}

.hook-arguments-column {
	margin: 0;
	overflow: hidden;
	border: 1px solid var(--card-nested-line);
	border-radius: 7px;
	background: var(--card-nested-surface);
}

.hook-arguments-column.effective {
	border-color: color-mix(in srgb, var(--caution) 40%, var(--card-nested-line));
}

.hook-arguments-column figcaption {
	padding: 5px 9px;
	border-bottom: 1px solid var(--card-nested-line);
	color: var(--muted-soft);
	font-size: var(--fs-10);
	letter-spacing: 0.08em;
	text-transform: uppercase;
}

.hook-arguments-column pre {
	max-height: 180px;
	margin: 0;
	padding: 9px;
	background: transparent;
	overflow: auto;
	white-space: pre-wrap;
	overflow-wrap: anywhere;
}

.hook-arguments-column code {
	font-size: var(--fs-105);
	line-height: 1.55;
}

.hook-list {
	display: grid;
	gap: 5px;
	margin: 0;
	padding: 0;
	list-style: none;
}

.hook-list li {
	display: flex;
	align-items: baseline;
	gap: 6px;
	font-size: var(--fs-11);
	line-height: 1.5;
	overflow-wrap: anywhere;
}

.hook-list.feedback li {
	color: var(--text-soft);
}

.hook-list.ignored li {
	color: var(--muted-soft);
}

.hook-list.feedback .hook-icon {
	color: var(--muted-soft);
}

.hook-list.ignored .hook-icon {
	color: var(--caution-icon);
}

.hook-source {
	flex: none;
	padding: 1px 5px;
	border: 1px solid var(--card-line);
	border-radius: 4px;
	background: var(--panel-soft);
	color: var(--muted);
}

.hook-text {
	min-width: 0;
	white-space: pre-wrap;
}
</style>
