using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Compatibility shim retained for tests still constructing legacy fakes.
/// Workers now wait on <see cref="Whizbang.Core.Workers.ISchemaReadyGate"/>.
/// </summary>
public interface IDatabaseReadinessCheck {
  /// <summary>Returns true once the database is reachable and migrated.</summary>
  Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
