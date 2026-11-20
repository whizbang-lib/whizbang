# Copyright (c) whizbang-lib contributors.
# SPDX-License-Identifier: MIT
#
# How many coverage-* artifacts a run's suites upload, from its jobs list (GET .../actions/runs/{id}/jobs): one per
# suite (per shard), whatever its legs. Every suite leg is a job named "Test · <suite> / Run suite", or
# "Test · <suite> / Run suite (<framework>)" for each framework of a release build (#2427), and only the leg on the
# coverage framework uploads coverage (#2135), so the legs of one suite count once.
[.jobs[].name
  | select(test("^Test · .* / Run suite( \\(net[0-9.]+\\))?$"))
  | sub(" \\(net[0-9.]+\\)$"; "")]
| unique
| length
