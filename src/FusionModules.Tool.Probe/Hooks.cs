using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace FusionModules.Tool.Probe;

/// <summary>
/// The Microsoft.Extensions.Hosting "HostBuilt" event, seen synchronously on the thread that calls
/// Build() — HostFactoryResolver only hands the host to another thread, which may look at it after
/// the application has already run on, and disposed it.
/// </summary>
internal sealed class HostBuiltHook : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly Action<object> onBuilt;
    private readonly List<IDisposable> subscriptions = [];
    private IDisposable? allListeners;

    private HostBuiltHook(Action<object> onBuilt) => this.onBuilt = onBuilt;

    public static HostBuiltHook Subscribe(Action<object> onBuilt)
    {
        var hook = new HostBuiltHook(onBuilt);
        hook.allListeners = DiagnosticListener.AllListeners.Subscribe(hook);
        return hook;
    }

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "Microsoft.Extensions.Hosting")
        {
            lock (subscriptions)
            {
                subscriptions.Add(value.Subscribe(this));
            }
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != "HostBuilt" || value.Value is null)
        {
            return;
        }

        try
        {
            onBuilt(value.Value);
        }
        catch
        {
            // Whatever the probe wanted from the host, the application's Build() must not fail for it.
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose()
    {
        allListeners?.Dispose();
        lock (subscriptions)
        {
            subscriptions.ForEach(s => s.Dispose());
            subscriptions.Clear();
        }
    }
}

/// <summary>
/// A last look at the container before it goes: Host disposes the content root's file provider
/// first and the service provider after it, so a provider that calls back on Dispose runs while
/// every service still resolves — when the application's own code failed and RunAsync or an
/// <c>await using</c> is disposing the host on the way out.
/// </summary>
internal static class HostDisposal
{
    public static void Hook(IHostEnvironment environment, Action onDisposing)
    {
        if (environment.ContentRootFileProvider is not CallbackFileProvider)
        {
            environment.ContentRootFileProvider = new CallbackFileProvider(environment.ContentRootFileProvider, onDisposing);
        }
    }

    private sealed class CallbackFileProvider(IFileProvider inner, Action onDisposing) : IFileProvider, IDisposable
    {
        private int disposed;

        public IFileInfo GetFileInfo(string subpath) => inner.GetFileInfo(subpath);

        public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

        public IChangeToken Watch(string filter) => inner.Watch(filter);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
            {
                return;
            }

            try
            {
                onDisposing();
            }
            catch
            {
                // A courtesy to the probe; disposal goes on regardless.
            }

            (inner as IDisposable)?.Dispose();
        }
    }
}
