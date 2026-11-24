using System.Net;
using System.Net.Sockets;

namespace Nexus.Web.Modules.Monitor;

// Docker Desktop does not publish ports from an internal-only network. This sandbox relay has a
// fixed upstream and lets only the relay join the bridge with the runner's localhost port.
internal static class SandboxIngress
{
    public static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("NexusSandbox__Ingress") != "true")
            throw new InvalidOperationException("This command requires the Monitor sandbox ingress profile.");

        var listener = new TcpListener(IPAddress.Any, 8080);
        using var sockets = new SemaphoreSlim(1200);
        listener.Start(512);
        try
        {
            while (true)
            {
                await sockets.WaitAsync();
                var incoming = await listener.AcceptTcpClientAsync();
                _ = RelayAsync(incoming, sockets);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RelayAsync(TcpClient incoming, SemaphoreSlim sockets)
    {
        using (incoming)
        using (var upstream = new TcpClient())
        {
            try
            {
                incoming.NoDelay = true;
                upstream.NoDelay = true;
                using var cancellation = new CancellationTokenSource(TimeSpan.FromHours(2));
                await upstream.ConnectAsync("web", 8080, cancellation.Token);
                var fromRunner = incoming.GetStream();
                var fromWeb = upstream.GetStream();
                var read = fromRunner.CopyToAsync(fromWeb, cancellation.Token);
                var write = fromWeb.CopyToAsync(fromRunner, cancellation.Token);
                await Task.WhenAny(read, write);
                await cancellation.CancelAsync();
                await Task.WhenAll(read, write);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                // The peer can close any time. No response or connection details belong in logs.
            }
            finally
            {
                sockets.Release();
            }
        }
    }
}
