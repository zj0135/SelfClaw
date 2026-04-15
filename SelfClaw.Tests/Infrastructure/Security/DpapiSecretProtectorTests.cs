using FluentAssertions;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Security;

namespace SelfClaw.Tests.Infrastructure.Security;

public sealed class DpapiSecretProtectorTests : IDisposable
{
    private readonly string _rootPath;

    public DpapiSecretProtectorTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawSecretTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task Stored_secret_can_be_retrieved()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var protector = new DpapiSecretProtector(storagePaths);

        var secretRef = await protector.StoreSecretAsync("top-secret");
        var secret = await protector.RetrieveSecretAsync(secretRef);

        secret.Should().Be("top-secret");
        File.Exists(Path.Combine(_rootPath, "secrets", secretRef.Replace("secret:", string.Empty, StringComparison.OrdinalIgnoreCase) + ".bin")).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }
}
