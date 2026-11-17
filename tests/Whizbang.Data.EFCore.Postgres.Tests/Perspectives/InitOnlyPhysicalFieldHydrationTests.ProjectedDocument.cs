// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// #2304: a query whose result is the projected document of a Split model (<c>Select(r =&gt; r.Data)</c>, the shape an
/// <c>IQueryable</c> composer returns) carries the promoted fields with their column values, as a whole-row read does.
/// It materialized only the document, which in Split mode no longer holds them, so they came back empty. The projection
/// is rewritten to read the columns beside the document and copy them in, when what follows it is paging or an element
/// operator. The registrations are the project's generated ones.
/// </summary>
public partial class InitOnlyPhysicalFieldHydrationTests {
  private WorkCoordinationDbContext _queryContext() {
    _registerGeneratedHydrators();
    var options = new DbContextOptionsBuilder<WorkCoordinationDbContext>(DbContextOptions)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;
    return new WorkCoordinationDbContext(options);
  }

  private async Task<Guid> _writeSplitRecordAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, _event(streamId));
    await using var writeContext = CreateDbContext();
    var provider = _services<InitOnlySplitPerspective>();
    var runner = new InitOnlySplitPerspectiveRunner(
        provider, provider.GetRequiredService<ILogger<InitOnlySplitPerspectiveRunner>>(), eventStore,
        new EFCorePostgresPerspectiveStore<InitOnlySplitModel>(writeContext, SPLIT_TABLE),
        provider.GetRequiredService<IServiceScopeFactory>());
    await runner.RunAsync(streamId, SPLIT_TABLE, null, CancellationToken.None);
    return streamId;
  }

  [Test]
  public async Task SplitRecord_AProjectedDocumentRead_CarriesThePromotedFieldsAsync() {
    var streamId = await _writeSplitRecordAsync();
    await using var context = _queryContext();

    var models = await context.Set<PerspectiveRow<InitOnlySplitModel>>()
      .Where(r => r.Id == streamId).Select(r => r.Data).ToListAsync();

    await Assert.That(models.Count).IsEqualTo(1);
    await Assert.That(models[0].Status).IsEqualTo("active")
      .Because("a Split field lives only in its column, so the projected document must be read with it");
    await Assert.That(models[0].Priority).IsEqualTo(7);
    await Assert.That(models[0].Note).IsEqualTo("document").Because("the fields that live in the document are kept");
  }

  [Test]
  public async Task SplitRecord_AProjectedDocumentRead_WithASortAndPaging_CarriesThePromotedFieldsAsync() {
    var streamId = await _writeSplitRecordAsync();
    await using var context = _queryContext();

    var models = await context.Set<PerspectiveRow<InitOnlySplitModel>>()
      .Where(r => r.Id == streamId).OrderBy(r => r.Id).Select(r => r.Data).Skip(0).Take(5).ToListAsync();

    await Assert.That(models.Single().Status).IsEqualTo("active")
      .Because("paging after the projection is evaluated in the database, and the copy still runs on each result");
  }

  [Test]
  public async Task SplitRecord_AProjectedFirstOrDefault_CarriesThePromotedFieldsAsync() {
    var streamId = await _writeSplitRecordAsync();
    await using var context = _queryContext();

    var model = await context.Set<PerspectiveRow<InitOnlySplitModel>>()
      .Where(r => r.Id == streamId).Select(r => r.Data).FirstOrDefaultAsync();

    await Assert.That(model!.Priority).IsEqualTo(7);
  }

  [Test]
  public async Task SplitRecord_ACountOverTheProjection_IsUnaffectedAsync() {
    var streamId = await _writeSplitRecordAsync();
    await using var context = _queryContext();

    var count = await context.Set<PerspectiveRow<InitOnlySplitModel>>()
      .Where(r => r.Id == streamId).Select(r => r.Data).CountAsync();

    await Assert.That(count).IsEqualTo(1);
  }

  [Test]
  public async Task SplitClass_WithAnInitOnlyPromotedField_AProjectedDocumentRead_CarriesItAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, _event(streamId));
    await using (var writeContext = CreateDbContext()) {
      var provider = _services<InitOnlySplitClassPerspective>();
      var runner = new InitOnlySplitClassPerspectiveRunner(
          provider, provider.GetRequiredService<ILogger<InitOnlySplitClassPerspectiveRunner>>(), eventStore,
          new EFCorePostgresPerspectiveStore<InitOnlySplitClassModel>(writeContext, SPLIT_CLASS_TABLE),
          provider.GetRequiredService<IServiceScopeFactory>());
      await runner.RunAsync(streamId, SPLIT_CLASS_TABLE, null, CancellationToken.None);
    }
    await using var context = _queryContext();

    var model = await context.Set<PerspectiveRow<InitOnlySplitClassModel>>()
      .Where(r => r.Id == streamId).Select(r => r.Data).SingleAsync();

    await Assert.That(model.Status).IsEqualTo("active").Because("an init-only field of a class is copied into a new instance");
    await Assert.That(model.Priority).IsEqualTo(7);
    await Assert.That(model.Note).IsEqualTo("document");
  }

  [Test]
  public async Task ExtractedRecord_AProjectedDocumentRead_IsUnchanged_TheDocumentHoldsTheFieldsAsync() {
    var streamId = Guid.CreateVersion7();
    var eventStore = new InMemoryEventStore();
    await _appendAsync(eventStore, _event(streamId));
    await using (var writeContext = CreateDbContext()) {
      var provider = _services<InitOnlyExtractedPerspective>();
      var runner = new InitOnlyExtractedPerspectiveRunner(
          provider, provider.GetRequiredService<ILogger<InitOnlyExtractedPerspectiveRunner>>(), eventStore,
          new EFCorePostgresPerspectiveStore<InitOnlyExtractedModel>(writeContext, EXTRACTED_TABLE),
          provider.GetRequiredService<IServiceScopeFactory>());
      await runner.RunAsync(streamId, EXTRACTED_TABLE, null, CancellationToken.None);
    }
    await _executeAsync($"UPDATE {EXTRACTED_TABLE} SET status = 'from-column' WHERE id = @id", streamId);
    await using var context = _queryContext();

    var model = await context.Set<PerspectiveRow<InitOnlyExtractedModel>>()
      .Where(r => r.Id == streamId).Select(r => r.Data).SingleAsync();

    await Assert.That(model.Status).IsEqualTo("active")
      .Because("an Extracted document already holds its fields, so its projection is not rewritten");
  }

  [Test]
  public async Task SplitRecord_AnOperatorAfterTheProjectionThatIsNotPaging_KeepsTodaysBehaviorAsync() {
    var streamId = await _writeSplitRecordAsync();
    await using var context = _queryContext();

    var models = await context.Set<PerspectiveRow<InitOnlySplitModel>>()
      .Where(r => r.Id == streamId).Select(r => r.Data).Where(m => m.Note == "document").ToListAsync();

    await Assert.That(models.Single().Note).IsEqualTo("document")
      .Because("a filter over the model after the projection cannot follow a client-evaluated copy, so the query is left "
        + "for the database to translate as before; moving such a filter beneath the projection is #2302's");
  }
}
