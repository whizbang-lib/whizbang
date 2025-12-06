// Template snippets for EF Core entity configuration generation.
// These are valid C# method bodies containing #region blocks that get extracted
// and used as templates during code generation.

using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.Entities;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Contains template snippets for EF Core entity configuration generation.
/// Each #region contains a code snippet that gets extracted and has placeholders replaced.
/// </summary>
public class EFCoreSnippets {

  /// <summary>
  /// Configuration for a PerspectiveRow&lt;TModel&gt; entity.
  /// Placeholders: __MODEL_TYPE__, __TABLE_NAME__
  /// </summary>
  public void PerspectiveEntityConfiguration(ModelBuilder modelBuilder) {
    #region PERSPECTIVE_ENTITY_CONFIG_SNIPPET
    // PerspectiveRow<__MODEL_TYPE__>
    modelBuilder.Entity<PerspectiveRow<__MODEL_TYPE__>>(entity => {
      entity.ToTable("__TABLE_NAME__");
      entity.HasKey(e => e.Id);

      // JSONB columns (Npgsql JSONB mapping)
      // Using HasColumnType("jsonb") tells Npgsql to store as JSONB
      // Npgsql automatically handles JSON serialization for complex types
      entity.Property(e => e.Data)
        .HasColumnType("jsonb")
        .IsRequired();

      entity.Property(e => e.Metadata)
        .HasColumnType("jsonb")
        .IsRequired();

      entity.Property(e => e.Scope)
        .HasColumnType("jsonb")
        .IsRequired();

      // System fields
      entity.Property(e => e.CreatedAt).IsRequired();
      entity.Property(e => e.UpdatedAt).IsRequired();
      entity.Property(e => e.Version).IsRequired();

      // Indexes
      entity.HasIndex(e => e.CreatedAt);
    });
    #endregion
  }

