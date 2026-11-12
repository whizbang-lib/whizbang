-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 401_CleanupReportsWhatItHasLeft.sql (#2289): both reading functions dropped. Reversible: they hold no state.

SELECT __SCHEMA__.drop_all_overloads('wh_pending_reclamation');
SELECT __SCHEMA__.drop_all_overloads('wh_ephemeral_history_walk_remaining');
