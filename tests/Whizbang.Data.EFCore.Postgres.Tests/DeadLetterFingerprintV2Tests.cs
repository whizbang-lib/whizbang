using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Locks fingerprint algorithm v2 (P2 of plans/dlq-stack-intelligence.md). The three v1
/// gaps, each measured against production behavior:</para>
/// <list type="number">
///   <item>Async state machines: <c>&lt;Method&gt;d__12.MoveNext</c> carries a compiler-assigned
///   number that changes on recompile, silently splitting a cohort across build generations —
///   the exact identity canary campaigns depend on.</item>
///   <item>Inner exceptions: the outer wrapper type hides the discriminator; the innermost
///   type is the failure's identity.</item>
///   <item>Prose errors (zero stack frames — 100% of the 2026-09-03 corpus): v1 hashed the
///   first PascalCase word; v2 scrubs digits/hex/GUIDs/quoted strings and hashes the template,
///   so placement is deterministic and volatile values can never split a cohort.</item>
/// </list>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/053_DeadLetterFingerprint.sql</code-under-test>
[Category("Shard2")]
public class DeadLetterFingerprintV2Tests : EFCoreTestBase {

  private async Task<string?> _fpAsync(string errorText) {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT compute_dead_letter_fingerprint(@t)";
    cmd.Parameters.AddWithValue("t", errorText);
    return (string?)await cmd.ExecuteScalarAsync();
  }

  [Test]
  public async Task Version_IsTwoAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT current_dead_letter_fingerprint_version()";
    await Assert.That((short)(await cmd.ExecuteScalarAsync() ?? (short)0)).IsEqualTo((short)2)
      .Because("the version function is what drives the maintenance re-hash of stale rows");
  }

  [Test]
  public async Task AsyncStateMachineFrames_NormalizeAcrossRecompilesAsync() {
    var build1 = "System.InvalidOperationException: boom\n"
      + "   at MyApp.Orders.OrderProcessor.<ApplyAsync>d__12.MoveNext()\n"
      + "   at MyApp.Orders.OrderService.<HandleAsync>d__4.MoveNext()";
    var build2 = "System.InvalidOperationException: boom\n"
      + "   at MyApp.Orders.OrderProcessor.<ApplyAsync>d__13.MoveNext()\n"
      + "   at MyApp.Orders.OrderService.<HandleAsync>d__7.MoveNext()";

    await Assert.That(await _fpAsync(build1)).IsEqualTo(await _fpAsync(build2))
      .Because("d__N is compiler-assigned and changes on recompile; a cohort split by a "
             + "rebuild is a canary campaign that loses its own identity mid-flight");
  }

  [Test]
  public async Task InnermostExceptionType_WinsOverTheWrapperAsync() {
    var wrappedTimeout = "MyApp.PipelineException: stage failed\n"
      + " ---> System.TimeoutException: The operation has timed out.\n"
      + "   at MyApp.Data.Repo.QueryAsync()";
    var wrappedNull = "MyApp.PipelineException: stage failed\n"
      + " ---> System.NullReferenceException: Object reference not set.\n"
      + "   at MyApp.Data.Repo.QueryAsync()";

    await Assert.That(await _fpAsync(wrappedTimeout)).IsNotEqualTo(await _fpAsync(wrappedNull))
      .Because("the wrapper is identical plumbing; the innermost type is the actual failure "
             + "— collapsing them under-groups and a passing canary would release the wrong rows");
  }

  [Test]
  public async Task ProseErrors_ScrubVolatileValues_KeepTheTemplateAsync() {
    var a = "Attempt 1 ended without a reported outcome: lease held by instance "
      + "01a064d6-57d0-75e4-86f4-d82890e6e1f2 expired at 2026-09-03 03:58:23.722689+00";
    var b = "Attempt 7 ended without a reported outcome: lease held by instance "
      + "9f00aa11-2233-4455-8677-889900aabbcc expired at 2026-09-04 11:11:11.000000+00";

    await Assert.That(await _fpAsync(a)).IsEqualTo(await _fpAsync(b))
      .Because("GUIDs, counters and timestamps are the volatile part; the TEMPLATE is the "
             + "failure identity");
  }

  [Test]
  public async Task ProseErrors_DifferentTemplates_StayDistinctAsync() {
    var lease = "Attempt 1 ended without a reported outcome: lease held by instance x expired";
    var observed = "Message 'x' has been durably observed 10 times, at or past the 10 bound";

    await Assert.That(await _fpAsync(lease)).IsNotEqualTo(await _fpAsync(observed))
      .Because("v1's first-word heuristic could collapse different prose failures that "
             + "happen to open with the same token — different templates are different bugs");
  }

  [Test]
  public async Task NullInput_StaysNullAsync() {
    await using var ctx = CreateDbContext();
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT compute_dead_letter_fingerprint(NULL) IS NULL";
    await Assert.That((bool)(await cmd.ExecuteScalarAsync() ?? false)).IsTrue();
  }
}
