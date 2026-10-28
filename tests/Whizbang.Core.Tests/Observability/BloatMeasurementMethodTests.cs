// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Issue #1807: every stored or reported bloat ratio carries how it was obtained, by a stable name that does not move
/// when the enum is reordered: <c>estimate</c>, <c>approx</c> or <c>exact</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/BloatMeasurement.cs</code-under-test>
/// <docs>operations/infrastructure/storage-maintenance#how-bloat-is-measured</docs>
[Category("Core")]
[Category("Observability")]
public class BloatMeasurementMethodTests {
  [Test]
  [Arguments(BloatMeasurementMethod.Estimate, "estimate")]
  [Arguments(BloatMeasurementMethod.Approx, "approx")]
  [Arguments(BloatMeasurementMethod.Exact, "exact")]
  public async Task EachMethod_IsStoredByItsStableName_AndReadsBackAsync(BloatMeasurementMethod method, string name) {
    await Assert.That(method.ToValue()).IsEqualTo(name)
      .Because("the name is what the queue row and the gauge tag hold, so it must not depend on the enum's order");
    await Assert.That(BloatMeasurementMethods.Parse(name)).IsEqualTo(method);
  }

  [Test]
  public async Task AMissingName_ReadsAsTheEstimateAsync() {
    await Assert.That(BloatMeasurementMethods.Parse(null)).IsEqualTo(BloatMeasurementMethod.Estimate)
      .Because("a ratio recorded before the method existed was the estimate, the only thing there was");
  }

  [Test]
  public async Task TheDefaultMethod_IsTheEstimateAsync() {
    await Assert.That(new Whizbang.Core.Messaging.TableRewriteCandidate("wh_outbox", 4.0, Requested: false).Method)
      .IsEqualTo(BloatMeasurementMethod.Estimate)
      .Because("a candidate a backend reports without a method is the catalog estimate");
  }

  [Test]
  public async Task AnUnknownName_IsRefusedAsync() {
    await Assert.That(() => BloatMeasurementMethods.Parse("guess")).Throws<ArgumentOutOfRangeException>()
      .Because("reading an unknown name as the estimate would hide a measurement this version does not understand");
  }

  [Test]
  public async Task AnUndefinedValue_HasNoNameAsync() {
    await Assert.That(() => ((BloatMeasurementMethod)42).ToValue()).Throws<ArgumentOutOfRangeException>();
  }
}
