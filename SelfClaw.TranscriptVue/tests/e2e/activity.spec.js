import { test, expect } from '@playwright/test';
import { installActivityHost } from '../fixtures/activityHost.js';

test.beforeEach(async ({ page }) => {
	await page.addInitScript(installActivityHost);
	await page.goto('/');
	await page.evaluate(() => window.activityFixture.transcript());
	await expect(page.locator('.activity-panel')).toHaveClass(/collapsed/);
	await expandActivityPanel(page);
});

async function expandActivityPanel(page) {
	await page.locator('.activity-toggle').click();
	await expect(page.locator('.task-row')).toHaveCount(3);
}


// 悬浮夹具页：一个位于主对话区右下角、声明了交互矩形的控件；离开矩形时自己发 release，
// 并记录外壳发回的 hit-released（用 data 属性暴露出来供断言）。
const FIXTURE_HUD_BODY = `<!doctype html><title>Fixture HUD</title>
<style>html,body{margin:0;height:100%;background:transparent}#hit{position:fixed;right:20px;bottom:20px;width:120px;height:40px;background:#3b5bfd}#anchored{position:fixed;right:20px;width:120px;height:30px;background:#f59e0b}</style>
<div id="hit"></div>
<div id="anchored"></div>
<script>
	const shell = (message) => window.parent.postMessage(Object.assign({ __selfclaw: 1 }, message), '*');
	const hit = document.getElementById('hit');
	let released = 0;
	document.documentElement.dataset.released = '0';
	// 与外层测试通道无关的小报告：把插件自己看到的几何发回宿主侧，用例才能断言“收起时依然有效”。
	const box = (element) => {
		const rect = element.getBoundingClientRect();
		return { x: Math.round(rect.x), y: Math.round(rect.y), width: Math.round(rect.width), height: Math.round(rect.height) };
	};
	const report = () => window.parent.postMessage({ __probe: {
		size: [window.innerWidth, window.innerHeight],
		hit: box(hit),
		anchored: box(document.getElementById('anchored')),
		composer: (window.__anchors || {}).composer || null,
	} }, '*');

	// 与 view-demo 的 HUD 同一种摆法：只在拿到 anchors 时算一次，不监听 resize。
	function place() {
		const composer = (window.__anchors || {}).composer;
		document.getElementById('anchored').style.bottom = composer ? (window.innerHeight - composer.y + 12) + 'px' : '16px';
	}

	shell({ kind: 'hello' });
	window.addEventListener('message', (event) => {
		const data = event.data || {};
		if (data.kind !== 'event') return;
		// 外壳解除武装后的显式通知：插件据此复位自己的 hover 态，不依赖 pointerout。
		if (data.type === 'hit-released') {
			released += 1;
			document.documentElement.dataset.released = String(released);
			hit.classList.remove('hovering');
			return;
		}

		if (data.type !== 'handshake' && data.type !== 'anchors-changed') return;
		// handshake 把地标藏在 payload.anchors 里；anchors-changed 的 payload 就是地标本身。
		window.__anchors = data.type === 'handshake' ? ((data.payload || {}).anchors || {}) : (data.payload || {});
		if (data.type === 'handshake') {
			const rect = hit.getBoundingClientRect();
			shell({ kind: 'notice', type: 'hit-regions', payload: { rects: [{ x: rect.left, y: rect.top, width: rect.width, height: rect.height }] } });
		}

		place();
		report();
	});
	document.addEventListener('pointermove', (event) => {
		const rect = hit.getBoundingClientRect();
		const inside = event.clientX >= rect.left && event.clientX < rect.right && event.clientY >= rect.top && event.clientY < rect.bottom;
		if (!inside) shell({ kind: 'notice', type: 'hit-regions-release' });
	});
	window.addEventListener('resize', report);
</script>`;

