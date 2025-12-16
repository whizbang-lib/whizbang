using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Configuration;

/// <summary>
/// EF Core configuration extensions for Whizbang infrastructure entities.
/// Uses C# 14 extension members syntax.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs</tests>
public static class WhizbangModelBuilderExtensions {

  extension(ModelBuilder modelBuilder) {

    /// <summary>
    /// Configures Whizbang infrastructure entities (Inbox, Outbox, EventStore, ServiceInstance).
    /// Call this from your DbContext.OnModelCreating() before adding perspective configurations.
    /// </summary>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs:ConfigureWhizbangInfrastructure_ConfiguresInboxEntityAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs:ConfigureWhizbangInfrastructure_ConfiguresOutboxEntityAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs:ConfigureWhizbangInfrastructure_ConfiguresEventStoreEntityAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs:ConfigureWhizbangInfrastructure_ConfiguresServiceInstanceEntityAsync</tests>
    /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangModelBuilderExtensionsTests.cs:ConfigureWhizbangInfrastructure_ConfiguresMessageDeduplicationEntityAsync</tests>
    public ModelBuilder ConfigureWhizbangInfrastructure() {
      ConfigureInbox(modelBuilder);
      ConfigureOutbox(modelBuilder);
      ConfigureEventStore(modelBuilder);
      ConfigureServiceInstance(modelBuilder);
      ConfigureMessageDeduplication(modelBuilder);
      return modelBuilder;
    }
  }

  private static void ConfigureInbox(ModelBuilder modelBuilder) {
    modelBuilder.Entity<InboxRecord>(entity => {
      entity.ToTable("wh_inbox");
      entity.HasKey(e => e.MessageId);

      entity.Property(e => e.MessageId).HasColumnName("message_id").IsRequired();
      entity.Property(e => e.HandlerName).HasColumnName("handler_name").IsRequired();
      entity.Property(e => e.MessageType).HasColumnName("event_type").IsRequired();
      entity.Property(e => e.MessageData).HasColumnName("event_data").IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Metadata).HasColumnName("metadata").IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb");
      entity.Property(e => e.StatusFlags).HasColumnName("status").IsRequired();
      entity.Property(e => e.Attempts).HasColumnName("attempts");
      entity.Property(e => e.Error).HasColumnName("error");
      entity.Property(e => e.ReceivedAt).HasColumnName("received_at").IsRequired();
      entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
      entity.Property(e => e.InstanceId).HasColumnName("instance_id");
      entity.Property(e => e.LeaseExpiry).HasColumnName("lease_expiry");
      entity.Property(e => e.StreamId).HasColumnName("stream_id");
      entity.Property(e => e.PartitionNumber).HasColumnName("partition_number");

      entity.HasIndex(e => e.StatusFlags);
      entity.HasIndex(e => e.ReceivedAt);
    });
  }

  private static void ConfigureOutbox(ModelBuilder modelBuilder) {
    modelBuilder.Entity<OutboxRecord>(entity => {
      entity.ToTable("wh_outbox");
      entity.HasKey(e => e.MessageId);

      entity.Property(e => e.MessageId).HasColumnName("message_id").IsRequired();
      entity.Property(e => e.Destination).HasColumnName("destination").IsRequired();
      entity.Property(e => e.MessageType).HasColumnName("event_type").IsRequired();
      entity.Property(e => e.MessageData).HasColumnName("event_data").IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Metadata).HasColumnName("metadata").IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb");
      entity.Property(e => e.StatusFlags).HasColumnName("status").IsRequired();
      entity.Property(e => e.Attempts).HasColumnName("attempts");
      entity.Property(e => e.Error).HasColumnName("error");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
      entity.Property(e => e.PublishedAt).HasColumnName("published_at");
      entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
      entity.Property(e => e.InstanceId).HasColumnName("instance_id");
      entity.Property(e => e.LeaseExpiry).HasColumnName("lease_expiry");
      entity.Property(e => e.StreamId).HasColumnName("stream_id");
      entity.Property(e => e.PartitionNumber).HasColumnName("partition_number");

      entity.HasIndex(e => e.MessageId);
      entity.HasIndex(e => e.StatusFlags);
      entity.HasIndex(e => e.CreatedAt);
    });
  }

  private static void ConfigureEventStore(ModelBuilder modelBuilder) {
    modelBuilder.Entity<EventStoreRecord>(entity => {
      entity.ToTable("wh_event_store");
      entity.HasKey(e => e.Id);

      entity.Property(e => e.Id).HasColumnName("event_id");
      entity.Property(e => e.StreamId).HasColumnName("stream_id").IsRequired();
      entity.Property(e => e.AggregateId).HasColumnName("aggregate_id").IsRequired();
      entity.Property(e => e.AggregateType).HasColumnName("aggregate_type").IsRequired();
      entity.Property(e => e.Sequence).HasColumnName("sequence_number").IsRequired();
      entity.Property(e => e.Version).HasColumnName("version").IsRequired();
      entity.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
      entity.Property(e => e.EventData).HasColumnName("event_data").IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Metadata).HasColumnName("metadata").IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

      entity.HasIndex(e => new { e.StreamId, e.Version }).IsUnique();
      entity.HasIndex(e => new { e.AggregateId, e.Version }).IsUnique();
      entity.HasIndex(e => new { e.StreamId, e.Sequence }).IsUnique();  // Required for ON CONFLICT in process_work_batch
      entity.HasIndex(e => e.StreamId);
      entity.HasIndex(e => e.CreatedAt);
    });
  }

  private static void ConfigureServiceInstance(ModelBuilder modelBuilder) {
    modelBuilder.Entity<ServiceInstanceRecord>(entity => {
      entity.ToTable("wh_service_instances");
      entity.HasKey(e => e.InstanceId);

      entity.Property(e => e.InstanceId).HasColumnName("instance_id").IsRequired();
      entity.Property(e => e.ServiceName).HasColumnName("service_name").IsRequired().HasMaxLength(200);
      entity.Property(e => e.HostName).HasColumnName("host_name").IsRequired().HasMaxLength(200);
      entity.Property(e => e.ProcessId).HasColumnName("process_id").IsRequired();
      entity.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
      entity.Property(e => e.LastHeartbeatAt).HasColumnName("last_heartbeat_at").IsRequired();
      entity.Property(e => e.Metadata).HasColumnName("metadata").HasColumnType("jsonb");

      entity.HasIndex(e => new { e.ServiceName, e.LastHeartbeatAt });
      entity.HasIndex(e => e.LastHeartbeatAt);
    });
  }


  private static void ConfigureMessageDeduplication(ModelBuilder modelBuilder) {
    modelBuilder.Entity<MessageDeduplicationRecord>(entity => {
      entity.ToTable("wh_message_deduplication");
      entity.HasKey(e => e.MessageId);

      entity.Property(e => e.MessageId).HasColumnName("message_id").IsRequired();
      entity.Property(e => e.FirstSeenAt).HasColumnName("first_seen_at").IsRequired();

      entity.HasIndex(e => e.FirstSeenAt).HasDatabaseName("idx_message_dedup_first_seen");
    });
  }
}
