using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whizbang.Core.Security.Extractors;

namespace Whizbang.Core.Security;

/// <summary>
/// Extension methods for registering message security services.
/// </summary>
/// <docs>core-concepts/message-security#registration</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/MessageSecurityServiceCollectionExtensionsTests.cs</tests>
public static class MessageSecurityServiceCollectionExtensions {
  /// <summary>
  /// Registers message security services for establishing security context from incoming messages.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">Optional configuration action for MessageSecurityOptions.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// This method registers:
  /// - IMessageSecurityContextProvider (DefaultMessageSecurityContextProvider)
  /// - IScopeContextAccessor (scoped)
  /// - IMessageContextAccessor (scoped) - provides access to current message context
  /// - IMessageContext (scoped) - injectable message context with UserId from security context
  /// - MessageHopSecurityExtractor (default extractor, priority 100)
  ///
  /// Additional extractors can be registered using AddSecurityExtractor&lt;T&gt;().
  /// Callbacks can be registered using AddSecurityContextCallback&lt;T&gt;().
  ///
  /// By default, AllowAnonymous is FALSE (least privilege). Messages without
  /// security context will be rejected unless explicitly configured otherwise.
  /// </remarks>
  /// <example>
  /// services.AddWhizbangMessageSecurity(options => {
  ///   // Must explicitly opt-in to allow anonymous messages
  ///   options.AllowAnonymous = false; // This is the default
  ///
  ///   // Exempt specific message types
  ///   options.ExemptMessageTypes.Add(typeof(HealthCheckMessage));
  /// });
  ///
  /// // Register custom extractors
  /// services.AddSecurityExtractor&lt;JwtPayloadExtractor&gt;();
  ///
  /// // Register callbacks
  /// services.AddSecurityContextCallback&lt;UserContextManagerCallback&gt;();
  /// </example>
  public static IServiceCollection AddWhizbangMessageSecurity(
    this IServiceCollection services,
    Action<MessageSecurityOptions>? configure = null) {
    // Create and configure options
    var options = new MessageSecurityOptions();
    configure?.Invoke(options);

    // Register options as singleton
    services.AddSingleton(options);

    // Register scoped IScopeContextAccessor
    services.TryAddScoped<IScopeContextAccessor, ScopeContextAccessor>();

    // Register scoped IMessageContextAccessor for accessing current message context
    services.TryAddScoped<IMessageContextAccessor, MessageContextAccessor>();

    // Register scoped IMessageContext that reads from accessors
    // Enables DI injection of IMessageContext in receptors with UserId from security context
    services.TryAddScoped<IMessageContext, ScopedMessageContext>();

    // Register default extractor
    services.AddSecurityExtractor<MessageHopSecurityExtractor>();

    // Register the provider
    services.AddScoped<IMessageSecurityContextProvider>(sp => {
      var extractors = sp.GetServices<ISecurityContextExtractor>();
      var callbacks = sp.GetServices<ISecurityContextCallback>();
      var opts = sp.GetRequiredService<MessageSecurityOptions>();
      return new DefaultMessageSecurityContextProvider(extractors, callbacks, opts);
    });

    return services;
  }

  /// <summary>
  /// Registers a security context extractor.
  /// Extractors are called in priority order (lower priority = runs first).
  /// </summary>
  /// <typeparam name="TExtractor">The extractor type to register.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <example>
  /// services.AddSecurityExtractor&lt;JwtPayloadExtractor&gt;();
  /// services.AddSecurityExtractor&lt;ServiceBusMetadataExtractor&gt;();
  /// </example>
  public static IServiceCollection AddSecurityExtractor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TExtractor>(this IServiceCollection services)
    where TExtractor : class, ISecurityContextExtractor {
    services.AddScoped<ISecurityContextExtractor, TExtractor>();
    return services;
  }

  /// <summary>
  /// Registers a security context callback.
  /// Callbacks are invoked after security context is successfully established.
  /// </summary>
  /// <typeparam name="TCallback">The callback type to register.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <example>
  /// services.AddSecurityContextCallback&lt;UserContextManagerCallback&gt;();
  /// </example>
  public static IServiceCollection AddSecurityContextCallback<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCallback>(this IServiceCollection services)
    where TCallback : class, ISecurityContextCallback {
    services.AddScoped<ISecurityContextCallback, TCallback>();
    return services;
  }
}
