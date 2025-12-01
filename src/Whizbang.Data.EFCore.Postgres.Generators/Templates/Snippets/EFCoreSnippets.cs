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
      entity.ToTable("inbox");
      entity.HasKey(e => e.MessageId);

      entity.Property(e => e.MessageId).IsRequired();
      entity.Property(e => e.HandlerName).IsRequired();
      entity.Property(e => e.EventType).IsRequired();
      entity.Property(e => e.EventData).IsRequired().HasColumnType("jsonb");
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
      entity.ToTable("outbox");
      entity.HasKey(e => e.Id);

      entity.Property(e => e.MessageId).IsRequired();
      entity.Property(e => e.Destination).IsRequired();
      entity.Property(e => e.EventType).IsRequired();
      entity.Property(e => e.EventData).IsRequired().HasColumnType("jsonb");
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
      entity.ToTable("event_store");
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
}
