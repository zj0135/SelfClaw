import { ref } from 'vue';

// 任务计划 dock 的折叠偏好：按会话记住用户的手动选择，默认值跟随忙碌态
// （忙碌展开、空闲收成一行），这样计划出现时不用点，回合结束后自动让位给转录。
// 与 useActivityPreferences 同样用模块级 Map + 32 项上限淘汰最早一项：
// 切过的旧会话迟早被更近的会话挤掉，不需要额外的清理时机。
const preferences = new Map();

export function useChatTodo(conversationIdRef) {
	// preferences 是普通 Map，读它不会建立响应式依赖；每次手动切换自增这个版本号，
	// isOpen 在模板求值时读一次，切换后模板就会重新求值。
	const revision = ref(0);

	function conversationKey() {
		return conversationIdRef?.value || '';
	}

	function isOpen(busy) {
		// 读一次版本号以获得响应式依赖（见上）。
		void revision.value;
		const remembered = preferences.get(conversationKey());
		return typeof remembered === 'boolean' ? remembered : Boolean(busy);
	}

	function toggle(busy) {
		const key = conversationKey();
		if (!preferences.has(key) && preferences.size >= 32) {
			preferences.delete(preferences.keys().next().value);
		}

		preferences.set(key, !isOpen(busy));
		revision.value += 1;
	}

	return { isOpen, toggle };
}
