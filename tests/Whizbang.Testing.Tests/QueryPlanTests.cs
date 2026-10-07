// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Testing.Tests;

/// <summary>
/// <see cref="QueryPlan"/> turns an <c>EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)</c> result into assertions a test
/// can state directly, so a query's PLAN can be pinned the way its result already is.
/// </summary>
/// <remarks>
/// <para>
/// Parsing is separated from capture on purpose: everything that decides whether a plan passes is a pure function
/// of the plan's JSON, so every rule here is tested against fixture plans with no database at all. Only
/// <see cref="QueryPlan.CaptureAsync"/> needs one, and it has a single integration test.
/// </para>
/// <para>
/// The amplification fixture is a real plan: the work claim measured on a consuming deployment, which returned 13
/// rows after visiting 79,590,235 shared buffers. Its duration alone reads as contention; its buffer count is what
/// shows the work is real, and that is the signal no correctness test and no plan-text match can see.
/// </para>
/// </remarks>
public class QueryPlanTests {

  // EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) over an index scan that touches almost nothing.
  private const string HEALTHY = """
  [{"Plan":{"Node Type":"Index Scan","Relation Name":"wh_inbox_state","Index Name":"idx_inbox_state_pending",
    "Actual Rows":12,"Actual Loops":1,"Actual Total Time":0.41,
    "Shared Hit Blocks":34,"Shared Read Blocks":0,"Shared Dirtied Blocks":0,"Shared Written Blocks":0}}]
  """;

  // The real shape this harness exists to catch.
  private const string AMPLIFIED = """
  [{"Plan":{"Node Type":"Function Scan","Relation Name":null,"Function Name":"claim_work",
    "Actual Rows":13,"Actual Loops":1,"Actual Total Time":167480.886,
    "Shared Hit Blocks":79590235,"Shared Read Blocks":6,"Shared Dirtied Blocks":131,"Shared Written Blocks":2}}]
  """;

  // A nested plan: the sequential scan is two levels down, inside a subplan array.
  private const string NESTED_SEQ_SCAN = """
  [{"Plan":{"Node Type":"Aggregate","Actual Rows":1,"Actual Loops":1,"Actual Total Time":940.0,
    "Shared Hit Blocks":5,"Shared Read Blocks":0,
    "Plans":[{"Node Type":"Nested Loop","Actual Rows":7,"Actual Loops":1,"Actual Total Time":938.0,
      "Shared Hit Blocks":4,"Shared Read Blocks":0,
      "Plans":[{"Node Type":"Seq Scan","Relation Name":"wh_event_store","Actual Rows":480000,"Actual Loops":3,
        "Actual Total Time":930.0,"Shared Hit Blocks":120000,"Shared Read Blocks":12},
       {"Node Type":"Index Only Scan","Relation Name":"wh_digest_epoch_frontiers",
        "Index Name":"pk_digest_epoch_frontiers","Actual Rows":7,"Actual Loops":1,"Actual Total Time":0.1,
        "Shared Hit Blocks":2,"Shared Read Blocks":0}]}]}}]
  """;

  // EXPLAIN run without BUFFERS: the buffer keys are simply absent.
  private const string NO_BUFFERS = """
  [{"Plan":{"Node Type":"Seq Scan","Relation Name":"wh_outbox","Actual Rows":3,"Actual Loops":1,
    "Actual Total Time":1.2}}]
  """;

  // "Plans" present but not an array: a shape the parser must step over rather than trust.
  private const string PLANS_NOT_AN_ARRAY = """
  [{"Plan":{"Node Type":"Result","Actual Rows":1,"Actual Loops":1,"Actual Total Time":0.01,
    "Shared Hit Blocks":1,"Shared Read Blocks":0,"Plans":{"Node Type":"Seq Scan","Relation Name":"wh_outbox"}}}]
  """;

