import { onUnmounted, ref } from 'vue';
export function useActivityClock() {
	const now = ref(Date.now());
	const timer = window.setInterval(() => { now.value = Date.now(); }, 1000);
	onUnmounted(() => window.clearInterval(timer));
	return now;
}
