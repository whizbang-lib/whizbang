using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Information about a discovered DbContext class.
/// Used by database-specific generators to emit DbContext configuration code.
/// </summary>
/// <param name="ClassName">
/// Simple class name (e.g., "MyDbContext").
/// </param>
/// <param name="FullyQualifiedName">
/// Fully-qualified class name (e.g., "global::MyApp.Data.MyDbContext").
/// </param>
/// <param name="Namespace">
/// Namespace containing the DbContext (e.g., "MyApp.Data").
/// </param>
/// <param name="ExistingPerspectives">
/// State types that already have DbSet properties defined by the user.
/// Generator should not emit DbSet properties for these.
/// </param>
/// <param name="Location">
/// Source location for diagnostic reporting.
/// </param>
/// <tests>tests/Whizbang.Generators.Tests/Models/DbContextInfoTests.cs</tests>
public sealed record DbContextInfo(
  string ClassName,
  string FullyQualifiedName,
  string Namespace,
  ImmutableArray<string> ExistingPerspectives,
  Location Location
);
