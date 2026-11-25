-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 572_FailureReportsAreFenced.sql (#2556): the fenced report is dropped; failures are reported through
-- report_failures alone.

SELECT __SCHEMA__.drop_all_overloads('wh_report_failures_fenced');
