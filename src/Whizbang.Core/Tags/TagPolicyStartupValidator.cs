using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Tags;

/// <summary>
/// Hosted seam for <see cref="TagPolicyValidator"/>: runs the tag-policy checks in
/// <c>StartAsync</c>, so a violation aborts <c>host.RunAsync()</c> — a hard boot failure, not a
/// degraded start. A plain <see cref="IHostedService"/> (the <c>WhizbangStartupLogger</c> /
/// configurator shape) because it must run after every assembly's <c>[ModuleInitializer]</c>
/// has populated <see cref="MessageTagRegistry"/>, which registration-time validation inside
/// <c>AddWhizbang</c> could not guarantee.
/// </summary>
/// <docs>fundamentals/messages/message-tags#validation</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorTests.cs:StartAsync_SysViolationInRegistrations_FailsHostStartAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorTests.cs:AddWhizbang_RegistersTheStartupValidatorAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagPolicyValidatorRouteNamespaceTests.cs:StartAsync_RouteBindingOnUnknownSysTag_FailsHostStartAsync</tests>
internal sealed class TagPolicyStartupValidator : IHostedService {
  private readonly TagOptions _options;
  private readonly SystemEvents.SystemEventOptions? _systemEventOptions;
  private readonly Func<IEnumerable<MessageTagRegistration>> _registrationSource;
  private readonly IConfiguration? _configuration;

  /// <summary>DI constructor — validates against the process-global tag registry.</summary>
  public TagPolicyStartupValidator(
      TagOptions options,
      IOptions<SystemEvents.SystemEventOptions>? systemEventOptions = null,
      IConfiguration? configuration = null)
    : this(options, MessageTagRegistry.GetAllTags, configuration) {
    _systemEventOptions = systemEventOptions?.Value;
  }

  /// <summary>Test seam: an injectable registration source avoids polluting the global registry.</summary>
  internal TagPolicyStartupValidator(
      TagOptions options,
      Func<IEnumerable<MessageTagRegistration>> registrationSource,
      IConfiguration? configuration = null) {
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _registrationSource = registrationSource ?? throw new ArgumentNullException(nameof(registrationSource));
    _configuration = configuration;
  }

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    // The built-in audit binding must be visible to validation (ambiguity counts effective
    // bindings), so apply the EnableAudit translation before validating. Idempotent and
    // add-if-absent — host bindings always survive.
    SystemEvents.SystemEventCoalesceDefaults.Apply(_options, _systemEventOptions);
    // Configuration-bound RouteNamespace bindings (Whizbang:Tags:RouteNamespace:<tag>) must
    // be visible too — a sys-* binding smuggled in via configuration fails startup exactly
    // like a code binding. Idempotent; the TransportNamespaceResolver factory applies the
    // same binder, whichever runs first.
    TagRouteNamespaceConfigurationBinder.Apply(_options, _configuration);
    TagPolicyValidator.Validate(_registrationSource(), _options.CoalesceBindings, _options.RouteNamespaceBindings);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
