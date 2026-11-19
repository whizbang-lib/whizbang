// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The route id and the slim form exist twice, in SQL on every write path (migration 285) and in C#
/// (<see cref="RouteNormalizer"/>, <see cref="EnvelopeMetadata.ToSlim"/>). One corpus of real hops runs through both,
/// and each entry must agree (#1725).
/// </summary>
/// <docs>data/event-store#routes-and-traces</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/285_StoredMetadataIsSlimAndStaged.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StoredMetadataRouteCorpusTests : EFCoreTestBase {
  [Test]
  public async Task EveryCorpusEntry_HasTheSameRouteId_InSqlAndInCSharpAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    foreach (var entry in StoredMetadataCorpus.Entries) {
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT _wh_route_text(split_part(@type, ',', 1), @hops::jsonb), _wh_route_id(split_part(@type, ',', 1), @hops::jsonb)";
      cmd.Parameters.AddWithValue("type", entry.EventType);
      cmd.Parameters.AddWithValue("hops", NpgsqlDbType.Text, StoredMetadataCorpus.HopsJson(entry.Hops));
      await using var reader = await cmd.ExecuteReaderAsync();
      await reader.ReadAsync();

      await Assert.That(reader.GetString(0)).IsEqualTo(RouteNormalizer.CanonicalText(entry.EventType, entry.Hops))
        .Because($"corpus entry '{entry.Name}': the canonical text is the contract both sides hash");
      await Assert.That(reader.GetGuid(1)).IsEqualTo(RouteNormalizer.RouteId(entry.EventType, entry.Hops))
        .Because($"corpus entry '{entry.Name}': a route found in C# and one stamped in SQL must be the same route");
    }
  }

  [Test]
  public async Task SlimForm_InSql_ReadsLikeToSlim_ForEveryCorpusEntryAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();

    foreach (var entry in StoredMetadataCorpus.Entries) {
      var full = new EnvelopeMetadata { MessageId = Whizbang.Core.ValueObjects.MessageId.New(), Hops = entry.Hops };
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = "SELECT _wh_slim_event_metadata(jsonb_build_object('MessageId', @id::text, 'Hops', @hops::jsonb), @rid)::text";
      cmd.Parameters.AddWithValue("id", full.MessageId.Value.ToString());
      cmd.Parameters.AddWithValue("hops", NpgsqlDbType.Text, StoredMetadataCorpus.HopsJson(entry.Hops));
      cmd.Parameters.AddWithValue("rid", RouteNormalizer.RouteId(entry.EventType, entry.Hops));
      var sqlSlim = StoredMetadataCorpus.Read((string)(await cmd.ExecuteScalarAsync())!);
      var csharpSlim = full.ToSlim();

      await Assert.That(sqlSlim.MessageId).IsEqualTo(full.MessageId);
      await Assert.That(sqlSlim.Hops.Count).IsEqualTo(csharpSlim.Hops.Count).Because($"corpus entry '{entry.Name}'");
      if (csharpSlim.Hops.Count == 0) {
        continue;
      }
      var (sqlHop, csharpHop) = (sqlSlim.Hops[0], csharpSlim.Hops[0]);
      await Assert.That(sqlHop.Timestamp).IsEqualTo(csharpHop.Timestamp).Because($"corpus entry '{entry.Name}'");
      await Assert.That(sqlHop.CorrelationId).IsEqualTo(csharpHop.CorrelationId).Because($"corpus entry '{entry.Name}'");
      await Assert.That(sqlHop.CausationId).IsEqualTo(csharpHop.CausationId).Because($"corpus entry '{entry.Name}'");
      await Assert.That(sqlHop.StreamId).IsEqualTo(csharpHop.StreamId).Because($"corpus entry '{entry.Name}'");
      await Assert.That(sqlHop.TraceParent).IsEqualTo(csharpHop.TraceParent).Because($"corpus entry '{entry.Name}'");
      await Assert.That(sqlHop.ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown)
        .Because("the slim hop names no instance, which most of a hop's bytes are");
      await Assert.That(StoredMetadataCorpus.Describe(EnvelopeContextExtractor.ExtractScope(sqlSlim.Hops)))
        .IsEqualTo(StoredMetadataCorpus.Describe(EnvelopeContextExtractor.ExtractScope(entry.Hops)))
        .Because($"corpus entry '{entry.Name}': the folded scope reads as the full hops' scope does");
    }
  }

  [Test]
  public async Task SlimForm_FoldsSeveralCurrentHops_AndTheControlEntryHasSeveralAsync() {
    // Control: the fold is only proven by an entry with more than one scoped Current hop, so the corpus must hold one.
    var scoped = StoredMetadataCorpus.Entries.Single(e => e.Name == "causation-and-folded-scopes").Hops
      .Count(h => h.Type == HopType.Current && h.Scope is not null);
    await Assert.That(scoped).IsGreaterThanOrEqualTo(3);

    var describe = StoredMetadataCorpus.Describe(EnvelopeContextExtractor.ExtractScope(
      StoredMetadataCorpus.Entries.Single(e => e.Name == "causation-and-folded-scopes").Hops));
    await Assert.That(describe).Contains("auditor,writer").Because("the last hop removed reader and added auditor");
    await Assert.That(describe).Contains("iss=issuer").Because("claims fold by remove then add");
  }
}
