using Nexus.LoadRunner;

if (args.Length > 0)
{
    Console.Error.WriteLine("Nexus.LoadRunner reads its trusted host configuration from environment variables.");
    return 2;
}

try
{
    var settings = RunnerSettings.FromEnvironment();
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    using var worker = new LoadWorker(settings);
    await worker.RunAsync(shutdown.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (RunnerFailure ex)
{
    Console.Error.WriteLine($"Nexus Load Runner could not start: {ex.Code}");
    return 1;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Nexus Load Runner could not open its local state directory.");
    return 1;
}
