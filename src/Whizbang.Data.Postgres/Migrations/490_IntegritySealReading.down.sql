-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 490_IntegritySealReading.sql (#2365): the reading goes, and the seal gauges report nothing.

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_seal_reading');
