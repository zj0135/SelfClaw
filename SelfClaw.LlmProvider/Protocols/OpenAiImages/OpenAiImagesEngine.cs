using Microsoft.Extensions.AI;
using OpenAI.Images;
using SelfClaw.LlmProvider.Protocols.Abstractions;
using SelfClaw.LlmProvider.Protocols.Dtos;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAIClientOptions = OpenAI.OpenAIClientOptions;

namespace SelfClaw.LlmProvider.Protocols.OpenAiImages;

/// <summary>OpenAI Images 协议。直接复用框架的 <see cref="IImageGenerator"/> 适配器。</summary>
internal sealed class OpenAiImagesEngine : ILlmProtocolEngine
{
    public string Protocol => LlmProtocols.OpenAiImages;

    public IImageGenerator CreateImageGenerator(LlmProtocolContext context, HttpClient httpClient)
    {
        var client = new ImageClient(
            context.Model.ModelId,
            new ApiKeyCredential(context.Connection.Credential.Value),
            new OpenAIClientOptions
            {
                Endpoint = context.Connection.Endpoint,
                Transport = new HttpClientPipelineTransport(httpClient)
            });
        return client.AsIImageGenerator();
    }
}
