// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated perspective runner delegates its idempotency decision to
/// <c>PerspectiveIdempotencyFilter</c> rather than carrying its own copy (#1054). These tests guard the
/// seam: the filter's own unit tests only protect behavior that the runner actually calls, so an inlined
/// comparison reappearing in the template would be invisible to them.
/// </summary>
/// <docs>fundamentals/perspectives/apply-exactly-once#idempotency-filter</docs>
public class PerspectiveRunnerIdempotencyFilterTests {
  private const string SOURCE = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderPlacedEvent : IEvent {
    public Guid Id { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderPlacedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderPlacedEvent @event) => currentData;
  }
}
""";

  private static string? _runner() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(SOURCE);
    return GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_CallsTheSharedIdempotencyFilterAsync() {
    var runner = _runner();

    await Assert.That(runner).IsNotNull();
    await Assert.That(runner)
      .Contains("global::Whizbang.Core.Perspectives.PerspectiveIdempotencyFilter.IsAlreadyApplied(")
      .Because("One tested implementation beats one copy per generated runner, where no unit test can "
        + "reach it.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_DoesNotCompareEventIdsItselfAsync() {
    var runner = _runner();

    await Assert.That(runner).DoesNotContain("string.Compare(e.MessageId.Value.ToString(\"D\")")
      .Because("This exact comparison is what discarded four confirmed writes: against a row whose id "
        + "was a v4 GUID it reported every incoming event as already applied. If it returns to the "
        + "template, the filter's tests will still pass while the runner drops events again.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_LogsAWholeDroppedBatchAboveDebugAsync() {
    var runner = _runner();
    await Assert.That(runner).IsNotNull();

    // Found by position rather than by matching a block of text: an assertion that spans newlines and
    // indentation can pass simply because the generator emitted different whitespace, and a test that
    // cannot fail is worse than no test. This locates the diagnostic, then reads backwards for the
    // logging call that emits it.
    var diagnostic = runner!.IndexOf("[diag.filter.all-dropped]", StringComparison.Ordinal);
    await Assert.That(diagnostic).IsGreaterThan(-1)
      .Because("The diagnostic has to survive, because it names the ids that were dropped.");

    var call = runner.LastIndexOf("_logger.Log", diagnostic, StringComparison.Ordinal);
    await Assert.That(call).IsGreaterThan(-1);
    var level = runner.Substring(call, "_logger.LogWarning".Length);

    await Assert.That(level).IsEqualTo("_logger.LogWarning")
      .Because("Reporting a batch Completed without touching the model is the one trace that a write was "
        + "discarded. At Debug nobody sees it, which is why a consumer reported this as data loss before "
        + "the library noticed it.");

    var guard = runner.LastIndexOf("IsEnabled(LogLevel.", diagnostic, StringComparison.Ordinal);
    await Assert.That(runner.Substring(guard, "IsEnabled(LogLevel.Warning".Length))
      .IsEqualTo("IsEnabled(LogLevel.Warning")
      .Because("A Warning behind a Debug guard would never be emitted — the guard and the level have to "
        + "agree, and nothing else in the method would reveal that they do not.");
  }
}
