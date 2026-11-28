using Microsoft.Extensions.AI;


using SelfClaw.LlmProvider.Client.Dtos;
using SelfClaw.LlmProvider.Client.Implementations;
using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Profiles.Dtos;

namespace SelfClaw.LlmProvider.Client.Abstractions;

/// <summary>
/// 唯一的入口：把已解析好的连接与模型变成可用的 M.E.AI 对象。
/// 每个 Create* 返回的租约拥有它自己那层 HttpClient，租约释放即释放。
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>造一回合用的聊天客户端。</summary>
    /// <exception cref="NotSupportedException">该模型声明的协议不是聊天协议。</exception>
    LlmClientLease CreateChatClient(
        LlmConnection connection,
        LlmModel model,
        LlmRequestOptions? options = null);

    /// <summary>只造 ChatOptions（采样、输出上限、工具绑定、厂商专有参数）。</summary>
    ChatOptions CreateChatOptions(
        LlmConnection connection,
        LlmModel model,
        LlmRequestOptions? options = null);

    /// <summary>造图像生成器。</summary>
    /// <exception cref="NotSupportedException">该模型声明的协议不是图像协议。</exception>
    LlmImageGeneratorLease CreateImageGenerator(LlmConnection connection, LlmModel model);

    /// <summary>造向量生成器。</summary>
    /// <exception cref="NotSupportedException">该模型声明的协议不是向量协议。</exception>
    LlmEmbeddingGeneratorLease CreateEmbeddingGenerator(
        LlmConnection connection,
        LlmModel model,
        int? dimensions = null);

    /// <summary>列出端点暴露的模型；厂商描述符决定使用哪个清单协议。</summary>
    Task<IReadOnlyList<LlmModelDescriptor>> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default);
}
