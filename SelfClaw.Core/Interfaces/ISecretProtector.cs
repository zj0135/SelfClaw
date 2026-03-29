namespace SelfClaw.Core.Interfaces;

public interface ISecretProtector
{
    Task<string> StoreSecretAsync(
        string secret,
        string? existingSecretRef = null,
        CancellationToken cancellationToken = default);

    Task<string?> RetrieveSecretAsync(string secretRef, CancellationToken cancellationToken = default);

    Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default);
}