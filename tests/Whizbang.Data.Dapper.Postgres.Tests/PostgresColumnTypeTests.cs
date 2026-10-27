// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The one spelling a column type is compared in when the Dapper driver decides how to migrate a changed
/// perspective table (#1826). The database reports a column through <c>format_type</c> and the DDL spells it
/// as the author wrote it; both pass through <see cref="PostgresColumnType.Canonicalize"/>, so an alias and
/// its long form compare equal while a different type, length or precision does not.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/PostgresColumnType.cs</code-under-test>
public class PostgresColumnTypeTests {
  /// <summary>Every DDL alias lands on the spelling <c>format_type</c> reports for the column it creates.</summary>
  [Test]
  [Arguments("TIMESTAMPTZ", "timestamp with time zone")]
  [Arguments("timestamp with time zone", "timestamp with time zone")]
  [Arguments("TIMESTAMP", "timestamp without time zone")]
  [Arguments("TIMESTAMP WITHOUT TIME ZONE", "timestamp without time zone")]
  [Arguments("TIMESTAMPTZ(3)", "timestamp(3) with time zone")]
  [Arguments("TIMESTAMP(6) WITH TIME ZONE", "timestamp(6) with time zone")]
  [Arguments("timestamp(3) without time zone", "timestamp(3) without time zone")]
  [Arguments("TIMETZ", "time with time zone")]
  [Arguments("TIME", "time without time zone")]
  [Arguments("VARCHAR(100)", "character varying(100)")]
  [Arguments("CHARACTER VARYING(100)", "character varying(100)")]
  [Arguments("VARCHAR", "character varying")]
  [Arguments("CHAR(3)", "character(3)")]
  [Arguments("BPCHAR(3)", "character(3)")]
  [Arguments("CHAR", "character(1)")]
  [Arguments("CHARACTER", "character(1)")]
  [Arguments("INT", "integer")]
  [Arguments("INT4", "integer")]
  [Arguments("INTEGER", "integer")]
  [Arguments("SERIAL", "integer")]
  [Arguments("SERIAL4", "integer")]
  [Arguments("BIGINT", "bigint")]
  [Arguments("INT8", "bigint")]
  [Arguments("BIGSERIAL", "bigint")]
  [Arguments("SERIAL8", "bigint")]
  [Arguments("SMALLINT", "smallint")]
  [Arguments("INT2", "smallint")]
  [Arguments("SMALLSERIAL", "smallint")]
  [Arguments("SERIAL2", "smallint")]
  [Arguments("DECIMAL", "numeric")]
  [Arguments("DECIMAL(10, 2)", "numeric(10,2)")]
  [Arguments("NUMERIC ( 10 , 2 )", "numeric(10,2)")]
  [Arguments("DOUBLE PRECISION", "double precision")]
  [Arguments("FLOAT8", "double precision")]
  [Arguments("FLOAT", "double precision")]
  [Arguments("REAL", "real")]
  [Arguments("FLOAT4", "real")]
  [Arguments("BOOL", "boolean")]
  [Arguments("BOOLEAN", "boolean")]
  [Arguments("UUID", "uuid")]
  [Arguments("JSONB", "jsonb")]
  [Arguments("TEXT", "text")]
  [Arguments("TEXT[]", "text[]")]
  [Arguments("REAL[]", "real[]")]
  [Arguments("INT[][]", "integer[]")]
  [Arguments("INTEGER[3]", "integer[]")]
  [Arguments("VARCHAR(20)[]", "character varying(20)[]")]
  [Arguments("vector(1536)", "vector(1536)")]
  [Arguments("public.vector(1536)", "vector(1536)")]
  [Arguments("\"public\".\"vector\"(3)", "vector(3)")]
  [Arguments("  Double   Precision  ", "double precision")]
  [Arguments("VARCHAR(10", "character varying(10)")]
  public async Task Canonicalize_AnAliasOrItsLongForm_IsTheFormatTypeSpellingAsync(string typeName, string expected) {
    await Assert.That(PostgresColumnType.Canonicalize(typeName)).IsEqualTo(expected);
  }

  /// <summary>
  /// A real type change is still a change: a different base type, length, precision, time zone handling or
  /// array-ness never canonicalizes to the same spelling, so the migration is still classified as destructive.
  /// </summary>
  [Test]
  [Arguments("TEXT", "INTEGER")]
  [Arguments("TEXT", "JSONB")]
  [Arguments("INT", "BIGINT")]
  [Arguments("REAL", "DOUBLE PRECISION")]
  [Arguments("VARCHAR(100)", "VARCHAR(200)")]
  [Arguments("VARCHAR(100)", "TEXT")]
  [Arguments("NUMERIC(10,2)", "NUMERIC(12,2)")]
  [Arguments("TIMESTAMP", "TIMESTAMPTZ")]
  [Arguments("TIMESTAMPTZ(3)", "TIMESTAMPTZ")]
  [Arguments("TEXT", "TEXT[]")]
  [Arguments("CHAR(2)", "CHAR")]
  public async Task Canonicalize_DifferentTypes_StayDifferentAsync(string left, string right) {
    await Assert.That(PostgresColumnType.Canonicalize(left)).IsNotEqualTo(PostgresColumnType.Canonicalize(right));
  }

  /// <summary>A canonical spelling is a fixed point, so canonicalizing either side twice changes nothing.</summary>
  [Test]
  [Arguments("TIMESTAMPTZ(3)")]
  [Arguments("VARCHAR(20)[]")]
  [Arguments("CHAR")]
  [Arguments("public.vector(3)")]
  public async Task Canonicalize_IsIdempotentAsync(string typeName) {
    var once = PostgresColumnType.Canonicalize(typeName);
    await Assert.That(PostgresColumnType.Canonicalize(once)).IsEqualTo(once);
  }
}
