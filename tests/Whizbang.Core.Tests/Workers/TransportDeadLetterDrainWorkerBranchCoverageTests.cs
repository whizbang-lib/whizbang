// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The options guard of <see cref="TransportDeadLetterDrainWorker"/>: a missing options wrapper and a
/// wrapper with no value are both rejected at construction, naming the parameter.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TransportDeadLetterDrainWorker.cs</code-under-test>
[Category("Workers")]
public class TransportDeadLetterDrainWorkerBranchCoverageTests {

  private sealed class NullValueOptions : IOptions<TransportDeadLetterDrainWorkerOptions> {
    public TransportDeadLetterDrainWorkerOptions Value => null!;
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    // The guard runs before the metrics counter is built, so the metrics argument is never read.
    await Assert.That(() => new TransportDeadLetterDrainWorker(
        sp.GetRequiredService<IServiceScopeFactory>(),
        null!,
        null!,
        NullLogger<TransportDeadLetterDrainWorker>.Instance,
        SchemaReadyGate.AlreadyReady()))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new TransportDeadLetterDrainWorker(
        sp.GetRequiredService<IServiceScopeFactory>(),
        new NullValueOptions(),
        null!,
        NullLogger<TransportDeadLetterDrainWorker>.Instance,
        SchemaReadyGate.AlreadyReady()))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }
}
