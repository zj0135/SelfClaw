import { test, expect } from '@playwright/test';
import { installActivityHost } from '../fixtures/activityHost.js';
import { installQueueHost } from '../fixtures/queueHost.js';

const SECOND_CONVERSATION = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';

test.beforeEach(async ({ page }) => {
    await page.addInitScript({ content: '(' + installActivityHost.toString() + ')();(' + installQueueHost.toString() + ')();' });
    await page.goto('/');
    await page.evaluate(() => window.queueFixture.transcript());
});

async function queuedItemIds(page) {
    return page.evaluate(() => (JSON.parse(localStorage.getItem('queue-items'))[window.activityFixture.parent] || []).map(item => item.inputId));
}

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

test('enables adjustment only after the host publishes canSteer and reports consumed steers', async ({ page }) => {
    const steer = page.getByRole('button', { name: '调整当前任务', exact: true });
    await expect(steer).toBeDisabled();

    await page.evaluate(() => window.queueFixture.setSteerable('turn-42'));
    const editor = page.locator('textarea').first();
    await editor.fill('focus on tests');
    await expect(steer).toBeEnabled();
    await steer.click();

    await expect(page.locator('.queue-item[data-kind="steer"]')).toContainText('等待生效');
    const sent = await page.evaluate(() => window.activityFixture.requests.find(request => request.type === 'send-prompt' && request.kind === 'steer'));
    expect(sent.targetTurnId).toBe('turn-42');

    const [inputId] = await queuedItemIds(page);
    await page.evaluate(id => window.queueFixture.consumeSteer(id), inputId);
    await expect(page.locator('.queue-notice')).toHaveText('已加入上下文');
});

test('keeps the steer draft and shows the host fallback when the turn cannot accept it', async ({ page }) => {
    await page.evaluate(() => window.queueFixture.setSteerable('turn-7'));
    await page.evaluate(() => window.queueFixture.steerFallback('hook-requires-new-turn'));
    const editor = page.locator('textarea').first();
    await editor.fill('must wait for a new turn');
    await page.getByRole('button', { name: '调整当前任务', exact: true }).click();

    await expect(page.locator('.composer-notice')).toContainText('完整审查');
    await expect(editor).toHaveValue('');
    // The host stored it as an ordinary follow-up, not as a steer.
    await expect(page.locator('.queue-item[data-kind="steer"]')).toHaveCount(0);
    await expect(page.locator('.queue-preview')).toHaveText('must wait for a new turn');
});

test('keeps the approval bar independent from steering and drops it after supersede', async ({ page }) => {
    await page.evaluate(() => window.activityFixture.send({ type: 'tool-approval/state', revision: 1,
        approval: { toolExecutionId: 'tool-1', displayName: 'Write file', conversationId: window.activityFixture.parent } }));
    await expect(page.locator('.tool-approval-bar')).toBeVisible();

    await page.evaluate(() => window.queueFixture.setSteerable('turn-3'));
    const editor = page.locator('textarea').first();
    await editor.fill('skip that write');
    await page.getByRole('button', { name: '调整当前任务', exact: true }).click();
    await expect(page.locator('.queue-item[data-kind="steer"]')).toHaveCount(1);
    await expect(page.locator('.tool-approval-bar')).toBeVisible();

    // The host supersedes the pending approval when the steer claim lands: the old allow button must go away.
    await page.evaluate(() => window.activityFixture.send({ type: 'tool-approval/state', revision: 2, approval: null }));
    await expect(page.locator('.tool-approval-bar')).toHaveCount(0);
});

test('promotes a pending follow-up without duplicating the input', async ({ page }) => {
    await page.evaluate(() => window.queueFixture.setSteerable('turn-9'));
    const editor = page.locator('textarea').first();
    await editor.fill('reuse my identity');
    await editor.press('Enter');
    const [inputId] = await queuedItemIds(page);

    await page.locator('.queue-item [aria-label="调整当前任务"]').click();

    await expect(page.locator('.queue-item[data-kind="steer"]')).toHaveCount(1);
    await expect(page.locator('.queue-item')).toHaveCount(1);
    expect(await queuedItemIds(page)).toEqual([inputId]);
});

test('keeps the edited body on a conflict and saves again with the refreshed revision', async ({ page }) => {
    const editor = page.locator('textarea').first();
    await editor.fill('first draft');
    await editor.press('Enter');
    await expect(page.locator('.queue-preview')).toHaveText('first draft');

    await page.locator('.queue-item [aria-label="编辑"]').click();
    const queueEditor = page.locator('textarea[aria-label="编辑待发送内容"]');
    await queueEditor.fill('conflicting edit');
    await page.evaluate(() => window.queueFixture.conflictNextEdit());
    await page.locator('[aria-label="保存编辑"]').click();

    await expect(page.locator('.queue-error')).toContainText('队列已变化');
    await expect(queueEditor).toHaveValue('conflicting edit');

    await page.locator('[aria-label="保存编辑"]').click();
    await expect(queueEditor).toHaveCount(0);
    await expect(page.locator('.queue-preview')).toHaveText('conflicting edit');
    await expect(page.locator('.queue-item')).toHaveCount(1);
});

test('an old conversation ACK cannot clear the new conversation draft', async ({ page }) => {
    await page.evaluate(() => window.queueFixture.holdAck(true));
    const editor = page.locator('textarea').first();
    await editor.fill('for conversation A');
    await editor.press('Enter');
    await expect(page.locator('.queue-preview')).toHaveText('for conversation A');

    await page.evaluate(id => window.queueFixture.transcript({ conversationId: id, message: 'Conversation B transcript' }), SECOND_CONVERSATION);
    await expect(page.locator('.queue-preview')).toHaveCount(0);
    await page.evaluate(() => window.queueFixture.releaseAck());
    await expect(editor).toHaveValue('for conversation A');
    await expect(page.locator('.composer-error')).toHaveCount(0);
});

test('held items stay blocking after resume and retry reuses the same input', async ({ page }) => {
    await page.evaluate(() => window.queueFixture.setSteerable('turn-1'));
    const editor = page.locator('textarea').first();
    await editor.fill('will be held');
    await editor.press('Enter');
    await page.evaluate(() => {
        const store = JSON.parse(localStorage.getItem('queue-items'));
        const item = store[window.activityFixture.parent][0];
        item.status = 'held';
        item.reasonCode = 'model-disabled';
        localStorage.setItem('queue-items', JSON.stringify(store));
    });
    await page.reload();
    await page.evaluate(() => window.queueFixture.transcript());

    const row = page.locator('.queue-item[data-status="held"]');
    await expect(row).toContainText('所选模型不可用');
    await expect(row).toContainText('待处理');
    const [inputId] = await queuedItemIds(page);
    await row.getByRole('button', { name: '重试', exact: true }).click();
    await expect(page.locator('.queue-item[data-status="pending"]')).toHaveCount(1);
    expect(await queuedItemIds(page)).toEqual([inputId]);
});

test('a busy CLI turn never offers queue or adjustment actions', async ({ page }) => {
    await page.evaluate(() => window.queueFixture.transcript({ agentMode: 'cli' }));
    await expect(page.getByRole('button', { name: '停止生成', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: '调整当前任务', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: '加入队列', exact: true })).toHaveCount(0);
    await expect(page.locator('.conversation-queue')).toHaveCount(0);
});