// 让任何用例都能读到插件侧的自述几何（size / hit / anchored）。
async function collectPluginProbes(page) {
	await page.addInitScript(() => {
		window.__probes = [];
		window.addEventListener('message', (event) => { if (event.data?.__probe) window.__probes.push(event.data.__probe); });
	});
}

// 同一个插件源同时提供停靠页与悬浮页：停靠页只有静态内容，悬浮页才有交互矩形。
async function installFixturePlugin(page) {
	await page.route('https://fixture.plugin.selfclaw.local/**', (route) => route.fulfill({
		contentType: 'text/html',
		body: route.request().url().includes('/hud.html')
			? FIXTURE_HUD_BODY
			: '<!doctype html><title>Fixture panel</title><p>Plugin fixture</p>',
	}));
}

// 悬浮页把矩形放在自己视口的右下角；该视口就是主对话区，所以窗口坐标要由宿主容器换算出来。
async function hoverHudCorner(page) {
	const box = await page.locator('.plugin-floating-layer').boundingBox();
	await page.mouse.move(box.x + box.width - 80, box.y + box.height - 40);
	return { x: box.x + box.width - 80, y: box.y + box.height - 40 };
}

test('streaming detail, tool completion, reload, and parent isolation', async ({ page }) => {
	const errors = [];
	page.on('pageerror', (error) => errors.push(error.message));
	await page.locator('.task-select').first().click();
	await page.locator('.task-detail .thinking-summary').click();
	await expect(page.locator('.task-detail .thinking-content')).toContainText('Reasoning for');
	await page.locator('.task-detail .tool-summary').click();
	await page.evaluate(() => window.activityFixture.text(' **new tokens** <img src=x onerror="window.injected=true">'));
	await expect(page.locator('.task-detail .body-segment')).toContainText('new tokens');
	await page.evaluate(() => window.activityFixture.finish());
	await expect(page.locator('.task-detail .tool-block')).toHaveClass(/completed/);
	await expect(page.locator('.task-detail .tool-details')).toContainText('Recorded tool result');
	await expect(page.locator('.task-detail')).toContainText('等待父代理处理');
	await expect(page.locator('[data-message-id="parent-message"]')).not.toContainText('new tokens');
	expect(await page.evaluate(() => window.injected)).toBeUndefined();
	await page.reload();
	await page.evaluate(() => window.activityFixture.transcript());
	await expandActivityPanel(page);
	expect(errors).toEqual([]);
});

test('detail scroll keeps the parent reader fixed while the floating panel stays out of flow', async ({ page }) => {
	await page.locator('.task-select').first().click();
	await page.evaluate(() => window.activityFixture.text('\n\n' + 'Child line.\n\n'.repeat(80)));
	await expect(page.locator('.task-detail .body-segment')).toContainText('Child line.');
	await page.locator('#transcript-scroll').evaluate((element) => { element.scrollTop = 60; element.dispatchEvent(new Event('scroll')); });
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 0; element.dispatchEvent(new Event('scroll')); });
	await page.evaluate(() => window.activityFixture.text('Frozen latest token'));
	await expect(page.locator('.new-content')).toBeVisible();
	await expect(page.locator('.task-detail .body-segment')).not.toContainText('Frozen latest token');
	expect(await page.locator('#transcript-scroll').evaluate((element) => element.scrollTop)).toBe(60);
	await page.locator('.new-content').click();
	await expect(page.locator('.task-detail .body-segment')).toContainText('Frozen latest token');
	expect(await page.locator('#transcript-scroll').evaluate((element) => element.scrollTop)).toBe(60);
	await page.locator('#transcript-scroll').evaluate((element) => { element.scrollTop = element.scrollHeight; });
	const metrics = () => page.evaluate(() => {
		const stage = document.querySelector('.activity-stage').getBoundingClientRect();
		const panel = document.querySelector('.activity-panel').getBoundingClientRect();
		const scroll = document.querySelector('#transcript-scroll');
		return { panel: { top: panel.top, right: panel.right, width: panel.width }, stage: { top: stage.top, right: stage.right }, scrollTop: scroll.scrollTop, scrollHeight: scroll.scrollHeight, clientHeight: scroll.clientHeight };
	});
	const expanded = await metrics();
	expect(Math.abs(expanded.panel.top - expanded.stage.top - 20)).toBeLessThanOrEqual(1);
	expect(Math.abs(expanded.panel.right - expanded.stage.right + 20)).toBeLessThanOrEqual(1);
	expect(expanded.panel.width).toBeGreaterThan(400);
	await page.locator('.activity-toggle').click();
	await expect(page.locator('.activity-panel')).toHaveClass(/collapsed/);
	const collapsed = await metrics();
	expect(collapsed.panel.width).toBeLessThan(240);
	expect(collapsed.panel.right).toBeCloseTo(expanded.panel.right, 0);
	expect(collapsed.scrollTop).toBe(expanded.scrollTop);
	expect(collapsed.scrollHeight).toBe(expanded.scrollHeight);
	expect(collapsed.clientHeight).toBe(expanded.clientHeight);
});