  // A non-numeric timing: present, but not a number.
  private const string TIME_NOT_A_NUMBER = """
  [{"Plan":{"Node Type":"Result","Actual Rows":1,"Actual Loops":1,"Actual Total Time":"unavailable",
    "Shared Hit Blocks":1,"Shared Read Blocks":0}}]
  """;

  // A node with no "Node Type" at all. EXPLAIN always writes one, so this stands for a future or truncated
  // plan shape: it must not crash the parse and must not read as a node type the planner never named.
  private const string NODE_WITHOUT_A_TYPE = """
  [{"Plan":{"Relation Name":"wh_outbox","Actual Rows":2,"Actual Loops":1,"Actual Total Time":0.2,
    "Shared Hit Blocks":2,"Shared Read Blocks":0}}]
  """;

  // A plan that reads no index at all, so the failure has nothing to list.
  private const string NO_INDEX_AT_ALL = """
  [{"Plan":{"Node Type":"Seq Scan","Relation Name":"wh_outbox","Actual Rows":4,"Actual Loops":1,
    "Actual Total Time":9.0,"Shared Hit Blocks":90,"Shared Read Blocks":0}}]
  """;

  private static readonly string[] _nestedNodeTypes =
    ["Aggregate", "Nested Loop", "Seq Scan", "Index Only Scan"];

  // ── parsing ───────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Parse_ReadsTheRootTotalsAsync() {
    var plan = QueryPlan.Parse(HEALTHY);

