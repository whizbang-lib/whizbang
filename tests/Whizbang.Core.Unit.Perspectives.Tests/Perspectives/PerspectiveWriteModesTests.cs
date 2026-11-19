// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// How the worker chooses each perspective's write mode (#2246): set-based by default, a perspective's declared mode over
/// the service's, and per stream for a perspective with no generated runner.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#selection</docs>
public class PerspectiveWriteModesTests {
  [Test]
  public async Task WorkerOptions_DefaultToSetBasedAsync() {
    await Assert.That(new PerspectiveWorkerOptions().WriteMode).IsEqualTo(PerspectiveWriteMode.SetBased)
      .Because("a service that configures nothing writes set-based");
  }

  [Test]
  public async Task AGeneratedRunnerThatDeclaresNothing_TakesTheServicesModeAsync() {
    var name = $"Tests.Undeclared{Guid.NewGuid():N}";
    PerspectiveWriteModes.Register(name, null);

    await Assert.That(PerspectiveWriteModes.Resolve(name, PerspectiveWriteMode.SetBased)).IsEqualTo(PerspectiveWriteMode.SetBased);
    await Assert.That(PerspectiveWriteModes.Resolve(name, PerspectiveWriteMode.PerStream)).IsEqualTo(PerspectiveWriteMode.PerStream);
  }

  [Test]
  public async Task ADeclaredMode_WinsOverTheServicesAsync() {
    var perStream = $"Tests.PerStream{Guid.NewGuid():N}";
    var setBased = $"Tests.SetBased{Guid.NewGuid():N}";
    PerspectiveWriteModes.Register(perStream, PerspectiveWriteMode.PerStream);
    PerspectiveWriteModes.Register(setBased, PerspectiveWriteMode.SetBased);

    await Assert.That(PerspectiveWriteModes.Resolve(perStream, PerspectiveWriteMode.SetBased)).IsEqualTo(PerspectiveWriteMode.PerStream);
    await Assert.That(PerspectiveWriteModes.Resolve(setBased, PerspectiveWriteMode.PerStream)).IsEqualTo(PerspectiveWriteMode.SetBased);
  }

  [Test]
  public async Task APerspectiveWithNoGeneratedRunner_IsAlwaysWrittenPerStreamAsync() {
    await Assert.That(PerspectiveWriteModes.Resolve($"Tests.HandWritten{Guid.NewGuid():N}", PerspectiveWriteMode.SetBased))
      .IsEqualTo(PerspectiveWriteMode.PerStream)
      .Because("a hand-written runner prepares no write, so it has nothing to write set-based");
  }

  [Test]
  public async Task TheAttribute_CarriesItsModeAsync() {
    await Assert.That(new PerspectiveWriteModeAttribute(PerspectiveWriteMode.PerStream).Mode).IsEqualTo(PerspectiveWriteMode.PerStream);
  }

  [Test]
  public async Task ArgumentsAreRequiredAsync() {
    await Assert.That(() => PerspectiveWriteModes.Register(" ", null)).Throws<ArgumentException>();
    await Assert.That(() => PerspectiveWriteModes.Resolve(null!, PerspectiveWriteMode.SetBased)).Throws<ArgumentNullException>();
  }
}