test('a historical block window and its reading position survive switching and remount', async ({ page }) => {
	await page.evaluate(() => window.activityFixture.longContent());
	await page.locator('.task-select').first().click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 86');
	await page.getByRole('button', { name: '更早内容', exact: true }).click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 180; element.dispatchEvent(new Event('scroll')); });
	await page.locator('.task-select').nth(1).click();
	await page.locator('.task-select').first().click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
	await page.locator('.activity-toggle').click();
	await page.evaluate(() => window.activityFixture.appendBlock('Output while hidden'));
	await page.locator('.activity-toggle').click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
	await page.getByRole('button', { name: '系统设置', exact: true }).click();
	await page.locator('.kind-chat').filter({ hasText: 'Activity verification' }).click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
});

test('returning from invalidated history resumes the live tail and continues scrolling', async ({ page }) => {
	await page.evaluate(() => window.activityFixture.longContent());
	await page.locator('.task-select').first().click();
	await page.getByRole('button', { name: '更早内容', exact: true }).click();
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await page.evaluate(() => window.activityFixture.appendBlock('New tail after history'));
	await expect(page.locator('.task-detail .body-segment').first()).toContainText('History block 22');
	await page.locator('.new-content').click();
	await expect(page.locator('.task-detail .body-segment').last()).toContainText('New tail after history');
	await page.evaluate(() => window.activityFixture.appendBlock('Continues following'));
	await expect(page.locator('.task-detail .body-segment').last()).toContainText('Continues following');
	await expect(page.locator('.new-content')).toHaveCount(0);
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => element.scrollHeight - element.scrollTop - element.clientHeight)).toBeLessThan(2);
});

test('task reading positions survive switching, collapse, and settings navigation', async ({ page }) => {
	await page.locator('.task-select').first().click();
	await page.evaluate(() => window.activityFixture.text('\n\n' + 'First child history.\n\n'.repeat(80)));
	await expect(page.locator('.task-detail .body-segment')).toContainText('First child history.');
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 180; element.dispatchEvent(new Event('scroll')); });
	await page.locator('.task-select').nth(1).click();
	await page.evaluate(() => window.activityFixture.text('\n\n' + 'Second child history.\n\n'.repeat(80), 1));
	await expect(page.locator('.task-detail .body-segment')).toContainText('Second child history.');
	await page.locator('.detail-scroll').evaluate((element) => { element.scrollTop = 320; element.dispatchEvent(new Event('scroll')); });
	await page.locator('.task-select').first().click();
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
	await page.locator('.activity-toggle').click();
	await page.locator('.activity-toggle').click();
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
	await page.getByRole('button', { name: '系统设置', exact: true }).click();
	await page.locator('.kind-chat').filter({ hasText: 'Activity verification' }).click();
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(180);
	await page.locator('.task-select').nth(1).click();
	await expect.poll(() => page.locator('.detail-scroll').evaluate((element) => Math.round(element.scrollTop))).toBe(320);
});

