using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Functions;

/// <summary>
/// Extension methods for registering Whizbang's custom PostgreSQL functions with EF Core.
/// </summary>
/// <docs>fundamentals/security/security#principal-filtering</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs:DispatchAsync_TenantScoped_AffectsAllRowsInScopeOnlyAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveDispatcherEFCoreIntegrationTests.cs:DispatchAsync_BumpsStoreManagedUpdatedAtAndVersionAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CanonicalTemporalConventionTests.cs</tests>
public static class WhizbangDbContextOptionsExtensions {
  /// <summary>
  /// Adds Whizbang's custom PostgreSQL function translators to the Npgsql provider.
  /// Call this method when configuring Npgsql options to enable optimized principal filtering.
  /// </summary>
  /// <param name="optionsBuilder">The Npgsql DbContext options builder.</param>
  /// <returns>The same options builder for fluent chaining.</returns>
  /// <example>
  /// <code>
  /// protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
  /// {
  ///   optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
  ///   {
  ///     npgsqlOptions.UseWhizbangFunctions();
  ///   });
  /// }
  /// </code>
  /// </example>
  /// <remarks>
  /// This registers the following custom function translators:
  /// <list type="bullet">
  ///   <item><see cref="JsonArrayContainsAnyTranslator"/> - Translates to PostgreSQL's ?| operator</item>
  /// </list>
  /// </remarks>
  public static NpgsqlDbContextOptionsBuilder UseWhizbangFunctions(
      this NpgsqlDbContextOptionsBuilder optionsBuilder) {
    ArgumentNullException.ThrowIfNull(optionsBuilder);

    // Add our custom method call translator plugin to the existing collection
    // This preserves Npgsql's built-in translators while adding our own
    return _update(optionsBuilder, static existing => existing ?? new WhizbangOptionsExtension());
  }

  /// <summary>
  /// Registers a consumer's own method-call translator plugin alongside the framework's.
  /// </summary>
  /// <typeparam name="TPlugin">
  /// The plugin to register. Resolved from the provider's internal container, so it may take
  /// dependencies that container has -- <c>ISqlExpressionFactory</c> being the one a translator
  /// almost always needs.
  /// </typeparam>
  /// <param name="optionsBuilder">The Npgsql DbContext options builder.</param>
  /// <returns>The same options builder for fluent chaining.</returns>
  /// <remarks>
  /// <para>
  /// A translator is how a predicate that calls something Entity Framework does not model stays on
  /// the server. Without a seam the options were to register a provider plugin directly and hope
  /// the framework's own options builder did not overwrite it, or to let the predicate fail
  /// translation -- the first undefined, the second a silent fall back to client evaluation or an
  /// exception at run time.
  /// </para>
  /// <para>
  /// <strong>Composition order is defined: the framework's translators are asked first.</strong>
  /// Entity Framework returns the first translation any plugin offers, so this decides who wins a
  /// method both claim. The framework goes first deliberately -- a consumer plugin that answered
  /// for a method the perspective pipeline depends on would change the meaning of a query the
  /// framework generates, and that is a worse failure than a consumer's own translation being
  /// ignored, because only one of the two is visible to the person who wrote it.
  /// </para>
  /// <para>
  /// Consumer plugins are asked in registration order, and registering the same type twice
  /// registers it once. Calling this without <see cref="UseWhizbangFunctions"/> still brings the
  /// framework's own translators, since they are part of the same extension.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// optionsBuilder.UseNpgsql(connectionString, npgsql => {
  ///   npgsql.UseWhizbangFunctions();
  ///   npgsql.AddWhizbangQueryTranslator&lt;MyTranslatorPlugin&gt;();
  /// });
  /// </code>
  /// </example>
  /// <docs>fundamentals/perspectives/query-translation</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/QueryTranslation/ConsumerTranslatorSeamTests.cs</tests>
  public static NpgsqlDbContextOptionsBuilder AddWhizbangQueryTranslator<
      [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPlugin>(
      this NpgsqlDbContextOptionsBuilder optionsBuilder)
      where TPlugin : class, IMethodCallTranslatorPlugin {
    ArgumentNullException.ThrowIfNull(optionsBuilder);

    // The registrar is built HERE, where TPlugin is statically known, and carried as a delegate.
    // Registering from a stored Type would mean asking the container to construct something the
    // trimmer cannot see a constructor for (IL2072); a delegate closed over the generic argument
    // keeps that constructor reachable. The Type rides along only as the identity used to
    // deduplicate and to key the service provider.
    return _update(optionsBuilder, static existing =>
      (existing ?? new WhizbangOptionsExtension()).WithConsumerPlugin(
        typeof(TPlugin),
        static services => services.AddScoped<IMethodCallTranslatorPlugin, TPlugin>()));
  }

  /// <summary>
  /// Reads the extension already on the builder, hands it to <paramref name="amend"/>, and puts the
  /// result back.
  /// </summary>
  /// <remarks>
  /// Read-amend-write rather than constructing a fresh extension, because AddOrUpdateExtension
  /// REPLACES the one of the same type. Constructing a new one would mean the last of
  /// UseWhizbangFunctions and AddWhizbangQueryTranslator silently discarded whatever the other had
  /// registered, making the result depend on call order.
  /// </remarks>
  private static NpgsqlDbContextOptionsBuilder _update(
      NpgsqlDbContextOptionsBuilder optionsBuilder,
      Func<WhizbangOptionsExtension?, WhizbangOptionsExtension> amend) {
    var coreOptionsBuilder = ((IRelationalDbContextOptionsBuilderInfrastructure)optionsBuilder).OptionsBuilder;
    var existing = coreOptionsBuilder.Options.FindExtension<WhizbangOptionsExtension>();

    ((IDbContextOptionsBuilderInfrastructure)coreOptionsBuilder).AddOrUpdateExtension(amend(existing));

    return optionsBuilder;
  }
}

/// <summary>
/// EF Core options extension that registers Whizbang's custom translators.
/// </summary>
internal sealed class WhizbangOptionsExtension : IDbContextOptionsExtension {
  /// <summary>
  /// One consumer plugin: the type that identifies it, and the registration captured where that
  /// type was still statically known.
  /// </summary>
  internal readonly record struct ConsumerPlugin(Type PluginType, Action<IServiceCollection> Register);

