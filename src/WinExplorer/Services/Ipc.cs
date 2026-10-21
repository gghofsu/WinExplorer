using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinExplorer.Core;

namespace WinExplorer.Services;

/// <summary>Request sent from a short lived CLI process to the running helper.</summary>
public sealed class IpcRequest
{
    public string Kind { get; set; } = "ping";
    public string? Path { get; set; }
    public string? Mode { get; set; }
    public bool Activate { get; set; } = true;
    public long Window { get; set; }
    public string? Origin { get; set; }
    public int TimeoutMs { get; set; } = 15000;
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Outcome { get; set; }
    public string? Message { get; set; }
    public long Window { get; set; }
    public long Tab { get; set; }
}

/// <summary>Named pipe server owned by the background helper.</summary>
public sealed class IpcServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<IpcRequest, Task<IpcResponse>> _handler;
    private readonly Logger _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public IpcServer(Logger log, Func<IpcRequest, Task<IpcResponse>> handler)
    {
        _log = log;
        _handler = handler;
    }

    /// <summary>Raised with the request kind, for the activity list.</summary>
    public event EventHandler<string>? RequestReceived;

    public void Start()
    {
        _loop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info("IPC server listening.");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(AppPaths.PipeName, PipeDirection.InOut,
                    4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                // The handler owns (and disposes) the pipe from here on.
                var connection = HandleConnectionAsync(pipe, cancellationToken);
                pipe = null;
                _ = connection;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Error("IPC accept failed", ex);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using var owned = pipe;
        try
        {
            using var reader = new StreamReader(owned, Encoding.UTF8, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(owned, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                return;

            IpcRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<IpcRequest>(line, Json);
            }
            catch (JsonException)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse
                {
                    Ok = false,
                    Message = "Malformed request."
                }, Json)).ConfigureAwait(false);
                return;
            }

            if (request is null)
                return;

            RequestReceived?.Invoke(this, request.Kind);

            IpcResponse response;
            try
            {
                response = await _handler(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("IPC request failed", ex);
                response = new IpcResponse { Ok = false, Message = ex.Message };
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away; nothing to do.
        }
        catch (Exception ex)
        {
            _log.Error("IPC connection failed", ex);
        }
        finally
        {
            try { owned.Disconnect(); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>Outcome of one attempt to reach the helper, including why it failed.</summary>
public sealed record IpcAttempt(IpcResponse? Response, string? Error)
{
    /// <summary>True when the helper answered (the answer itself may still be a failure).</summary>
    public bool Answered => Response is not null;
}

/// <summary>Client used by a CLI invocation to talk to the helper.</summary>
public static class IpcClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Sends a request; returns null when no helper is listening.</summary>
    public static async Task<IpcResponse?> SendAsync(IpcRequest request, int connectTimeoutMs = 2000,
        CancellationToken cancellationToken = default)
    {
        var attempt = await TrySendAsync(request, connectTimeoutMs, Math.Max(3000, request.TimeoutMs),
            cancellationToken).ConfigureAwait(false);
        return attempt.Response;
    }

    /// <summary>
    /// Sends a request and reports *why* it failed. A short connect timeout lets callers poll
    /// cheaply while a helper is still starting up; a long response timeout covers a busy UI.
    /// </summary>
    public static async Task<IpcAttempt> TrySendAsync(IpcRequest request, int connectTimeoutMs = 2000,
        int responseTimeoutMs = 15000, CancellationToken cancellationToken = default)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(Math.Max(50, connectTimeoutMs));
                try
                {
                    await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return cancellationToken.IsCancellationRequested
                        ? new IpcAttempt(null, "cancelled")
                        : new IpcAttempt(null, "no helper is listening (connect timed out)");
                }
                catch (TimeoutException)
                {
                    return new IpcAttempt(null, "no helper is listening (connect timed out)");
                }
                catch (IOException ex)
                {
                    return new IpcAttempt(null, $"the pipe could not be opened: {ex.Message}");
                }
            }

            await using var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json)).ConfigureAwait(false);

            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseTimeout.CancelAfter(Math.Max(250, responseTimeoutMs));

            string? line;
            try
            {
                line = await reader.ReadLineAsync(responseTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new IpcAttempt(null, "the running instance did not answer in time");
            }

            if (string.IsNullOrWhiteSpace(line))
                return new IpcAttempt(null, "the running instance closed the connection");

            var response = JsonSerializer.Deserialize<IpcResponse>(line, Json);
            return response is null
                ? new IpcAttempt(null, "the running instance sent an empty reply")
                : new IpcAttempt(response, null);
        }
        catch (Exception ex)
        {
            return new IpcAttempt(null, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (pipe is not null)
            {
                try { await pipe.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            }
        }
    }

    public static IpcRequest FromCommand(CommandLineOptions options, string? mode = null) => new()
    {
        Kind = options.Kind switch
        {
            CommandKind.None => "settings",
            CommandKind.Open or CommandKind.OpenNewTab or CommandKind.OpenNewWindow => "open",
            CommandKind.DuplicateActiveTab => "duplicate",
            CommandKind.ReopenClosedTab => "reopen",
            CommandKind.Toggle => "toggle",
            CommandKind.Pause => "pause",
            CommandKind.Resume => "resume",
            CommandKind.Settings => "settings",
            CommandKind.Exit => "exit",
            _ => "ping"
        },
        Path = options.Path,
        Mode = mode,
        Activate = options.Activate,
        Window = options.Window
    };
}