test('terminal, dark theme, larger text, and settings remount preserve usable activity', async ({ page }) => {
	await page.locator('.task-select').first().click();
	await page.evaluate(() => {
		document.documentElement.dataset.theme = 'dark';
		document.documentElement.style.setProperty('--ui-font-scale', '1.25');
		window.activityFixture.send({ type: 'terminal-state', isOpen: true, isRunning: false });
	});
	await expect(page.locator('.terminal-panel')).toBeVisible();
	await expect.poll(() => page.locator('.terminal-panel').evaluate((element) => element.getBoundingClientRect().height)).toBeGreaterThanOrEqual(280);
	const bounds = await page.evaluate(() => {
		const panel = document.querySelector('.activity-panel').getBoundingClientRect();
		const composer = document.querySelector('.composer-shell').getBoundingClientRect();
		const stage = document.querySelector('.activity-stage').getBoundingClientRect();
		const terminal = document.querySelector('.terminal-panel').getBoundingClientRect();
		return { top: panel.top, bottom: panel.bottom, stageTop: stage.top, composerTop: composer.top, terminalBottom: terminal.bottom, viewportBottom: innerHeight };
	});
	expect(bounds.top).toBeGreaterThanOrEqual(bounds.stageTop);
	expect(bounds.bottom).toBeLessThanOrEqual(bounds.composerTop);
	expect(bounds.terminalBottom).toBeLessThanOrEqual(bounds.viewportBottom + 1);
	await page.screenshot({ path: 'test-results/activity-terminal-dark.png', fullPage: true });
	await page.getByRole('button', { name: '系统设置', exact: true }).click();
	await expect(page.locator('.activity-panel')).toHaveCount(0);
	await page.locator('.kind-chat').filter({ hasText: 'Activity verification' }).click();
	await expect(page.locator('.task-detail .body-segment')).toContainText('Partial answer');
});

test('plugin column confines the dock and a short stage collapses without losing the selected task', async ({ page }) => {
	await installFixturePlugin(page);
	await page.evaluate(() => localStorage.setItem('activity:plugin', '1'));
	await page.reload();
	await page.evaluate(() => window.activityFixture.transcript());
	await expect(page.locator('.plugin-dock-host')).toBeVisible();
	await expandActivityPanel(page);
	await page.locator('.task-select').first().click();
	await expect(page.locator('.task-detail .body-segment')).toContainText('Partial answer');
	const bounds = await page.evaluate(() => ({
		panelRight: document.querySelector('.activity-panel').getBoundingClientRect().right,
		pluginLeft: document.querySelector('.plugin-dock-host').getBoundingClientRect().left,
	}));
	expect(bounds.panelRight).toBeLessThanOrEqual(bounds.pluginLeft);
	await page.screenshot({ path: 'test-results/activity-plugin.png', fullPage: true });
	await page.locator('.activity-stage').evaluate((stage) => { stage.style.height = '150px'; });
	await expect(page.locator('.activity-panel')).toHaveClass(/collapsed/);
	await page.evaluate(() => window.activityFixture.text(' while short'));
	await page.locator('.activity-stage').evaluate((stage) => { stage.style.height = ''; });
	await expect(page.locator('.task-detail .body-segment')).toContainText('while short');
});

