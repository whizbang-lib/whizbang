using System.Runtime.CompilerServices;

// Testing Support — allow test assemblies to access internal test hooks
// (e.g., PgSharedNotifyConnection.RegistryForTesting + IsConnectionOpenForTesting).
[assembly: InternalsVisibleTo("Whizbang.Core.Tests")]
[assembly: InternalsVisibleTo("Whizbang.Data.Postgres.Tests")]
[assembly: InternalsVisibleTo("Whizbang.Data.EFCore.Postgres.Tests")]
[assembly: InternalsVisibleTo("Whizbang.Data.Dapper.Postgres.Tests")]
