// Copyright (c) 2026 crossVault GmbH.

using HydraData.Host;

// Console entry point for scheduler/CI. All logic lives in small, testable
// classes; Program.cs only wires the cancellation token and returns the process exit code (0/1/2) verbatim
// from the run report.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the run roll back/propagate rather than hard-killing the process.
    cts.Cancel();
};

// Dispatch:
//   "hydradata session"        → interactive REPL (SessionBootstrap).
//   "hydradata resume <order>" → batch resume-from run; validates all, executes order >= <order>.
//   no args (anything else)  → existing full-run batch mode (HostBootstrap).
if (args.Length >= 1 && args[0].Equals("session", StringComparison.OrdinalIgnoreCase))
    return await SessionBootstrap.RunAsync(Directory.GetCurrentDirectory(), ct: cts.Token);

if (args.Length >= 1 && args[0].Equals("resume", StringComparison.OrdinalIgnoreCase))
{
    var orderArg = args.Length >= 2 ? args[1] : null;
    if (!OrderKeyParser.TryParse(orderArg, out var resumeFrom))
    {
        await Console.Error.WriteLineAsync(
            $"Cannot parse '{orderArg}' as a step order. " +
            "Usage: hydradata resume <order>  (e.g. hydradata resume 02_10 or 02_10_01).").ConfigureAwait(false);
        return 1; // config-class failure: the run never started.
    }

    return await HostBootstrap.RunResumeAsync(Directory.GetCurrentDirectory(), resumeFrom!, cts.Token);
}

return await HostBootstrap.RunAsync(Directory.GetCurrentDirectory(), cts.Token);