for (const viewport of [{ width: 1920, height: 1080 }, { width: 1280, height: 800 }, { width: 1024, height: 768 }, { width: 640, height: 768 }]) {
	test(`activity stays inside stage at ${viewport.width}x${viewport.height}`, async ({ page }) => {
		await page.setViewportSize(viewport);
		if (viewport.width === 640) await page.evaluate(() => { const shell = document.querySelector('.activity-stage'); shell.style.width = '360px'; shell.style.maxWidth = '100%'; });
		await page.locator('.task-select').first().click();
		await expect(page.locator('.task-detail .thinking-summary')).toBeVisible();
		await page.locator('.task-detail .thinking-summary').click();
		await expect(page.locator('.task-detail .body-segment')).toContainText('Partial answer');
		const bounds = await page.evaluate(() => {
			const stage = document.querySelector('.activity-stage').getBoundingClientRect();
			const panel = document.querySelector('.activity-panel').getBoundingClientRect();
			const composer = document.querySelector('.composer-shell').getBoundingClientRect();
			return { composerTop: composer.top, stage: { x: stage.x, y: stage.y, right: stage.right, bottom: stage.bottom }, panel: { x: panel.x, y: panel.y, right: panel.right, bottom: panel.bottom }, overflow: document.querySelector('.activity-panel').scrollWidth - panel.width };
		});
		expect(bounds.panel.x).toBeGreaterThanOrEqual(bounds.stage.x);
		expect(bounds.panel.y).toBeGreaterThanOrEqual(bounds.stage.y);
		expect(bounds.panel.right).toBeLessThanOrEqual(bounds.stage.right);
		expect(bounds.panel.bottom).toBeLessThanOrEqual(bounds.stage.bottom);
		expect(bounds.panel.bottom).toBeLessThanOrEqual(bounds.composerTop);
		expect(bounds.overflow).toBeLessThanOrEqual(1);
		await page.screenshot({ path: `test-results/activity-${viewport.width}.png`, fullPage: true });
	});
}

// 悬浮视图的真实浏览器验证：层盖满窗口但默认对指针透明，只有插件声明的交互矩形在命中时才唤醒
// 那一帧。这三点（未声明即穿透、命中唤醒、离开释放）在 jsdom 里只能验证逻辑，几何命中要真浏览器。
test('a floating view stays click-through except inside the rect it declares', async ({ page }) => {
	await installFixturePlugin(page);
	await page.evaluate(() => localStorage.setItem('activity:hud', '1'));
	await page.reload();
	await page.evaluate(() => window.activityFixture.transcript());

	const layer = page.locator('.plugin-floating-layer');
	await expect(layer).toBeVisible();
	const slot = page.locator('.float-slot').first();
	await expect(slot).toHaveCSS('pointer-events', 'none');

	// 未声明区域的像素对宿主透明：悬浮层盖满窗口也不会拦住侧栏。
	await expect(page.locator('.sidebar .tool-btn').filter({ hasText: '插件' })).toBeEnabled();
	await page.locator('.sidebar .tool-btn').filter({ hasText: '插件' }).click();
	await expect(page.locator('.launcher')).toBeVisible();
	await page.locator('.launcher-backdrop').click({ position: { x: 60, y: 700 } });
	await expect(page.locator('.launcher')).toHaveCount(0);

	// 指针进入声明的矩形：只有那一帧变成可交互。
	// 用轮询而不是一次性断言：插件帧加载与握手都是异步的，矩形可能还没送到外壳。
	let corner = null;
	await expect.poll(async () => {
		corner = await hoverHudCorner(page);
		return slot.evaluate((element) => getComputedStyle(element).pointerEvents);
	}).toBe('auto');
	expect(await page.evaluate((point) => {
		const element = document.elementFromPoint(point.x, point.y);
		return Boolean(element?.closest('.plugin-floating-layer'));
	}, corner)).toBe(true);

	// 指针离开矩形：插件发 release，外壳立刻取消武装，像素重新透明。
	await expect.poll(async () => {
		await page.mouse.move(400, 300);
		return slot.evaluate((element) => getComputedStyle(element).pointerEvents);
	}).toBe('none');
	expect(await page.evaluate((point) => {
		const element = document.elementFromPoint(point.x, point.y);
		return Boolean(element?.closest('.plugin-floating-layer'));
	}, corner)).toBe(false);

	// 解除武装的显式通知确实到达了插件文档（插件据此清理 hover 态）。
	await expect(page.frameLocator('.float-slot iframe').locator('html')).toHaveAttribute('data-released', '1');
	await page.screenshot({ path: 'test-results/plugin-floating-layer.png', fullPage: true });
});

