// Copyright (c) 2026 crossVault GmbH.

using Xunit;

// The host's end-to-end tests run the real PumpEngine, which captures the process-global Console.Out/Error
// per step (StepOutputCapture). Running such tests in parallel races on that global state, so the whole
// assembly is serialised — the Engine test project does the same via a shared collection. The host suite is
// small, so disabling parallelism outright is simpler than threading a collection through every test class.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
