import { test, expect } from '@playwright/test';
import { installActivityHost } from '../fixtures/activityHost.js';
import { installQueueHost } from '../fixtures/queueHost.js';

test.beforeEach(async ({ page }) => {
    await page.addInitScript({ content: '(' + installActivityHost.toString() + ')();(' + installQueueHost.toString() + ')();' });
    await page.goto('/');
    await page.evaluate(() => window.queueFixture.transcript());
});

test('queues while busy, preserves the draft until ACK, and keeps list after pause/resume results', async ({ page }) => {
    await page.evaluate(() => window.queueFixture.holdAck(true));
    const editor = page.locator('textarea').first();
    await editor.fill('Queued B');
    await editor.press('Enter');
    await expect(page.locator('.queue-preview')).toHaveText('Queued B');
    await expect(editor).toHaveValue('Queued B');
    await page.evaluate(() => window.queueFixture.releaseAck());
    await expect(editor).toHaveValue('');
    await page.getByRole('button', { name: '暂停队列', exact: true }).click();
    await expect(page.getByRole('button', { name: '恢复队列', exact: true })).toBeVisible();
    await expect(page.locator('.queue-preview')).toHaveText('Queued B');
    await page.getByRole('button', { name: '恢复队列', exact: true }).click();
    await expect(page.getByRole('button', { name: '暂停队列', exact: true })).toBeVisible();
    await expect(page.locator('.queue-preview')).toHaveText('Queued B');
    await page.locator('.conversation-queue').getByRole('button', { name: '取消', exact: true }).click();
    await expect(page.locator('.queue-preview')).toHaveCount(0);
});

test('reload restores the accepted queue through a new GUID subscription', async ({ page }) => {
    const editor = page.locator('textarea').first();
    await editor.fill('Survives reload');
    await editor.press('Enter');
    await expect(page.locator('.queue-preview')).toHaveText('Survives reload');
    await page.reload();
    await page.evaluate(() => window.queueFixture.transcript());
    await expect(page.locator('.queue-preview')).toHaveText('Survives reload');
    await expect(page.locator('.queue-item[data-status="pending"]')).toHaveCount(1);
});

