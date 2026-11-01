import { nextTick } from 'vue';

// ComposerPanel 的输入区是 contenteditable（ComposerEditor），@vue/test-utils 的 setValue 只认表单控件：
// 这里直接写文本节点并派发 input，走和真实打字一致的序列化路径。
export const editorSelector = '.composer-editor';

export async function typeIntoEditor(wrapper, text) {
	const editor = wrapper.find(editorSelector);
	editor.element.textContent = text;
	await editor.trigger('input');
	await nextTick();
	return editor;
}
