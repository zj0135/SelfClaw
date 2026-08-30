<script setup>
import { onBeforeUnmount, onMounted, ref } from 'vue';

// 插件页面的宿主。sandbox 里 allow-same-origin 是必须的：去掉它 iframe 的源会变成不透明的
// null，既拿不到 per-plugin 存储，外壳也失去了 event.origin 这个身份依据。它与
// allow-scripts 同时出现通常危险，但仅当子框架与父文档同源时成立——这里插件主机名与应用
// 主机名不同，够不着父文档。
const props = defineProps({
	tab: { type: Object, required: true },
	active: { type: Boolean, default: false },
});

const emit = defineEmits(['register']);
const frameRef = ref(null);

onMounted(() => emit('register', props.tab.key, frameRef.value));
onBeforeUnmount(() => emit('register', props.tab.key, null));
</script>

<template>
	<div class="plugin-frame" :class="{ active }" :aria-hidden="!active">
		<iframe ref="frameRef" :src="tab.url" :title="tab.panel.title"
			sandbox="allow-scripts allow-same-origin allow-forms allow-modals" allow="" referrerpolicy="no-referrer"
			loading="eager"></iframe>
	</div>
</template>

<style scoped>
.plugin-frame {
	position: absolute;
	inset: 0;
	display: none;
}

.plugin-frame.active {
	display: block;
}

iframe {
	display: block;
	width: 100%;
	height: 100%;
	border: 0;
	background: #ffffff;
}
</style>