    await Assert.That(plan.SharedBuffersHit).IsEqualTo(34L);
    await Assert.That(plan.SharedBuffersRead).IsEqualTo(0L);
    await Assert.That(plan.ActualRows).IsEqualTo(12L);
    await Assert.That(plan.ActualTotalTimeMs).IsEqualTo(0.41);
  }

  [Test]
  public async Task Parse_FlattensEveryNestedNodeAsync() {
    var plan = QueryPlan.Parse(NESTED_SEQ_SCAN);

    await Assert.That(plan.Nodes.Select(n => n.NodeType))
      .IsEquivalentTo(_nestedNodeTypes)
      .Because("a rule about a sequential scan is worthless if it only sees the root node; the scan that costs "
        + "the query is almost always several levels down.");
  }

  [Test]
  public async Task Parse_KeepsEachNodesRelationIndexAndLoopsAsync() {
    var plan = QueryPlan.Parse(NESTED_SEQ_SCAN);
    var scan = plan.Nodes.Single(n => n.NodeType == "Seq Scan");

    await Assert.That(scan.RelationName).IsEqualTo("wh_event_store");
    await Assert.That(scan.ActualLoops).IsEqualTo(3L)
      .Because("loops are what turn a cheap node into an expensive one; a scan repeated per row is the shape that "
        + "produces millions of buffer hits for a handful of returned rows.");
    var indexOnly = plan.Nodes.Single(n => n.NodeType == "Index Only Scan");
    await Assert.That(indexOnly.IndexName).IsEqualTo("pk_digest_epoch_frontiers");
  }

  [Test]
  public async Task Parse_AnExplainWithoutBuffersReadsAsZeroAsync() {
    var plan = QueryPlan.Parse(NO_BUFFERS);

    await Assert.That(plan.SharedBuffersHit).IsEqualTo(0L);
    await Assert.That(plan.Nodes.Single().RelationName).IsEqualTo("wh_outbox")
      .Because("a plan captured without BUFFERS is still a usable plan for the structural rules; only the buffer "
        + "ceilings become vacuous, and a zero reads as 'not measured' rather than failing the parse.");
  }

  [Test]
  public async Task Parse_EmptyOrMalformedInputSaysWhatWasExpectedAsync() {
    foreach (var bad in new[] { "", "   ", "[]", "{}", "not json" }) {
      await Assert.That(() => QueryPlan.Parse(bad)).Throws<ArgumentException>()
        .Because($"'{bad}' is not an EXPLAIN FORMAT JSON result, and a harness that returned an empty plan for it "
          + "would make every structural rule pass vacuously.");
    }
  }

  // ── no sequential scan ────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task MustNotSequentiallyScan_PassesWhenTheRelationIsReadByIndexAsync() {
    var plan = QueryPlan.Parse(HEALTHY);

    await Assert.That(plan.MustNotSequentiallyScan("wh_inbox_state")).IsSameReferenceAs(plan)
      .Because("a rule that holds returns the plan it judged, which is what lets rules chain and is the only "
        + "observable sign of a pass.");
  }

  [Test]
  public async Task MustNotSequentiallyScan_FailsOnANestedScanOfTheNamedRelationAsync() {
    var plan = QueryPlan.Parse(NESTED_SEQ_SCAN);

    await Assert.That(() => plan.MustNotSequentiallyScan("wh_event_store"))
      .Throws<QueryPlanAssertionException>();
  }

  [Test]
  public async Task MustNotSequentiallyScan_NamesTheRelationLoopsAndRowsInTheFailureAsync() {
    var plan = QueryPlan.Parse(NESTED_SEQ_SCAN);

    var error = Assert.Throws<QueryPlanAssertionException>(() => plan.MustNotSequentiallyScan("wh_event_store"));

    await Assert.That(error!.Message).Contains("wh_event_store");
    await Assert.That(error.Message).Contains("480000");
    await Assert.That(error.Message).Contains("3")
      .Because("the loop count is the difference between one unavoidable scan and the same scan repeated per row, "
        + "so a failure that omits it sends the reader back to the database to find out which it was.");
  }

  [Test]
  public async Task MustNotSequentiallyScan_IgnoresAScanOfADifferentRelationAsync() {
    var plan = QueryPlan.Parse(NESTED_SEQ_SCAN);

    await Assert.That(plan.MustNotSequentiallyScan("wh_outbox")).IsSameReferenceAs(plan)
      .Because("the plan does scan a table sequentially, just not this one; naming an unrelated relation must "
        + "not borrow another relation's failure.");
  }

  [Test]
  public async Task MustNotSequentiallyScan_MatchesTheRelationNameCaseInsensitivelyAsync() {
    var plan = QueryPlan.Parse(NESTED_SEQ_SCAN);

    await Assert.That(() => plan.MustNotSequentiallyScan("WH_Event_Store"))
      .Throws<QueryPlanAssertionException>()
      .Because("Postgres folds unquoted identifiers to lower case, so a caller writing the name as it appears in "
        + "a migration must not silently assert nothing.");
  }

  [Test]
  public async Task MustNotSequentiallyScan_ChecksEveryRelationItIsGivenAsync() {
    var plan = QueryPlan.Parse(NESTED_SEQ_SCAN);

    await Assert.That(() => plan.MustNotSequentiallyScan("wh_outbox", "wh_event_store"))
      .Throws<QueryPlanAssertionException>()
      .Because("the first relation passing must not short-circuit the rest.");
  }

  [Test]
  public async Task MustNotSequentiallyScan_RequiresAtLeastOneRelationAsync() {
    var plan = QueryPlan.Parse(HEALTHY);

    await Assert.That(() => plan.MustNotSequentiallyScan()).Throws<ArgumentException>()
      .Because("asserting nothing is a test that cannot fail; it should be a mistake the caller hears about.");
  }

  // ── index usage ───────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task MustUseIndex_PassesWhenThePlanReadsThatIndexAsync() {
    var plan = QueryPlan.Parse(HEALTHY);

    await Assert.That(plan.MustUseIndex("idx_inbox_state_pending")).IsSameReferenceAs(plan);
  }

  [Test]
  public async Task MustUseIndex_FailsAndListsTheIndexesActuallyUsedAsync() {
    var plan = QueryPlan.Parse(NESTED_SEQ_SCAN);

    var error = Assert.Throws<QueryPlanAssertionException>(() => plan.MustUseIndex("idx_event_store_origin"));

    await Assert.That(error!.Message).Contains("idx_event_store_origin");
    await Assert.That(error.Message).Contains("pk_digest_epoch_frontiers")
      .Because("naming the indexes the planner did choose is what tells the reader whether the index is missing, "
        + "dead, or simply not preferred.");
  }

  // ── buffer ceilings ───────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task MustVisitAtMostSharedBuffers_PassesUnderTheCeilingAsync() {
    var plan = QueryPlan.Parse(HEALTHY);

    await Assert.That(plan.MustVisitAtMostSharedBuffers(100)).IsSameReferenceAs(plan);
    await Assert.That(plan.SharedBuffersVisited).IsEqualTo(34L)
      .Because("the ceiling is judged on hit plus read, so the sum is what the rule reads.");
  }

  [Test]
  public async Task MustVisitAtMostSharedBuffers_FailsOnTheAmplifiedPlanAsync() {
    var plan = QueryPlan.Parse(AMPLIFIED);

    var error = Assert.Throws<QueryPlanAssertionException>(() => plan.MustVisitAtMostSharedBuffers(10_000));

    await Assert.That(error!.Message).Contains("79590241")
      .Because("hit plus read is what the query actually visited; reporting only hits would understate a plan that "
        + "is also reading from disk.");
  }

  [Test]
  public async Task MustNotAmplifyBeyond_FailsOnTheRealWorkClaimPlanAsync() {
    var plan = QueryPlan.Parse(AMPLIFIED);

    var error = Assert.Throws<QueryPlanAssertionException>(() => plan.MustNotAmplifyBeyond(1_000));

    await Assert.That(error!.Message).Contains("13");
    await Assert.That(error.Message).Contains("6122326")
      .Because("buffers per returned row is the ratio that makes this defect legible: 13 rows is a correct result, "
        + "and six million pages per row is the bug.");
  }

  [Test]
  public async Task MustNotAmplifyBeyond_PassesOnAProportionatePlanAsync() {
    var plan = QueryPlan.Parse(HEALTHY);

    await Assert.That(plan.MustNotAmplifyBeyond(10)).IsSameReferenceAs(plan)
      .Because("34 buffers for 12 rows is under three per row, which is what a proportionate plan looks like.");
  }

  [Test]
  public async Task MustNotAmplifyBeyond_APlanReturningNoRowsIsJudgedOnItsBuffersAloneAsync() {
    // Zero returned rows cannot be a ratio. A plan that visited almost nothing still passes, and one that
    // scanned the table to return nothing still fails, which is the case worth catching.
    const string EMPTY_CHEAP = """
    [{"Plan":{"Node Type":"Index Scan","Relation Name":"wh_outbox","Index Name":"pk_outbox",
      "Actual Rows":0,"Actual Loops":1,"Actual Total Time":0.05,"Shared Hit Blocks":3,"Shared Read Blocks":0}}]
    """;
    const string EMPTY_EXPENSIVE = """
    [{"Plan":{"Node Type":"Seq Scan","Relation Name":"wh_outbox","Actual Rows":0,"Actual Loops":1,
      "Actual Total Time":820.0,"Shared Hit Blocks":500000,"Shared Read Blocks":0}}]
    """;

    var cheap = QueryPlan.Parse(EMPTY_CHEAP);
    await Assert.That(cheap.MustNotAmplifyBeyond(1_000)).IsSameReferenceAs(cheap);
    await Assert.That(() => QueryPlan.Parse(EMPTY_EXPENSIVE).MustNotAmplifyBeyond(1_000))
      .Throws<QueryPlanAssertionException>();
  }

  [Test]
  public async Task MustNotAmplifyBeyond_RejectsANonPositiveCeilingAsync() {
    var plan = QueryPlan.Parse(HEALTHY);

    await Assert.That(() => plan.MustNotAmplifyBeyond(0)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => plan.MustVisitAtMostSharedBuffers(-1)).Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task Parse_APlansPropertyThatIsNotAnArray_IsSteppedOverAsync() {
    var plan = QueryPlan.Parse(PLANS_NOT_AN_ARRAY);

    await Assert.That(plan.Nodes.Count).IsEqualTo(1)
      .Because("EXPLAIN always writes Plans as an array; anything else is not a child list, and reading it as one "
        + "would either throw or invent nodes the planner never reported.");
  }

  [Test]
  public async Task Parse_ANodeWithNoTypeIsNamedUnknownRatherThanGuessedAsync() {
    var plan = QueryPlan.Parse(NODE_WITHOUT_A_TYPE);

    await Assert.That(plan.Nodes.Single().NodeType).IsEqualTo("(unknown)")
      .Because("every structural rule matches on the node type, so an absent one must not read as a type the "
        + "planner never named - a node defaulting to \"Seq Scan\" would fail rules it should not, and one "
        + "defaulting to empty would pass rules it should not.");
    plan.MustNotSequentiallyScan("wh_outbox");
  }

  [Test]
  public async Task Parse_ANonNumericTimingReadsAsZeroAsync() {
    var plan = QueryPlan.Parse(TIME_NOT_A_NUMBER);

    await Assert.That(plan.ActualTotalTimeMs).IsEqualTo(0d)
      .Because("a timing that is not a number is not a timing; zero reads as 'not measured', which is how a plan "
        + "captured without ANALYZE already behaves.");
  }

  [Test]
  public async Task Parse_KeepsTheScannedFunctionsNameAsync() {
    var plan = QueryPlan.Parse(AMPLIFIED);

    await Assert.That(plan.Nodes.Single().FunctionName).IsEqualTo("claim_work")
      .Because("a Function Scan names no relation, so the function is the only thing identifying what the node "
        + "ran - and it is what a failure has to point at.");
  }

  [Test]
  public async Task MustUseIndex_WhenThePlanReadsNoIndexAtAllTheFailureSaysSoAsync() {
    var plan = QueryPlan.Parse(NO_INDEX_AT_ALL);

    var error = Assert.Throws<QueryPlanAssertionException>(() => plan.MustUseIndex("idx_outbox_pending"));

    await Assert.That(error!.Message).Contains("no index at all")
      .Because("an empty list of indexes used would read as a formatting bug; saying the plan reads none is the "
        + "actual finding, and it points at a different fix from a wrong index being preferred.");
  }

  // ── the failure type ──────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task AssertionException_CarriesThePlanAlongsideTheMessageAsync() {
    var plan = QueryPlan.Parse(NO_INDEX_AT_ALL);

    var error = Assert.Throws<QueryPlanAssertionException>(() => plan.MustUseIndex("idx_outbox_pending"));

    await Assert.That(error!.Plan).IsEqualTo(plan.Json)
      .Because("the planner's own output travels with the failure so a reader does not have to reproduce it.");
    await Assert.That(error.Message).Contains(plan.Json);
  }

  [Test]
  public async Task AssertionException_SupportsTheConventionalConstructorsAsync() {
    // The standard exception shape, so a caller can wrap or rethrow one the ordinary way.
    await Assert.That(new QueryPlanAssertionException().Plan).IsNull();
    await Assert.That(new QueryPlanAssertionException("a rule failed").Message).IsEqualTo("a rule failed");
    var wrapped = new QueryPlanAssertionException("a rule failed", new InvalidOperationException("why"));
    await Assert.That(wrapped.InnerException!.Message).IsEqualTo("why");
    await Assert.That(wrapped.Plan).IsNull()
      .Because("only the plan-carrying constructor sets it; the others leave it unset rather than empty, so a "
        + "reader can tell 'no plan captured' from 'an empty plan'.");
  }

  // ── chaining ──────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Assertions_ReturnThePlanSoRulesChainAsync() {
    var plan = QueryPlan.Parse(HEALTHY);

    var chained = plan
      .MustNotSequentiallyScan("wh_inbox_state")
      .MustUseIndex("idx_inbox_state_pending")
      .MustVisitAtMostSharedBuffers(100)
      .MustNotAmplifyBeyond(10);

    await Assert.That(chained).IsSameReferenceAs(plan);
  }
}
