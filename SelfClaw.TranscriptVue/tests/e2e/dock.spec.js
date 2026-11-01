// 右侧功能面板（SideDock）：标题栏单个「面板」按钮、启动页、可关闭的工具 tab 与终端联动。
// 宿主由 activityHost 夹具模拟：toggle-terminal / terminal-close 只记录不响应，终端状态靠 fixture.terminal() 推送。
import { test, expect } from '@playwright/test';
import { installActivityHost } from '../fixtures/activityHost.js';

test.beforeEach(async ({ page }) => {
	await page.addInitScript(installActivityHost);
	await page.goto('/');
	await page.evaluate(() => window.activityFixture.transcript());
});

const requestCount = (page, type) => page.evaluate((kind) => window.activityFixture.requests.filter((request) => request.type === kind).length, type);
const panelButton = (page) => page.getByRole('button', { name: 'Panel', exact: true });

test('the panel button opens the launcher; tools become closable tabs; closing the last tab returns to the launcher', async ({ page }) => {
	const dock = page.locator('.side-dock');
	await expect(dock).toBeHidden();
	await panelButton(page).click();
	await expect(dock).toBeVisible();
	await expect(page.locator('.dock-launcher-item')).toHaveCount(3);
	await expect(page.locator('.dock-tab')).toHaveCount(0);
	await expect(page.locator('.dock-strip-title')).toHaveText('功能面板');

	await page.locator('.dock-launcher-item[data-tool="terminal"]').click();
	await expect(page.locator('.dock-tab.active')).toHaveText(/命令行/);
	await expect(page.locator('.terminal-pane')).toBeVisible();
	expect(await requestCount(page, 'toggle-terminal')).toBe(1);
	await page.evaluate(() => window.activityFixture.terminal(true));
	await expect(page.locator('.terminal-status')).toHaveClass(/running/);

	await page.locator('.dock-new-tab').click();
	await expect(page.locator('.dock-launcher')).toBeVisible();
	await expect(page.locator('.dock-launcher-item[data-tool="terminal"] .dock-launcher-action')).toHaveText('切换');
	await expect(page.locator('.terminal-pane')).toBeHidden();
	expect(await requestCount(page, 'terminal-close')).toBe(0);

	await page.locator('.dock-launcher-item[data-tool="subagents"]').click();
	await expect(page.locator('.dock-tab')).toHaveCount(2);
	await expect(page.locator('.dock-tab.active')).toHaveText(/子代理/);
	await expect(page.locator('.task-select')).toBeVisible();
	expect(await requestCount(page, 'terminal-close')).toBe(0);

	await page.locator('.dock-tab[data-tool="terminal"] .dock-tab-select').click();
	await expect(page.locator('.terminal-pane')).toBeVisible();
	expect(await requestCount(page, 'toggle-terminal')).toBe(1);

	await page.locator('.dock-tab[data-tool="terminal"] .dock-tab-close').click();
	await expect(page.locator('.dock-tab')).toHaveCount(1);
	await expect(page.locator('.dock-tab.active')).toHaveText(/子代理/);
	await expect(page.locator('.terminal-pane')).toBeHidden();
	expect(await requestCount(page, 'terminal-close')).toBe(1);

	await page.locator('.dock-tab[data-tool="subagents"] .dock-tab-close').click();
	await expect(page.locator('.dock-tab')).toHaveCount(0);
	await expect(page.locator('.dock-launcher')).toBeVisible();
	await expect(dock).toBeVisible();

	await page.locator('.dock-close').click();
	await expect(dock).toBeHidden();
	await panelButton(page).click();
	await expect(dock).toBeVisible();
	await expect(page.locator('.dock-launcher')).toBeVisible();
	await expect(page.locator('.dock-tab')).toHaveCount(0);
});

test('a host terminal push opens the dock on a terminal tab; closing the dock pauses it and reopening resumes it', async ({ page }) => {
	await page.evaluate(() => window.activityFixture.terminal(true));
	await expect(page.locator('.side-dock')).toBeVisible();
	await expect(page.locator('.dock-tab.active')).toHaveText(/命令行/);
	await expect(page.locator('.terminal-pane')).toBeVisible();
	expect(await requestCount(page, 'toggle-terminal')).toBe(0);

	await page.locator('.dock-close').click();
	await expect(page.locator('.side-dock')).toBeHidden();
	expect(await requestCount(page, 'terminal-close')).toBe(1);

	await panelButton(page).click();
	await expect(page.locator('.dock-tab.active')).toHaveText(/命令行/);
	expect(await requestCount(page, 'toggle-terminal')).toBe(1);
});

