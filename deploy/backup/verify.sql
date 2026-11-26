-- Checks a restored gateway database. psql exits with an error if the copy is incomplete or inconsistent.
\set ON_ERROR_STOP on

DO $$
DECLARE
    unbalanced integer;
    too_short integer;
    schema_version text;
BEGIN
    -- Every journal entry balances, separately for every asset.
    SELECT count(*) INTO unbalanced FROM (
        SELECT entry_id, asset_key
          FROM ledger.journal_line
         GROUP BY entry_id, asset_key
        HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0) AS lines;
    IF unbalanced > 0 THEN
        RAISE EXCEPTION 'restore check: % journal entries do not balance', unbalanced;
    END IF;

    -- Every journal entry kept all its lines.
    SELECT count(*) INTO too_short FROM (
        SELECT e.entry_id
          FROM ledger.journal_entry AS e
          LEFT JOIN ledger.journal_line AS l ON l.entry_id = e.entry_id
         GROUP BY e.entry_id
        HAVING count(l.entry_id) < 2) AS entries;
    IF too_short > 0 THEN
        RAISE EXCEPTION 'restore check: % journal entries have fewer than two lines', too_short;
    END IF;

    SELECT max(version) INTO schema_version FROM ops.schema_migration;
    IF schema_version IS NULL THEN
        RAISE EXCEPTION 'restore check: no schema migrations are recorded';
    END IF;

    -- The protections came back with the data: the ledger and review log are still append-only.
    IF (SELECT count(*) FROM pg_trigger
         WHERE tgname IN ('journal_entry_append_only', 'journal_line_append_only', 'review_action_append_only')) < 3 THEN
        RAISE EXCEPTION 'restore check: the append-only protections are missing';
    END IF;

    RAISE NOTICE 'restore check passed: the ledger balances, the schema is at %, and the append-only protections are in place',
        schema_version;
END $$;
