# Flaky test: `WorkCoordinatorOptions_WhenNotConfigured_ShouldUseDefaultAsync`

**Status**: pre-existing failure, 1 in the full suite
**Observed**: 2026-04-18, multiple runs during the receptor-firing investigation
**Scope**: isolated — the other 6 900 tests pass

---

## Symptom

```
failed WorkCoordinatorOptions_WhenNotConfigured_ShouldUseDefaultAsync (1-2ms)
  TUnit.Engine.Exceptions.TestFailedException: AssertionException: Expected to be 600
  but found 30
```

Consistently fails in every full `dotnet run` of `Whizbang.Core.Tests`. Fails the same way in isolation. Fails both before and after the receptor-firing work (Phase 1 / 2 / 3) committed in this session, confirming it's pre-existing.

## The test

`tests/Whizbang.Core.Tests/Messaging/WorkCoordinatorOptionsRegistrationTests.cs:67`

```csharp
[Test]
public async Task WorkCoordinatorOptions_WhenNotConfigured_ShouldUseDefaultAsync() {
  // ...builds a ServiceProvider without configuring WorkCoordinatorOptions...
  var resolvedOptions = /* get IOptions<WorkCoordinatorOptions> */;
  await Assert.That(resolvedOptions.StaleThresholdSeconds).IsEqualTo(600); // Default
}
```

The assertion expects the default `StaleThresholdSeconds` to be **600**, but gets **30**.

## Likely cause

Either:

1. Someone changed the default on `WorkCoordinatorOptions.StaleThresholdSeconds` from 600 to 30 without updating this test's assertion — the class default likely no longer matches what the test expects.
2. A DI registration somewhere (probably in `ServiceCollectionExtensions.AddWhizbangDefaults` or similar) is calling `services.Configure<WorkCoordinatorOptions>(o => o.StaleThresholdSeconds = 30)` unconditionally, overriding the class-level default even in the "not configured" path the test simulates.

## Suggested investigation

1. `git log --follow src/Whizbang.Core/Messaging/WorkCoordinatorOptions.cs` — find the commit that changed the default.
2. `git blame` on the field + the test's assertion.
3. Decide whether 30 s or 600 s is correct, and update the other side.

## Why this session didn't fix it

Scope discipline — it's unrelated to receptor firing, the doubled-fire investigation, or the Phase 1/2/3 deliverables. Fixing it would have required tracing through work-coordinator scheduling semantics that aren't adjacent to the receptor work. Left for a session that's already in `WorkCoordinator` territory.

## Impact on the receptor work

None. The 6 900 of 6 901 pass rate has been stable across all three phases of this session's work. No runtime regression; purely a stale-assertion or stale-default mismatch in one unit test.
