using Microsoft.EntityFrameworkCore;
using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Custom;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

// A Split perspective in a context of its own, whose storage the tests move between the document and
// columns the way a release does, so the schema pass that moves them is the shipped one. See
// SplitPromotionTests, PhysicalFieldDemotionTests and PhysicalFieldMoveNoticeTests (#1021, #1022).

/// <summary>A Split model whose promoted fields cover each kind of column, and whose other fields stay in the document.</summary>
public static class MovedTicket {
  /// <summary>An enumeration, stored as its number in the column and in the document.</summary>
  public enum Stage { Backlog = 0, Doing = 1, Done = 2 }

  /// <summary>An element of a collection kept in a jsonb column.</summary>
  public sealed class Tag {
    public string Label { get; set; } = "";
    public int Weight { get; set; }
  }

  [PerspectiveStorage(FieldStorageMode.Split)]
  [SuppressIndexAdvisory("test fixture; the moved fields are read back, never filtered on")]
  public class Model {
    [StreamId]
    public Guid Id { get; set; }

    [PhysicalField]
    public string? Title { get; set; }

    [PhysicalField]
    public int Points { get; set; }

    [PhysicalField]
    public Stage Lane { get; set; }

    [PhysicalField(ColumnType = "uuid[]")]
    public List<Guid> Watchers { get; set; } = [];

    [PhysicalField(ColumnType = "jsonb")]
    public List<Tag>? Tags { get; set; }

    [PhysicalField]
    public DateTimeOffset? DueAt { get; set; }

    /// <summary>Kept in the document: a demoted number.</summary>
    public int Score { get; set; }

    /// <summary>Kept in the document: a demoted collection, formerly in a jsonb column.</summary>
    public List<Tag>? Notes { get; set; }

    /// <summary>Kept in the document: a demoted array.</summary>
    public List<string>? Labels { get; set; }

    /// <summary>Kept in the document: a demoted instant.</summary>
    public DateTimeOffset? SeenAt { get; set; }

    /// <summary>Kept in the document: its old column holds a type the document cannot.</summary>
    public string? Spot { get; set; }
  }
}

public record MovedTicketNoted([property: StreamId] Guid Id) : IEvent;

[WhizbangPerspective("physical-moves")]
public class MovedTicketProjection : IPerspectiveFor<MovedTicket.Model, MovedTicketNoted> {
  public MovedTicket.Model Apply(MovedTicket.Model currentData, MovedTicketNoted eventData) => currentData;
}

[WhizbangDbContext("physical-moves", Schema = "public")]
public partial class PhysicalMovesDbContext(DbContextOptions<PhysicalMovesDbContext> options) : DbContext(options) {
}

/// <summary>The database a move test runs against, and the statements that put its table in an earlier release's shape.</summary>
internal static class PhysicalMoves {
  internal const string TABLE = "wh_per_moved_ticket";

  internal static PhysicalMovesDbContext Context(string connectionString) => new(
    new DbContextOptionsBuilder<PhysicalMovesDbContext>()
      .UseNpgsql(connectionString)
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

  /// <summary>One start of the release under test: the schema pass, as an application runs it.</summary>
  internal static async Task StartAsync(string connectionString) {
    await using var context = Context(connectionString);
    await context.EnsureWhizbangDatabaseInitializedAsync();
  }

  /// <summary>Forgets the table's schema hash, so the next start applies its DDL as a release that changed it does.</summary>
  internal static Task ForgetSchemaAsync(string connectionString) =>
    ExecAsync(connectionString, $"DELETE FROM wh_schema_migrations WHERE file_name = 'perspective:{TABLE}';");

  internal static async Task ExecAsync(string connectionString, string sql) {
    await using var db = new NpgsqlConnection(connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  internal static async Task<string> ScalarAsync(string connectionString, string sql) {
    await using var db = new NpgsqlConnection(connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  /// <summary>The number of sync triggers on the table.</summary>
  internal static Task<string> SyncTriggersAsync(string connectionString) => ScalarAsync(connectionString,
    $"SELECT count(*) FROM pg_trigger WHERE tgrelid = '{TABLE}'::regclass AND tgname LIKE 'wh_mv_%'");
}
