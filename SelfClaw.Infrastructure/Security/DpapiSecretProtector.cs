using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Security;

public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly StoragePaths _storagePaths;
    private readonly ILogger<DpapiSecretProtector> _logger;

    public DpapiSecretProtector(StoragePaths storagePaths, ILogger<DpapiSecretProtector>? logger = null)
    {
        _storagePaths = storagePaths;
        _logger = logger ?? NullLogger<DpapiSecretProtector>.Instance;
    }

    public async Task<string> StoreSecretAsync(string secret, string? existingSecretRef = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(_storagePaths.SecretsDirectory);

            var secretRef = string.IsNullOrWhiteSpace(existingSecretRef)
                ? $"secret:{Guid.NewGuid():D}"
                : existingSecretRef;

            var path = GetSecretPath(secretRef);
            var bytes = System.Text.Encoding.UTF8.GetBytes(secret);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken);
            return secretRef;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Secret storage was canceled. ExistingSecretRef={ExistingSecretRef}", existingSecretRef);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to store protected secret. ExistingSecretRef={ExistingSecretRef}", existingSecretRef);
            throw;
        }
    }

    public async Task<string?> RetrieveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = GetSecretPath(secretRef);
            if (!File.Exists(path))
            {
                return null;
            }

            var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Secret retrieval was canceled. SecretRef={SecretRef}", secretRef);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to retrieve protected secret. SecretRef={SecretRef}", secretRef);
            throw;
        }
    }

    public Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = GetSecretPath(secretRef);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to delete protected secret. SecretRef={SecretRef}", secretRef);
            throw;
        }
    }

    private string GetSecretPath(string secretRef)
    {
        var safeName = secretRef.Replace("secret:", string.Empty, StringComparison.OrdinalIgnoreCase);
        return Path.Combine(_storagePaths.SecretsDirectory, safeName + ".bin");
    }
}
