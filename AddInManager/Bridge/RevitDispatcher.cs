using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace RevitAddinManager.Bridge;

/// <summary>
/// Marshals work from the bridge's background threads onto Revit's API thread through an ExternalEvent.
/// </summary>
public sealed class RevitDispatcher : IExternalEventHandler
{
    private readonly ConcurrentQueue<WorkItem> queue = new ConcurrentQueue<WorkItem>();
    private readonly ExternalEvent externalEvent;

    /// <summary>
    /// Must be created inside a Revit API context (e.g. OnStartup).
    /// </summary>
    public RevitDispatcher()
    {
        externalEvent = ExternalEvent.Create(this);
    }

    public Task<object> InvokeAsync(Func<UIApplication, object> func, CancellationToken cancellationToken)
    {
        var item = new WorkItem(func);
        cancellationToken.Register(() => item.Completion.TrySetCanceled());
        queue.Enqueue(item);
        externalEvent.Raise();
        return item.Completion.Task;
    }

    public void Execute(UIApplication app)
    {
        while (queue.TryDequeue(out var item))
        {
            // Skip requests whose caller already gave up waiting.
            if (item.Completion.Task.IsCompleted) continue;
            try
            {
                item.Completion.TrySetResult(item.Func(app));
            }
            catch (Exception ex)
            {
                item.Completion.TrySetException(ex);
            }
        }
    }

    public string GetName() => "RevitAddinManager.Bridge";

    private sealed class WorkItem
    {
        public WorkItem(Func<UIApplication, object> func)
        {
            Func = func;
        }

        public Func<UIApplication, object> Func { get; }

        // Continuations must not run on Revit's thread.
        public TaskCompletionSource<object> Completion { get; } =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
