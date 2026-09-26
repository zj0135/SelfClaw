<script setup>
import { computed } from 'vue';
import { ArrowRight } from 'lucide-vue-next';

const props = defineProps({
	// PluginHookView[] resolved from the installed manifest.
	hooks: { type: Array, default: () => [] },
});

const rows = computed(() => props.hooks || []);
</script>

<template>
	<section class="hook-list" aria-label="Hook 列表">
		<h3>Hooks <span>{{ rows.length }}</span></h3>
		<p v-if="!rows.length" class="empty">此插件未声明 hook。</p>
		<ul v-else class="rows">
			<li v-for="hook in rows" :key="hook.id" class="hook-row">
				<div class="hook-head">
					<code class="hook-id">{{ hook.id }}</code>
					<span class="hook-event">{{ hook.event }}</span>
					<span class="hook-timeout">{{ hook.timeoutSeconds }}s</span>
					<span v-if="hook.onFailure" class="hook-tag">{{ hook.onFailure }}</span>
					<span v-if="hook.isAsync" class="hook-tag">async</span>
					<span v-if="hook.includeRequestBody" class="hook-tag caution">body</span>
				</div>
				<div class="hook-match">
					<span>{{ hook.matcherSummary }}</span>
					<ArrowRight :size="12" aria-hidden="true" />
					<code class="hook-command">{{ hook.commandLine }}</code>
				</div>
			</li>
		</ul>
	</section>
</template>

<style scoped>
@import '../../../styles/settings-console.css';

.hook-list {
	display: grid;
	gap: 9px;
	margin: 0 0 18px;
}

h3 {
	display: flex;
	align-items: baseline;
	gap: 7px;
	margin: 0;
	color: var(--sc-text);
	font-size: var(--fs-11);
	font-weight: 650;
	letter-spacing: 0.06em;
	text-transform: uppercase;
}

h3 span {
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.empty {
	margin: 0;
	color: var(--sc-faint);
	font-size: var(--fs-11);
}

.rows {
	display: grid;
	gap: 6px;
	margin: 0;
	padding: 0;
	list-style: none;
}

.hook-row {
	display: grid;
	gap: 5px;
	padding: 9px 10px;
	border: 1px solid var(--sc-line);
	border-radius: 6px;
	background: var(--sc-panel);
}

.hook-head {
	display: flex;
	align-items: center;
	flex-wrap: wrap;
	gap: 7px;
}

.hook-id {
	color: var(--sc-text);
	font-family: var(--sc-mono);
	font-size: var(--fs-11);
	font-weight: 650;
}

.hook-event {
	padding: 1px 6px;
	border-radius: 999px;
	background: var(--sc-acid-soft, var(--sc-raise));
	color: var(--sc-acid);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.hook-timeout {
	margin-left: auto;
	color: var(--sc-faint);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.hook-tag {
	padding: 1px 6px;
	border: 1px solid var(--sc-line-2);
	border-radius: 4px;
	color: var(--sc-mute);
	font-family: var(--sc-mono);
	font-size: var(--fs-9);
}

.hook-tag.caution {
	border-color: var(--caution-line);
	color: var(--caution-icon);
}

.hook-match {
	display: flex;
	align-items: center;
	gap: 7px;
	min-width: 0;
	color: var(--sc-faint);
	font-size: var(--fs-10);
}

.hook-match > span {
	flex: none;
	max-width: 40%;
	overflow: hidden;
	text-overflow: ellipsis;
	white-space: nowrap;
}

.hook-command {
	min-width: 0;
	flex: 1;
	overflow: hidden;
	color: var(--sc-soft);
	font-family: var(--sc-mono);
	font-size: var(--fs-10);
	text-overflow: ellipsis;
	white-space: nowrap;
}
</style>
