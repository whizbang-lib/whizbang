using Microsoft.Extensions.Hosting;

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
internal sealed class TagPolicyStartupValidator : IHostedService {
  private readonly TagOptions _options;
  private readonly Func<IEnumerable<MessageTagRegistration>> _registrationSource;

  /// <summary>DI constructor — validates against the process-global tag registry.</summary>
  public TagPolicyStartupValidator(TagOptions options)
    : this(options, MessageTagRegistry.GetAllTags) { }

  /// <summary>Test seam: an injectable registration source avoids polluting the global registry.</summary>
  internal TagPolicyStartupValidator(TagOptions options, Func<IEnumerable<MessageTagRegistration>> registrationSource) {
    _options = options ?? throw new ArgumentNullException(nameof(options));
    _registrationSource = registrationSource ?? throw new ArgumentNullException(nameof(registrationSource));
  }

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken) {
    TagPolicyValidator.Validate(_registrationSource(), _options.CoalesceBindings);
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
