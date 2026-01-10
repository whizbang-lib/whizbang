// Template snippets for EF Core entity configuration generation.
// These are valid C# method bodies containing #region blocks that get extracted
// and used as templates during code generation.

using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Data.Schema;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Contains template snippets for EF Core entity configuration generation.
/// Each #region contains a code snippet that gets extracted and has placeholders replaced.
/// </summary>
public class EFCoreSnippets {

  /// <summary>
  /// Configuration for a PerspectiveRow&lt;TModel&gt; entity.
  /// Placeholders: __MODEL_TYPE__, __TABLE_NAME__, __SCHEMA__
  /// </summary>
  public void PerspectiveEntityConfiguration(ModelBuilder modelBuilder) {
    #region PERSPECTIVE_ENTITY_CONFIG_SNIPPET
    // PerspectiveRow<__MODEL_TYPE__>
    // Schema is set via HasDefaultSchema() - do NOT pass schema parameter to ToTable()
    modelBuilder.Entity<PerspectiveRow<__MODEL_TYPE__>>(entity => {
      entity.ToTable("__TABLE_NAME__");
      entity.HasKey(e => e.Id);

      // Primary key
      entity.Property(e => e.Id).HasColumnName("id");

      // JSONB columns (PostgreSQL with Npgsql)
      // Property().HasColumnType("jsonb") enables POCO JSON mapping for custom types
      // Requires ConfigureJsonOptions() THEN EnableDynamicJson() on NpgsqlDataSourceBuilder (order matters!)
      // JSON serialization uses source-generated converters (WhizbangJsonContext)
      entity.Property(e => e.Data)
        .HasColumnName("data")
        .HasColumnType("jsonb")
        .IsRequired();

      entity.Property(e => e.Metadata)
        .HasColumnName("metadata")
        .HasColumnType("jsonb")
        .IsRequired();

      entity.Property(e => e.Scope)
        .HasColumnName("scope")
        .HasColumnType("jsonb")
        .IsRequired();

      // System fields
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
      entity.Property(e => e.Version).HasColumnName("version").IsRequired();

      // Indexes
      entity.HasIndex(e => e.CreatedAt);
    });
    #endregion
  }




  /// <summary>
  /// AOT-compatible registration for core infrastructure (Inbox, Outbox, EventStore, WorkCoordinator).
  /// Placeholders: __DBCONTEXT_FQN__
  /// </summary>
  public void RegisterInfrastructure(IServiceCollection services) {
    #region REGISTER_INFRASTRUCTURE_SNIPPET
    // Register core infrastructure (EventStore, WorkCoordinator, WorkCoordinatorStrategy) - AOT compatible
    // JsonSerializerOptions are created from JsonContextRegistry (auto-discovers all registered contexts)
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

    // Register WorkCoordinatorOptions (if not already registered)
    // This is defensive - users can override by registering their own options before calling .WithDriver.Postgres
    if (!services.Any(sd => sd.ServiceType == typeof(Whizbang.Core.Messaging.WorkCoordinatorOptions))) {
      services.AddSingleton(new Whizbang.Core.Messaging.WorkCoordinatorOptions());
    }

    // Register shared work channel writer as singleton
    // Shared between ScopedWorkCoordinatorStrategy (writer) and WorkCoordinatorPublisherWorker (reader)
    // Enables immediate processing of work returned from process_work_batch
    if (!services.Any(sd => sd.ServiceType == typeof(Whizbang.Core.Messaging.IWorkChannelWriter))) {
      services.AddSingleton<Whizbang.Core.Messaging.IWorkChannelWriter, Whizbang.Core.Messaging.WorkChannelWriter>();
    }

    // Register scoped work coordinator strategy for dispatcher outbox routing
    // ScopedWorkCoordinatorStrategy batches operations within a scope (e.g., HTTP request)
    // This enables the dispatcher to route messages to outbox when no local receptor exists
    services.AddScoped<Whizbang.Core.Messaging.IWorkCoordinatorStrategy>(sp => {
      var coordinator = sp.GetRequiredService<Whizbang.Core.Messaging.IWorkCoordinator>();
      var instanceProvider = sp.GetRequiredService<Whizbang.Core.Observability.IServiceInstanceProvider>();
      var channelWriter = sp.GetRequiredService<Whizbang.Core.Messaging.IWorkChannelWriter>();
      var options = sp.GetRequiredService<Whizbang.Core.Messaging.WorkCoordinatorOptions>();
      var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<Whizbang.Core.Messaging.ScopedWorkCoordinatorStrategy>>();
      var lifecycleInvoker = sp.GetService<Whizbang.Core.Messaging.ILifecycleInvoker>();
      var lifecycleMessageDeserializer = sp.GetService<Whizbang.Core.Messaging.ILifecycleMessageDeserializer>();
      return new Whizbang.Core.Messaging.ScopedWorkCoordinatorStrategy(
        coordinator,
        instanceProvider,
        channelWriter,
        options,
        logger,
        lifecycleInvoker,
        lifecycleMessageDeserializer
      );
    });
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

    // Register ILensQuery<__MODEL_TYPE__> - scoped (for web APIs, receptors)
    services.AddScoped<Whizbang.Core.Lenses.ILensQuery<__MODEL_TYPE__>>(sp => {
      var context = sp.GetRequiredService<__DBCONTEXT_FQN__>();
      return new Whizbang.Data.EFCore.Postgres.EFCorePostgresLensQuery<__MODEL_TYPE__>(context, "__TABLE_NAME__");
    });

    // Register IScopedLensQuery<__MODEL_TYPE__> - singleton (auto-scoping for background services)
    services.AddSingleton<Whizbang.Core.Lenses.IScopedLensQuery<__MODEL_TYPE__>>(sp => {
      var scopeFactory = sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
      return new Whizbang.Core.Lenses.ScopedLensQuery<__MODEL_TYPE__>(scopeFactory);
    });

    // Register ILensQueryFactory<__MODEL_TYPE__> - singleton (manual scope control for batch operations)
    services.AddSingleton<Whizbang.Core.Lenses.ILensQueryFactory<__MODEL_TYPE__>>(sp => {
      var scopeFactory = sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
      return new Whizbang.Core.Lenses.LensQueryFactory<__MODEL_TYPE__>(scopeFactory);
    });
    #endregion
  }
}
