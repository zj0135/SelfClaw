using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.LlmProvider.Chat.Implementations;
using SelfClaw.LlmProvider.Client.Abstractions;
using SelfClaw.LlmProvider.Client.Dtos;
using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Errors;
using SelfClaw.LlmProvider.Parameters.Implementations;
using SelfClaw.LlmProvider.Profiles.Dtos;
using SelfClaw.LlmProvider.Profiles.Implementations;
using SelfClaw.LlmProvider.Protocols.Dtos;
using SelfClaw.LlmProvider.Protocols.Implementations;
using SelfClaw.LlmProvider.Quirks.Implementations;
using SelfClaw.LlmProvider.Transport.Abstractions;
using SelfClaw.LlmProvider.Transport.Implementations;
using SelfClaw.LlmProvider.Vendors.Dtos;
using SelfClaw.LlmProvider.Vendors.Implementations;

namespace SelfClaw.LlmProvider.Client.Implementations;

/// <summary>
/// <see cref="ILlmClientFactory"/> 的默认实现。它只做装配：查厂商描述符、查协议引擎、按参数表求值、
/// 建一层薄 HttpClient。没有任何 I/O、缓存、路径或密钥存储。
/// </summary>
public sealed class LlmClientFactory : ILlmClientFactory
{
    private readonly LlmVendorCatalog _vendors;
    private readonly LlmProtocolRegistry _protocols;
    private readonly LlmHttpPipeline _pipeline;

    /// <param name="vendors">
    /// 厂商目录。必填：库不带任何厂商数据，内置厂商与用户厂商都是调用方的产品配置。
    /// </param>
    /// <param name="transport">出站口；null 表示跟随系统代理。</param>
    /// <param name="protocols">协议注册表；null 表示五个内置协议。</param>
    /// <param name="quirks">quirk 注册表；null 表示四个内置 quirk。</param>
    public LlmClientFactory(
        LlmVendorCatalog vendors,
        ILlmTransport? transport = null,
        LlmProtocolRegistry? protocols = null,
        LlmQuirkRegistry? quirks = null)
    {
        ArgumentNullException.ThrowIfNull(vendors);
        _vendors = vendors;
        _protocols = protocols ?? LlmProtocolRegistry.CreateDefault();
        _pipeline = new LlmHttpPipeline(transport ?? LlmTransports.Default, quirks ?? LlmQuirkRegistry.CreateDefault());
    }

    public LlmVendorCatalog Vendors => _vendors;

    public LlmClientLease CreateChatClient(
        LlmConnection connection,
        LlmModel model,
        LlmRequestOptions? options = null)
    {
        var requestOptions = options ?? LlmRequestOptions.Empty;
        var context = CreateContext(connection, model, requestOptions);
        var engine = _protocols.GetRequired(model.Protocol);
        var httpClient = _pipeline.CreateClient(connection, context.Vendor, model.Protocol);
        try
        {
            var client = engine.CreateChatClient(context, httpClient);
            if (requestOptions.StreamIdleTimeout is { } idleTimeout)
            {
                client = new IdleGuardingChatClient(client, idleTimeout);
            }

            var chatOptions = engine.CreateChatOptions(context, requestOptions.Tools ?? []);
            return new LlmClientLease(client, chatOptions, httpClient);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    public ChatOptions CreateChatOptions(
        LlmConnection connection,
        LlmModel model,
        LlmRequestOptions? options = null)
    {
        var requestOptions = options ?? LlmRequestOptions.Empty;
        var context = CreateContext(connection, model, requestOptions);
        return _protocols.GetRequired(model.Protocol)
            .CreateChatOptions(context, requestOptions.Tools ?? []);
    }

    public LlmImageGeneratorLease CreateImageGenerator(LlmConnection connection, LlmModel model)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(model);
        var context = CreateContext(connection, model, LlmRequestOptions.Empty);
        var engine = _protocols.GetRequired(model.Protocol);
        var httpClient = _pipeline.CreateClient(connection, context.Vendor, model.Protocol);
        try
        {
            return new LlmImageGeneratorLease(engine.CreateImageGenerator(context, httpClient), httpClient);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    public LlmEmbeddingGeneratorLease CreateEmbeddingGenerator(
        LlmConnection connection,
        LlmModel model,
        int? dimensions = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(model);
        var context = CreateContext(connection, model, LlmRequestOptions.Empty);
        var engine = _protocols.GetRequired(model.Protocol);
        var httpClient = _pipeline.CreateClient(connection, context.Vendor, model.Protocol);
        try
        {
            var generator = engine.CreateEmbeddingGenerator(context, httpClient, dimensions);
            return new LlmEmbeddingGeneratorLease(generator, httpClient);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    public Task<IReadOnlyList<LlmModelDescriptor>> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var vendor = _vendors.GetRequired(connection.VendorId);
        if (vendor.ModelListing is not { } listing)
        {
            throw new NotSupportedException(
                $"Vendor '{vendor.VendorId}' does not declare a model listing protocol.");
        }

        return ListModelsCoreAsync(connection, vendor, listing, cancellationToken);
    }

    private async Task<IReadOnlyList<LlmModelDescriptor>> ListModelsCoreAsync(
        LlmConnection connection,
        LlmVendorDescriptor vendor,
        LlmVendorModelListing listing,
        CancellationToken cancellationToken)
    {
        using var httpClient = _pipeline.CreateClient(connection, vendor, null);
        return await LlmModelListReader
            .ReadAsync(httpClient, connection, vendor, listing, cancellationToken)
            .ConfigureAwait(false);
    }

    private LlmProtocolContext CreateContext(
        LlmConnection connection,
        LlmModel model,
        LlmRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(model);
        var vendor = _vendors.GetRequired(connection.VendorId);
        if (!vendor.Supports(model.Protocol))
        {
            throw new LlmProviderException(
                $"Vendor '{vendor.VendorId}' does not declare protocol '{model.Protocol}'. " +
                $"Declared protocols: {string.Join(", ", vendor.Protocols.Keys.OrderBy(key => key, StringComparer.Ordinal))}.");
        }

        var logger = options.LoggerFactory?.CreateLogger($"SelfClaw.LlmProvider.{model.Protocol}")
            ?? NullLogger.Instance;
        var parameters = new LlmOptionResolver(connection, model, logger)
            .Resolve(vendor.ProtocolOrNull(model.Protocol)?.Parameters);
        return new LlmProtocolContext(connection, model, vendor, options, parameters);
    }
}
