using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests.Observability;

/// <summary>
/// The interceptor that puts the filter's field where the database will keep it.
/// </summary>
/// <remarks>
/// Every way a command leaves a context has to carry the tag, because a query that reads a
/// perspective can leave by any of them, and a filter whose field went unnamed is exactly the one
/// the advisory would have had something to say about.
/// </remarks>
/// <docs>operations/diagnostics/whiz306</docs>
[Category("Shard4")]
public class DocumentFilterTagInterceptorTests {
  private const string FILTERED =
    "SELECT w.id FROM wh_per_documents AS w WHERE (w.data ->> 'TenantId') = @p0";

  // Ignored by every override, which is what lets each be exercised without standing up the
  // diagnostics machinery that fills it in.
  private static CommandEventData Unused => null!;

  // The synchronous overrides are two of the four ways a command leaves a context, so calling
  // them is the point rather than an oversight; the rule that prefers the asynchronous one has
  // nothing to say about the one being tested.
#pragma warning disable S6966
  [Test]
  public async Task EveryWayACommandLeaves_CarriesTheTagAsync() {
    var interceptor = new DocumentFilterTagInterceptor();

    using var reader = new NpgsqlCommand(FILTERED);
    interceptor.ReaderExecuting(reader, Unused, default);
    await Assert.That(reader.CommandText).StartsWith("/* wh:f=data.TenantId */")
      .Because("a query that reads rows is the one the advisory is about");

    using var readerAsync = new NpgsqlCommand(FILTERED);
    await interceptor.ReaderExecutingAsync(readerAsync, Unused, default);
    await Assert.That(readerAsync.CommandText).StartsWith("/* wh:f=data.TenantId */")
      .Because("asynchronously is how a context sends almost all of them");

    using var scalar = new NpgsqlCommand(FILTERED);
    interceptor.ScalarExecuting(scalar, Unused, default);
    await Assert.That(scalar.CommandText).StartsWith("/* wh:f=data.TenantId */")
      .Because("a count over a filtered perspective is a scalar, and is exactly the shape that scans");

    using var scalarAsync = new NpgsqlCommand(FILTERED);
    await interceptor.ScalarExecutingAsync(scalarAsync, Unused, default);
    await Assert.That(scalarAsync.CommandText).StartsWith("/* wh:f=data.TenantId */");
  }

  /// <summary>A command with nothing to name is handed on untouched.</summary>
  /// <remarks>
  /// This is nearly every command, so it has to end at the substring scan and leave the text as the
  /// same instance rather than rebuild it.
  /// </remarks>
  [Test]
  public async Task ACommandWithNothingToName_IsHandedOnUnchangedAsync() {
    const string plain = "SELECT id FROM wh_outbox WHERE (metadata ->> 'Kind') = @p0";

    using var command = new NpgsqlCommand(plain);
    new DocumentFilterTagInterceptor().ReaderExecuting(command, Unused, default);

    await Assert.That(command.CommandText).IsEqualTo(plain)
      .Because("the framework's own tables are not a consumer's to promote a column on");
  }
#pragma warning restore S6966

  /// <summary>The naming is off until it is asked for, and asking for it registers the interceptor.</summary>
  /// <remarks>
  /// Off by default because it reads the text of every command the context sends: a cost on the
  /// query path, for a diagnostic, that buys nothing where statement statistics are not collected.
  /// </remarks>
  [Test]
  public async Task TheNamingIsOffUntilItIsAskedForAsync() {
    var bare = new DbContextOptionsBuilder().UseNpgsql("Host=localhost");
    await Assert.That(_interceptors(bare.Options)).IsEmpty()
      .Because("a context that did not ask for the naming must not pay for it");

    var asked = new DbContextOptionsBuilder().UseNpgsql("Host=localhost").UseWhizbangFilterNaming();
    await Assert.That(_interceptors(asked.Options).OfType<DocumentFilterTagInterceptor>()).IsNotEmpty()
      .Because("asking for it is the whole way it is turned on");
  }

  private static IEnumerable<IInterceptor> _interceptors(DbContextOptions options) =>
    options.FindExtension<CoreOptionsExtension>()?.Interceptors
      ?? Enumerable.Empty<IInterceptor>();
}
