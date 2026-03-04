using TUnit.Core;

// Force all test classes to run sequentially (not in parallel) to prevent Service Bus message stealing.
// This prevents race conditions where concurrent test fixtures drain messages from shared subscriptions
// (sub-00-a and sub-01-a), causing tests to timeout waiting for events that were stolen by other fixtures.
// Each test class will now initialize, run, and dispose completely before the next class starts.
//
// Root cause: Multiple test classes running concurrently all drain from the same 2 Service Bus subscriptions,
// causing message stealing where Test A's events are drained by Test B's fixture initialization.
//
// Alternative solutions considered:
// - Dedicated subscriptions per test class: More complex, requires Config-Named.json changes
// - Locking mechanism: Added complexity without significant benefit
// - Sequential execution: Simple, reliable, trades speed for correctness
[assembly: NotInParallel]
