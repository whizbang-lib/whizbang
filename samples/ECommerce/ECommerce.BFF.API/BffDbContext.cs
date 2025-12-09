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
/// AOT-compatible: All configuration is done via source generators, no reflection needed.
/// </summary>
[WhizbangDbContext]
public partial class BffDbContext : DbContext {
#pragma warning disable IL2026 // EF Core uses reflection internally - AOT support experimental in EF10, stable in EF12
#pragma warning disable IL2046 // EF Core uses reflection internally - AOT support experimental in EF10, stable in EF12
#pragma warning disable IL3050 // EF Core uses dynamic code generation - AOT support experimental in EF10, stable in EF12
  public BffDbContext(DbContextOptions<BffDbContext> options) : base(options) { }
#pragma warning restore IL3050
#pragma warning restore IL2046
#pragma warning restore IL2026

  // DbSet properties auto-generated in BffDbContext.Generated.g.cs
  // OnModelCreating override auto-generated with ConfigureWhizbang() call
  // Implement OnModelCreatingExtended() for custom configurations (optional)
}
