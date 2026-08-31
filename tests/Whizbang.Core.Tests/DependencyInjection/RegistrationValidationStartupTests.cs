using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DependencyInjection;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// Tests the startup check that validates the composed service collection.
/// </summary>
/// <remarks>
/// <para>
/// Validation cannot run at the end of <c>AddWhizbang</c>. Storage and transport drivers register
/// their services afterwards, so a check at that point would report every driver-supplied service
/// as missing. A guard that fails on correct compositions gets switched off, which is worse than no
/// guard at all.
/// </para>
/// <para>
/// It runs at startup instead, against the collection as it finally stands, and still inspects
/// descriptors rather than resolving anything, so nothing is constructed in order to check it.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/registration-validation</docs>
[Category("DependencyInjection")]
public class RegistrationValidationStartupTests {

  [Test]
  public async Task StartupFailsNamingADependencyNothingRegistersAsync() {
    var services = new ServiceCollection();
    var check = new RegistrationValidationStartup(
      services,
      [new ServiceRequirement(typeof(Consumer), [typeof(IAbsent)])],
      enabled: true);

    var ex = await Assert.ThrowsAsync<WhizbangRegistrationException>(
      async () => await check.StartAsync(CancellationToken.None));

    await Assert.That(ex!.Missing).Count().IsEqualTo(1);
    await Assert.That(ex.Message).Contains(nameof(IAbsent));
  }

  [Test]
  public async Task StartupPassesWhenADriverRegisteredTheDependencyLaterAsync() {
    var services = new ServiceCollection();
    var check = new RegistrationValidationStartup(
      services,
      [new ServiceRequirement(typeof(Consumer), [typeof(IAbsent)])],
      enabled: true);

    // Exactly the sequence a storage driver produces: the requirement is recorded during
    // AddWhizbang, and the registration arrives afterwards on the builder chain.
    services.AddSingleton<IAbsent>(new Absent());

    await check.StartAsync(CancellationToken.None);

    await Assert.That(services.Any(d => d.ServiceType == typeof(IAbsent))).IsTrue();
  }

  [Test]
  public async Task DisabledValidationDoesNotThrowAsync() {
    var services = new ServiceCollection();
    var check = new RegistrationValidationStartup(
      services,
      [new ServiceRequirement(typeof(Consumer), [typeof(IAbsent)])],
      enabled: false);

    await check.StartAsync(CancellationToken.None);

    // The dependency is still absent, so the pass came from validation being off rather than from
    // the gap having been filled. Without this the test would pass even if the flag did nothing.
    await Assert.That(services.Any(d => d.ServiceType == typeof(IAbsent))).IsFalse()
      .Because("the escape hatch exists for partial compositions that deliberately register a "
             + "subset, so it must skip a genuinely unsatisfied requirement");
  }

  [Test]
  public async Task StoppingIsANoOpAsync() {
    var services = new ServiceCollection();
    var check = new RegistrationValidationStartup(services, [], enabled: true);

    await check.StopAsync(CancellationToken.None);

    await Assert.That(services).IsEmpty();
  }

  private interface IAbsent { }
  private sealed class Absent : IAbsent { }
  private sealed class Consumer { }
}
