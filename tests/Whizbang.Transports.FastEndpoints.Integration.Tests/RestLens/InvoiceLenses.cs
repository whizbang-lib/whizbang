// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Transports.FastEndpoints;

namespace Whizbang.Transports.FastEndpoints.Integration.Tests.RestLens;

/// <summary>The lifecycle state of a test invoice.</summary>
public enum InvoiceStatus {
  /// <summary>Not yet paid.</summary>
  Open,

  /// <summary>Paid.</summary>
  Closed,
}

/// <summary>A read model with one property of each kind a REST lens filters and sorts by.</summary>
public class InvoiceModel {
  /// <summary>The invoice id.</summary>
  public Guid Id { get; set; }

  /// <summary>Text.</summary>
  public string? Customer { get; set; }

  /// <summary>A parsable number.</summary>
  public decimal Amount { get; set; }

  /// <summary>An enumeration.</summary>
  public InvoiceStatus Status { get; set; }

  /// <summary>A nullable number.</summary>
  public int? Priority { get; set; }
}

/// <summary>Every capability on, with a small page so paging is visible.</summary>
[RestLens(Route = "/api/invoices", DefaultPageSize = 2, MaxPageSize = 10)]
public interface IInvoiceLens : ILensQuery<InvoiceModel>;

/// <summary>Every capability off.</summary>
[RestLens(Route = "/api/invoices-plain", EnableFiltering = false, EnableSorting = false, EnablePaging = false)]
public interface IPlainInvoiceLens : ILensQuery<InvoiceModel>;

/// <summary>An EF Core in-memory store of invoice rows.</summary>
/// <param name="options">The context options.</param>
public sealed class InvoiceDbContext(DbContextOptions<InvoiceDbContext> options) : DbContext(options) {
  /// <inheritdoc />
  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    modelBuilder.Entity<PerspectiveRow<InvoiceModel>>(entity => {
      entity.HasKey(e => e.Id);
      entity.OwnsOne(e => e.Data, data => data.WithOwner());
      entity.OwnsOne(e => e.Metadata, metadata => metadata.WithOwner());
      entity.Property(e => e.Scope).HasConversion(
        v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
        v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);
    });
  }
}

/// <summary>
/// Serves both lenses from the in-memory store, read-only as the production lens is, through the
/// default-scope query the generated endpoints use.
/// </summary>
/// <param name="db">The store.</param>
public sealed class InMemoryInvoiceLens(InvoiceDbContext db) : IInvoiceLens, IPlainInvoiceLens {
  /// <inheritdoc />
  public IQueryable<PerspectiveRow<InvoiceModel>> Query => db.Set<PerspectiveRow<InvoiceModel>>().AsNoTracking();

  /// <inheritdoc />
  public IScopedLensAccess<InvoiceModel> DefaultScope => new Scoped(Query);

  /// <inheritdoc />
  public IScopedLensAccess<InvoiceModel> Scope(QueryScope scope) => throw new NotSupportedException();

  /// <inheritdoc />
  public IScopedLensAccess<InvoiceModel> ScopeOverride(QueryScope scope, ScopeFilterOverride overrideValues) =>
    throw new NotSupportedException();

  /// <inheritdoc />
  public Task<InvoiceModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
    throw new NotSupportedException();

  private sealed class Scoped(IQueryable<PerspectiveRow<InvoiceModel>> query) : IScopedLensAccess<InvoiceModel> {
    public IQueryable<PerspectiveRow<InvoiceModel>> Query => query;

    public Task<InvoiceModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
  }
}
