// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;
using Whizbang.Transports.FastEndpoints.Integration.Tests.RestLens.Generated;

namespace Whizbang.Transports.FastEndpoints.Integration.Tests.RestLens;

/// <summary>
/// The REST lens endpoints the generator writes, compiled and run (#1194). EnableFiltering,
/// EnableSorting and EnablePaging each switch their capability on or off, and a request the endpoint
/// cannot honor is a 400 rather than results the caller did not ask for.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.FastEndpoints.Generators/RestLensEndpointGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.FastEndpoints/Endpoints/LensQueryShaping.cs</code-under-test>
/// <docs>apis/rest/filtering</docs>
[Category("Integration")]
[Category("FastEndpoints")]
public class GeneratedRestLensEndpointTests {
  private static Guid _id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

  private static readonly InvoiceModel[] _invoices = [
    new() { Id = _id(1), Customer = "Acme", Amount = 10m, Status = InvoiceStatus.Open, Priority = 1 },
    new() { Id = _id(2), Customer = "Acme", Amount = 30m, Status = InvoiceStatus.Closed, Priority = 2 },
    new() { Id = _id(3), Customer = "Beta", Amount = 20m, Status = InvoiceStatus.Open, Priority = null },
    new() { Id = _id(4), Customer = "Gamma", Amount = 30m, Status = InvoiceStatus.Open, Priority = 3 },
    new() { Id = _id(5), Customer = "Beta", Amount = 5m, Status = InvoiceStatus.Closed, Priority = 1 },
  ];

  private static async Task<InMemoryInvoiceLens> _lensAsync() {
    var db = new InvoiceDbContext(new DbContextOptionsBuilder<InvoiceDbContext>()
      .UseInMemoryDatabase($"invoices-{Guid.NewGuid():N}")
      .Options);
    foreach (var invoice in _invoices) {
      db.Add(new PerspectiveRow<InvoiceModel> {
        Id = invoice.Id,
        Data = invoice,
        Metadata = new PerspectiveMetadata { EventType = "InvoiceRecorded", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
        Scope = new PerspectiveScope(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Version = 1,
      });
    }

    await db.SaveChangesAsync();
    return new InMemoryInvoiceLens(db);
  }

  private static async Task<LensResponse<InvoiceModel>> _getAsync(LensRequest request) {
    var endpoint = Factory.Create<InvoiceLensEndpoint>(await _lensAsync());
    await endpoint.HandleAsync(request, CancellationToken.None);
    return endpoint.Response;
  }

  /// <summary>The invoice numbers a response carries, in order, as "1,2,3".</summary>
  private static string _numbers(LensResponse<InvoiceModel> response) =>
    string.Join(",", response.Data.Select(i => int.Parse(i.Id.ToString()[^12..], System.Globalization.CultureInfo.InvariantCulture)));

  [Test]
  public async Task NoParameters_ReturnsTheFirstPageInIdOrderAsync() {
    var response = await _getAsync(new LensRequest());

    await Assert.That(_numbers(response)).IsEqualTo("1,2");
    await Assert.That(response.TotalCount).IsEqualTo(5);
    await Assert.That(response.PageSize).IsEqualTo(2);
  }

  [Test]
  [Arguments("customer", "Beta", "3,5", 2)]
  [Arguments("CUSTOMER", "Beta", "3,5", 2)]
  [Arguments("status", "closed", "2,5", 2)]
  [Arguments("amount", "30", "2,4", 2)]
  [Arguments("priority", "1", "1,5", 2)]
  public async Task Filter_NarrowsToMatchingRowsAsync(string field, string value, string expected, int count) {
    var response = await _getAsync(new LensRequest { PageSize = 10, Filter = new() { [field] = value } });

    await Assert.That(_numbers(response)).IsEqualTo(expected);
    await Assert.That(response.TotalCount).IsEqualTo(count)
      .Because("the total counts the filtered rows, not the table");
  }

  [Test]
  public async Task Filters_AreAndedTogetherAsync() {
    var response = await _getAsync(new LensRequest {
      PageSize = 10,
      Filter = new() { ["customer"] = "Acme", ["status"] = "Open" },
    });

    await Assert.That(_numbers(response)).IsEqualTo("1");
  }

  [Test]
  public async Task Sort_OrdersByEachKeyInTurnAsync() {
    var response = await _getAsync(new LensRequest { PageSize = 10, Sort = "-amount,customer" });

    await Assert.That(_numbers(response)).IsEqualTo("2,4,3,1,5");
  }

  [Test]
  public async Task Sort_BreaksRemainingTiesByIdAsync() {
    var response = await _getAsync(new LensRequest { PageSize = 10, Sort = "status" });

    await Assert.That(_numbers(response)).IsEqualTo("1,3,4,2,5");
  }

  [Test]
  public async Task Paging_ReturnsTheRequestedPageWithinTheMaximumAsync() {
    var second = await _getAsync(new LensRequest { Page = 2 });
    var capped = await _getAsync(new LensRequest { PageSize = 500 });

    await Assert.That(_numbers(second)).IsEqualTo("3,4");
    await Assert.That(capped.PageSize).IsEqualTo(10).Because("MaxPageSize caps what a caller can ask for");
  }

  [Test]
  [Arguments("colour", "red")]
  [Arguments("amount", "lots")]
  [Arguments("status", "Pending")]
  public async Task Filter_TheEndpointCannotHonor_IsABadRequestAsync(string field, string value) {
    await Assert.That(async () => await _getAsync(new LensRequest { Filter = new() { [field] = value } }))
      .Throws<ValidationFailureException>();
  }

  [Test]
  public async Task Sort_OnAFieldTheEndpointDoesNotOffer_IsABadRequestAsync() {
    await Assert.That(async () => await _getAsync(new LensRequest { Sort = "colour" }))
      .Throws<ValidationFailureException>();
  }

  [Test]
  public async Task EverythingDisabled_IgnoresFilterSortAndPageAsync() {
    var endpoint = Factory.Create<PlainInvoiceLensEndpoint>(await _lensAsync());

    await endpoint.HandleAsync(new LensRequest {
      Page = 3,
      PageSize = 1,
      Sort = "-amount",
      Filter = new() { ["customer"] = "Beta" },
    }, CancellationToken.None);

    var response = endpoint.Response;
    await Assert.That(_numbers(response)).IsEqualTo("1,2,3,4,5")
      .Because("a lens with every capability off returns every row in Id order whatever is asked");
    await Assert.That(response.TotalCount).IsEqualTo(5);
    await Assert.That(response.Page).IsEqualTo(1);
    await Assert.That(response.PageSize).IsEqualTo(5);
  }
}
