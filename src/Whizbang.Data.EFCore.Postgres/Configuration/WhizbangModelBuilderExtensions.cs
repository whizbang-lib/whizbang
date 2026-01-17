using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Configuration;

/// <summary>
/// EF Core configuration extensions for Whizbang infrastructure entities.
/// Uses C# 14 extension members syntax.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs</tests>
public static class WhizbangModelBuilderExtensions {
  private const string COLUMN_TYPE_JSONB = "jsonb";
  private const string COLUMN_NAME_METADATA = "metadata";
  private const string COLUMN_NAME_STREAM_ID = "stream_id";

  extension(ModelBuilder modelBuilder) {

    /// <summary>
    /// Configures Whizbang infrastructure entities (Inbox, Outbox, EventStore, ServiceInstance).
    /// Call this from your DbContext.OnModelCreating() before adding perspective configurations.
    /// Schema is set via HasDefaultSchema() in generated code.
    /// </summary>
    /// <param name="schema">Unused parameter for backward compatibility with generated code. Schema is set via HasDefaultSchema().</param>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs:ConfigureWhizbangInfrastructure_ConfiguresInboxEntityAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs:ConfigureWhizbangInfrastructure_ConfiguresOutboxEntityAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs:ConfigureWhizbangInfrastructure_ConfiguresEventStoreEntityAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs:ConfigureWhizbangInfrastructure_ConfiguresServiceInstanceEntityAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs:ConfigureWhizbangInfrastructure_ConfiguresMessageDeduplicationEntityAsync</tests>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Parameter retained for backward compatibility with generated code")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "S1172:Unused method parameters should be removed", Justification = "Parameter retained for backward compatibility with generated code")]
    public ModelBuilder ConfigureWhizbangInfrastructure(string? schema = null) {
      _configureInbox(modelBuilder);
      _configureOutbox(modelBuilder);
      _configureEventStore(modelBuilder);
      _configureServiceInstance(modelBuilder);
      _configureMessageDeduplication(modelBuilder);
      _configureMessageAssociations(modelBuilder);
      _configurePerspectiveCheckpoints(modelBuilder);
      return modelBuilder;
    }
  }

  private static void _configureInbox(ModelBuilder modelBuilder) {
    modelBuilder.Entity<InboxRecord>(entity => {
      // Schema is set via HasDefaultSchema() in generated code - do NOT pass schema here
      entity.ToTable("wh_inbox");
      entity.HasKey(e => e.MessageId);

      entity.Property(e => e.MessageId).HasColumnName("message_id").IsRequired();
      entity.Property(e => e.HandlerName).HasColumnName("handler_name").IsRequired();
      entity.Property(e => e.MessageType).HasColumnName("event_type").IsRequired();
      entity.Property(e => e.MessageData).HasColumnName("event_data").HasColumnType(COLUMN_TYPE_JSONB).IsRequired();
      entity.Property(e => e.Metadata).HasColumnName(COLUMN_NAME_METADATA).HasColumnType(COLUMN_TYPE_JSONB).IsRequired();
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType(COLUMN_TYPE_JSONB);
      entity.Property(e => e.StatusFlags).HasColumnName("status").IsRequired();
      entity.Property(e => e.Attempts).HasColumnName("attempts");
      entity.Property(e => e.Error).HasColumnName("error");
      entity.Property(e => e.ReceivedAt).HasColumnName("received_at").IsRequired();
      entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
      entity.Property(e => e.InstanceId).HasColumnName("instance_id");
      entity.Property(e => e.LeaseExpiry).HasColumnName("lease_expiry");
      entity.Property(e => e.StreamId).HasColumnName(COLUMN_NAME_STREAM_ID);
      entity.Property(e => e.PartitionNumber).HasColumnName("partition_number");
      entity.Property(e => e.FailureReason).HasColumnName("failure_reason").IsRequired();
      entity.Property(e => e.ScheduledFor).HasColumnName("scheduled_for");

      entity.HasIndex(e => e.StatusFlags);
      entity.HasIndex(e => e.ReceivedAt);
    });
  }

