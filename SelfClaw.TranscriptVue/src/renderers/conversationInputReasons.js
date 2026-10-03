const reasons = {
    'queue-paused': '队列已暂停',
    'turn-cancelled': '已停止生成，队列暂停',
    'turn-failed': '上一项执行失败，请检查后恢复',
    'turn-blocked': '上一项被阻止，请检查后恢复',
    'turn-truncated': '上一项未完整完成，请检查后恢复',
    'persist-failed': '执行结果保存失败，队列已暂停',
    'agent-changed': '代理定义已变化，请重试待处理项后恢复',
    'agent-missing': '代理不可用，请处理待发送项',
    'model-disabled': '所选模型不可用，请启用模型后重试',
    'workspace-missing': '原工作目录不可用，请恢复目录后重试',
    'interrupted-by-restart': '已恢复待发送内容，请确认后恢复队列',
};
export function describeInputReason(reason) { return reasons[reason] || '队列已暂停，请检查待发送项'; }
