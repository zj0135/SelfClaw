<script setup>
import { computed } from 'vue';
import { ChevronDown, FileText, X } from 'lucide-vue-next';
import { useActivityContent } from '../../composables/useActivityContent.js';
import { activityErrorLabel } from '../../renderers/activityLabels.js';
const props = defineProps({ detail: { type: Object, required: true }, readContent: { type: Function, required: true } });
const { selected, text, nextOffset, loading, error, close, load } = useActivityContent(computed(() => props.detail), props.readContent);
const references = computed(() => props.detail.content.filter((reference) => reference.isTruncated || reference.field === 'arguments'));
const label = (reference) => ({ taskText: '任务说明', markdown: '内容', arguments: '工具参数', detailText: '工具结果' }[reference.field] || '内容');
</script>

<template>
	<div v-if="references.length" class="content-reader">
		<div class="content-links"><button v-for="reference in references" :key="reference.contentId" type="button" @click="load(reference, false)"><FileText :size="12" />{{ label(reference) }}<small>{{ reference.totalCharacters.toLocaleString() }}</small></button></div>
		<section v-if="selected" class="content-window"><header><strong>{{ label(selected) }}</strong><button type="button" aria-label="关闭内容" title="关闭内容" @click="close"><X :size="13" /></button></header><pre>{{ text }}</pre><p v-if="error" role="alert">{{ activityErrorLabel(error) }}</p><button v-if="nextOffset !== null" class="more" type="button" :disabled="loading" @click="load()"><ChevronDown :size="13" />{{ loading ? '读取中...' : '继续读取' }}</button></section>
	</div>
</template>

<style scoped>
.content-reader { min-width: 0; border-top: 1px solid var(--border); margin-top: 10px; padding-top: 6px; }
.content-links { display: flex; gap: 4px 10px; flex-wrap: wrap; }.content-links button { display: flex; align-items: center; gap: 4px; border: 0; padding: 4px 0; background: transparent; color: var(--accent); font-size: var(--fs-11); }.content-links small { color: var(--muted); }
.content-window { border-top: 1px solid var(--border); margin-top: 6px; }header { display: flex; justify-content: space-between; padding: 8px 0; font-size: var(--fs-11); }header button { display: grid; place-items: center; border: 0; background: transparent; color: var(--muted); }
pre { max-height: 240px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; padding: 8px; margin: 0; background: var(--panel-soft); font: var(--fs-11)/1.6 var(--font-mono); }p { color: var(--danger); font-size: var(--fs-11); }.more { display: flex; align-items: center; gap: 5px; border: 0; background: transparent; color: var(--accent); padding: 8px 0; font-size: var(--fs-11); }
</style>