  private static void _configureOutbox(ModelBuilder modelBuilder) {
    modelBuilder.Entity<OutboxRecord>(entity => {
      // Schema is set via HasDefaultSchema() in generated code - do NOT pass schema here
      entity.ToTable("wh_outbox");
      entity.HasKey(e => e.MessageId);

      entity.Property(e => e.MessageId).HasColumnName("message_id").IsRequired();
      entity.Property(e => e.Destination).HasColumnName("destination").IsRequired();
      entity.Property(e => e.MessageType).HasColumnName("event_type").IsRequired();
      entity.Property(e => e.MessageData).HasColumnName("event_data").HasColumnType(COLUMN_TYPE_JSONB).IsRequired();
      entity.Property(e => e.Metadata).HasColumnName(COLUMN_NAME_METADATA).HasColumnType(COLUMN_TYPE_JSONB).IsRequired();
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType(COLUMN_TYPE_JSONB);
      entity.Property(e => e.StatusFlags).HasColumnName("status").IsRequired();
      entity.Property(e => e.Attempts).HasColumnName("attempts");
      entity.Property(e => e.Error).HasColumnName("error");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
      entity.Property(e => e.PublishedAt).HasColumnName("published_at");
      entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
      entity.Property(e => e.InstanceId).HasColumnName("instance_id");
      entity.Property(e => e.LeaseExpiry).HasColumnName("lease_expiry");
      entity.Property(e => e.StreamId).HasColumnName(COLUMN_NAME_STREAM_ID);
      entity.Property(e => e.PartitionNumber).HasColumnName("partition_number");
      entity.Property(e => e.FailureReason).HasColumnName("failure_reason").IsRequired();
      entity.Property(e => e.ScheduledFor).HasColumnName("scheduled_for");

      entity.HasIndex(e => e.MessageId);
      entity.HasIndex(e => e.StatusFlags);
      entity.HasIndex(e => e.CreatedAt);
    });
  }

  private static void _configureEventStore(ModelBuilder modelBuilder) {
    modelBuilder.Entity<EventStoreRecord>(entity => {
      // Schema is set via HasDefaultSchema() in generated code - do NOT pass schema here
      entity.ToTable("wh_event_store");
      entity.HasKey(e => e.Id);

      entity.Property(e => e.Id).HasColumnName("event_id");
      entity.Property(e => e.StreamId).HasColumnName(COLUMN_NAME_STREAM_ID).IsRequired();
      entity.Property(e => e.AggregateId).HasColumnName("aggregate_id").IsRequired();
      entity.Property(e => e.AggregateType).HasColumnName("aggregate_type").IsRequired();
      entity.Property(e => e.Version).HasColumnName("version").IsRequired();
      entity.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
      entity.Property(e => e.EventData).HasColumnName("event_data").HasColumnType(COLUMN_TYPE_JSONB).IsRequired();
      entity.Property(e => e.Metadata).HasColumnName(COLUMN_NAME_METADATA).HasColumnType(COLUMN_TYPE_JSONB).IsRequired();
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType(COLUMN_TYPE_JSONB);
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

      entity.HasIndex(e => new { e.StreamId, e.Version }).IsUnique();  // Required for ON CONFLICT in process_work_batch
      entity.HasIndex(e => new { e.AggregateId, e.Version }).IsUnique();
      entity.HasIndex(e => e.StreamId);
      entity.HasIndex(e => e.CreatedAt);
    });
  }