// 截图里的那条 bug：右栏与悬浮同时打开时，悬浮 HUD 落在右栏上方且点不动（只有关掉右栏才行）。
// 根因是指针位于子 iframe 上时父文档收不到 pointermove（§13.1 实测），命中测试因此从不运行。
// 现在悬浮层只覆盖主对话区：既不会与右栏的插件帧重叠，也不会越出这一列。
test('a floating view stays inside the conversation column and usable while the dock is open', async ({ page }) => {
	await installFixturePlugin(page);
	await page.evaluate(() => localStorage.setItem('activity:plugin', '1'));
	await page.evaluate(() => localStorage.setItem('activity:hud', '1'));
	await page.reload();
	await page.evaluate(() => window.activityFixture.transcript());

	await expect(page.locator('.plugin-dock-host')).toBeVisible();
	const layer = await page.locator('.plugin-floating-layer').boundingBox();
	const dock = await page.locator('.plugin-dock-host').boundingBox();
	const stage = await page.locator('.main-content').boundingBox();
	expect(layer.x + layer.width).toBeLessThanOrEqual(dock.x + 0.5);
	// 层就是主对话区本身：它必须正好等于 .main-content 的盒子，不能有被 overflow 裁掉的一半
	// （被裁掉的像素插件既看不见也点不到，等于潜伏着同一个 bug）。
	expect(Math.abs(layer.x - stage.x)).toBeLessThanOrEqual(1);
	expect(Math.abs(layer.y - stage.y)).toBeLessThanOrEqual(1);
	expect(Math.abs(layer.width - stage.width)).toBeLessThanOrEqual(1);
	expect(Math.abs(layer.height - stage.height)).toBeLessThanOrEqual(1);

	// 右栏开着也要能唤醒并点击悬浮帧。
	const slot = page.locator('.float-slot').first();
	let corner = null;
	await expect.poll(async () => {
		corner = await hoverHudCorner(page);
		return slot.evaluate((element) => getComputedStyle(element).pointerEvents);
	}).toBe('auto');

	const click = await page.evaluate((point) => {
		const element = document.elementFromPoint(point.x, point.y);
		return { inLayer: Boolean(element?.closest('.plugin-floating-layer')), tag: element?.tagName ?? '' };
	}, corner);
	expect(click.inLayer).toBe(true);
	expect(click.tag).toBe('IFRAME');

	await page.screenshot({ path: 'test-results/plugin-floating-with-dock.png', fullPage: true });
});

// 标题栏那两颗按钮的职责边界：只是显隐开关。没有可显隐的内容时是禁用态，
// 绝不弹启动器——打开视图的唯一入口是左侧导航的「插件」。
test('titlebar plugin buttons only show and hide, never open the launcher', async ({ page }) => {
	await installFixturePlugin(page);
	const panelButton = page.getByRole('button', { name: /插件面板/ });
	const floatButton = page.getByRole('button', { name: /悬浮视图/ });

	await expect(panelButton).toBeDisabled();
	await expect(floatButton).toBeDisabled();
	await expect(page.locator('.launcher')).toHaveCount(0);

	await page.evaluate(() => localStorage.setItem('activity:plugin', '1'));
	await page.evaluate(() => localStorage.setItem('activity:hud', '1'));
	await page.reload();
	await page.evaluate(() => window.activityFixture.transcript());
	await expect(page.locator('.plugin-dock-host')).toBeVisible();
	await expect(page.locator('.plugin-floating-layer')).toBeVisible();

	await panelButton.click();
	await expect(page.locator('.plugin-dock-host')).toBeHidden();
	await expect(panelButton).toHaveAttribute('title', '显示插件面板');
	await panelButton.click();
	await expect(page.locator('.plugin-dock-host')).toBeVisible();

	await floatButton.click();
	await expect(page.locator('.plugin-floating-layer')).toBeHidden();
	await expect(floatButton).toHaveAttribute('title', '显示悬浮视图');
	await floatButton.click();
	await expect(page.locator('.plugin-floating-layer')).toBeVisible();
	await expect(page.locator('.launcher')).toHaveCount(0);

	// 左侧「插件」仍然是打开/关闭视图的那个入口。
	await page.locator('.sidebar .tool-btn').filter({ hasText: '插件' }).click();
	await expect(page.locator('.launcher')).toBeVisible();
	await expect(page.locator('.launcher .view-close')).toHaveCount(2);
});

