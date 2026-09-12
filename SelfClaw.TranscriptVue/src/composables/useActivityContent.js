import { ref, watch } from 'vue';

export function useActivityContent(detail, readContent) {
	const selected = ref(null);
	const text = ref('');
	const nextOffset = ref(null);
	const loading = ref(false);
	const error = ref('');
	let generation = 0;
	function close() { generation++; selected.value = null; text.value = ''; nextOffset.value = null; error.value = ''; loading.value = false; }
	watch(() => `${detail.value?.taskId}:${detail.value?.contentVersion}`, close);
	async function load(reference = selected.value, append = true) {
		if (!reference) return;
		const current = ++generation;
		if (!append) { selected.value = reference; text.value = ''; nextOffset.value = 0; }
		loading.value = true;
		error.value = '';
		try {
			const page = await readContent(reference, nextOffset.value ?? 0, detail.value?.contentVersion);
			if (current !== generation) return;
			text.value += page.text;
			nextOffset.value = page.nextOffset ?? null;
		} catch (failure) { if (current === generation) error.value = failure.message; }
		finally { if (current === generation) loading.value = false; }
	}
	return { selected, text, nextOffset, loading, error, close, load };
}
