using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Squeue.ViewModels;

/// Lets a second launch of Squeue hand its command line to the one that is already running,
/// over a named pipe only the current user can open.
public sealed class CommandChannel : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _listening;

    public CommandChannel(string pipeName) => _pipeName = pipeName;

    public static string DefaultName => $"Squeue.Commands.{Environment.UserName}";

    /// Starts accepting command lines; <paramref name="received"/> runs on a background thread.
    public void Listen(Action<string[]> received)
    {
        if (_listening is not null) throw new InvalidOperationException("Already listening.");
        _listening = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null) continue;
                    if (JsonSerializer.Deserialize<string[]>(line) is { } args) received(args);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // A sender that went away mid-message; wait for the next one.
                    System.Diagnostics.Trace.WriteLine($"Squeue command channel: {ex.Message}");
                }
            }
        });
    }

    /// Sends a command line to the running Squeue. Returns false if none answered within <paramref name="timeout"/>.
    public static bool TrySend(string pipeName, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                int wait = (int)Math.Max(1, Math.Min(500, (deadline - DateTime.UtcNow).TotalMilliseconds));
                client.Connect(wait);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                writer.WriteLine(JsonSerializer.Serialize(args));
                writer.Flush();
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                // The running Squeue may still be starting up; keep trying until the deadline.
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listening?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
