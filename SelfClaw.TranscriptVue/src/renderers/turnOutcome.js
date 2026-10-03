import { formatElapsedTime } from './elapsedTime.js';

const labels = {
    succeeded: '已完成',
    failed: '执行失败',
    cancelled: '已取消',
    truncated: '回答已截断',
    blocked: '已阻止',
    interrupted: '已中断',
};

export function formatTurnOutcome(outcome) {
    if (!outcome || outcome.status === 'running') return null;
    const details = [];
    if (outcome.durationMs != null) details.push(formatElapsedTime(outcome.durationMs));
    // A missing normalized total is unknown, even when individual counters are present.
    if (outcome.usage?.totalTokens != null) details.push(`${outcome.usage.totalTokens.toLocaleString()} tokens`);
    return { label: labels[outcome.status] || outcome.status, details: details.join(' · ') };
}
