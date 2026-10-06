// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Branch backfill for <see cref="PerspectiveRowRetentionConfigurator"/>'s constructor guard: a null
/// options accessor and an accessor whose value is null both fail fast.
/// </summary>
public class PerspectiveRowRetentionConfiguratorBranchCoverageTests {

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    var caught = await Assert.That(() => new PerspectiveRowRetentionConfigurator(
        null!, NullLogger<PerspectiveRowRetentionConfigurator>.Instance))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(caught!.ParamName).IsEqualTo("options");
  }

  [Test]
  public async Task Constructor_OptionsValueNull_ThrowsArgumentNullExceptionAsync() {
    var caught = await Assert.That(() => new PerspectiveRowRetentionConfigurator(
        new NullValueOptions(), NullLogger<PerspectiveRowRetentionConfigurator>.Instance))
      .ThrowsExactly<ArgumentNullException>()
      .Because("an accessor that yields no options is as unusable as no accessor; it must fail at construction, not at startup");
    await Assert.That(caught!.ParamName).IsEqualTo("options");
  }

  private sealed class NullValueOptions : IOptions<PerspectiveRowRetentionOptions> {
    public PerspectiveRowRetentionOptions Value => null!;
  }
}
