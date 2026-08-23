using Rtmd.Worker;

return await WorkerHost.RunAsync(Console.In, Console.Out, CancellationToken.None);
