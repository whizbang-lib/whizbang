namespace Whizbang.Core.Configuration;

/// <summary>
/// Options for controlling auto-registered service behavior.
/// Used by <see cref="ServiceRegistrationCallbacks"/> when registering discovered services.
/// </summary>
/// <remarks>
/// <para>
/// These options are passed to the generated service registration callbacks,
/// allowing users to control how lenses and perspectives are registered.
/// </para>
/// <example>
/// <code>
/// services.AddWhizbang(options => {
///   options.Services.IncludeSelfRegistration = false;  // Only register interfaces
/// });
/// </code>
/// </example>
/// </remarks>
/// <docs>operations/configuration/service-registration-options</docs>
public sealed class ServiceRegistrationOptions {
  /// <summary>
  /// If true, registers concrete types as themselves in addition to their interfaces.
  /// Default: true.
  /// </summary>
  /// <remarks>
  /// <para>
  /// When enabled, both interface and concrete registrations are created:
  /// <code>
  /// services.AddTransient&lt;IOrderLens, OrderLens&gt;();  // Interface registration
  /// services.AddTransient&lt;OrderLens&gt;();               // Self-registration
  /// </code>
  /// </para>
  /// <para>
  /// When disabled, only interface registrations are created:
  /// <code>
  /// services.AddTransient&lt;IOrderLens, OrderLens&gt;();  // Interface registration only
  /// </code>
  /// </para>
  /// </remarks>
  public bool IncludeSelfRegistration { get; set; } = true;
}
