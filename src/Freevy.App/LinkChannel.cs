using System.IO.Pipes;

namespace Freevy.App;

/// <summary>
/// Hands a freevy:// link from the short-lived process Windows starts for it to the running app.
/// The pipe is limited to the current Windows user; only one line, and only freevy: links, are read.
/// </summary>
internal sealed class LinkChannel : IDisposable
{
    private const int MaxLength = 4096;
    private static readonly string PipeName = $"Freevy.Link.{Environment.UserDomainName}.{Environment.UserName}";
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Listens in the background; <paramref name="received"/> runs on the UI context.</summary>
    public LinkChannel(SynchronizationContext ui, Action<string> received)
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is { Length: > 0 and <= MaxLength } && UrlProtocol.IsLink(line))
                    {
                        ui.Post(_ => received(line), null);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // A client that hung up early; wait for the next one.
                }
            }
        });
    }

    /// <summary>Sends a link to the running app. False when no app is listening.</summary>
    public static bool Send(string link)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(link);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
