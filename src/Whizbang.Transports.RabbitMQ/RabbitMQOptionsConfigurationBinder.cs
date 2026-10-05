// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Whizbang.Core.Configuration;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.RabbitMQ;

/// <summary>
/// AOT-safe binder for <see cref="RabbitMQOptions"/>: reads the
/// <c>Whizbang:Transports:RabbitMQ</c> section by hand (no reflection-based options binder) and
/// applies it OVER the code callback, the same idiom as the <c>AzureServiceBus</c> section, so an
/// operator can correct any knob from appsettings.json or an environment variable
/// (<c>Whizbang__Transports__RabbitMQ__PrefetchCount</c>) without a redeploy.
/// </summary>
/// <docs>operations/configuration/configuration-reference#transport-sections</docs>
/// <tests>tests/Whizbang.Transports.RabbitMQ.Tests/RabbitMQOptionsConfigurationTests.cs</tests>
internal static class RabbitMQOptionsConfigurationBinder {
  /// <summary>This transport's key under <c>Whizbang:Transports</c>.</summary>
  internal const string TRANSPORT_SECTION_NAME = "RabbitMQ";

  /// <summary>Configuration section every knob binds from.</summary>
  internal const string CONFIGURATION_SECTION = TransportConfigurationSection.ROOT + ":" + TRANSPORT_SECTION_NAME;

  /// <summary>
  /// Applies every configured key onto <paramref name="options"/> and returns it. No-ops when
  /// <paramref name="configuration"/> is null or the section is absent.
  /// </summary>
  internal static RabbitMQOptions Apply(RabbitMQOptions options, IConfiguration? configuration) {
    ArgumentNullException.ThrowIfNull(options);
    var section = configuration?.GetSection(CONFIGURATION_SECTION);
    if (section?.Exists() != true) {
      return options;
    }

    ConfigurationValueBinder.BindInt(section, nameof(RabbitMQOptions.MaxChannels), v => options.MaxChannels = v);
    ConfigurationValueBinder.BindInt(section, nameof(RabbitMQOptions.MaxDeliveryAttempts), v => options.MaxDeliveryAttempts = v);
    ConfigurationValueBinder.BindString(section, nameof(RabbitMQOptions.DefaultQueueName), v => options.DefaultQueueName = v);
    ConfigurationValueBinder.BindUShort(section, nameof(RabbitMQOptions.PrefetchCount), v => options.PrefetchCount = v);
    ConfigurationValueBinder.BindBool(section, nameof(RabbitMQOptions.AutoDeclareDeadLetterExchange), v => options.AutoDeclareDeadLetterExchange = v);
    ConfigurationValueBinder.BindBool(section, nameof(RabbitMQOptions.EnableSingleActiveConsumer), v => options.EnableSingleActiveConsumer = v);
    ConfigurationValueBinder.BindInt(section, nameof(RabbitMQOptions.InitialRetryAttempts), v => options.InitialRetryAttempts = v);
    ConfigurationValueBinder.BindTimeSpan(section, nameof(RabbitMQOptions.InitialRetryDelay), v => options.InitialRetryDelay = v);
    ConfigurationValueBinder.BindTimeSpan(section, nameof(RabbitMQOptions.MaxRetryDelay), v => options.MaxRetryDelay = v);
    ConfigurationValueBinder.BindDouble(section, nameof(RabbitMQOptions.BackoffMultiplier), v => options.BackoffMultiplier = v);
    ConfigurationValueBinder.BindBool(section, nameof(RabbitMQOptions.RetryIndefinitely), v => options.RetryIndefinitely = v);
    return options;
  }
}
