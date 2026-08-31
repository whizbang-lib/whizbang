using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Whizbang.Core.DependencyInjection;

/// <summary>
/// Validates the composed service collection once, at startup.
/// </summary>
/// <remarks>
/// <para>
/// The check cannot run at the end of <c>AddWhizbang</c>. Storage and transport drivers register
/// their services afterwards on the builder chain, so validating there would report every
/// driver-supplied service as missing. A guard that fails on correct compositions is worse than no
/// guard: it gets switched off, and takes the real failures with it.
/// </para>
/// <para>
/// Holding the <see cref="IServiceCollection"/> and checking it at startup sees the composition as
/// it finally stands. It still reads descriptors rather than resolving anything, so nothing is
/// constructed in order to be checked and no factory side effect fires.
/// </para>
/// <para>
/// Failing here rather than at first use is the point: a dependency nothing registers is a defect
/// in the composition, and the alternative is that it surfaces later as a feature that quietly does
/// nothing.
/// </para>
/// </remarks>
/// <docs>operations/dependency-injection/registration-validation</docs>
/// <tests>tests/Whizbang.Core.Tests/DependencyInjection/RegistrationValidationStartupTests.cs</tests>
internal sealed class RegistrationValidationStartup : IHostedService {

  private readonly IServiceCollection _services;
  private readonly IReadOnlyList<ServiceRequirement> _requirements;
  private readonly bool _enabled;

  /// <summary>Creates the startup check.</summary>
  /// <param name="services">The collection to validate, as it stands at startup.</param>
  /// <param name="requirements">The generated manifest of constructor dependencies.</param>
  /// <param name="enabled">False to skip validation entirely.</param>
  public RegistrationValidationStartup(
      IServiceCollection services,
      IReadOnlyList<ServiceRequirement> requirements,
      bool enabled) {
    _services = services;
    _requirements = requirements;
    _enabled = enabled;
  }

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    if (_enabled) {
      _services.ValidateWhizbangRegistrations(_requirements);
    }
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
