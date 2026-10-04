using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// The only place reason codes become user copy. Submission rejections, bridge errors and the
/// pushed queue state all read it, so the transcript never maps codes on its own.
/// </summary>
internal static class ConversationInputReasonText
{
    internal static string Describe(string? reason) => reason switch
    {
        ConversationInputReason.Capacity => "队列已满，请先等待或取消部分输入。",
        ConversationInputReason.AttachmentsUnsupported => "当前版本不支持发送附件。",
        ConversationInputReason.DirectOnly => "只有 Direct 代理支持该输入。",
        ConversationInputReason.ModelUnavailable => "没有可用的模型，请先在模型管理中启用模型。",
        ConversationInputReason.RequestConflict => "该请求与已存在的输入冲突，请重试。",
        ConversationInputReason.ConversationUnavailable => "该会话正在删除或应用正在退出。",
        ConversationInputReason.QueueRevisionConflict => "队列已变化，请根据最新列表重试。",
        ConversationInputReason.SubscriptionInvalid => "队列订阅已失效，请重新打开会话。",
        ConversationInputReason.QueuePaused => "队列已暂停。",
        ConversationInputReason.TurnCancelled => "已停止生成，队列已暂停。",
        ConversationInputReason.TurnFailed => "上一项执行失败，请检查后恢复队列。",
        ConversationInputReason.TurnBlocked => "上一项被阻止，请检查后恢复队列。",
        ConversationInputReason.TurnTruncated => "上一项未完整完成，请检查后恢复队列。",
        ConversationInputReason.TurnInterrupted => "上一项被中断，请检查后恢复队列。",
        ConversationInputReason.PersistFailed => "执行结果保存失败，队列已暂停。",
        ConversationInputReason.InterruptedByRestart => "应用重启前未完成的输入已保留，请确认后处理。",
        ConversationInputReason.StaleSteerTarget => "引导目标回合已结束，请转为后续输入或取消。",
        ConversationInputReason.AgentChanged => "代理定义已变化，请重试该项以使用新定义。",
        ConversationInputReason.AgentMissing => "原代理不可用，请恢复代理定义或取消该项。",
        ConversationInputReason.ModelDisabled => "所选模型不可用，请启用模型后重试。",
        ConversationInputReason.WorkspaceMissing => "原工作目录不可用，请恢复目录后重试。",
        ConversationInputReason.ExecutionStartFailed => "启动执行失败，请重试或取消该项。",
        _ => "输入操作未生效，请重试。"
    };
}