  /// <summary>
  /// Configuration for InboxRecord entity.
  /// No placeholders.
  /// </summary>
  public void InboxEntityConfiguration(ModelBuilder modelBuilder) {
    #region INBOX_ENTITY_CONFIG_SNIPPET
    // InboxRecord - Message deduplication
    modelBuilder.Entity<InboxRecord>(entity => {
      entity.ToTable("wh_inbox");
      entity.HasKey(e => e.MessageId);

      entity.Property(e => e.MessageId).IsRequired();
      entity.Property(e => e.HandlerName).IsRequired();
      entity.Property(e => e.MessageType).IsRequired();
      entity.Property(e => e.MessageData).IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Metadata).IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Scope).HasColumnType("jsonb");
      entity.Property(e => e.Status).IsRequired();
      entity.Property(e => e.ReceivedAt).IsRequired();

      entity.HasIndex(e => e.Status);
      entity.HasIndex(e => e.ReceivedAt);
    });
    #endregion
  }

  /// <summary>
  /// Configuration for OutboxRecord entity.
  /// No placeholders.
  /// </summary>
  public void OutboxEntityConfiguration(ModelBuilder modelBuilder) {
    #region OUTBOX_ENTITY_CONFIG_SNIPPET
    // OutboxRecord - Transactional messaging
    modelBuilder.Entity<OutboxRecord>(entity => {
      entity.ToTable("wh_outbox");
      entity.HasKey(e => e.Id);

      entity.Property(e => e.MessageId).IsRequired();
      entity.Property(e => e.Destination).IsRequired();
      entity.Property(e => e.MessageType).IsRequired();
      entity.Property(e => e.MessageData).IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Metadata).IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Scope).HasColumnType("jsonb");
      entity.Property(e => e.Status).IsRequired();
      entity.Property(e => e.CreatedAt).IsRequired();

      entity.HasIndex(e => e.MessageId);
      entity.HasIndex(e => e.Status);
      entity.HasIndex(e => e.CreatedAt);
    });
    #endregion
  }

  /// <summary>
  /// Configuration for EventStoreRecord entity.
  /// No placeholders.
  /// </summary>
  public void EventStoreEntityConfiguration(ModelBuilder modelBuilder) {
    #region EVENTSTORE_ENTITY_CONFIG_SNIPPET
    // EventStoreRecord - Event sourcing
    modelBuilder.Entity<EventStoreRecord>(entity => {
      entity.ToTable("wh_events");
      entity.HasKey(e => e.Id);

      entity.Property(e => e.StreamId).IsRequired();
      entity.Property(e => e.Sequence).IsRequired();
      entity.Property(e => e.EventType).IsRequired();
      entity.Property(e => e.EventData).IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Metadata).IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Scope).HasColumnType("jsonb");
      entity.Property(e => e.CreatedAt).IsRequired();

      // Unique constraint on (StreamId, Sequence) for optimistic concurrency
      entity.HasIndex(e => new { e.StreamId, e.Sequence }).IsUnique();
      entity.HasIndex(e => e.StreamId);
      entity.HasIndex(e => e.CreatedAt);
    });
    #endregion
  }

  /// <summary>
  /// Configuration for ServiceInstanceRecord entity.
  /// No placeholders.
  /// </summary>
  public void ServiceInstanceEntityConfiguration(ModelBuilder modelBuilder) {
    #region SERVICE_INSTANCE_ENTITY_CONFIG_SNIPPET
    // ServiceInstanceRecord - Service instance tracking
    modelBuilder.Entity<ServiceInstanceRecord>(entity => {
      entity.ToTable("wh_service_instances");
      entity.HasKey(e => e.InstanceId);

      entity.Property(e => e.InstanceId).IsRequired();
      entity.Property(e => e.ServiceName).IsRequired().HasMaxLength(200);
      entity.Property(e => e.HostName).IsRequired().HasMaxLength(200);
      entity.Property(e => e.ProcessId).IsRequired();
      entity.Property(e => e.StartedAt).IsRequired();
      entity.Property(e => e.LastHeartbeatAt).IsRequired();
      entity.Property(e => e.Metadata).HasColumnType("jsonb");

      entity.HasIndex(e => new { e.ServiceName, e.LastHeartbeatAt });
      entity.HasIndex(e => e.LastHeartbeatAt);
    });
    #endregion
  }

  /// <summary>
  /// AOT-compatible registration for core infrastructure (Inbox, Outbox, EventStore, WorkCoordinator).
  /// Placeholders: __DBCONTEXT_FQN__
  /// </summary>
  public void RegisterInfrastructure(IServiceCollection services) {
    #region REGISTER_INFRASTRUCTURE_SNIPPET
    // Register core infrastructure (Inbox, Outbox, EventStore, WorkCoordinator) - AOT compatible
    // JsonSerializerOptions are created from JsonContextRegistry (auto-discovers all registered contexts)
    services.AddScoped<Whizbang.Core.Messaging.IInbox>(sp => {
      var context = sp.GetRequiredService<__DBCONTEXT_FQN__>();
      var jsonOptions = Whizbang.Data.EFCore.Postgres.Serialization.EFCoreJsonContext.CreateCombinedOptions();
      return new Whizbang.Data.EFCore.Postgres.EFCoreInbox<__DBCONTEXT_FQN__>(context, jsonOptions);
    });
    services.AddScoped<Whizbang.Core.Messaging.IOutbox>(sp => {
      var context = sp.GetRequiredService<__DBCONTEXT_FQN__>();
      var jsonOptions = Whizbang.Data.EFCore.Postgres.Serialization.EFCoreJsonContext.CreateCombinedOptions();
      return new Whizbang.Data.EFCore.Postgres.EFCoreOutbox<__DBCONTEXT_FQN__>(context, jsonOptions);
    });
    services.AddScoped<Whizbang.Core.Messaging.IEventStore>(sp => {
      var context = sp.GetRequiredService<__DBCONTEXT_FQN__>();
      var jsonOptions = Whizbang.Data.EFCore.Postgres.Serialization.EFCoreJsonContext.CreateCombinedOptions();
      var perspectiveInvoker = sp.GetService<Whizbang.Core.Perspectives.IPerspectiveInvoker>();
      return new Whizbang.Data.EFCore.Postgres.EFCoreEventStore<__DBCONTEXT_FQN__>(
        context,
        jsonOptions,
        perspectiveInvoker
      );
    });
    services.AddScoped<Whizbang.Core.Messaging.IWorkCoordinator, Whizbang.Data.EFCore.Postgres.EFCoreWorkCoordinator<__DBCONTEXT_FQN__>>();
    #endregion
  }

  /// <summary>
  /// AOT-compatible registration for a perspective model (IPerspectiveStore + ILensQuery).
  /// Placeholders: __MODEL_TYPE__, __DBCONTEXT_FQN__, __TABLE_NAME__
  /// </summary>
  public void RegisterPerspectiveModel(IServiceCollection services, IDbUpsertStrategy upsertStrategy) {
    #region REGISTER_PERSPECTIVE_MODEL_SNIPPET
    // Register IPerspectiveStore<__MODEL_TYPE__> - AOT compatible
    services.AddScoped<Whizbang.Core.Perspectives.IPerspectiveStore<__MODEL_TYPE__>>(sp => {
      var context = sp.GetRequiredService<__DBCONTEXT_FQN__>();
      return new Whizbang.Data.EFCore.Postgres.EFCorePostgresPerspectiveStore<__MODEL_TYPE__>(context, "__TABLE_NAME__", upsertStrategy);
    });

    // Register ILensQuery<__MODEL_TYPE__> - AOT compatible
    services.AddScoped<Whizbang.Core.Lenses.ILensQuery<__MODEL_TYPE__>>(sp => {
      var context = sp.GetRequiredService<__DBCONTEXT_FQN__>();
      return new Whizbang.Data.EFCore.Postgres.EFCorePostgresLensQuery<__MODEL_TYPE__>(context, "__TABLE_NAME__");
    });
    #endregion
  }
}
