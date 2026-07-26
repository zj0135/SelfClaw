<script setup>
import { computed } from 'vue';

const props = defineProps({
	status: { type: String, default: 'broken' },
});

const labels = {
	ready: '可用',
	disabled: '已停用',
	'needs-config': '待配置',
	'needs-permission': '待授权',
	broken: '异常',
	connecting: '连接中',
	degraded: '受限',
};

const label = computed(() => labels[props.status] || props.status);
</script>

<template>
	<span class="status" :class="status"><span aria-hidden="true"></span>{{ label }}</span>
</template>

<style scoped>
@import '../settings-console.css';
.status { display: inline-flex; align-items: center; gap: 6px; min-height: 22px; color: var(--sc-mute); font-size: 11px; font-weight: 650; white-space: nowrap; }
.status span { width: 6px; height: 6px; border-radius: 50%; background: currentColor; }
.status.ready { color: var(--sc-ok); }
.status.disabled { color: var(--sc-faint); }
.status.needs-config, .status.needs-permission { color: #b7791f; }
.status.broken { color: var(--sc-err); }
.status.connecting { color: var(--sc-acid); }
</style>