// 重启后悬浮层仍处于收起状态（显隐是持久化的用户偏好），收起不该破坏插件的几何。
// 这条用例复刻用户报告的现象：HUD 只在 handshake / anchors-changed 时定位一次（view-demo 就是这种
// 写法），所以一旦收起用 display: none，帧的视口就是 0×0、anchors 退化成窗口坐标，HUD 会被算到视口
// 外；显示回来时 anchors 没变、也没有 resize 监听，插件不会重算 →「显示悬浮视图」看起来毫无反应。
// 现在收起用 visibility: hidden：不绘制、不参与命中测试，但布局与视口尺寸始终有效。
test('a floating view keeps valid geometry while the layer is concealed', async ({ page }) => {
	await collectPluginProbes(page);
	await installFixturePlugin(page);
	await page.evaluate(() => localStorage.setItem('activity:hud', '1'));
	await page.reload();
	await page.evaluate(() => window.activityFixture.transcript());

	// 用户收起悬浮层后关掉应用：这一状态会持久化，重启后按钮读到的是「显示悬浮视图」。
	await page.getByRole('button', { name: /悬浮视图/ }).click();
	await expect(page.locator('.plugin-floating-layer')).toBeHidden();
	await page.reload();
	await page.evaluate(() => window.activityFixture.transcript());

	const button = page.getByRole('button', { name: /悬浮视图/ });
	await expect(button).toHaveAttribute('title', '显示悬浮视图');
	await expect(page.locator('.plugin-floating-layer')).toBeHidden();

	// 收起期间插件的几何必须仍然有效：视口没有塌成 0×0，按 anchors 摆好的控件仍在视口内。
	const column = await page.locator('.main-content').boundingBox();
	await expect.poll(async () => (await page.evaluate(() => window.__probes)).length).toBeGreaterThan(0);
	const concealed = (await page.evaluate(() => window.__probes)).at(-1);
	expect(concealed.size[0]).toBeCloseTo(column.width, 0);
	expect(concealed.size[1]).toBeCloseTo(column.height, 0);
	// anchors 是换算到帧自己坐标系里的：原点就是主对话列，所以输入区的地标必然落在视口内。
	expect(concealed.composer.x).toBeLessThan(concealed.size[0]);
	expect(concealed.composer.y).toBeLessThan(concealed.size[1]);
	expect(concealed.anchored.y).toBeGreaterThan(0);
	expect(concealed.anchored.y + concealed.anchored.height).toBeLessThanOrEqual(concealed.size[1]);

	// 第一次点击就要看到它，而且那一帧立刻可以交互。
	await button.click();
	await expect(page.locator('.plugin-floating-layer')).toBeVisible();
	const slot = page.locator('.float-slot').first();
	let corner = null;
	await expect.poll(async () => {
		corner = await hoverHudCorner(page);
		return slot.evaluate((element) => getComputedStyle(element).pointerEvents);
	}).toBe('auto');
	expect(await page.evaluate((point) => Boolean(document.elementFromPoint(point.x, point.y)?.closest('.plugin-floating-layer')), corner)).toBe(true);

	const revealed = (await page.evaluate(() => window.__probes)).at(-1);
	expect(revealed.anchored.y).toBeGreaterThan(0);
	expect(revealed.anchored.y + revealed.anchored.height).toBeLessThanOrEqual(revealed.size[1]);
});
