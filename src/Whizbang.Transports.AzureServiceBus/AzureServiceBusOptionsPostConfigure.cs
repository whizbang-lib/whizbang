using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// AOT-safe <see cref="IPostConfigureOptions{TOptions}"/> for <see cref="AzureServiceBusOptions"/>.
/// Reads the <c>Whizbang:Transports:AzureServiceBus</c> section manually (no reflection-based
/// options binder, avoiding IL2026/IL3050) and applies it AFTER the code callback, so an operator
/// can correct any runtime knob from configuration — appsettings.json or environment variables
/// (<c>Whizbang__Transports__AzureServiceBus__SessionIdleTimeout</c>) — without a redeploy.
/// <para>
/// <see cref="AzureServiceBusOptions.AutoProvisionInfrastructure"/> is deliberately NOT bound:
/// it shapes dependency-injection registrations (whether the admin client is registered) at
/// registration time, and configuration cannot re-shape a container that is already built.
/// </para>
/// </summary>
/// <docs>messaging/transports/azure-service-bus#configuration-options</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceCollectionExtensionsTests.cs:AddAzureServiceBusTransport_BindsEveryRuntimeKnobFromConfigurationAsync</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceCollectionExtensionsTests.cs:AddAzureServiceBusTransport_ConfigurationOverridesCodeCallbackAsync</tests>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/ServiceCollectionExtensionsTests.cs:AddAzureServiceBusTransport_AutoProvisionInfrastructure_IsCodeOnlyAsync</tests>
internal sealed class AzureServiceBusOptionsPostConfigure(IConfiguration? configuration)
  : IPostConfigureOptions<AzureServiceBusOptions> {

  /// <summary>Configuration section every runtime knob binds from.</summary>
  internal const string CONFIGURATION_SECTION = "Whizbang:Transports:AzureServiceBus";

  public void PostConfigure(string? name, AzureServiceBusOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var section = configuration?.GetSection(CONFIGURATION_SECTION);
    if (section is null || !section.Exists()) {
      return;
    }

    _bindTimeSpan(section, "SendTimeout", v => options.SendTimeout = v);
    _bindInt(section, "MaxConcurrentCalls", v => options.MaxConcurrentCalls = v);
    _bindInt(section, "PublishMaxConcurrency", v => options.PublishMaxConcurrency = v);
    _bindTimeSpan(section, "MaxAutoLockRenewalDuration", v => options.MaxAutoLockRenewalDuration = v);
    _bindTimeSpan(section, "SubscriptionLockDuration", v => options.SubscriptionLockDuration = v);
    _bindInt(section, "MaxDeliveryAttempts", v => options.MaxDeliveryAttempts = v);
    _bindString(section, "DefaultSubscriptionName", v => options.DefaultSubscriptionName = v);
    _bindBool(section, "EnableSessions", v => options.EnableSessions = v);
    _bindInt(section, "MaxConcurrentSessions", v => options.MaxConcurrentSessions = v);
    _bindTimeSpan(section, "SessionIdleTimeout", v => options.SessionIdleTimeout = v);
    _bindInt(section, "PrefetchCount", v => options.PrefetchCount = v);
    _bindBool(section, "EnableReceiveLivenessWatchdog", v => options.EnableReceiveLivenessWatchdog = v);
    _bindTimeSpan(section, "ReceiveLivenessProbeInterval", v => options.ReceiveLivenessProbeInterval = v);
    _bindTimeSpan(section, "ReceiveLivenessSilenceThreshold", v => options.ReceiveLivenessSilenceThreshold = v);
    _bindInt(section, "InitialRetryAttempts", v => options.InitialRetryAttempts = v);
    _bindTimeSpan(section, "InitialRetryDelay", v => options.InitialRetryDelay = v);
    _bindTimeSpan(section, "MaxRetryDelay", v => options.MaxRetryDelay = v);
    _bindDouble(section, "BackoffMultiplier", v => options.BackoffMultiplier = v);
    _bindBool(section, "RetryIndefinitely", v => options.RetryIndefinitely = v);
    _bindBool(section, "EnableOpsRateSelfCheck", v => options.EnableOpsRateSelfCheck = v);
    _bindDouble(section, "OpsRateWarningThresholdPerSecond", v => options.OpsRateWarningThresholdPerSecond = v);
  }

  private static void _bindTimeSpan(IConfigurationSection section, string key, Action<TimeSpan> apply) {
    if (TimeSpan.TryParse(section[key], CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }

  private static void _bindInt(IConfigurationSection section, string key, Action<int> apply) {
    if (int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }

  private static void _bindDouble(IConfigurationSection section, string key, Action<double> apply) {
    if (double.TryParse(section[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
      apply(value);
    }
  }

  private static void _bindBool(IConfigurationSection section, string key, Action<bool> apply) {
    if (bool.TryParse(section[key], out var value)) {
      apply(value);
    }
  }

  private static void _bindString(IConfigurationSection section, string key, Action<string> apply) {
    var value = section[key];
    if (!string.IsNullOrWhiteSpace(value)) {
      apply(value);
    }
  }
}
