using System.Collections.Concurrent;
using WinExplorer.Interop;

namespace WinExplorer.Services;

/// <summary>
/// A single long lived STA thread used for every shell COM call. COM shell objects
/// (Shell.Application, IShellFolder, MSAA) must be created and consumed on one STA
/// thread; funneling them through here keeps the UI responsive and avoids
/// cross-apartment marshalling surprises.
/// </summary>
internal sealed class StaExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private bool _disposed;

    public StaExecutor(string name = "WinExplorer.Shell", int timeoutMs = 8000)
    {
        TimeoutMs = timeoutMs;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public int TimeoutMs { get; }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    private void Loop()
    {
        NativeMethods.CoInitializeEx(0, NativeMethods.COINIT_APARTMENTTHREADED | NativeMethods.COINIT_DISABLE_OLE1DDE);
        try
        {
            foreach (var work in _work.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch
                {
                    // A single failing shell call must not kill the apartment.
                }
            }
        }
        finally
        {
            NativeMethods.CoUninitialize();
        }
    }

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed)
        {
            tcs.SetException(new ObjectDisposedException(nameof(StaExecutor)));
            return tcs.Task;
        }

        try
        {
            _work.Add(() =>
            {
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        return tcs.Task;
    }

    public Task RunAsync(Action action) =>
        RunAsync(() =>
        {
            action();
            return true;
        });

    /// <summary>Blocks the caller until the work item ran (only safe when not on the STA thread).</summary>
    public T Run<T>(Func<T> func, int? timeoutMs = null)
    {
        if (IsCurrentThread)
            return func();

        var task = RunAsync(func);
        if (!task.Wait(timeoutMs ?? TimeoutMs))
            throw new TimeoutException("A shell call did not finish in time.");

        return task.Result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _work.CompleteAdding();
    }
}
