# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT
#
# The Compile job's conclusion from a run's jobs list (GET .../actions/runs/{id}/jobs), or "pending".
# Matches ONLY the Compile job: "Build · Compile / <child>" while it runs, or the bare parent name
# "Build · Compile" when that reusable job is skipped. A prefix match also caught "Build · Compile net10.0",
# which is skipped on merge-queue runs, so the coverage wait read the build as skipped and hung (#2385).
[.jobs[]
  | select(.name == "Build · Compile" or (.name | startswith("Build · Compile / ")))
  | .conclusion // "pending"]
| first // "pending"
