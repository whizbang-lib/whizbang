// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace ECommerce.InMemory.Integration.Tests.Fixtures;

/// <summary>
/// Starts the shared in-memory fixture once per test session, before any test's own time starts.
/// </summary>
/// <remarks>
/// <para>
/// A test's <c>[Timeout]</c> covers its whole lifecycle: the assembly, class and test
/// <c>[Before]</c> hooks and the body, while the duration a failure reports counts only the
/// body. Starting the fixture from a <c>[Before(Test)]</c> hook charged its entire start (a
/// database, two schemas' migrations, two hosts) to whichever test happened to run first. On a
/// loaded machine running every test project against one shared Postgres, that start took most
/// of the first test's minute, and the test "timed out after 00:01:00" with a reported duration
/// of a few seconds.
/// </para>
/// <para>
/// Session hooks run before the per-test timeout begins and are bounded by their own hook
/// timeout, so the start is paid once, outside every test's budget. The per-test hooks still call
/// <see cref="SharedInMemoryFixtureSource.GetFixtureAsync"/>, which now returns the started
/// fixture at once.
/// </para>
/// </remarks>
public static class SharedInMemoryFixtureSessionHooks {
  [Before(TestSession)]
  [RequiresUnreferencedCode("EF Core in tests may use unreferenced code")]
  [RequiresDynamicCode("EF Core in tests may use dynamic code")]
  public static async Task StartSharedFixtureAsync() {
    _ = await SharedInMemoryFixtureSource.GetFixtureAsync();
  }
}
