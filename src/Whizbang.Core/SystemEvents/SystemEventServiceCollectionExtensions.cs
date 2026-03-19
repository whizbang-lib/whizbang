using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Pipeline;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Extension methods for registering system event services with dependency injection.
/// </summary>
/// <docs>fundamentals/events/system-events#registration</docs>
public static class SystemEventServiceCollectionExtensions {
  /// <summary>
  /// Adds system event infrastructure services.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">Optional configuration action for system event options.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// This method registers:
  /// - <see cref="ITransportPublishFilter"/> for transport filtering
  /// - <see cref="AuditingEventStoreDecorator"/> when event audit is enabled
  /// </para>
  /// <para>
  /// The decorator uses <see cref="IDeferredOutboxChannel"/> (singleton) to queue
  /// audit events, avoiding circular DI dependencies.
  /// </para>
  /// <code>
  /// services.AddSystemEvents(options => {
  ///   options.EnableAudit(); // Enable both event and command auditing
  /// });
  /// </code>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Basic setup with audit enabled
  /// services.AddSystemEvents(options => options.EnableAudit());
  ///
  /// // OptIn mode: only audit explicitly marked events
  /// services.AddSystemEvents(options => {
  ///   options.EnableEventAudit();
  ///   options.AuditMode = AuditMode.OptIn;
  /// });
  /// </code>
  /// </example>
  public static IServiceCollection AddSystemEvents(
      this IServiceCollection services,
      Action<SystemEventOptions>? configure = null) {
    // Configure options
    var options = new SystemEventOptions();
    configure?.Invoke(options);
    services.TryAddSingleton(Options.Create(options));

    // Wire up custom humanizers if provided
    if (options.EventNameHumanizer != null) {
      Audit.AuditEventProjection.CustomHumanizer = options.EventNameHumanizer;
    }
    if (options.EventDescriptionHumanizer != null) {
      Audit.AuditEventProjection.CustomDescriptionHumanizer = options.EventDescriptionHumanizer;
    }

    // Register core services
    services.TryAddSingleton<ITransportPublishFilter, SystemEventTransportFilter>();

    // Decorate the IEventStore with auditing if event audit is enabled
    if (options.EventAuditEnabled) {
      var eventStoreDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IEventStore));
      if (eventStoreDescriptor != null) {
        var captured = eventStoreDescriptor;
        services.Remove(captured);

        // Re-register IEventStore wrapped with AuditingEventStoreDecorator
        // No circular dependency: decorator depends on IDeferredOutboxChannel (singleton)
        // and IOptions<SystemEventOptions> (singleton), not IEventStore or ISystemEventEmitter
        services.Add(new ServiceDescriptor(
            typeof(IEventStore),
            sp => {
              IEventStore inner;
              if (captured.ImplementationFactory != null) {
                inner = (IEventStore)captured.ImplementationFactory(sp);
              } else if (captured.ImplementationType != null) {
                inner = (IEventStore)ActivatorUtilities.CreateInstance(sp, captured.ImplementationType);
              } else {
                inner = (IEventStore)captured.ImplementationInstance!;
              }
              var channel = sp.GetRequiredService<IDeferredOutboxChannel>();
              var opts = sp.GetRequiredService<IOptions<SystemEventOptions>>();
              return new AuditingEventStoreDecorator(inner, channel, opts);
            },
            captured.Lifetime));
      }
    }

    return services;
  }

  /// <summary>
  /// Adds system event auditing with automatic event store decoration.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="configure">Optional configuration action for system event options.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// This method registers all system event services plus:
  /// - Wraps <see cref="IEventStore"/> with <see cref="AuditingEventStoreDecorator"/>
  ///   for automatic event auditing
  /// - Registers <see cref="CommandAuditPipelineBehavior{TCommand,TResponse}"/>
  ///   for command auditing
  /// </para>
  /// <para>
  /// Call this method AFTER registering your IEventStore implementation.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// services
  ///   .AddWhizbang()
  ///   .WithEFCore&lt;MyDbContext&gt;()
  ///   .WithDriver.Postgres;
  ///
  /// // Add auditing AFTER storage is configured
  /// services.AddSystemEventAuditing(options => {
  ///   options.EnableEventAudit();
  ///   options.EnableCommandAudit();
  /// });
  /// </code>
  /// </example>
  public static IServiceCollection AddSystemEventAuditing(
      this IServiceCollection services,
      Action<SystemEventOptions>? configure = null) {
    // Add base system event services
    services.AddSystemEvents(configure);

    // Register pipeline behavior for command auditing
    // This will be applied to all commands flowing through the pipeline
    services.TryAddSingleton(
        typeof(IPipelineBehavior<,>),
        typeof(CommandAuditPipelineBehavior<,>));

    return services;
  }

  /// <summary>
  /// Decorates an existing <see cref="IEventStore"/> registration with auditing.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// This method uses the decorator pattern to wrap an existing IEventStore
  /// with the <see cref="AuditingEventStoreDecorator"/>. The decorator queues
  /// <see cref="EventAudited"/> to the deferred outbox channel.
  /// </para>
  /// <para>
  /// Call this method AFTER registering your IEventStore implementation
  /// and AFTER calling <see cref="AddSystemEvents"/> or <see cref="AddSystemEventAuditing"/>.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// services.AddSingleton&lt;IEventStore, PostgresEventStore&gt;();
  /// services.AddSystemEvents(options => options.EnableEventAudit());
  /// services.DecorateEventStoreWithAuditing();
  /// </code>
  /// </example>
  public static IServiceCollection DecorateEventStoreWithAuditing(
      this IServiceCollection services) {
    // Find existing IEventStore registration
    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IEventStore));
    if (descriptor == null) {
      throw new InvalidOperationException(
          "No IEventStore registration found. Register your IEventStore implementation " +
          "before calling DecorateEventStoreWithAuditing().");
    }

    // Remove existing registration
    services.Remove(descriptor);

    // Re-register with the decorator wrapping the original
    services.Add(new ServiceDescriptor(
        typeof(IEventStore),
        sp => {
          IEventStore inner;
          if (descriptor.ImplementationFactory != null) {
            inner = (IEventStore)descriptor.ImplementationFactory(sp);
          } else if (descriptor.ImplementationType != null) {
            inner = (IEventStore)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
          } else {
            inner = (IEventStore)descriptor.ImplementationInstance!;
          }
          var channel = sp.GetRequiredService<IDeferredOutboxChannel>();
          var opts = sp.GetRequiredService<IOptions<SystemEventOptions>>();
          return new AuditingEventStoreDecorator(inner, channel, opts);
        },
        descriptor.Lifetime));

    return services;
  }
}
