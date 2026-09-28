// Strings.Lang is process-wide (the app has one UI language); tests that switch it to Dutch must not race with
// tests that assert English engine messages, so test classes run one after another.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
