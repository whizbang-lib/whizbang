// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Branch backfill for <see cref="PoisonMessageDetector"/>'s constructor: an options accessor whose
/// value is null fails fast instead of surfacing as a null dereference on the first message.
/// </summary>
public class PoisonMessageDetectorBranchCoverageTests {

  [Test]
  public async Task Constructor_OptionsValueNull_ThrowsArgumentNullExceptionAsync() {
    using var meter = new Meter("Whizbang.Core.Tests.PoisonMessageDetectorBranchCoverage");

    var caught = await Assert.That(() => new PoisonMessageDetector(
        new NullValueOptions(), NullLogger<PoisonMessageDetector>.Instance, meter))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(caught!.ParamName).IsEqualTo("options");
  }

  private sealed class NullValueOptions : IOptions<PoisonMessageOptions> {
    public PoisonMessageOptions Value => null!;
  }
}
