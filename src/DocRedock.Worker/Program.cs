using DocRedock.Worker;

return await WorkerHost.RunAsync(Console.In, Console.Out, CancellationToken.None);
