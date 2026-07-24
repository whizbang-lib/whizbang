using Microsoft.EntityFrameworkCore;
using Whizbang.Data.EFCore.Custom;

namespace ECommerce.Lifecycle.Integration.Tests;

/// <summary>
/// EF Core DbContext for lifecycle test perspective models.
/// Uses public schema for simplicity (test databases are ephemeral).
/// In root namespace so generated code lands in same namespace as perspective associations.
/// </summary>
[WhizbangDbContext(Schema = "public", ConnectionStringName = "lifecycle-test-db")]
public partial class LifecycleTestDbContext : DbContext {
#pragma warning disable IL2026, IL2046, IL3050
  public LifecycleTestDbContext(DbContextOptions<LifecycleTestDbContext> options) : base(options) { }
#pragma warning restore IL2026, IL2046, IL3050
}
