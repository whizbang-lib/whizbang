namespace Whizbang.Hosting.AspNet;

/// <summary>
/// How <see cref="DatabaseAvailabilityMiddleware"/> gates traffic while the schema is not ready.
/// </summary>
/// <docs>resilience/database-availability-middleware</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/DatabaseAvailabilityMiddlewareModeTests.cs:MutationsOnly_NotReady_ReadPassesThroughAsync</tests>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/DatabaseAvailabilityMiddlewareModeTests.cs:MutationsOnly_NotReady_WriteIsGatedAsync</tests>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/DatabaseAvailabilityMiddlewareModeTests.cs:AllNonExempt_NotReady_ReadIsGatedAsync</tests>
public enum AvailabilityGateMode {
  /// <summary>503 every non-exempt request until the schema is ready (the original behavior).</summary>
  AllNonExempt,

  /// <summary>
  /// 503 only unsafe (mutating) requests — POST/PUT/PATCH/DELETE; safe reads (GET/HEAD/OPTIONS) pass
  /// through. Lets a host serve reads (which hit the read-model tables, untouched by an event-store
  /// migration) while the write/append path waits for the migration.
  /// </summary>
  MutationsOnly
}
