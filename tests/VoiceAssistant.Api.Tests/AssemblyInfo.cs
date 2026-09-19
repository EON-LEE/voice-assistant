using Xunit;

// Meter listeners are process-wide; keep instrumentation assertions isolated from socket sessions.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