  private static void _configureServiceInstance(ModelBuilder modelBuilder) {
    modelBuilder.Entity<ServiceInstanceRecord>(entity => {
      // Schema is set via HasDefaultSchema() in generated code - do NOT pass schema here
      entity.ToTable("wh_service_instances");
      entity.HasKey(e => e.InstanceId);

      entity.Property(e => e.InstanceId).HasColumnName("instance_id").IsRequired();
      entity.Property(e => e.ServiceName).HasColumnName("service_name").IsRequired().HasMaxLength(200);
      entity.Property(e => e.HostName).HasColumnName("host_name").IsRequired().HasMaxLength(200);
      entity.Property(e => e.ProcessId).HasColumnName("process_id").IsRequired();
      entity.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
      entity.Property(e => e.LastHeartbeatAt).HasColumnName("last_heartbeat_at").IsRequired();
      entity.Property(e => e.Metadata).HasColumnName(COLUMN_NAME_METADATA).HasColumnType(COLUMN_TYPE_JSONB);

      entity.HasIndex(e => new { e.ServiceName, e.LastHeartbeatAt });
      entity.HasIndex(e => e.LastHeartbeatAt);
    });
  }


  private static void _configureMessageDeduplication(ModelBuilder modelBuilder) {
    modelBuilder.Entity<MessageDeduplicationRecord>(entity => {
      // Schema is set via HasDefaultSchema() in generated code - do NOT pass schema here
      entity.ToTable("wh_message_deduplication");
      entity.HasKey(e => e.MessageId);

      entity.Property(e => e.MessageId).HasColumnName("message_id").IsRequired();
      entity.Property(e => e.FirstSeenAt).HasColumnName("first_seen_at").IsRequired();

      entity.HasIndex(e => e.FirstSeenAt).HasDatabaseName("idx_message_dedup_first_seen");
    });
  }

  private static void _configureMessageAssociations(ModelBuilder modelBuilder) {
    modelBuilder.Entity<MessageAssociationRecord>(entity => {
      // Schema is set via HasDefaultSchema() in generated code - do NOT pass schema here
      entity.ToTable("wh_message_associations");
      entity.HasKey(e => e.Id);

      entity.Property(e => e.Id).HasColumnName("id").IsRequired();
      entity.Property(e => e.MessageType).HasColumnName("message_type").IsRequired().HasMaxLength(500);
      entity.Property(e => e.AssociationType).HasColumnName("association_type").IsRequired().HasMaxLength(50);
      entity.Property(e => e.TargetName).HasColumnName("target_name").IsRequired().HasMaxLength(500);
      entity.Property(e => e.ServiceName).HasColumnName("service_name").IsRequired().HasMaxLength(500);
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

      // Indexes
      entity.HasIndex(e => e.MessageType).HasDatabaseName("idx_message_associations_message_type");
      entity.HasIndex(e => e.AssociationType).HasDatabaseName("idx_message_associations_association_type");
      entity.HasIndex(e => e.TargetName).HasDatabaseName("idx_message_associations_target_name");
      entity.HasIndex(e => e.ServiceName).HasDatabaseName("idx_message_associations_service_name");
      entity.HasIndex(e => new { e.AssociationType, e.TargetName, e.ServiceName }).HasDatabaseName("idx_message_associations_target_lookup");
    });
  }

  private static void _configurePerspectiveCheckpoints(ModelBuilder modelBuilder) {
    modelBuilder.Entity<PerspectiveCheckpointRecord>(entity => {
      // Schema is set via HasDefaultSchema() in generated code - do NOT pass schema here
      entity.ToTable("wh_perspective_checkpoints");
      entity.HasKey(e => new { e.StreamId, e.PerspectiveName });

      entity.Property(e => e.StreamId).HasColumnName(COLUMN_NAME_STREAM_ID).IsRequired();
      entity.Property(e => e.PerspectiveName).HasColumnName("perspective_name").IsRequired().HasMaxLength(500);
      entity.Property(e => e.LastEventId).HasColumnName("last_event_id").IsRequired();
      entity.Property(e => e.Status).HasColumnName("status").IsRequired();
      entity.Property(e => e.ProcessedAt).HasColumnName("processed_at").IsRequired();
      entity.Property(e => e.Error).HasColumnName("error");

      // Indexes
      entity.HasIndex(e => e.Status).HasDatabaseName("idx_perspective_checkpoints_status");
      entity.HasIndex(e => e.ProcessedAt).HasDatabaseName("idx_perspective_checkpoints_processed_at");
    });
  }
}
