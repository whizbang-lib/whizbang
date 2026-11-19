-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 395_ARepairSendThatFailsIsWithdrawn.sql (#2223): drops wh_integrity_withdraw_answer_pending. An older
-- release never calls it, and it stores nothing of its own, so the reverse loses no data.

SELECT __SCHEMA__.drop_all_overloads('wh_integrity_withdraw_answer_pending');
