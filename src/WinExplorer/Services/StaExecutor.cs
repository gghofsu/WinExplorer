using System.Collections.Concurrent;
using WinExplorer.Interop;

namespace WinExplorer.Services;

/// <summary>
/// A single long lived STA thread used for every shell COM call. COM shell objects
/// (Shell.Application, IShellFolder, MSAA) must be created and consumed on one STA
/// thread; funneling them through here keeps the UI responsive and avoids
/// cross-apartment marshalling surprises.
/// </summary>
/// <remarks>
/// The thread <b>pumps messages</b> while it waits for work. That is not cosmetic: Explorer's
/// automation objects live in another apartment, and a bare STA loop that never dispatches
/// makes those calls fail with <c>RPC_E_*</c> errors (or hang) instead of returning data -
/// which looks exactly like "the shell reports no folder for any window".
/// </remarks>
internal sealed class StaExecutor : IDisposable
{
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Logger? _log;
    private readonly string _name;
    private static readonly List<StaExecutor> Instances = new();

    private Thread? _thread;
    private volatile bool _stop;
    private bool _disposed;
    private int _hungCalls;
    private int _threadGeneration;

    public StaExecutor(string name = "WinExplorer.Shell", int timeoutMs = 8000, Logger? log = null)
    {
        TimeoutMs = timeoutMs;
        _name = name;
        _log = log;

        lock (Instances)
        {
            Instances.RemoveAll(e => e._disposed);
            Instances.Add(this);
        }

        StartThread();
    }

    public int TimeoutMs { get; }

    /// <summary>How often a call had to be abandoned because the shell did not answer.</summary>
    public int HungCalls => Volatile.Read(ref _hungCalls);

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    private void StartThread()
    {
        var generation = Interlocked.Increment(ref _threadGeneration);
        var thread = new Thread(() => Loop(generation))
        {
            IsBackground = true,
            Name = $"{_name}.{generation}"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _thread = thread;
    }

    private void Loop(int generation)
    {
        var coInit = NativeMethods.CoInitializeEx(0, NativeMethods.COINIT_APARTMENTTHREADED | NativeMethods.COINIT_DISABLE_OLE1DDE);
        try
        {
            while (!_stop && generation == Volatile.Read(ref _threadGeneration))
            {
                if (_work.TryDequeue(out var work))
                {
                    try
                    {
                        work();
                    }
                    catch
                    {
                        // A single failing shell call must not kill the apartment.
                    }

                    continue;
                }

                // Wait for work *and* for COM input, then dispatch whatever Windows queued.
                NativeMethods.MsgWaitForMultipleObjectsEx(0, null, 25, NativeMethods.QS_ALLINPUT,
                    NativeMethods.MWMO_INPUTAVAILABLE);
                PumpMessages();
            }
        }
        catch (Exception ex)
        {
            _log?.Error($"The shell thread ({_name}) died", ex);
        }
        finally
        {
            if (coInit >= 0)
                NativeMethods.CoUninitialize();
        }
    }

    /// <summary>Dispatches pending window messages - COM needs this to complete cross-thread calls.</summary>
    private static void PumpMessages()
    {
        try
        {
            while (NativeMethods.PeekMessage(out var message, 0, 0, 0, NativeMethods.PM_REMOVE))
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        catch
        {
            // Never let the pump kill the thread.
        }
    }

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed || _thread is null)
        {
            tcs.SetException(new ObjectDisposedException(nameof(StaExecutor)));
            return tcs.Task;
        }

        _work.Enqueue(() =>
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

        return tcs.Task;
    }

    public Task RunAsync(Action action) =>
        RunAsync(() =>
        {
            action();
            return true;
        });

    /// <summary>
    /// Runs a call and gives up after <paramref name="timeoutMs"/>. A shell call that never
    /// returns must not stop the app: the caller gets <paramref name="fallback"/> and the
    /// apartment is restarted when calls keep hanging.
    /// </summary>
    public async Task<T> RunWithTimeoutAsync<T>(Func<T> func, T fallback, int timeoutMs = 2500)
    {
        var task = RunAsync(func);
        var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (completed != task)
        {
            OnHungCall("a shell call did not finish in time");
            return fallback;
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Debug($"Shell call failed: {ex.GetType().Name}: {ex.Message}");
            return fallback;
        }
    }

    private void OnHungCall(string reason)
    {
        var hung = Interlocked.Increment(ref _hungCalls);
        _log?.Warn($"Shell call abandoned ({hung} in total): {reason}.");

        // The queued work is still sitting in the wedged apartment. After two abandoned calls
        // the apartment is replaced: the old thread stays behind (it is a background thread
        // and cannot be killed safely) and a fresh one takes over.
        if (hung % 2 == 0)
        {
            _log?.Warn("Restarting the shell thread after repeated timeouts.");
            try
            {
                StartThread();
            }
            catch (Exception ex)
            {
                _log?.Error("Could not restart the shell thread", ex);
            }
        }
    }

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

    /// <summary>The thread currently serving calls (for diagnostics).</summary>
    public string ThreadName => _thread?.Name ?? "(none)";

    /// <summary>One line per shell thread, for --diagnose / --probe.</summary>
    public static string HealthSummary()
    {
        List<StaExecutor> alive;
        lock (Instances)
        {
            Instances.RemoveAll(e => e._disposed);
            alive = Instances.ToList();
        }

        if (alive.Count == 0)
            return "(no shell threads)";

        return string.Join("; ", alive.Select(e => $"{e.ThreadName}: {e.HungCalls} abandoned call(s)"));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stop = true;
    }
}
