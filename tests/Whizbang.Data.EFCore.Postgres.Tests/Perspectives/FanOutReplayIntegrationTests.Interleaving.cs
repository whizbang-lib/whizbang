// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// #2622: a fan-out row is the same live as on replay when its own events and the foreign events that target it
/// interleave. Each interleaving is produced by the order the test runs the two live paths in: the order's runner applying
/// its committed events, and the collective sink applying a committed rename.
/// </summary>
public partial class FanOutReplayIntegrationTests {
  // The sink applied the rename before the order's runner folded OrderPlaced: the update found no row with the customer.
  [Test]
  public async Task Live_TheSinkAppliesARenameBeforeTheOrdersPlacedIsFolded_TheOrderStillTakesIt_AsReplayAsync() {
    await using var sp = _buildServices();
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    await _appendAsync(sp, order, _placed(order, customer, "Acme"));
    await _renameAsync(sp, customer, "Acme Ltd");
    await _applyLiveAsync(sp, order);

    await _assertRebuildIsLiveAsync(sp, [order], expectedName: "Acme Ltd",
      because: "the rename was committed after OrderPlaced, so it lands after it, however late the order's runner ran");
  }

  // The sink applied a rename committed after OrderMoved to the row before the runner folded OrderMoved, whose copied
  // name would otherwise overwrite it.
  [Test]
  public async Task Live_TheSinkAppliesANewerRenameBeforeTheOrdersOlderEventIsFolded_TheRenameStaysLast_AsReplayAsync() {
    await using var sp = _buildServices();
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    await _ownAsync(sp, order, _placed(order, customer, "Acme"));
    await _appendAsync(sp, order, new FanOutReplayOrderMoved { OrderId = order, CustomerId = customer, CustomerName = "Copied" });
    await _renameAsync(sp, customer, "Acme Ltd");
    await _applyLiveAsync(sp, order);

    await _assertRebuildIsLiveAsync(sp, [order], expectedName: "Acme Ltd");
  }

  // The runner folded OrderMoved, committed after the rename, before the sink applied the rename: the update lands last.
  [Test]
  public async Task Live_TheSinkAppliesAnOlderRenameAfterTheOrdersNewerEvent_RewindsTheOrder_AsReplayAsync() {
    await using var sp = _buildServices();
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    await _ownAsync(sp, order, _placed(order, customer, "Acme"));
    var rename = new FanOutReplayCustomerRenamed { CustomerId = customer, Name = "Acme Ltd" };
    var renameId = await _appendAsync(sp, customer, rename);
    var moved = await _ownAsync(sp, order, new FanOutReplayOrderMoved { OrderId = order, CustomerId = customer, CustomerName = "Copied" });
    await _sinkAppliesAsync(sp, customer, rename, renameId);

    await Assert.That((await _rowsAsync(sp, [order]))[order].Name).IsEqualTo("Acme Ltd")
      .Because("the control: the sink's update landed after the order's newer event");
    await Assert.That(await _scalarAsync<Guid>(
        $"SELECT rewind_trigger_event_id FROM wh_perspective_cursors WHERE stream_id = '{order}' AND (status & 32) <> 0"))
      .IsEqualTo(moved).Because("the order is flagged to rewind from its first event committed after the rename");

    await _rewindAsync(sp, order, moved);

    await Assert.That((await _rowsAsync(sp, [order]))[order].Name).IsEqualTo("Copied");
    var live = await _rowsAsync(sp, [order]);
    await _rebuildAsync(sp, [order]);
    await Assert.That(await _rowsAsync(sp, [order])).IsEquivalentTo(live).Because("the rewound row is its rebuild");
  }

  // The sink has not applied a rename yet: a live batch leaves it to the sink, whose update lands after the batch.
  [Test]
  public async Task Live_ARenameTheSinkHasNotAppliedYet_IsLeftToTheSink_AndLandsLastAsync() {
    await using var sp = _buildServices();
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    await _ownAsync(sp, order, _placed(order, customer, "Acme"));
    await _appendAsync(sp, order, new FanOutReplayOrderMoved { OrderId = order, CustomerId = customer, CustomerName = "Copied" });
    var rename = new FanOutReplayCustomerRenamed { CustomerId = customer, Name = "Acme Ltd" };
    var renameId = await _appendAsync(sp, customer, rename);
    // Its sink row, still pending, as the store's emit chain writes it.
    await _executeAsync(
      "INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at) "
      + $"VALUES (gen_random_uuid(), '{customer}', '__collective__', '{renameId}', 0, 1, 0, now())");
    await _applyLiveAsync(sp, order);

    await Assert.That((await _rowsAsync(sp, [order]))[order].Name).IsEqualTo("Copied")
      .Because("the rename is still the sink's to apply, so the batch does not fold it");

    await _executeAsync($"DELETE FROM wh_perspective_events WHERE event_id = '{renameId}'");
    await _sinkAppliesAsync(sp, customer, rename, renameId);
    await _assertRebuildIsLiveAsync(sp, [order], expectedName: "Acme Ltd");
  }
}
