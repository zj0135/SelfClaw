<script setup>
import { onBeforeUnmount, onMounted, ref } from 'vue';

// 一个插件视图对应的沙箱 iframe。定位、背景与显隐属于各自的容器（停靠栏 / 悬浮层），
// 所以这里只负责注册帧、sandbox 属性与尺寸。
//
// sandbox 里 allow-same-origin 是必须的：去掉它 iframe 的源会变成不透明的 null，既拿不到
// per-plugin 存储，外壳也失去了 event.origin 这个身份依据。它与 allow-scripts 同时出现通常危险，
// 但仅当子框架与父文档同源时成立——这里插件主机名与应用主机名不同，够不着父文档。
const props = defineProps({
	view: { type: Object, required: true },
	url: { type: String, required: true },
});

const emit = defineEmits(['register']);
const frameRef = ref(null);

onMounted(() => emit('register', props.view.key, frameRef.value));
onBeforeUnmount(() => emit('register', props.view.key, null));
</script>

<template>
	<iframe ref="frameRef" :src="url" :title="view.title"
		sandbox="allow-scripts allow-same-origin allow-forms allow-modals" allow="" referrerpolicy="no-referrer"
		loading="eager"></iframe>
</template>

<style scoped>
iframe {
	display: block;
	width: 100%;
	height: 100%;
	border: 0;
	background: transparent;
}
</style>
