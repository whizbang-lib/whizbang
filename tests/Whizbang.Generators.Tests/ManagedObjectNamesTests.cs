// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using ManagedObjectNames = shared::Whizbang.Generators.Shared.Utilities.ManagedObjectNames;
using PerspectiveIndexSql = shared::Whizbang.Generators.Shared.Models.PerspectiveIndexSql;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generator declares, as managed objects, exactly the indexes and constraints a perspective's generated
/// DDL creates, read from that DDL so the declaration cannot drift from what the schema pass builds.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
public class ManagedObjectNamesTests {
  [Test]
  public async Task AnEnsuredIndex_IsDeclaredByItsNameAsync() {
    var sql = PerspectiveIndexSql.Ensure(
      "CREATE INDEX IF NOT EXISTS idx_job_status ON \"public\".wh_per_job ((data ->> 'Status'));", "\"public\"");

    await Assert.That(ManagedObjectNames.Extract(sql)).IsEquivalentTo([("index", "idx_job_status")]);
  }

  [Test]
  public async Task UniqueQuotedAndConcurrentIndexes_AreDeclaredUnquotedAsync() {
    const string sql = """
      CREATE UNIQUE INDEX IF NOT EXISTS "idx_job_code" ON public.wh_per_job (code);
      CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_job_owner ON public.wh_per_job (owner);
      create index idx_job_lower on public.wh_per_job (lower(code));
      """;

    await Assert.That(ManagedObjectNames.Extract(sql)).IsEquivalentTo([
      ("index", "idx_job_code"), ("index", "idx_job_owner"), ("index", "idx_job_lower"),
    ]);
  }

  [Test]
  public async Task AConstraintAddedInAGuardedBlock_IsDeclaredAsync() {
    const string sql = """
      DO $$ BEGIN
        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_wh_per_job_code_len') THEN
          ALTER TABLE public.wh_per_job ADD CONSTRAINT ck_wh_per_job_code_len CHECK (length(code) <= 50) NOT VALID;
        END IF;
      END $$;
      """;

    await Assert.That(ManagedObjectNames.Extract(sql)).IsEquivalentTo([("constraint", "ck_wh_per_job_code_len")]);
  }

  [Test]
  public async Task ADroppedIndex_IsNotDeclaredAsync() {
    const string sql = """
      DROP INDEX IF EXISTS public.idx_job_status_json;
      CREATE INDEX IF NOT EXISTS idx_job_status ON public.wh_per_job ((data ->> 'Status'));
      """;

    await Assert.That(ManagedObjectNames.Extract(sql)).IsEquivalentTo([("index", "idx_job_status")]);
  }

  [Test]
  public async Task TheSameNameTwice_IsDeclaredOnceAsync() {
    const string sql = """
      CREATE INDEX IF NOT EXISTS idx_job_status ON public.wh_per_job ((data ->> 'Status'));
      CREATE INDEX IF NOT EXISTS idx_job_status ON public.wh_per_job ((data ->> 'Status'));
      """;

    await Assert.That(ManagedObjectNames.Extract(sql)).IsEquivalentTo([("index", "idx_job_status")]);
  }

  [Test]
  public async Task NoDdl_DeclaresNothingAsync() {
    await Assert.That(ManagedObjectNames.Extract("SELECT 1;")).IsEmpty();
  }
}
