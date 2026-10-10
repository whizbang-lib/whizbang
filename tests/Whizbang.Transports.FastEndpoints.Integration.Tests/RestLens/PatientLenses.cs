// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Transports.FastEndpoints.Integration.Tests.RestLens;

/// <summary>A read model with a protected property under each masking strategy.</summary>
public class PatientRecord {
  /// <summary>The patient id.</summary>
  public Guid Id { get; set; }

  /// <summary>Unprotected.</summary>
  public string? Name { get; set; }

  /// <summary>Partial.</summary>
  [FieldPermission("pii:view", MaskingStrategy.Partial)]
  public string? Ssn { get; set; }

  /// <summary>Hide (the default).</summary>
  [FieldPermission("pii:view")]
  public string? Email { get; set; }

  /// <summary>Mask.</summary>
  [FieldPermission("pii:view", MaskingStrategy.Mask)]
  public string? Phone { get; set; }

  /// <summary>Redact, under an explicit JSON name.</summary>
  [FieldPermission("pii:view", MaskingStrategy.Redact)]
  [JsonPropertyName("tax_id")]
  public string? TaxId { get; set; }

  /// <summary>A non-string member under Mask, which hides it.</summary>
  [FieldPermission("finance:view", MaskingStrategy.Mask)]
  public decimal Balance { get; set; }

  /// <summary>A nested type with a protected property of its own.</summary>
  public PatientAddress Address { get; set; } = new();
}

/// <summary>A nested type.</summary>
public class PatientAddress {
  /// <summary>Unprotected.</summary>
  public string? City { get; set; }

  /// <summary>Mask.</summary>
  [FieldPermission("pii:view", MaskingStrategy.Mask)]
  public string? Street { get; set; }
}

/// <summary>A lens over protected patient records.</summary>
[RestLens(Route = "/api/patients")]
public interface IPatientLens : ILensQuery<PatientRecord>;

/// <summary>An EF Core in-memory store of patient rows.</summary>
/// <param name="options">The context options.</param>
public sealed class PatientDbContext(DbContextOptions<PatientDbContext> options) : DbContext(options) {
  /// <inheritdoc />
  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    modelBuilder.Entity<PerspectiveRow<PatientRecord>>(entity => {
      entity.HasKey(e => e.Id);
      entity.OwnsOne(e => e.Data, data => {
        data.WithOwner();
        data.OwnsOne(d => d.Address);
      });
      entity.OwnsOne(e => e.Metadata, metadata => metadata.WithOwner());
      entity.Property(e => e.Scope).HasConversion(
        v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
        v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);
    });
  }
}

/// <summary>Serves the patient lens from the in-memory store, read-only, through its default scope.</summary>
/// <param name="db">The store.</param>
public sealed class InMemoryPatientLens(PatientDbContext db) : IPatientLens {
  /// <inheritdoc />
  public IQueryable<PerspectiveRow<PatientRecord>> Query => db.Set<PerspectiveRow<PatientRecord>>().AsNoTracking();

  /// <inheritdoc />
  public IScopedLensAccess<PatientRecord> DefaultScope => new Scoped(Query);

  /// <inheritdoc />
  public IScopedLensAccess<PatientRecord> Scope(QueryScope scope) => throw new NotSupportedException();

  /// <inheritdoc />
  public IScopedLensAccess<PatientRecord> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
    throw new NotSupportedException();

  /// <inheritdoc />
  public Task<PatientRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
    throw new NotSupportedException();

  private sealed class Scoped(IQueryable<PerspectiveRow<PatientRecord>> query) : IScopedLensAccess<PatientRecord> {
    public IQueryable<PerspectiveRow<PatientRecord>> Query => query;

    public Task<PatientRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
  }
}
