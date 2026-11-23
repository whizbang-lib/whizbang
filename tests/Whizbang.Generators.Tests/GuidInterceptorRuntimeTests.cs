// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// What an intercepted call returns at run time (#2528). The generated interceptors are compiled into a probe the way a
/// consumer build compiles them, loaded, and called: an intercepted <c>Guid.NewGuid()</c> or <c>Guid.CreateVersion7()</c>
/// returns a framework UUIDv7, a call under <c>[SuppressGuidInterception]</c> keeps the original v4, and interception
/// runs when the project sets nothing.
/// </summary>
[Category("Generators")]
[Category("Interceptors")]
public class GuidInterceptorRuntimeTests {
  private const string INTERCEPTORS_NAMESPACE = "Whizbang.Generators.Generated";

  private const string PROBE = """
    using System;
    using Whizbang.Core;

    namespace App;

    public static class Probe {
      public static Guid NewGuid() => Guid.NewGuid();

      public static Guid CreateVersion7() => Guid.CreateVersion7();

      [SuppressGuidInterception]
      public static Guid Suppressed() => Guid.NewGuid();

      public static Guid[] ManyNewGuids() {
        var ids = new Guid[2000];
        for (var i = 0; i < ids.Length; i++) {
          ids[i] = Guid.NewGuid();
        }
        return ids;
      }
    }
    """;

  [RequiresAssemblyFiles]
  private static Guid _call(Dictionary<string, string>? options, string method) {
    var assembly = GeneratorTestHelper.CompileAndLoadWithInterceptors(new GuidInterceptorGenerator(), PROBE, INTERCEPTORS_NAMESPACE, options);
    return (Guid)assembly.GetType("App.Probe", throwOnError: true)!.GetMethod(method)!.Invoke(null, null)!;
  }

  /// <summary>An intercepted <c>Guid.NewGuid()</c> returns version 7, with the project setting nothing.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task InterceptedNewGuid_WithTheDefaultConfiguration_ReturnsVersion7Async() {
    var id = _call(options: null, "NewGuid");

    await Assert.That(id.Version).IsEqualTo(7)
      .Because("interception is on by default and replaces the call with a framework UUIDv7");
  }

  /// <summary>An intercepted <c>Guid.CreateVersion7()</c> returns version 7 as well.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task InterceptedCreateVersion7_ReturnsVersion7Async() {
    var id = _call(options: null, "CreateVersion7");

    await Assert.That(id.Version).IsEqualTo(7);
  }

  /// <summary>
  /// The replacement is the framework generator, which is monotonic within a millisecond by its counter. The runtime's
  /// own <c>Guid.CreateVersion7()</c> and <c>Guid.NewGuid()</c> fill those bits at random, so a long run of them is
  /// never strictly increasing; the framework's always is.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task InterceptedNewGuid_IsTheCounterPrecisionGenerator_StrictlyIncreasingAsync() {
    var assembly = GeneratorTestHelper.CompileAndLoadWithInterceptors(new GuidInterceptorGenerator(), PROBE, INTERCEPTORS_NAMESPACE);
    var ids = (Guid[])assembly.GetType("App.Probe", throwOnError: true)!.GetMethod("ManyNewGuids")!.Invoke(null, null)!;

    for (var i = 1; i < ids.Length; i++) {
      await Assert.That(ids[i].CompareTo(ids[i - 1])).IsGreaterThan(0)
        .Because($"id {i} must sort after id {i - 1}");
    }
  }

  /// <summary>A call under <c>[SuppressGuidInterception]</c> keeps the original v4.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task SuppressedNewGuid_ReturnsVersion4Async() {
    var id = _call(options: null, "Suppressed");

    await Assert.That(id.Version).IsEqualTo(4);
  }

  /// <summary>A project that sets the property to false keeps the original call.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task InterceptionTurnedOff_ReturnsVersion4Async() {
    var id = _call(new Dictionary<string, string> { ["build_property.WhizbangGuidInterceptionEnabled"] = "false" }, "NewGuid");

    await Assert.That(id.Version).IsEqualTo(4);
  }
}
