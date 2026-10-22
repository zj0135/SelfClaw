<script setup>
import PluginFrame from './PluginFrame.vue';

// 主对话区的悬浮层：跟随主内容列（转录 + 输入区），不覆盖侧栏与右侧面板。
//
// 为什么必须限定在这一列（不是可选的审美）：
//   实测指针位于子 iframe 上时，父文档收不到任何 pointermove（宿主区 2 次、移到 iframe 上后仍是 2 次）。
//   外壳的命中测试跑在窗口 pointermove 上，因此悬浮层一旦与右栏的插件帧重叠，那一帧永远不会被唤醒
//   （可点不动，只有关掉右栏才行）。把悬浮层限定在没有其它 iframe 的主对话列，是让命中测试成立的前提。
//
// 坐标：本元素 position: absolute; inset: 0，包含块是 .main-content（position: relative），
// 因此插件的视口就是主对话区，插件坐标 = 主对话区局部坐标。宿主地标在推送前会换算到每个帧自己的
// 坐标系里（见 usePluginFrames.localizeAnchors），所以插件作者不需要关心原点差异。
// .main-content 的 overflow: hidden 顺带保证插件画不出这一列。
//
// 指针：默认 pointer-events: none，未声明交互矩形的像素对宿主完全透明。只有被命中测试选中的那一帧
// 才由 armedKey 临时设为 auto（见 useFloatingPointer.js）。
const props = defineProps({
	views: { type: Array, required: true },
	armedKey: { type: String, default: '' },
	hidden: { type: Boolean, default: false },
});

defineEmits(['register']);

function pointerEvents(key) {
	return props.armedKey === key ? 'auto' : 'none';
}
</script>

<template>
	<div class="plugin-floating-layer" :class="{ concealed: props.hidden }" aria-label="插件悬浮视图">
		<div v-for="view in views" :key="view.key" class="float-slot" :style="{ pointerEvents: pointerEvents(view.key) }">
			<PluginFrame :view="view" :url="view.url"
				@register="(key, element) => $emit('register', key, element)" />
		</div>
	</div>
</template>

<style scoped>
/* 主对话区之上、宿主自己的浮层（确认框、启动器、Toast）之下。标题栏在 .main-content 之外，
   本来就压不住，所以「顶部 46px 归宿主」这条旧限制随悬浮层一起消失了。 */
.plugin-floating-layer {
	position: absolute;
	inset: 0;
	z-index: 450;
	overflow: hidden;
	background: transparent;
	/* 根元素必须自带 pointer-events: none：没有悬浮视图时它就是一层盖满主对话区的空 div，
	   默认的 auto 会吃掉整列的点击。子元素用自己的值恢复可交互（inline 可以盖过继承值）。 */
	pointer-events: none;
}

.float-slot {
	position: absolute;
	inset: 0;
	/* 默认透明；只有被武装的那一帧由内联样式改写。 */
	pointer-events: none;
}

/* 整体收起（标题栏那颗开关）用 visibility，不用 display: none。理由不是性能而是正确性：
   display: none 会把背帧的视口变成 0×0、让 frame.getBoundingClientRect() 变成 (0,0)，于是
   ① 插件测到的 window.innerWidth/innerHeight 与所有矩形都是垃圾，② 外壳换算给它看的 anchors
   退化成窗口坐标（原点偏移错），③ 插件按 anchors 算好的位置会落到视口外。而且显示回来时没有
   任何事件能让它重算（anchors 没变、没有 resize 监听），于是「显示悬浮视图」看起来毫无反应。
   visibility: hidden 同样不绘制、不参与命中测试，但布局与视口尺寸保持不变。 */
.plugin-floating-layer.concealed {
	visibility: hidden;
}
</style>
