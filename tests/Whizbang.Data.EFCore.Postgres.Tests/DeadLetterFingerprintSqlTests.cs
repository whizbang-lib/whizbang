using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Slice 2 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis) — locks the
/// SQL fingerprint utility introduced in migration 053:
/// <c>compute_dead_letter_fingerprint(p_error_text TEXT)</c> and
/// <c>current_dead_letter_fingerprint_version()</c>.
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>Algorithm version 1: SHA256 of <c>{type}:{frame1}:{frame2}:{frame3}</c>
/// (concatenated with literal colons), truncated to the first 16 lowercase hex characters.</description></item>
/// <item><description>Type token: first dotted PascalCase identifier on line 1 (matches
/// <c>InvalidOperationException</c>, <c>System.NullReferenceException</c>, etc.).</description></item>
/// <item><description>Frame extraction: lines matching <c>^\s+at\s+(\S+)\s</c>; excluding
/// framework namespaces (<c>Microsoft.*</c>, <c>System.*</c>, <c>Npgsql.*</c>) and the Whizbang
/// catch-and-forward sites (<c>Whizbang.Core.Workers.*</c>, <c>Whizbang.Core.Messaging.Internal.*</c>).</description></item>
/// <item><description>Framework-frame drift (different Npgsql / System.Threading versions in the
/// stack) MUST NOT shift the fingerprint — only the first surviving in-app frame matters.</description></item>
/// <item><description>NULL input → NULL output (lets the column NULLability flow through naturally
/// when error_text is somehow absent).</description></item>
/// <item><description><c>current_dead_letter_fingerprint_version()</c> returns 1 — Slice 6's
/// version-aware aggregation predicate keys off this so a single integer bump rehashes
/// every row on the next maintenance tick.</description></item>
/// </list>
///
/// <para>This is the canonical algorithm spec. Bumping the version (slice 6+) requires
/// updating <c>current_dead_letter_fingerprint_version()</c> AND the test corpus below
/// AND adding a regression test that proves the version-aware UPDATE in
/// <c>aggregate_dead_letters</c> recomputes old rows.</para>
/// </summary>
/// <docs>operations/dead-letter-queue/error-fingerprinting</docs>
[Category("Shard3")]
public class DeadLetterFingerprintSqlTests : EFCoreTestBase {

  private const string _typicalStack = """
    System.InvalidOperationException: Could not open connection to 'appservice_db'
       at Whizbang.Data.EFCore.Postgres.Functions.OutboxClaim.LeaseAsync(Guid instanceId)
       at Whizbang.Core.Workers.OutboxDrainWorker.InvokeOutboxLifecycleStageAsync()
       at Whizbang.Core.Workers.OutboxDrainWorker.ExecuteAsync(CancellationToken ct)
       at System.Threading.Tasks.Task.RunContinuations()
    """;

  // Same in-app frame as _typicalStack but different framework versions interleaved —
  // captures the "Npgsql patched, .NET hotfixed" scenario that would otherwise
  // fragment a single root cause across many fingerprint clusters.
  private const string _typicalStackDifferentFramework = """
    System.InvalidOperationException: Could not open connection to 'appservice_db'
       at Npgsql.NpgsqlConnection.OpenAsync(CancellationToken token)
       at System.Threading.Tasks.ValueTask.GetResult()
       at Whizbang.Data.EFCore.Postgres.Functions.OutboxClaim.LeaseAsync(Guid instanceId)
       at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.OpenAsync()
       at System.Threading.Tasks.Task.RunContinuations()
    """;

  // Same exception type, DIFFERENT first in-app frame — must produce a different fingerprint.
  private const string _typicalStackDifferentInAppFrame = """
    System.InvalidOperationException: Could not open connection to 'appservice_db'
       at Whizbang.Data.EFCore.Postgres.Functions.InboxDispatch.ClaimAsync(Guid instanceId)
       at Whizbang.Core.Workers.InboxDispatchWorker.ExecuteAsync(CancellationToken ct)
    """;

  // Different exception type, same first in-app frame — must produce a different fingerprint.
  private const string _typicalStackDifferentExceptionType = """
    System.NullReferenceException: Object reference not set to an instance of an object
       at Whizbang.Data.EFCore.Postgres.Functions.OutboxClaim.LeaseAsync(Guid instanceId)
       at Whizbang.Core.Workers.OutboxDrainWorker.InvokeOutboxLifecycleStageAsync()
    """;

  // No parseable in-app frames after the type — fingerprint should reflect type only.
  private const string _typeOnlyStack = "System.OperationCanceledException: A task was canceled.";

  // --- helpers ---

