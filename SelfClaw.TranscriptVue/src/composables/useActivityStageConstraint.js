import { onMounted, onUnmounted, ref } from 'vue';

const MINIMUM_STAGE_HEIGHT = 180;

/** 舞台过矮时面板强制折叠；面板自身的尺寸由 CSS 决定，不参与文档流。 */
export function useActivityStageConstraint(stage) {
	const constrained = ref(false);
	let observer;
	onMounted(() => {
		observer = new ResizeObserver((entries) => {
			for (const entry of entries) {
				if (entry.target === stage.value) constrained.value = entry.contentRect.height < MINIMUM_STAGE_HEIGHT;
			}
		});
		if (stage.value) observer.observe(stage.value);
	});
	onUnmounted(() => observer?.disconnect());
	return { constrained };
}
