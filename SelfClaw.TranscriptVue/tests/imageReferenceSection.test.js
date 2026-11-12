import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import ImageReferenceSection from '../src/components/image-generation/ImageReferenceSection.vue';
import ImagePreviewGrid from '../src/components/image-generation/ImagePreviewGrid.vue';

describe('ImageReferenceSection', () => {
	it('renders header with label and optional hint', () => {
		const wrapper = mount(ImageReferenceSection, {
			props: { referenceImage: null },
		});

		expect(wrapper.find('.field-label').text()).toBe('参考图片');
		expect(wrapper.find('.field-optional').text()).toBe('/ 可选');
		expect(wrapper.find('.upload-label').text()).toBe('添加参考图');
	});

	it('emits upload-reference when a file is selected', async () => {
		const wrapper = mount(ImageReferenceSection, {
			props: { referenceImage: null },
		});

		const file = new File(['fake-image-bytes'], 'reference.png', { type: 'image/png' });
		const fileInput = wrapper.find('.hidden-file-input');

		Object.defineProperty(fileInput.element, 'files', {
			value: [file],
			writable: false,
		});

		await fileInput.trigger('change');

		expect(wrapper.emitted('upload-reference')).toBeTruthy();
		expect(wrapper.emitted('upload-reference')[0][0]).toBe(file);
	});

	it('emits upload-reference on image drop', async () => {
		const wrapper = mount(ImageReferenceSection, {
			props: { referenceImage: null },
		});

		const file = new File(['fake-bytes'], 'dropped.webp', { type: 'image/webp' });
		const dropzone = wrapper.find('.dropzone-box');

		await dropzone.trigger('dragover');
		expect(wrapper.find('.dragging').exists()).toBe(true);

		await dropzone.trigger('drop', {
			dataTransfer: { files: [file] },
		});

		expect(wrapper.emitted('upload-reference')).toBeTruthy();
		expect(wrapper.emitted('upload-reference')[0][0]).toBe(file);
		expect(wrapper.find('.dragging').exists()).toBe(false);
	});

	it('ignores drop for non-image files', async () => {
		const wrapper = mount(ImageReferenceSection, {
			props: { referenceImage: null },
		});

		const textFile = new File(['hello'], 'document.txt', { type: 'text/plain' });
		const dropzone = wrapper.find('.dropzone-box');

		await dropzone.trigger('drop', {
			dataTransfer: { files: [textFile] },
		});

		expect(wrapper.emitted('upload-reference')).toBeFalsy();
	});

	it('renders loaded reference image preview and filename', () => {
		const wrapper = mount(ImageReferenceSection, {
			props: {
				referenceImage: {
					name: 'sample_photo.jpg',
					url: 'blob:http://localhost/sample',
					base64: 'data:image/jpeg;base64,1234',
				},
			},
		});

		expect(wrapper.find('.dropzone-box.has-file').exists()).toBe(true);
		expect(wrapper.find('.ref-status').text()).toBe('已载入参考图');
		expect(wrapper.find('.ref-filename').text()).toBe('sample_photo.jpg');
		expect(wrapper.find('.thumb-img').attributes('src')).toBe('blob:http://localhost/sample');
		expect(wrapper.find('.upload-label').exists()).toBe(false);
	});

	it('emits clear-reference when clear button is clicked', async () => {
		const wrapper = mount(ImageReferenceSection, {
			props: {
				referenceImage: {
					name: 'to_remove.png',
					url: 'https://example.com/test.png',
				},
			},
		});

		const clearBtn = wrapper.find('.clear-btn');
		await clearBtn.trigger('click');

		expect(wrapper.emitted('clear-reference')).toBeTruthy();
	});
});

describe('ImagePreviewGrid without reference upload card', () => {
	it('renders empty-card spanning the grid when gallery is empty', () => {
		const wrapper = mount(ImagePreviewGrid, {
			props: {
				gallery: [],
				zoomLevel: 100,
				isGenerating: false,
			},
		});

		expect(wrapper.find('.empty-card').exists()).toBe(true);
		expect(wrapper.find('.empty-title').text()).toBe('还没有生成结果');
		// 不应再包含旧的 upload-card
		expect(wrapper.find('.upload-card').exists()).toBe(false);
		expect(wrapper.find('.ref-thumb').exists()).toBe(false);
	});

	it('renders gallery items and allows fullscreen and removal', async () => {
		const item = {
			id: 'img-1',
			url: 'https://generated-images.selfclaw.local/img-1.png',
			prompt: 'a cute cat',
			createdAt: new Date().toISOString(),
		};

		const wrapper = mount(ImagePreviewGrid, {
			props: {
				gallery: [item],
				zoomLevel: 100,
				isGenerating: false,
			},
		});

		expect(wrapper.findAll('.image-card')).toHaveLength(1);
		expect(wrapper.find('.caption-text').text()).toBe('a cute cat');

		// 触发全屏预览
		const overlayBtns = wrapper.findAll('.overlay-btn');
		expect(overlayBtns).toHaveLength(3);

		await overlayBtns[0].trigger('click');
		expect(wrapper.emitted('preview-fullscreen')).toBeTruthy();
		expect(wrapper.emitted('preview-fullscreen')[0][0]).toEqual({ image: item, index: 0 });

		// 触发编辑图片
		await overlayBtns[1].trigger('click');
		expect(wrapper.emitted('edit-image')).toBeTruthy();
		expect(wrapper.emitted('edit-image')[0][0]).toEqual(item);

		// 触发删除
		await overlayBtns[2].trigger('click');
		expect(wrapper.emitted('remove-image')).toBeTruthy();
		expect(wrapper.emitted('remove-image')[0][0]).toBe('img-1');
	});

	it('scales gallery card min-width dynamically with zoomLevel without CSS zoom on container', async () => {
		const wrapper = mount(ImagePreviewGrid, {
			props: {
				gallery: [{ id: 'img-1', url: 'https://example.com/1.png' }],
				zoomLevel: 100,
				isGenerating: false,
			},
		});

		const grid = wrapper.find('.image-grid');
		expect(grid.attributes('style')).toContain('--card-min-width: 200px');
		expect(grid.attributes('style')).not.toContain('zoom:');

		await wrapper.setProps({ zoomLevel: 150 });
		expect(grid.attributes('style')).toContain('--card-min-width: 300px');

		await wrapper.setProps({ zoomLevel: 60 });
		expect(grid.attributes('style')).toContain('--card-min-width: 120px');
	});
});
