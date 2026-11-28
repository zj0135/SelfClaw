using Microsoft.Extensions.AI;
using SelfClaw.LlmProvider.Client.Dtos;
using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Profiles.Dtos;
using SelfClaw.LlmProvider.Protocols.Dtos;
using SelfClaw.LlmProvider.Vendors.Dtos;

namespace SelfClaw.LlmProvider.Protocols.Abstractions;

/// <summary>
/// 一个协议的实现。本库自带五个内置协议；需要完全自定义线格式时实现这个接口并注册到
/// <c>LlmProtocolRegistry</c>。
/// </summary>
public interface ILlmProtocolEngine
{
    /// <summary>本引擎负责的协议 id。</summary>
    string Protocol { get; }

    /// <summary>造聊天客户端。非聊天协议实现可以抛 <see cref="NotSupportedException"/>。</summary>
    IChatClient CreateChatClient(LlmProtocolContext context, HttpClient httpClient) =>
        throw new NotSupportedException($"Protocol '{Protocol}' does not serve chat.");

    /// <summary>造聊天选项。</summary>
    ChatOptions CreateChatOptions(LlmProtocolContext context, IReadOnlyList<AITool> tools) =>
        throw new NotSupportedException($"Protocol '{Protocol}' does not serve chat.");

    /// <summary>造图像生成器。</summary>
    IImageGenerator CreateImageGenerator(LlmProtocolContext context, HttpClient httpClient) =>
        throw new NotSupportedException($"Protocol '{Protocol}' does not serve images.");

    /// <summary>造向量生成器。</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        LlmProtocolContext context, HttpClient httpClient, int? dimensions) =>
        throw new NotSupportedException($"Protocol '{Protocol}' does not serve embeddings.");
}
