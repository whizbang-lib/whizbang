// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Transports.FastEndpoints;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Tests for <see cref="LensQueryShaping"/> and <see cref="InvalidLensRequestException"/>: what a
/// generated REST lens endpoint shapes its query with.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.FastEndpoints/Endpoints/LensQueryShaping.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.FastEndpoints/Endpoints/InvalidLensRequestException.cs</code-under-test>
/// <docs>apis/rest/filtering</docs>
public class LensQueryShapingTests {
  public enum Status { Open, Closed }

  private sealed record Row(int Number, string Name);

  private static readonly Row[] _rows = [new(2, "b"), new(1, "c"), new(2, "a"), new(1, "a")];

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("   ")]
  public async Task ParseSort_NoSort_IsEmptyAsync(string? sort) {
    await Assert.That(LensQueryShaping.ParseSort(sort)).IsEmpty();
  }

  [Test]
  public async Task ParseSort_ReadsDirectionAndOrderAsync() {
    var parsed = LensQueryShaping.ParseSort("-createdAt, name ,+status");

    await Assert.That(parsed).IsEquivalentTo(new[] {
      new SortExpression("createdAt", true),
      new SortExpression("name", false),
      new SortExpression("status", false),
    });
  }

  [Test]
  public async Task ParseSort_SkipsEntriesWithNoFieldAsync() {
    var parsed = LensQueryShaping.ParseSort("-, + ,,name");

    await Assert.That(parsed).IsEquivalentTo(new[] { new SortExpression("name", false) });
  }

  [Test]
  [Arguments(false, new[] { 1, 1, 2, 2 })]
  [Arguments(true, new[] { 2, 2, 1, 1 })]
  public async Task ThenOrderBy_FirstKey_OrdersByItAsync(bool descending, int[] expected) {
    var ordered = LensQueryShaping.ThenOrderBy(_rows.AsQueryable(), null, r => r.Number, descending);

    await Assert.That(ordered.Select(r => r.Number).ToArray()).IsEquivalentTo(expected);
  }

  [Test]
  [Arguments(false, new[] { "a", "c", "a", "b" })]
  [Arguments(true, new[] { "c", "a", "b", "a" })]
  public async Task ThenOrderBy_NextKey_BreaksTiesOfTheFirstAsync(bool descending, string[] expected) {
    var query = _rows.AsQueryable();
    var first = LensQueryShaping.ThenOrderBy(query, null, r => r.Number, false);

    var ordered = LensQueryShaping.ThenOrderBy(query, first, r => r.Name, descending);

    await Assert.That(ordered.Select(r => r.Name).ToArray()).IsEquivalentTo(expected);
  }

  [Test]
  public async Task ThenOrderBy_NullArguments_ThrowAsync() {
    await Assert.That(() => LensQueryShaping.ThenOrderBy<Row, int>(null!, null, r => r.Number, false))
      .ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => LensQueryShaping.ThenOrderBy<Row, int>(_rows.AsQueryable(), null, null!, false))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Parse_ReadsInTheInvariantCultureAsync() {
    await Assert.That(LensQueryShaping.Parse<decimal>("amount", "1.5")).IsEqualTo(1.5m);
    await Assert.That(LensQueryShaping.Parse<bool>("paid", "true")).IsTrue();
    await Assert.That(LensQueryShaping.Parse<Guid>("id", "6f1c1d1e-0000-0000-0000-000000000001"))
      .IsEqualTo(Guid.Parse("6f1c1d1e-0000-0000-0000-000000000001"));
  }

  [Test]
  [Arguments("not-a-number")]
  [Arguments(null)]
  public async Task Parse_AnUnreadableValue_IsAnInvalidRequestAsync(string? value) {
    await Assert.That(() => LensQueryShaping.Parse<int>("priority", value))
      .ThrowsExactly<InvalidLensRequestException>()
      .WithMessageContaining("filter[priority]");
  }

  [Test]
  [Arguments("Closed", Status.Closed)]
  [Arguments("open", Status.Open)]
  [Arguments("1", Status.Closed)]
  public async Task ParseEnum_ReadsANameOrNumberAsync(string value, Status expected) {
    await Assert.That(LensQueryShaping.ParseEnum<Status>("state", value)).IsEqualTo(expected);
  }

  [Test]
  [Arguments("Pending")]
  [Arguments("7")]
  [Arguments(null)]
  public async Task ParseEnum_AnUndefinedMember_IsAnInvalidRequestAsync(string? value) {
    await Assert.That(() => LensQueryShaping.ParseEnum<Status>("state", value))
      .ThrowsExactly<InvalidLensRequestException>();
  }

  [Test]
  public async Task UnknownField_NamesTheParameterAndFieldAsync() {
    var exception = InvalidLensRequestException.UnknownField("sort", "colour");

    await Assert.That(exception.Message).IsEqualTo("'colour' is not a field this endpoint can sort by.");
  }

  [Test]
  public async Task InvalidValue_NamesTheValueAndFieldAsync() {
    var exception = InvalidLensRequestException.InvalidValue("priority", "high");

    await Assert.That(exception.Message).IsEqualTo("'high' is not a valid value for filter[priority].");
  }

  [Test]
  public async Task Constructors_CarryTheirMessageAndCauseAsync() {
    var cause = new FormatException("bad");

    await Assert.That(new InvalidLensRequestException().Message).IsNotEmpty();
    await Assert.That(new InvalidLensRequestException("why").Message).IsEqualTo("why");
    await Assert.That(new InvalidLensRequestException("why", cause).InnerException).IsSameReferenceAs(cause);
  }
}
