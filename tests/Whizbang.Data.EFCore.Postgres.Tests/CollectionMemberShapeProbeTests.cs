using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions.Extensions;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Which member shapes the mapped path can materialize, and that the opaque path takes the rest.
/// </summary>
/// <remarks>
/// <para>
/// This is the executable source of the list in <c>MappedPathDiscovery</c>. That discovery decides
/// whether a perspective's document is stored as mapped properties or as one opaque value, and both
/// ways of being wrong are quiet: a shape wrongly called mappable takes a service down at startup,
/// and a shape wrongly called unmappable loses its indexing with nobody asking.
/// </para>
/// <para>
/// So the shapes are asserted against a real Entity Framework model build rather than reasoned
/// about. If a future Entity Framework accepts or refuses something different, this fails and the
/// discovery gets revisited, which is the point of keeping it.
/// </para>
/// <para>
/// No database is touched: building a model needs a provider, not a connection.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
// Shard4 holds the fewest classes, and these build models without touching the database, so
// they add no container time to whichever shard carries them.
[Category("Shard4")]
public class CollectionMemberShapeProbeTests {
  public record Tag(Guid TagId, string Label);

  /// <summary>A nested positional record whose constructor takes a collection.</summary>
  public record Turn(Guid TurnId, string Content, IReadOnlyList<Tag>? Tags = null);

  public class ListModel { [StreamId] public Guid Id { get; init; } public List<Tag> Items { get; init; } = []; }
  public class IListModel { [StreamId] public Guid Id { get; init; } public IList<Tag> Items { get; init; } = new List<Tag>(); }
  public class ImmutableModel { [StreamId] public Guid Id { get; init; } public ImmutableList<Tag> Items { get; init; } = ImmutableList<Tag>.Empty; }
  public class PrimitiveInterfaceModel { [StreamId] public Guid Id { get; init; } public IReadOnlyList<string> Items { get; init; } = new List<string>(); }

  public class ICollectionModel { [StreamId] public Guid Id { get; init; } public ICollection<Tag> Items { get; init; } = new List<Tag>(); }
  public class IEnumerableModel { [StreamId] public Guid Id { get; init; } public IEnumerable<Tag> Items { get; init; } = new List<Tag>(); }
  public class IReadOnlyListModel { [StreamId] public Guid Id { get; init; } public IReadOnlyList<Tag> Items { get; init; } = new List<Tag>(); }
  public class IReadOnlyCollectionModel { [StreamId] public Guid Id { get; init; } public IReadOnlyCollection<Tag> Items { get; init; } = new List<Tag>(); }
  public class UnconstructibleElementModel { [StreamId] public Guid Id { get; init; } public List<Turn> Turns { get; init; } = []; }

  /// <summary>The document mapped property by property, which walks the whole graph.</summary>
  private sealed class MappedContext<TModel>(DbContextOptions<MappedContext<TModel>> options)
    : DbContext(options) where TModel : class {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<PerspectiveRow<TModel>>(entity => {
        entity.ToTable("wh_per_probe");
        entity.HasKey(e => e.Id);
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
      });
  }

  /// <summary>The document as one serialized value, which Entity Framework never looks inside.</summary>
  private sealed class OpaqueContext<TModel>(DbContextOptions<OpaqueContext<TModel>> options)
    : DbContext(options) where TModel : class {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<PerspectiveRow<TModel>>(entity => {
        entity.ToTable("wh_per_probe");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb");
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
      });
  }

  private static string? _buildError<TContext>(Func<DbContextOptions<TContext>, TContext> make, Type rowType)
    where TContext : DbContext {
    try {
      using var context = make(new DbContextOptionsBuilder<TContext>()
        .UseNpgsql("Host=localhost;Database=probe;Username=u;Password=p").Options);
      _ = context.Model.FindEntityType(rowType);
      return null;
    } catch (InvalidOperationException ex) {
      return ex.Message;
    }
  }

  private static string? _mapped<TModel>() where TModel : class =>
    _buildError<MappedContext<TModel>>(o => new MappedContext<TModel>(o), typeof(PerspectiveRow<TModel>));

  private static string? _opaque<TModel>() where TModel : class =>
    _buildError<OpaqueContext<TModel>>(o => new OpaqueContext<TModel>(o), typeof(PerspectiveRow<TModel>));

  /// <summary>The shapes the mapped path accepts, which must therefore keep their indexing.</summary>
  [Test]
  public async Task TheMappedPathAcceptsTheseShapesAsync() {
    await Assert.That(_mapped<ListModel>()).IsNull().Because("List of a complex element maps");
    await Assert.That(_mapped<IListModel>()).IsNull().Because("IList maps: it can be filled");
    await Assert.That(_mapped<ImmutableModel>()).IsNull().Because("ImmutableList maps");
    await Assert.That(_mapped<PrimitiveInterfaceModel>()).IsNull()
      .Because("a collection of primitives is stored as a value whatever interface declares it, "
        + "which is why the discovery checks the element type and not only the collection");
  }

  /// <summary>
  /// The shapes it refuses, each of which must route to the opaque form instead of failing at startup.
  /// </summary>
  [Test]
  public async Task TheMappedPathRefusesTheseShapesAsync() {
    await Assert.That(_mapped<ICollectionModel>()).IsNotNull();
    await Assert.That(_mapped<IEnumerableModel>()).IsNotNull();
    await Assert.That(_mapped<IReadOnlyListModel>()).IsNotNull();
    await Assert.That(_mapped<IReadOnlyCollectionModel>()).IsNotNull();
    await Assert.That(_mapped<UnconstructibleElementModel>()).IsNotNull()
      .Because("a collection cannot be bound to a constructor parameter, and a positional record "
        + "declares no other constructor, so the element cannot be constructed at all");
  }

  /// <summary>
  /// The opaque form takes every shape the mapped path refuses, which is what makes the routing a fix.
  /// </summary>
  /// <remarks>
  /// Without this the routing would only be a different way to fail. The serializer handles positional
  /// records and read-only collections without complaint, which is how such documents round-trip
  /// today.
  /// </remarks>
  [Test]
  public async Task TheOpaqueFormTakesWhatTheMappedPathRefusesAsync() {
    await Assert.That(_opaque<IReadOnlyListModel>()).IsNull();
    await Assert.That(_opaque<UnconstructibleElementModel>()).IsNull()
      .Because("the whole document is one value the serializer owns, so the shape inside it stops "
        + "being Entity Framework's problem");
  }
}
