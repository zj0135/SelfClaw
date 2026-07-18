using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class AiChatClientFactoryTests
{
    [Fact]
    public async Task CreateAsync_resolves_request_builds_pipeline_and_disposes_native_client()
    {
        var data = CreateData();
        var protector = new FakeSecretProtector { Secrets = { ["secret:openai"] = "sk-test" } };
        var nativeClient = new FakeChatClient();
        var expectedOptions = new ChatOptions { Temperature = 0.4f };
        var adapter = new FakeAdapter(nativeClient, expectedOptions);
        var tool = AIFunctionFactory.Create(() => "ok", "test_tool");
        var factory = CreateFactory(data.Repository, protector, adapter);

        using (var lease = await factory.CreateAsync(
                   data.Profile.Id,
                   new AiChatRuntimeInputs(true, [tool])))
        {
            lease.Profile.Should().Be(data.Profile);
            lease.Options.Should().BeSameAs(expectedOptions);
            lease.Client.GetService(typeof(LoggingChatClient)).Should().NotBeNull();
            lease.Client.GetService(typeof(FunctionInvokingChatClient)).Should().NotBeNull();
            adapter.LastRequest.Should().NotBeNull();
            adapter.LastRequest!.Secrets["api_key"].Should().Be("sk-test");
            adapter.LastRequest.EnableReasoning.Should().BeTrue();
            adapter.LastRequest.Tools.Should().ContainSingle().Which.Should().BeSameAs(tool);
            nativeClient.IsDisposed.Should().BeFalse();
        }

        nativeClient.IsDisposed.Should().BeTrue();
        protector.RetrievedRefs.Should().Equal("secret:openai");
    }

    [Fact]
    public async Task CreateAsync_skips_secret_resolution_for_no_auth_connections()
    {
        var data = CreateData(authKind: AiProviderAuthKind.None);
        var protector = new FakeSecretProtector();
        var adapter = new FakeAdapter(new FakeChatClient(), new ChatOptions());
        var factory = CreateFactory(data.Repository, protector, adapter);

        using var lease = await factory.CreateAsync(data.Profile.Id, new AiChatRuntimeInputs(false, []));

        protector.RetrievedRefs.Should().BeEmpty();
        adapter.LastRequest!.Secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_rejects_missing_or_disabled_model()
    {
        var data = CreateData();
        var factory = CreateFactory(data.Repository, new FakeSecretProtector(), new FakeAdapter());
        var missingId = Guid.NewGuid();

        var missing = () => factory.CreateAsync(missingId, new AiChatRuntimeInputs(false, []));
        await missing.Should().ThrowAsync<KeyNotFoundException>().WithMessage($"*{missingId}*not found*");

        data.Repository.Models[data.Profile.Id] = data.Profile with { IsEnabled = false };
        var disabled = () => factory.CreateAsync(data.Profile.Id, new AiChatRuntimeInputs(false, []));
        await disabled.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Test model*disabled*");
    }

    [Fact]
    public async Task CreateAsync_rejects_missing_or_disabled_connection()
    {
        var data = CreateData();
        var factory = CreateFactory(data.Repository, new FakeSecretProtector(), new FakeAdapter());
        data.Repository.Connections.Clear();

        var missing = () => factory.CreateAsync(data.Profile.Id, new AiChatRuntimeInputs(false, []));
        await missing.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*connection*Test model*not found*");

        data.Repository.Connections[data.Connection.Id] = data.Connection with { IsEnabled = false };
        var disabled = () => factory.CreateAsync(data.Profile.Id, new AiChatRuntimeInputs(false, []));
        await disabled.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Test provider*disabled*");
    }

    [Fact]
    public async Task CreateAsync_rejects_missing_api_key_and_unsupported_format()
    {
        var data = CreateData();
        var protector = new FakeSecretProtector();
        var unsupportedAdapter = new FakeAdapter { SupportsFormat = false };
        var missingKeyFactory = CreateFactory(data.Repository, protector, new FakeAdapter());

        var missingKey = () => missingKeyFactory.CreateAsync(
            data.Profile.Id,
            new AiChatRuntimeInputs(false, []));
        await missingKey.Should().ThrowAsync<InvalidOperationException>().WithMessage("*api_key*");

        protector.Secrets["secret:openai"] = "sk-test";
        var unsupportedFactory = CreateFactory(data.Repository, protector, unsupportedAdapter);
        var unsupported = () => unsupportedFactory.CreateAsync(
            data.Profile.Id,
            new AiChatRuntimeInputs(false, []));
        await unsupported.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*OpenAIChatCompletions*Test model*");
        unsupportedAdapter.CreateClientCalls.Should().Be(0);
    }

    [Fact]
    public async Task CreateForScopeAsync_uses_selection_or_requests_a_direct_default()
    {
        var data = CreateData(authKind: AiProviderAuthKind.None);
        var adapter = new FakeAdapter();
        var factory = CreateFactory(data.Repository, new FakeSecretProtector(), adapter);

        var missing = () => factory.CreateForScopeAsync(
            AiModelSelectionScopes.DesktopDefault,
            new AiChatRuntimeInputs(false, []));
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*default Direct model*");

        data.Repository.Selections[AiModelSelectionScopes.DesktopDefault] = new AiModelProfileSelection(
            AiModelSelectionScopes.DesktopDefault,
            data.Profile.Id,
            DateTimeOffset.UtcNow);
        using var lease = await factory.CreateForScopeAsync(
            AiModelSelectionScopes.DesktopDefault,
            new AiChatRuntimeInputs(false, []));

        lease.Profile.Id.Should().Be(data.Profile.Id);
        adapter.CreateClientCalls.Should().Be(1);
    }

    private static AiChatClientFactory CreateFactory(
        FakeRepository repository,
        FakeSecretProtector protector,
        FakeAdapter adapter)
        => new(
            repository,
            new AiProviderRegistry([adapter]),
            protector,
            NullLoggerFactory.Instance);

    private static TestData CreateData(AiProviderAuthKind authKind = AiProviderAuthKind.ApiKey)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(
            Guid.NewGuid(),
            "openai",
            "Test provider",
            AiProviderKind.OpenAI,
            new Uri("https://api.example.test/v1/"),
            authKind,
            new Dictionary<string, string> { ["api_key"] = "secret:openai" },
            new Dictionary<string, JsonElement>(),
            now,
            now);
        var profile = new AiModelProfile(
            Guid.NewGuid(),
            connection.Id,
            "Test model",
            AiProviderApiFormat.OpenAIChatCompletions,
            "test-model",
            new AiSamplingOptions(false, 0.7, false, 0.7),
            new Dictionary<string, JsonElement>(),
            now,
            now);
        var repository = new FakeRepository();
        repository.Connections[connection.Id] = connection;
        repository.Models[profile.Id] = profile;
        return new TestData(repository, connection, profile);
    }

    private sealed record TestData(
        FakeRepository Repository,
        AiProviderConnection Connection,
        AiModelProfile Profile);

    private sealed class FakeRepository : IAiProviderRepository
    {
        public Dictionary<Guid, AiProviderConnection> Connections { get; } = [];
        public Dictionary<Guid, AiModelProfile> Models { get; } = [];
        public Dictionary<string, AiModelProfileSelection> Selections { get; } = new(StringComparer.Ordinal);

        public Task<AiProviderConnection?> GetProviderConnectionAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Connections.GetValueOrDefault(id));

        public Task<AiModelProfile?> GetModelProfileAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Models.GetValueOrDefault(id));

        public Task<AiModelProfileSelection?> GetModelProfileSelectionAsync(
            string scope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Selections.GetValueOrDefault(scope));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiProviderConnection>> ListProviderConnectionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiProviderConnection>> ListAllProviderConnectionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiProviderConnection> UpsertProviderConnectionAsync(AiProviderConnection connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetProviderConnectionEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteProviderConnectionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelProfile>> ListModelProfilesAsync(Guid? id = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiModelProfile> UpsertModelProfileAsync(AiModelProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetModelProfileEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetAllModelProfilesEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelProfile>> ListEnabledModelProfilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteModelProfileAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiModelProfileSelection> SetModelProfileSelectionAsync(AiModelProfileSelection selection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public Dictionary<string, string> Secrets { get; } = [];
        public List<string> RetrievedRefs { get; } = [];

        public Task<string?> RetrieveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
        {
            RetrievedRefs.Add(secretRef);
            return Task.FromResult(Secrets.GetValueOrDefault(secretRef));
        }

        public Task<string> StoreSecretAsync(string secret, string? existingSecretRef = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeAdapter : IAiProviderAdapter
    {
        private readonly FakeChatClient _client;
        private readonly ChatOptions _options;

        public FakeAdapter(FakeChatClient? client = null, ChatOptions? options = null)
        {
            _client = client ?? new FakeChatClient();
            _options = options ?? new ChatOptions();
        }

        public AiProviderKind ProviderKind => AiProviderKind.OpenAI;
        public bool SupportsModelListing => false;
        public bool SupportsFormat { get; init; } = true;
        public AiProviderClientRequest? LastRequest { get; private set; }
        public int CreateClientCalls { get; private set; }

        public bool SupportsApiFormat(AiProviderApiFormat apiFormat) => SupportsFormat;

        public IChatClient CreateChatClient(AiProviderClientRequest request)
        {
            LastRequest = request;
            CreateClientCalls++;
            return _client;
        }

        public ChatOptions CreateChatOptions(AiProviderClientRequest request)
        {
            LastRequest = request;
            return _options;
        }

        public Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
            AiProviderConnection connection,
            IReadOnlyDictionary<string, string> secrets,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeChatClient : IChatClient
    {
        public bool IsDisposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => IsDisposed = true;
    }
}
