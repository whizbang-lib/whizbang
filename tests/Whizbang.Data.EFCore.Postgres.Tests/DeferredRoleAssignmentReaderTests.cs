using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The <c>roles</c> health source reads through a deferred reader, so listing health sources never
/// constructs the role elector (whose construction resolves the notification data source and, under
/// auto-discovery, the application's DbContext model).
/// </summary>
[Category("Shard3")]
public class DeferredRoleAssignmentReaderTests {

  [Test]
  public async Task Construction_ResolvesNothing_AndTheFirstRead_UsesTheRegisteredReaderAsync() {
    var counting = new CountingReader();
    var resolutions = 0;
    var services = new ServiceCollection();
    services.AddSingleton<IRoleAssignmentReader>(_ => {
      resolutions++;
      return counting;
    });
    await using var provider = services.BuildServiceProvider();

    var deferred = new DeferredRoleAssignmentReader(provider);
    await Assert.That(resolutions).IsEqualTo(0)
      .Because("constructing the deferred reader must not construct the real one");

    var snapshots = await deferred.ReadAssignmentsAsync(CancellationToken.None);

    await Assert.That(resolutions).IsEqualTo(1);
    await Assert.That(counting.Reads).IsEqualTo(1);
    await Assert.That(snapshots).IsEmpty();
  }

  private sealed class CountingReader : IRoleAssignmentReader {
    public int Reads { get; private set; }

    public Task<IReadOnlyList<RoleAssignmentSnapshot>> ReadAssignmentsAsync(CancellationToken cancellationToken) {
      Reads++;
      return Task.FromResult<IReadOnlyList<RoleAssignmentSnapshot>>([]);
    }
  }
}
