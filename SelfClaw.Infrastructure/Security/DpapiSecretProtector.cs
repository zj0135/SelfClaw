using System.Security.Cryptography;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Security;

public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly StoragePaths _storagePaths;

    public DpapiSecretProtector(StoragePaths storagePaths)
    {
        _storagePaths = storagePaths;
    }

    public async Task<string> StoreSecretAsync(string secret, string? existingSecretRef = null, CancellationToken cancellationToken = default)
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

    public async Task<string?> RetrieveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
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

    public Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(secretRef);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetSecretPath(string secretRef)
    {
        var safeName = secretRef.Replace("secret:", string.Empty, StringComparison.OrdinalIgnoreCase);
        return Path.Combine(_storagePaths.SecretsDirectory, safeName + ".bin");
    }
}