test('the subagent toolbar shows the task name in the dropdown and only the run time outside it', async ({ page }) => {
	await panelButton(page).click();
	await page.locator('.dock-launcher-item[data-tool="subagents"]').click();
	await expect(page.locator('.task-select option')).toHaveCount(3);
	await expect(page.locator('.task-select option').first()).toHaveText('Reviewer 1');
	await expect(page.locator('.task-select option').first()).toHaveAttribute('data-phase', 'thinking');
	await expect(page.locator('.task-status')).toHaveCount(0);
	await expect(page.locator('.task-timing')).toHaveText(/^运行 \d+s$/);
	expect(await page.locator('.task-select').evaluate((element) => getComputedStyle(element).fontWeight)).toBe('400');
	await page.evaluate(() => window.activityFixture.finish());
	await expect(page.locator('.task-select option').first()).toHaveAttribute('data-phase', 'succeeded');
	await expect(page.locator('.task-status')).toHaveCount(0);
	await expect(page.locator('.dock-tab-badge')).toHaveCount(0);
	await expect(page.locator('.tool-badge')).toHaveCount(0);
});

test('the floating rail in the chat view opens the dock to a tool and marks subagent presence with a quiet dot', async ({ page }) => {
	const rail = page.locator('.dock-rail');
	await expect(rail).toBeVisible();
	await expect(rail.locator('.dock-rail-button')).toHaveCount(3);
	await expect(rail.locator('.dock-rail-button[data-tool="subagents"] .dock-rail-dot')).toHaveClass(/running/);
	await expect(rail.locator('.dock-rail-button[data-tool="terminal"] .dock-rail-dot')).toHaveCount(0);
	await expect(rail.locator('.dock-rail-dot')).toHaveText('');

	await rail.locator('.dock-rail-button[data-tool="subagents"]').click();
	await expect(page.locator('.side-dock')).toBeVisible();
	await expect(page.locator('.dock-tab.active')).toHaveText(/子代理/);
	await expect(rail.locator('.dock-rail-button[data-tool="subagents"]')).toHaveClass(/active/);
	await expect(page.locator('.task-detail')).toBeVisible();

	await rail.locator('.dock-rail-button[data-tool="terminal"]').click();
	await expect(page.locator('.dock-tab.active')).toHaveText(/命令行/);
	await expect(rail.locator('.dock-rail-button[data-tool="terminal"]')).toHaveClass(/active/);
	expect(await requestCount(page, 'toggle-terminal')).toBe(1);

	await rail.locator('.dock-rail-button[data-tool="terminal"]').click();
	await expect(page.locator('.side-dock')).toBeHidden();
	await expect(rail).toBeVisible();
	await expect(rail.locator('.dock-rail-button.active')).toHaveCount(0);
});

test('dragging the left edge resizes the dock and the width survives a reload', async ({ page }) => {
	// 与桌面窗口的默认尺寸一致：CSS 上限会给对话区留 520px，视口太窄时拖不到 560。
	await page.setViewportSize({ width: 1440, height: 900 });
	await panelButton(page).click();
	const dock = page.locator('.side-dock');
	await expect(dock).toBeVisible();
	const dockWidth = () => dock.evaluate((element) => Math.round(element.getBoundingClientRect().width));
	// 宽度有 240ms 过渡：等到位再量，拖拽也从稳定几何开始。
	await expect.poll(dockWidth).toBe(440);

	const handle = await page.locator('.dock-resizer').boundingBox();
	const x = handle.x + handle.width / 2;
	const y = handle.y + 200;
	await page.mouse.move(x, y);
	await page.mouse.down();
	await page.mouse.move(x - 120, y, { steps: 6 });
	await page.mouse.up();

	expect(await dockWidth()).toBe(560);
	expect(await page.evaluate(() => localStorage.getItem('selfclaw:dock-width'))).toBe('560');

	await page.reload();
	await page.evaluate(() => window.activityFixture.transcript());
	await expect(page.locator('.side-dock')).toBeHidden();
	await panelButton(page).click();
	await expect(page.locator('.dock-launcher')).toBeVisible();
	await expect.poll(dockWidth).toBe(560);

	// 继续拖到上限（CSS 会按"对话区至少 520px"封顶）：转录列、输入栏都必须留在 dock 左侧，
	// 工作区的 grid 列不能被输入栏状态条的 min-content 撑到 dock 下面去。
	const edge = await page.locator('.dock-resizer').boundingBox();
	await page.mouse.move(edge.x + edge.width / 2, y);
	await page.mouse.down();
	await page.mouse.move(edge.x - 400, y, { steps: 8 });
	await page.mouse.up();
	const geometry = await page.evaluate(() => {
		const rect = (selector) => document.querySelector(selector).getBoundingClientRect();
		return {
			dockLeft: rect('.side-dock').left,
			workspace: { width: rect('.workspace').width, right: rect('.workspace').right },
			transcriptRight: rect('.transcript-content').right,
			composerRight: rect('.composer-stack').right,
		};
	});
	expect(Math.abs(geometry.workspace.right - geometry.dockLeft)).toBeLessThanOrEqual(1);
	expect(geometry.workspace.width).toBeGreaterThanOrEqual(520);
	expect(geometry.transcriptRight).toBeLessThanOrEqual(geometry.dockLeft + 1);
	expect(geometry.composerRight).toBeLessThanOrEqual(geometry.dockLeft + 1);
});
