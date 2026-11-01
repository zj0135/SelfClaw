import { ref } from 'vue';

const STORAGE_KEY = 'selfclaw:review-files-collapsed';

function readCollapsed() {
	try { return window.localStorage.getItem(STORAGE_KEY) === 'true'; } catch { return false; }
}

// 审查面板的布局偏好：右侧文件列表是否折叠。跨会话记住，和 dock 宽度一样只在用户操作时写入。
export function useReviewLayout() {
	const filesCollapsed = ref(readCollapsed());

	function toggleFiles() {
		filesCollapsed.value = !filesCollapsed.value;
		try { window.localStorage.setItem(STORAGE_KEY, String(filesCollapsed.value)); } catch { /* 持久化失败不影响本次会话 */ }
	}

	return { filesCollapsed, toggleFiles };
}
