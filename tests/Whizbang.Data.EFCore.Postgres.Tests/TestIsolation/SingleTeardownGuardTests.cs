// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.TestIsolation;

/// <summary>
/// A fixture tears down in exactly one place (#1954). TUnit runs a fixture's <c>[After(Test)]</c> hooks and then disposes
/// the instance when it is <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>, so a fixture that is both ran its
/// teardown twice: once as the hook, again as the dispose that called it, or the hook that was itself the dispose method.
/// Idempotent teardown hid it; a teardown that is not (a second drop of a database another test already reuses the name
/// of, a second dispose of a shared data source) would not.
/// </summary>
/// <remarks>
/// Read from this assembly's types, so a base class counts for every fixture that derives from it. The teardown stays in
/// the <c>[After(Test)]</c> hook, which TUnit runs whether the test passed or failed, and the interface goes.
/// </remarks>
[Category("Shard1")]
public class SingleTeardownGuardTests {
  private static bool _isTestTeardown(MethodInfo method) =>
    method.GetCustomAttributesData().Any(a =>
      a.AttributeType == typeof(AfterAttribute)
      && a.ConstructorArguments.Count > 0
      && a.ConstructorArguments[0].Value is int hook
      && hook == (int)HookType.Test);

  private static IEnumerable<Type> _fixturesWithATestTeardown() =>
    typeof(SingleTeardownGuardTests).Assembly.GetTypes()
      .Where(t => t is { IsClass: true }
        && t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(_isTestTeardown));

  [Test]
  public async Task NoFixtureWithATestTeardownHook_IsAlsoDisposableAsync() {
    var withHooks = _fixturesWithATestTeardown().ToList();

    await Assert.That(withHooks).Contains(typeof(SingleTeardownGuardTests).Assembly.GetType(
      "Whizbang.Data.EFCore.Postgres.Tests.EFCoreTestBase")!)
      .Because("the scan must find the base class's hook, or an empty answer proves nothing");

    var offenders = withHooks
      .Where(t => typeof(IAsyncDisposable).IsAssignableFrom(t) || typeof(IDisposable).IsAssignableFrom(t))
      .Select(t => t.FullName)
      .Order(StringComparer.Ordinal)
      .ToList();
    await Assert.That(offenders).IsEmpty()
      .Because("TUnit runs the [After(Test)] hook and then disposes a disposable fixture, so its teardown runs twice; keep "
        + $"the hook and drop the interface: {string.Join(", ", offenders)}");
  }
}
