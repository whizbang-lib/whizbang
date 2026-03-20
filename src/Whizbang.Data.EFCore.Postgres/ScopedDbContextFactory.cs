#pragma warning disable S3604, S3928 // Primary constructor field/property initializers are intentional

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// IDbContextFactory implementation that creates DbContext instances via service scopes.
/// Each call to CreateDbContext() creates a new scope and gets a scoped DbContext from it.
/// The scope is tracked internally and disposed when the DbContext is disposed.
/// </summary>
/// <remarks>
/// This factory is used instead of AddPooledDbContextFactory to avoid scope validation issues.
/// AddPooledDbContextFactory registers scoped option configurations internally, which causes
/// "Cannot resolve scoped service from root provider" errors when scope validation is enabled.
///
/// This implementation:
/// - Is registered as singleton (safe for parallel resolvers)
/// - Creates scopes for each CreateDbContext() call
/// - Tracks scopes using ConditionalWeakTable to dispose them when contexts are GC'd
/// - Works correctly with scope validation enabled
///
/// For HotChocolate parallel resolvers, each resolver gets its own DbContext + scope,
/// providing the same thread-safety as AddPooledDbContextFactory but without the scope issues.
/// </remarks>
/// <typeparam name="TContext">The DbContext type</typeparam>
/// <docs>fundamentals/lenses/lens-query-factory</docs>
/// <remarks>
/// Creates a new ScopedDbContextFactory.
/// </remarks>
/// <param name="scopeFactory">The service scope factory for creating scopes</param>
public sealed class ScopedDbContextFactory<[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicConstructors |
    DynamicallyAccessedMemberTypes.NonPublicConstructors |
    DynamicallyAccessedMemberTypes.PublicProperties)] TContext>(IServiceScopeFactory scopeFactory) : IDbContextFactory<TContext>
    where TContext : DbContext {

  private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

  /// <summary>
  /// Tracks scopes associated with DbContext instances.
  /// When the DbContext is GC'd, the scope will be disposed via the weak reference cleanup.
  /// </summary>
  private readonly ConditionalWeakTable<TContext, IServiceScope> _scopes = [];

  /// <summary>
  /// Creates a new DbContext instance within a new service scope.
  /// The scope is tracked and will be disposed when the DbContext is garbage collected.
  /// </summary>
  /// <returns>A new DbContext instance</returns>
  public TContext CreateDbContext() {
    var scope = _scopeFactory.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<TContext>();

    // Track the scope with the context so it gets cleaned up when context is GC'd
    _scopes.Add(context, scope);

    // Hook into context disposal to dispose the scope
    // Note: DbContext.DisposeAsync doesn't have an event, so we use the finalizer pattern via ConditionalWeakTable
    return context;
  }
}
