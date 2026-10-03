using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Desktop.Composition;
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Infrastructure;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Composition;

/// <summary>
/// The queue graph must resolve from the real composition root: the dispatcher, service, bridge,
/// publisher, deletion owner and the store contract all share one singleton wiring.
/// </summary>
public sealed class DesktopServiceRegistrationTests
{
    [Fact]
    public Task Desktop_registration_resolves_the_input_queue_graph()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
            var paths = StoragePathDefaults.Create(root, Path.Combine(root, "selfclaw.db"), Path.Combine(root, "secrets"));
            try
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSelfClawInfrastructure(paths);
                services.AddSelfClawDesktop(Dispatcher.CurrentDispatcher);
                await using var provider = services.BuildServiceProvider();

                var store = provider.GetRequiredService<IConversationInputStore>();
                store.Should().BeOfType<SqliteConversationInputRepository>();
                provider.GetRequiredService<IConversationInputSchedulerStore>().Should().BeSameAs(store);
                provider.GetRequiredService<IConversationInputRepository>().Should().BeSameAs(store);

                provider.GetRequiredService<IConversationInputCoordinator>().Should().BeOfType<ConversationInputService>();
                provider.GetRequiredService<ConversationInputDispatcher>().Should().NotBeNull();
                provider.GetRequiredService<ConversationInputBridge>().Should().NotBeNull();
                provider.GetRequiredService<ConversationInputPublisher>().Should().NotBeNull();
                provider.GetRequiredService<ConversationDeletionService>().Should().NotBeNull();
                provider.GetRequiredService<ConversationInputFeatureSwitch>().Should().NotBeNull();
                await Task.CompletedTask;
            }
            finally
            {
                SqliteTestPools.ClearFor(paths.DatabasePath);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
}