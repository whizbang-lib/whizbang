using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Serialization;

namespace Whizbang.SignalR.DependencyInjection;

/// <summary>
/// Extension methods for configuring SignalR with Whizbang's AOT-compatible JSON serialization.
/// </summary>
/// <docs>apis/signalr/signalr</docs>
public static class SignalRServiceCollectionExtensions {
  /// <summary>
  /// Adds SignalR to the service collection and configures it to use Whizbang's
  /// <see cref="JsonContextRegistry"/> for AOT-compatible polymorphic JSON serialization.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>An <see cref="ISignalRServerBuilder"/> for further configuration.</returns>
  /// <remarks>
  /// <para>
  /// This method automatically configures SignalR's JSON protocol to use the combined
  /// <see cref="System.Text.Json.JsonSerializerOptions"/> from <see cref="JsonContextRegistry"/>,
  /// which includes:
  /// </para>
  /// <list type="bullet">
  /// <item>All Whizbang core types (MessageEnvelope, MessageHop, etc.)</item>
  /// <item>Application message types (ICommand, IEvent implementations)</item>
  /// <item>Polymorphic types with [JsonPolymorphic] and [JsonDerivedType] attributes</item>
  /// <item>WhizbangId value objects</item>
  /// </list>
  /// <para>
  /// This enables turn-key support for pushing polymorphic types over SignalR without
  /// manual serialization configuration.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // In Program.cs
  /// var builder = WebApplication.CreateBuilder(args);
  ///
  /// builder.Services.AddWhizbangSignalR()
  ///     .AddHubOptions&lt;NotificationHub&gt;(options => {
  ///         options.EnableDetailedErrors = true;
  ///     });
  ///
  /// var app = builder.Build();
  /// app.MapHub&lt;NotificationHub&gt;("/notifications");
  /// </code>
  /// </example>
  /// <docs>apis/signalr/signalr</docs>
  public static ISignalRServerBuilder AddWhizbangSignalR(this IServiceCollection services) {
    return services.AddSignalR()
        .AddJsonProtocol(options => {
          options.PayloadSerializerOptions = JsonContextRegistry.CreateCombinedOptions();
        });
  }

  /// <summary>
  /// Adds SignalR to the service collection with additional configuration and configures it to use
  /// Whizbang's <see cref="JsonContextRegistry"/> for AOT-compatible polymorphic JSON serialization.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">An action to configure SignalR hub options.</param>
  /// <returns>An <see cref="ISignalRServerBuilder"/> for further configuration.</returns>
  /// <example>
  /// <code>
  /// builder.Services.AddWhizbangSignalR(options => {
  ///     options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
  ///     options.KeepAliveInterval = TimeSpan.FromSeconds(10);
  /// });
  /// </code>
  /// </example>
  /// <docs>apis/signalr/signalr</docs>
  public static ISignalRServerBuilder AddWhizbangSignalR(
      this IServiceCollection services,
      Action<HubOptions> configure) {
    return services.AddSignalR(configure)
        .AddJsonProtocol(options => {
          options.PayloadSerializerOptions = JsonContextRegistry.CreateCombinedOptions();
        });
  }
}
