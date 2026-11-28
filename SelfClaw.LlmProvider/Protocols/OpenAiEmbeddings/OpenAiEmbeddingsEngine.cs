using Microsoft.Extensions.AI;
using OpenAI.Embeddings;
using SelfClaw.LlmProvider.Protocols.Abstractions;
using SelfClaw.LlmProvider.Protocols.Dtos;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAIClientOptions = OpenAI.OpenAIClientOptions;

namespace SelfClaw.LlmProvider.Protocols.OpenAiEmbeddings;

/// <summary>OpenAI Embeddings 协议。直接复用框架的 <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> 适配器。</summary>
internal sealed class OpenAiEmbeddingsEngine : ILlmProtocolEngine
{
    public string Protocol => LlmProtocols.OpenAiEmbeddings;

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        LlmProtocolContext context,
        HttpClient httpClient,
        int? dimensions)
    {
        var client = new EmbeddingClient(
            context.Model.ModelId,
            new ApiKeyCredential(context.Connection.Credential.Value),
            new OpenAIClientOptions
            {
                Endpoint = context.Connection.Endpoint,
                Transport = new HttpClientPipelineTransport(httpClient)
            });
        return client.AsIEmbeddingGenerator(dimensions);
    }
}
