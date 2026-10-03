using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Builds the EF Core driver's <see cref="IPerspectiveTableSwapper"/>: the shared PostgreSQL swapper, over
/// connections of its own opened from the context's data source, in the schema the context maps.
/// </summary>
/// <remarks>
/// The swap holds a transaction open while the rebuild's last catch-up runs on the context's own connection, so it
/// needs a second connection. That comes from <see cref="SchemaBoundaryConnections.Resolve"/>, the one answer to
/// where an out-of-band connection comes from; when it has none, there is no swapper, and a blue-green rebuild
/// replays in place.
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild#blue-green</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/BlueGreenRebuildIntegrationTests.cs</tests>
internal static class EFCorePerspectiveTableSwapperFactory {
  /// <summary>The swapper, or null when no independent connection can be opened.</summary>
  public static IPerspectiveTableSwapper? Create(IServiceProvider services, Type dbContextType) {
    using var scope = services.CreateScope();
    var context = (DbContext)scope.ServiceProvider.GetRequiredService(dbContextType);
    var schema = context.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    var connections = SchemaBoundaryConnections.Resolve(context, initConnectionString: null, services);
    if (connections is null) {
      return null;
    }
    return new PostgresPerspectiveTableSwapper(async ct => {
      var connection = connections();
      await connection.OpenAsync(ct).ConfigureAwait(false);
      return connection;
    }, schema);
  }
}
