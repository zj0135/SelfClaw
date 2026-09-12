import { onMounted, onUnmounted, ref } from 'vue';
export function useActivityPanelSize(element, stage) {
	const height = ref(0);
	const constrained = ref(false);
	let observer;
	onMounted(() => {
		observer = new ResizeObserver((entries) => {
			for (const entry of entries) {
				if (entry.target === element.value) height.value = entry.contentRect.height;
				if (entry.target === stage.value) constrained.value = entry.contentRect.height < 180;
			}
		});
		if (element.value) observer.observe(element.value);
		if (stage.value) observer.observe(stage.value);
	});
	onUnmounted(() => observer?.disconnect());
	return { height, constrained };
}
