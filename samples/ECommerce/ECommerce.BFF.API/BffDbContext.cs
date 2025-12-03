using System.Diagnostics.CodeAnalysis;
using ECommerce.BFF.API.Lenses;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Custom;
using Whizbang.Data.EFCore.Postgres.Generated;

namespace ECommerce.BFF.API;

/// <summary>
/// EF Core DbContext for BFF read models (perspectives).
/// OnModelCreating is auto-generated in partial class with ConfigureWhizbang() call.
/// Implement OnModelCreatingExtended() to add custom model configurations.
/// DbSet properties are auto-generated from discovered IPerspectiveOf implementations.
/// </summary>
[WhizbangDbContext]
public partial class BffDbContext : DbContext {
  [RequiresDynamicCode()]
  [RequiresUnreferencedCode()]
  public BffDbContext(DbContextOptions<BffDbContext> options) : base(options) { }

  // DbSet properties auto-generated in BffDbContext.Generated.g.cs
  // OnModelCreating override auto-generated with ConfigureWhizbang() call
  // Implement OnModelCreatingExtended() for custom configurations (optional)
}
