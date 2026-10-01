using Microsoft.EntityFrameworkCore;
using Pgvector;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The reader a Split load hands the generated map: each promoted column comes from the tracked row's shadow
/// property, already the model property's type, and a vector comes back as its components.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EntityEntryPhysicalColumnReader.cs</code-under-test>
[Category("Shard2")]
public class EntityEntryPhysicalColumnReaderTests {
  private static readonly float[] _components = [1f, 2f, 3f];

  private sealed class Probe {
    public Guid Id { get; set; }
  }

  // Only the model is built; nothing connects.
  private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<Probe>(entity => {
        entity.HasKey(e => e.Id);
        entity.Property<string?>("status");
        entity.Property<int?>("priority");
        entity.Property<Vector?>("embedding").HasColumnType("vector(3)");
      });
    }
  }

  private static ProbeDbContext _context() => new(new DbContextOptionsBuilder<ProbeDbContext>()
      .UseNpgsql("Host=unused", o => o.UseVector())
      .Options);

  [Test]
  public async Task Read_ReturnsEachShadowValue_AndAVectorAsItsComponentsAsync() {
    await using var context = _context();
    var entry = context.Attach(new Probe { Id = Guid.CreateVersion7() });
    entry.Property("status").CurrentValue = "set";
    entry.Property("priority").CurrentValue = 4;
    entry.Property("embedding").CurrentValue = new Vector(_components);
    var reader = new EntityEntryPhysicalColumnReader(entry);

    await Assert.That(reader.Read<string?>("status")).IsEqualTo("set");
    await Assert.That(reader.Read<int?>("priority")).IsEqualTo(4);
    await Assert.That(reader.GetVector("embedding")).IsEquivalentTo(_components);
  }

  [Test]
  public async Task Read_ANullColumn_IsTheDefaultAsync() {
    await using var context = _context();
    var reader = new EntityEntryPhysicalColumnReader(context.Attach(new Probe { Id = Guid.CreateVersion7() }));

    await Assert.That(reader.Read<string?>("status")).IsNull();
    await Assert.That(reader.Read<int>("priority")).IsEqualTo(0)
      .Because("a null column reads as the default of a property that cannot hold null");
    await Assert.That(reader.GetVector("embedding")).IsNull();
  }
}
