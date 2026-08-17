# Whizbang.Soak.Tests

Load, stress and soak tests. **Deliberately excluded from CI.**

## Why this project exists

Three production incidents in a row shared one shape: behaviour that is correct per item and
unbounded in aggregate. Each was found in production, not by tests — the divergence storm, the
`P0001` claim spiral, and the audit sweep that starved the request pipeline until the liveness
probe stopped being answered and the fleet entered a restart loop.

The unit and integration suites now assert the *deterministic* half of that: fan-out counts stay
capped, caps are mutation-verified, and `scripts/Lint-UnboundedFanOut.ps1` fails on a new awaited
fan-out inside a loop. Those belong in the gate because they cannot flake.

What they cannot assert is the *emergent* half — latency under sustained load, memory and
connection growth over hours, whether a health endpoint keeps getting a thread while a sweep runs.
Those are wall-clock properties. On a busy CI runner they flap, and a flapping test gets rerun
until it goes green, which is worse than not having it at all. This repo bans timing assertions
elsewhere for exactly that reason.

So they get a room of their own, where wall-clock measurement is the subject rather than a hazard.

## How the exclusion works

`Whizbang.Soak.Tests.csproj` declares:

```xml
<WhizbangTestType>Soak</WhizbangTestType>
```

`Run-Tests.ps1` classifies every project by that property, and each of its modes filters for
`Unit` or `Integration` specifically. `Soak` matches neither, so this project is excluded **by
construction** — there is no opt-out list to maintain and no way for a slow test in here to start
blocking a pull request by accident.

## Running them

```bash
pwsh scripts/Run-Soak.ps1                    # everything
pwsh scripts/Run-Soak.ps1 -Filter Starvation # one scenario
```

Requires Docker for the scenarios that need a real PostgreSQL.

## What belongs here

- **Starvation / responsiveness** — does a health endpoint keep getting a thread while a heavy
  sweep runs? (`IntegrityAuditStarvationSoakTests`)
- **Sustained soak** — run the work pump for a long window; assert memory, connection count and
  table sizes reach a steady state instead of climbing.
- **Throughput baselines** — record ops/sec so a regression is visible as a number, not a vibe.
- **Volume behaviour that is genuinely emergent** — anything where the interesting property only
  appears at scale and cannot be reduced to a count.

## What does NOT belong here

If the property can be asserted as a **count, a bound, or an invariant**, it belongs in the normal
suites where it runs on every PR. "Publishes at most N reports for a manifest of 500" is a unit
test. "Stays responsive while publishing them" is a soak test. Prefer the former whenever the
property can be expressed that way — a deterministic test that runs every time beats a realistic
one that runs when someone remembers.

## Reading a failure

These tests measure the machine they run on. A failure means "on this hardware, under this load,
the property did not hold" — investigate before treating it as a code regression, and record what
the baseline was. A soak failure is the start of an investigation, not a verdict.
