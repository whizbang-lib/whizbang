using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Whizbang.Core.Messaging;
using Whizbang.Core.Pipeline;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// Extension methods for registering system event services with dependency injection.
/// </summary>
/// <docs>core-concepts/system-events#registration</docs>
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
  /// - <see cref="ISystemEventEmitter"/> for emitting system events
  /// - <see cref="ITransportPublishFilter"/> for transport filtering
  /// </para>
  /// <para>
  /// To enable event or command auditing, configure the options:
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
  /// // Centralized monitoring with broadcast
  /// services.AddSystemEvents(options => {
  ///   options.EnableAll();
  ///   options.Broadcast();
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

    // Register core services
    services.TryAddSingleton<ITransportPublishFilter, SystemEventTransportFilter>();

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

    // Note: The ISystemEventEmitter requires an IEventStore for the system stream
    // This is typically provided by the storage provider (EF Core, Dapper, etc.)
    // The AuditingEventStoreDecorator wraps the user's event store

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
  /// with the <see cref="AuditingEventStoreDecorator"/>. The decorator emits
  /// <see cref="EventAudited"/> system events when events are appended.
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

    // Re-register the original as the inner store with a specific key
    // This uses a factory to resolve the original implementation
    if (descriptor.ImplementationInstance != null) {
      services.AddSingleton(new InnerEventStoreHolder(descriptor.ImplementationInstance));
    } else if (descriptor.ImplementationFactory != null) {
      services.AddSingleton(sp => new InnerEventStoreHolder(
          descriptor.ImplementationFactory(sp)));
    } else if (descriptor.ImplementationType != null) {
      services.AddSingleton(sp => new InnerEventStoreHolder(
          ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType)));
    }

    // Register the decorator
    services.AddSingleton<IEventStore>(sp => {
      var holder = sp.GetRequiredService<InnerEventStoreHolder>();
      var emitter = sp.GetRequiredService<ISystemEventEmitter>();
      return new AuditingEventStoreDecorator((IEventStore)holder.Instance, emitter);
    });

    return services;
  }

  /// <summary>
  /// Holder for the inner event store instance to enable decoration.
  /// </summary>
  private sealed class InnerEventStoreHolder {
    public object Instance { get; }

    public InnerEventStoreHolder(object instance) {
      Instance = instance;
    }
  }
}
