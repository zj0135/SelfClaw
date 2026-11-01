// 右侧面板的「审查」工具：工作区 Git 改动列表 + 统一视图 diff。宿主由 activityHost 夹具模拟，
// useWorkspace() 让 get-workspace-selection 回一个选中根，否则面板会停在「尚未选择工作目录」。
import { test, expect } from '@playwright/test';
import { installActivityHost } from '../fixtures/activityHost.js';

test.beforeEach(async ({ page }) => {
	await page.addInitScript(installActivityHost);
	await page.addInitScript(() => window.activityFixture.useWorkspace());
	await page.goto('/');
	await page.evaluate(() => window.activityFixture.transcript());
});

const panelButton = (page) => page.getByRole('button', { name: 'Panel', exact: true });
const requestCount = (page, type) => page.evaluate((kind) => window.activityFixture.requests.filter((request) => request.type === kind).length, type);

async function openReview(page) {
	await panelButton(page).click();
	await page.locator('.dock-launcher-item[data-tool="review"]').click();
	await expect(page.locator('.dock-tab.active')).toHaveText(/审查/);
}

test('the review tool lists workspace changes, auto-selects the first file, and renders a unified diff', async ({ page }) => {
	await openReview(page);
	await expect(page.locator('.scope-select')).toBeVisible();
	await expect(page.locator('.file-row')).toHaveCount(3);
	await expect(page.locator('.file-row.active')).toHaveAttribute('data-path', 'src/App.vue');
	await expect(page.locator('.diff-path')).toHaveText('src/App.vue');
	await expect(page.locator('.diff-line.add')).toHaveCount(3);
	await expect(page.locator('.diff-line.remove')).toHaveCount(1);
	await expect(page.locator('.pane-branch')).toHaveText('main');
	expect(await requestCount(page, 'git-diff/changes')).toBe(1);

	// 代码面跟随主题：浅色白底 + 浅色语法色；切到深色后用代码块的深底与 --syntax-* 那组色。
	const surface = page.locator('.diff-scroll');
	const keyword = page.locator('.diff-line.remove .code .hljs-keyword').first();
	await expect(surface).toHaveCSS('background-color', 'rgb(255, 255, 255)');
	await expect(keyword).toHaveCSS('color', 'rgb(124, 58, 237)');
	await page.evaluate(() => document.documentElement.setAttribute('data-theme', 'dark'));
	await expect(surface).toHaveCSS('background-color', 'rgb(13, 16, 22)');
	await expect(keyword).toHaveCSS('color', 'rgb(199, 146, 234)');
	await page.evaluate(() => document.documentElement.removeAttribute('data-theme'));

	// 切换作用域不再请求列表，只拉新选中文件的 diff；纯重命名没有 hunk。
	await page.locator('.scope-select').selectOption('staged');
	await expect(page.locator('.file-row')).toHaveCount(1);
	await expect(page.locator('.file-row.active')).toHaveAttribute('data-kind', 'renamed');
	await expect(page.locator('.diff-notice')).toHaveText(/仅重命名/);
	expect(await requestCount(page, 'git-diff/changes')).toBe(1);

	await page.locator('.scope-select').selectOption('unstaged');
	await page.locator('.file-row[data-path="assets/logo.png"]').click();
	await expect(page.locator('.diff-notice')).toHaveText(/二进制/);

	// 工具条的上一个 / 下一个文件，以及文件列表折叠（折叠后仍能用按钮换文件）。
	await page.locator('.review-pane .pane-step-prev').click();
	await expect(page.locator('.file-row.active')).toHaveAttribute('data-path', 'src/fresh.js');
	await page.locator('.review-pane .pane-collapse-files').click();
	await expect(page.locator('.review-files')).toBeHidden();
	await page.locator('.review-pane .pane-step-next').click();
	await expect(page.locator('.diff-path')).toHaveText('assets/logo.png');
	await page.locator('.review-pane .pane-collapse-files').click();
	await expect(page.locator('.review-files')).toBeVisible();
	await expect(page.locator('.file-row.active')).toHaveAttribute('data-path', 'assets/logo.png');

	await page.locator('.review-pane .pane-refresh').click();
	await expect.poll(() => requestCount(page, 'git-diff/changes')).toBe(2);
	await expect(page.locator('.file-row.active')).toHaveAttribute('data-path', 'assets/logo.png');
});

test('the review tool keeps its state across tab switches and shows the empty state outside a repository', async ({ page }) => {
	await openReview(page);
	await expect(page.locator('.file-row')).toHaveCount(3);
	await page.locator('.dock-new-tab').click();
	await page.locator('.dock-launcher-item[data-tool="subagents"]').click();
	await expect(page.locator('.review-pane')).toBeHidden();
	await page.locator('.dock-tab[data-tool="review"] .dock-tab-select').click();
	await expect(page.locator('.review-pane')).toBeVisible();
	await expect.poll(() => requestCount(page, 'git-diff/changes')).toBe(2);

	await page.evaluate(() => window.activityFixture.gitRepository(false));
	await page.locator('.review-pane .pane-refresh').click();
	await expect(page.locator('.review-empty strong')).toHaveText('当前 workspace 不在 Git 仓库中');
	await expect(page.locator('.file-row')).toHaveCount(0);
	await expect(page.locator('.scope-select')).toBeVisible();
});