  private readonly IReadOnlyList<ConsumerPlugin> _consumerPlugins;

  public WhizbangOptionsExtension() : this([]) { }

  private WhizbangOptionsExtension(IReadOnlyList<ConsumerPlugin> consumerPlugins) =>
    _consumerPlugins = consumerPlugins;

  /// <summary>The consumer's own translator plugins, in the order they were registered.</summary>
  public IReadOnlyList<ConsumerPlugin> ConsumerPlugins => _consumerPlugins;

  /// <summary>
  /// This extension with <paramref name="plugin"/> added, or itself when it already has it.
  /// </summary>
  /// <remarks>
  /// A new instance rather than a mutation: an options extension is expected to be immutable, and
  /// Entity Framework reads one after the options are built. Registering the same type twice
  /// registers it once, so a call repeated across a shared configuration helper is harmless.
  /// </remarks>
  public WhizbangOptionsExtension WithConsumerPlugin(Type plugin, Action<IServiceCollection> register) {
    ArgumentNullException.ThrowIfNull(plugin);
    ArgumentNullException.ThrowIfNull(register);

    return _consumerPlugins.Any(p => p.PluginType == plugin)
      ? this
      : new WhizbangOptionsExtension([.. _consumerPlugins, new ConsumerPlugin(plugin, register)]);
  }

  public DbContextOptionsExtensionInfo Info => new WhizbangOptionsExtensionInfo(this);

  public void ApplyServices(IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    // Add our translator plugin to the collection (alongside Npgsql's built-in plugins)
    // Must be scoped because it depends on NpgsqlSqlExpressionFactory which is scoped
    services.AddScoped<IMethodCallTranslatorPlugin, WhizbangMethodCallTranslatorPlugin>();

    // The consumer's own, AFTER the framework's. Entity Framework returns the first translation any
    // plugin offers, so this is what decides who wins a method both claim, and the framework
    // winning is the safer way round: a consumer plugin that answered for a method the perspective
    // pipeline depends on would change the meaning of a query the framework generates, which is a
    // worse failure than a consumer's own translation being ignored because only one of the two is
    // visible to the person who wrote it.
    foreach (var plugin in _consumerPlugins) {
      plugin.Register(services);
    }

    // The canonical temporal form rides here rather than in generated configuration, so that what
    // Entity Framework converts is decided by the model it built and not by a generator's partial
    // discovery of it. Every generated perspective context carries this extension.
    services.AddSingleton<IConventionSetPlugin, CanonicalTemporalConventionSetPlugin>();
  }

  public void Validate(IDbContextOptions options) {
    // No validation needed
  }
}

internal sealed class WhizbangOptionsExtensionInfo(WhizbangOptionsExtension extension)
    : DbContextOptionsExtensionInfo(extension) {
  private WhizbangOptionsExtension _extension => (WhizbangOptionsExtension)Extension;

  public override bool IsDatabaseProvider => false;

  public override string LogFragment =>
    _extension.ConsumerPlugins.Count == 0
      ? "WhizbangFunctions "
      : $"WhizbangFunctions(+{_extension.ConsumerPlugins.Count} translator plugin(s)) ";

  /// <summary>
  /// Hashed over the registered plugins, not constant.
  /// </summary>
  /// <remarks>
  /// This and <see cref="ShouldUseSameServiceProvider"/> decide whether two contexts share Entity
  /// Framework's internal service provider. They were a constant zero and an unconditional true,
  /// which was correct while the extension carried nothing: every instance really was
  /// interchangeable. Once it carries the consumer's plugin list it is not -- two contexts
  /// registering different translators would have shared one provider, and whichever built it first
  /// would have decided the translators BOTH used. The plugin list is part of the identity now.
  /// </remarks>
  public override int GetServiceProviderHashCode() {
    var hash = new HashCode();
    foreach (var plugin in _extension.ConsumerPlugins) {
      hash.Add(plugin.PluginType);
    }
    return hash.ToHashCode();
  }

  public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
    other is WhizbangOptionsExtensionInfo info
    && _extension.ConsumerPlugins.Select(static p => p.PluginType)
         .SequenceEqual(info._extension.ConsumerPlugins.Select(static p => p.PluginType));

  public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) {
    ArgumentNullException.ThrowIfNull(debugInfo);

    debugInfo["Whizbang:Functions"] = "enabled";
    if (_extension.ConsumerPlugins.Count > 0) {
      debugInfo["Whizbang:ConsumerTranslators"] =
        string.Join(", ", _extension.ConsumerPlugins.Select(
          static p => Whizbang.Core.TypeNameFormatter.DisplayName(p.PluginType)));
    }
  }
}
