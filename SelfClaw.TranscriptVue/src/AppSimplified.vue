<script setup>
import { ref } from 'vue';
import CenterContent from './components/CenterContent.vue';

// 简化版 App，只负责中间内容区域
// 左侧和右侧由 WPF 负责
</script>

<template>
	<div class="app-simplified">
		<CenterContent />
	</div>
</template>

<style>
* {
	margin: 0;
	padding: 0;
	box-sizing: border-box;
}

html,
body,
#app {
	width: 100%;
	height: 100%;
	overflow: hidden;
}

.app-simplified {
	width: 100%;
	height: 100%;
	font-family: 'Segoe UI Variable', 'Segoe UI', system-ui, -apple-system, sans-serif;
	-webkit-font-smoothing: antialiased;
	-moz-osx-font-smoothing: grayscale;
}
</style>
