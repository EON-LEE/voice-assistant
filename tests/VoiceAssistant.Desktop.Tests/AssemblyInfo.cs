// WPF windows and the process-wide microphone lease are shared state; run these UI tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
