# EF Core Native AOT Compatibility Status

**Last Updated**: 2025-11-30
**EF Core Version**: 10.0.0
**Status**: ⚠️ Experimental - Not Production Ready

---

## Current State

Entity Framework Core 10 has **experimental** Native AOT support that is **not yet suited for production use** (per Microsoft documentation).

### Known Limitations

1. **DbContext Constructor Warnings**: The `DbContext(DbContextOptions)` base constructor has `RequiresUnreferencedCodeAttribute` and `RequiresDynamicCodeAttribute`, causing IL2026 and IL3050 warnings in all projects using EF Core with AOT enabled.

2. **Design-Time Operations**: Migrations, `EnsureCreated()`, and model building require dynamic code generation and are incompatible with Native AOT.

3. **Query Limitations**:
   - Dynamic queries unsupported
   - LINQ comprehension syntax not supported
   - Precompiled queries required for all database access

4. **Expected Timeline**: Microsoft indicates full AOT compatibility may arrive in **EF Core 12+** (not EF Core 10 or 11).

---

## Whizbang's Approach

### Configuration

Whizbang EF Core projects are configured with:

```xml
<PropertyGroup>
  <!-- Enable Native AOT publishing -->
  <PublishAot>true</PublishAot>
  <IsAotCompatible>true</IsAotCompatible>

  <!-- Opt-in to C# interceptors for EF Core precompiled queries (when ready) -->
  <InterceptorsNamespaces>$(InterceptorsNamespaces);Microsoft.EntityFrameworkCore.GeneratedInterceptors</InterceptorsNamespaces>
</PropertyGroup>

<ItemGroup>
  <!-- Required for precompiled queries -->
  <PackageReference Include="Microsoft.EntityFrameworkCore.Tasks" />
</ItemGroup>
```

**Why?** This configuration signals our **intent** to support Native AOT and prepares the project for when EF Core's experimental features mature.

### Expected Warnings

The following warnings are **expected and acceptable** in EF Core projects:

#### IL2026: RequiresUnreferencedCode
```
BffDbContext.cs(18,63): warning IL2026: Using member 'Microsoft.EntityFrameworkCore.DbContext.DbContext(DbContextOptions)'
which has 'RequiresUnreferencedCodeAttribute' can break functionality when trimming application code.
EF Core isn't fully compatible with trimming, and running the application may generate unexpected runtime failures.
```

**Cause**: DbContext base class constructor
**Impact**: Known Microsoft limitation
**Resolution**: None available - warning expected until EF Core 12+

#### IL3050: RequiresDynamicCode
```
BffDbContext.cs(18,63): warning IL3050: Using member 'Microsoft.EntityFrameworkCore.DbContext.DbContext(DbContextOptions)'
which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling.
EF Core isn't fully compatible with NativeAOT, and running the application may generate unexpected runtime failures.
```

**Cause**: DbContext base class constructor
**Impact**: Known Microsoft limitation
**Resolution**: None available - warning expected until EF Core 12+

### What We DON'T Do

❌ **No suppression attributes** - We don't use `[RequiresDynamicCode]`, `[RequiresUnreferencedCode]`, or `[UnconditionalSuppressMessage]`
❌ **No .editorconfig suppressions** - Warnings remain visible to track Microsoft's progress
❌ **No hiding the truth** - Documentation clearly states current limitations

### What We DO

✅ **Accept warnings** - IL2026/IL3050 warnings from EF Core are expected
✅ **Document limitations** - Clear communication about what works and what doesn't
✅ **Prepare for the future** - Configuration ready for when EF Core matures
✅ **Provide alternatives** - Dapper-based implementations are fully AOT-compatible

---

## Migration Path

### For Development/Non-AOT Scenarios
✅ EF Core works perfectly
✅ Full LINQ query support
✅ Migrations, EnsureCreated(), etc. all work
✅ Use EF Core.Postgres for perspectives

### For Production/AOT Scenarios
⚠️ EF Core has limitations (experimental support)
✅ Dapper-based perspectives are fully AOT-compatible
✅ Switch to `Whizbang.Data.Dapper.Postgres` for production AOT builds
✅ Zero reflection, zero dynamic code, full AOT support

---

## Testing Strategy

### Integration Tests
Integration tests using EF Core **do not publish as Native AOT**, so warnings don't affect test execution. Tests run normally using the runtime EF Core model builder.

### Production Builds
For true Native AOT production builds:
1. Use Dapper-based storage backends (fully AOT-compatible)
2. OR accept experimental EF Core AOT support with known limitations
3. OR wait for EF Core 12+ with production-ready AOT support

---

## References

- [EF Core Native AOT Documentation](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries)
- [EF Core Issue #29754: NativeAOT work](https://github.com/dotnet/efcore/issues/29754)
- [ASP.NET Core Native AOT Support](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot)

---

## Summary

**EF Core AOT warnings are expected and acceptable.** They represent Microsoft's current experimental state, not Whizbang deficiencies. Whizbang provides fully AOT-compatible alternatives (Dapper) while preparing EF Core projects for future compatibility.
