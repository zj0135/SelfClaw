using System.Windows.Threading;

namespace SelfClaw.Tests.TestDoubles;

internal static class WpfDispatcherTest
{
    internal static Task RunAsync(Func<Task> test, int maximumSeconds = 30)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await test();
                    completion.SetResult();
                }
                catch (OperationCanceledException exception)
                {
                    completion.SetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(maximumSeconds));
    }
}
