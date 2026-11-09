// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Signals;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The scope's open transaction on any framework DbContext, for the application signal channel (#2076): a publish made
/// through a channel resolved in the same scope is sent inside that transaction and delivered only on commit.
/// </summary>
/// <remarks>
/// One source per scope reads every context the Postgres driver was composed with, in registration order, and reports the
/// first transaction begun on one (<c>Database.BeginTransaction</c> or <c>UseTransaction</c>). A context enlisted in a
/// <c>TransactionScope</c> reports none; the transport defers to the scope's outcome instead. A context created by
/// <c>IDbContextFactory</c> lives in a scope of its own and is not this scope's.
/// </remarks>
/// <docs>fundamentals/work-coordinator/app-signals#transactional-publish</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreAppSignalTransactionSourceTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/AppSignalTransactionalPublishTests.cs</tests>
internal sealed class EFCoreAppSignalTransactionSource(IServiceProvider scope, EFCoreAppSignalTransactionSource.Contexts contexts)
    : IAppSignalTransactionSource {

  /// <summary>The context types this source reads, in registration order.</summary>
  internal IReadOnlyList<Type> ContextTypes => contexts.Types;

  /// <inheritdoc />
  public DbTransaction? CurrentTransaction {
    get {
      foreach (var type in contexts.Types) {
        var context = (DbContext)scope.GetRequiredService(type);
        if (context.Database.CurrentTransaction is { } transaction) {
          return transaction.GetDbTransaction();
        }
      }
      return null;
    }
  }

  /// <summary>
  /// Adds <paramref name="dbContextType"/> to the contexts the scope's source reads, registering the source on first use.
  /// Idempotent per context type; called by the Postgres driver, so no application wires it.
  /// </summary>
  internal static void Register(IServiceCollection services, Type dbContextType) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(dbContextType);

    if (services.FirstOrDefault(static d => d.ServiceType == typeof(Contexts))?.ImplementationInstance is not Contexts contexts) {
      contexts = new Contexts();
      services.AddSingleton(contexts);
      services.AddScoped<IAppSignalTransactionSource>(static sp =>
        new EFCoreAppSignalTransactionSource(sp, sp.GetRequiredService<Contexts>()));
    }
    contexts.Add(dbContextType);
  }

  /// <summary>The framework context types, collected while the services are composed.</summary>
  internal sealed class Contexts {
    private readonly List<Type> _types = [];

    /// <summary>The context types, in registration order.</summary>
    public IReadOnlyList<Type> Types => _types;

    /// <summary>Adds <paramref name="type"/> unless it is already there.</summary>
    public void Add(Type type) {
      if (!_types.Contains(type)) {
        _types.Add(type);
      }
    }
  }
}
