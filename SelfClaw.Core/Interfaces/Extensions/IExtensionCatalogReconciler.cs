namespace SelfClaw.Core.Interfaces;

public interface IExtensionCatalogReconciler
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