  private async Task<string?> _computeAsync(string? errorText) {
    await using var dbContext = CreateDbContext();
    await using var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT compute_dead_letter_fingerprint(@error_text)";
    var p = cmd.CreateParameter();
    p.ParameterName = "error_text";
    p.Value = (object?)errorText ?? DBNull.Value;
    cmd.Parameters.Add(p);
    var raw = await cmd.ExecuteScalarAsync();
    return raw is DBNull or null ? null : (string)raw;
  }

  private async Task<short> _currentVersionAsync() {
    await using var dbContext = CreateDbContext();
    await using var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT current_dead_letter_fingerprint_version()";
    var raw = await cmd.ExecuteScalarAsync();
    return (short)raw!;
  }

  // --- tests ---

  [Test]
  public async Task ComputeDeadLetterFingerprint_TypicalStack_Returns16CharLowercaseHexAsync() {
    var result = await _computeAsync(_typicalStack);

    await Assert.That(result).IsNotNull()
      .Because("A well-formed exception stack must produce a fingerprint.");
    await Assert.That(result!.Length).IsEqualTo(16)
      .Because("16 hex chars = 8 bytes = 2^64 fingerprint space; first 16 of SHA256 by spec.");
    await Assert.That(result).Matches("^[0-9a-f]{16}$")
      .Because("Algorithm specifies lowercase hex output for operator-friendly GROUP BY listings.");
  }

  [Test]
  public async Task ComputeDeadLetterFingerprint_FrameworkFramesVary_FingerprintStableAsync() {
    var fingerprintA = await _computeAsync(_typicalStack);
    var fingerprintB = await _computeAsync(_typicalStackDifferentFramework);

    await Assert.That(fingerprintB).IsEqualTo(fingerprintA)
      .Because("Algorithm MUST exclude framework frames (Microsoft.*, System.*, Npgsql.*) so .NET patch versions and Npgsql client revs do not fragment a single root cause into many clusters. Both stacks throw from the same OutboxClaim.LeaseAsync — that's the cluster key.");
  }

  [Test]
  public async Task ComputeDeadLetterFingerprint_DifferentInAppFrame_DifferentFingerprintAsync() {
    var fingerprintA = await _computeAsync(_typicalStack);
    var fingerprintC = await _computeAsync(_typicalStackDifferentInAppFrame);

    await Assert.That(fingerprintC).IsNotEqualTo(fingerprintA)
      .Because("Different first in-app frame = different root cause = different fingerprint. OutboxClaim.LeaseAsync vs InboxDispatch.ClaimAsync are two distinct bugs and operators must see them as distinct clusters.");
  }

  [Test]
  public async Task ComputeDeadLetterFingerprint_DifferentExceptionType_DifferentFingerprintAsync() {
    var fingerprintA = await _computeAsync(_typicalStack);
    var fingerprintD = await _computeAsync(_typicalStackDifferentExceptionType);

    await Assert.That(fingerprintD).IsNotEqualTo(fingerprintA)
      .Because("Same code site, different thrown type (InvalidOperation vs NullReference) is two distinct failure modes. Type token is the leading component of the fingerprint string for exactly this reason.");
  }

  [Test]
  public async Task ComputeDeadLetterFingerprint_TypeOnlyStack_ReturnsHashOfTypeAsync() {
    var result = await _computeAsync(_typeOnlyStack);

    await Assert.That(result).IsNotNull()
      .Because("Even with no parseable in-app frames, the type alone clusters errors meaningfully (e.g. OperationCanceledException from shutdown).");
    await Assert.That(result!.Length).IsEqualTo(16)
      .Because("Type-only fingerprint still produces the canonical 16-char hex output.");
  }

  [Test]
  public async Task ComputeDeadLetterFingerprint_DeterministicAsync() {
    var first = await _computeAsync(_typicalStack);
    var second = await _computeAsync(_typicalStack);

    await Assert.That(second).IsEqualTo(first)
      .Because("Pure function: identical input MUST produce identical output. Aggregation, version-aware backfill, and the slice-8 round-trip lock all depend on determinism.");
  }

  [Test]
  public async Task ComputeDeadLetterFingerprint_NullInput_ReturnsNullAsync() {
    var result = await _computeAsync(null);

    await Assert.That(result).IsNull()
      .Because("NULL passthrough: when error_text is somehow NULL, the fingerprint column NULLability flows through naturally rather than producing a spurious 'all-nulls' fingerprint cluster.");
  }

  [Test]
  public async Task CurrentDeadLetterFingerprintVersion_ReturnsOneAsync() {
    var version = await _currentVersionAsync();

    await Assert.That(version).IsEqualTo((short)1)
      .Because("Algorithm v1 is the locked baseline. Bumping requires updating this function body + the C# test corpus + adding a regression test for version-aware backfill in Slice 6's aggregate_dead_letters.");
  }
}
