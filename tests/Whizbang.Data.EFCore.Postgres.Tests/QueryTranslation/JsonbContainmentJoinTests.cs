using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Joins between perspectives: the filters still reach the index, and the join keys are left alone.
/// </summary>
/// <remarks>
/// <para>
/// A join changes the shape of the lambda parameter. After one, a predicate reads
/// <c>pair.Left.Data.Field</c> through a transparent identifier rather than <c>row.Data.Field</c>
/// directly, and it would be easy for a rewrite keyed on the member chain to stop recognizing it.
/// These assert on the compiled SQL, which is where that would show.
/// </para>
/// <para>
/// The join condition itself compares two rows, so it is never a candidate: containment tests a
/// document against a literal document, not a document against another column.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
[SuppressMessage("Readability", "RCS1118:Mark local variable as const",
  Justification = "These locals are captured into an expression tree on purpose. A const local is inlined by the compiler as a literal, which turns the parameterized filter under test into a constant one: in the matrix that collapses every /param row onto its /const twin, and elsewhere it stops exercising the captured-parameter path altogether.")]
public class JsonbContainmentJoinTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=joins;Username=u;Password=p";

  [SuppressIndexAdvisory("compiled, never run")]
  public class OrderModel {
    public Guid CustomerId { get; init; }
    public string Status { get; init; } = string.Empty;
    public int Total { get; init; }
  }

  [SuppressIndexAdvisory("compiled, never run")]
  public class CustomerModel {
    public Guid CustomerId { get; init; }
    public string Region { get; init; } = string.Empty;
  }

  private sealed class JoinDbContext(DbContextOptions<JoinDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      _map<OrderModel>(modelBuilder, "wh_per_order");
      _map<CustomerModel>(modelBuilder, "wh_per_customer");
      modelBuilder.UseWhizbangJsonbContainment();
    }

    private static void _map<TModel>(ModelBuilder modelBuilder, string table) where TModel : class =>
      modelBuilder.Entity<PerspectiveRow<TModel>>(entity => {
        entity.ToTable(table);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").IsRequired();
      });
  }

  private static readonly DbContextOptions<JoinDbContext> _options =
    new DbContextOptionsBuilder<JoinDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static readonly JoinDbContext _db = new(_options);

  private static IQueryable<PerspectiveRow<OrderModel>> _orders => _db.Set<PerspectiveRow<OrderModel>>();
  private static IQueryable<PerspectiveRow<CustomerModel>> _customers => _db.Set<PerspectiveRow<CustomerModel>>();

  private static int _containments(string sql) {
    var count = 0;
    var index = 0;
    while ((index = sql.IndexOf("@>", index, StringComparison.Ordinal)) >= 0) {
      count++;
      index += 2;
    }

    return count;
  }

  /// <summary>A filter on the left side of an inner join still reaches the index.</summary>
  [Test]
  public async Task InnerJoin_FilterOnTheLeft_ReachesTheIndexAsync() {
    var status = "open";

    var sql = _orders
      .Where(o => o.Data.Status == status)
      .Join(_customers, o => o.Data.CustomerId, c => c.Data.CustomerId, (o, c) => new { o, c })
      .Select(pair => pair.o)
      .ToQueryString();

    await Assert.That(_containments(sql)).IsEqualTo(1);
  }

  /// <summary>And a filter applied after the join, through the transparent identifier.</summary>
  [Test]
  public async Task InnerJoin_FilterAfterTheJoin_ReachesTheIndexAsync() {
    var region = "north";

    var sql = _orders
      .Join(_customers, o => o.Data.CustomerId, c => c.Data.CustomerId, (o, c) => new { o, c })
      .Where(pair => pair.c.Data.Region == region)
      .Select(pair => pair.o)
      .ToQueryString();

    await Assert.That(_containments(sql)).IsEqualTo(1);
  }

  /// <summary>Both sides filtered gives two containment tests, one per table.</summary>
  [Test]
  public async Task InnerJoin_FilterOnBothSides_ReachesTheIndexTwiceAsync() {
    var status = "open";
    var region = "north";

    var sql = _orders
      .Where(o => o.Data.Status == status)
      .Join(_customers, o => o.Data.CustomerId, c => c.Data.CustomerId, (o, c) => new { o, c })
      .Where(pair => pair.c.Data.Region == region)
      .Select(pair => pair.o)
      .ToQueryString();

    await Assert.That(_containments(sql)).IsEqualTo(2);
  }

  /// <summary>
  /// The join condition compares two rows, so it stays an extraction on both sides. Containment
  /// tests a document against a literal, and there is no literal here.
  /// </summary>
  [Test]
  public async Task JoinKeys_AreNeverRewrittenAsync() {
    var sql = _orders
      .Join(_customers, o => o.Data.CustomerId, c => c.Data.CustomerId, (o, c) => new { o, c })
      .Select(pair => pair.o)
      .ToQueryString();

    await Assert.That(_containments(sql)).IsEqualTo(0);
    await Assert.That(sql).Contains("data ->>", StringComparison.Ordinal);
  }

  /// <summary>Query syntax reaches the same place.</summary>
  [Test]
  public async Task QuerySyntaxJoin_FilterReachesTheIndexAsync() {
    var status = "open";

    var query =
      from o in _orders
      join c in _customers on o.Data.CustomerId equals c.Data.CustomerId
      where o.Data.Status == status
      select o;

    await Assert.That(_containments(query.ToQueryString())).IsEqualTo(1);
  }

  /// <summary>A left join, spelled the way Entity Framework expects it.</summary>
  [Test]
  public async Task LeftJoin_FilterReachesTheIndexAsync() {
    var status = "open";

    var query =
      from o in _orders
      join c in _customers on o.Data.CustomerId equals c.Data.CustomerId into matched
      from c in matched.DefaultIfEmpty()
      where o.Data.Status == status
      select o;

    await Assert.That(_containments(query.ToQueryString())).IsEqualTo(1);
  }

  /// <summary>A cross join through SelectMany, with a filter on each side.</summary>
  [Test]
  public async Task CrossJoin_FiltersReachTheIndexAsync() {
    var status = "open";
    var region = "north";

    var query = _orders
      .SelectMany(_ => _customers, (o, c) => new { o, c })
      .Where(pair => pair.o.Data.Status == status && pair.c.Data.Region == region)
      .Select(pair => pair.o);

    await Assert.That(_containments(query.ToQueryString())).IsEqualTo(2);
  }

  /// <summary>
  /// A filter that compares a member of one row with a member of the other is not a containment
  /// candidate, and must not become one.
  /// </summary>
  [Test]
  public async Task CrossRowComparison_IsNotRewrittenAsync() {
    var query = _orders
      .SelectMany(_ => _customers, (o, c) => new { o, c })
      .Where(pair => pair.o.Data.Status == pair.c.Data.Region)
      .Select(pair => pair.o);

    await Assert.That(_containments(query.ToQueryString())).IsEqualTo(0);
  }

  /// <summary>Ordering after a join does not disturb the filter that preceded it.</summary>
  [Test]
  public async Task JoinThenOrder_KeepsTheContainmentAsync() {
    var status = "open";

    var query = _orders
      .Where(o => o.Data.Status == status)
      .Join(_customers, o => o.Data.CustomerId, c => c.Data.CustomerId, (o, c) => new { o, c })
      .OrderBy(pair => pair.o.UpdatedAt)
      .Select(pair => pair.o);

    await Assert.That(_containments(query.ToQueryString())).IsEqualTo(1);
  }

  /// <summary>A grouped join projecting a count still carries the filter to the index.</summary>
  [Test]
  public async Task GroupJoin_FilterReachesTheIndexAsync() {
    var region = "north";

    var query = _customers
      .Where(c => c.Data.Region == region)
      .GroupJoin(_orders, c => c.Data.CustomerId, o => o.Data.CustomerId, (c, orders) => new {
        c.Id,
        Count = orders.Count(),
      });

    await Assert.That(_containments(query.ToQueryString())).IsEqualTo(1);
  }
}
