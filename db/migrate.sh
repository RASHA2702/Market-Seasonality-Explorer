#!/usr/bin/env bash
# Applies db/migrations/*.sql in file-name order, skipping versions already recorded in
# ops.schema_migration. Each migration runs in its own transaction and records its own version,
# so files applied by Docker's first-start init are recognised here as well.
#
# Connection settings come from the standard libpq variables (PGHOST, PGPORT, PGDATABASE,
# PGUSER, PGPASSWORD) or ~/.pgpass. Example:
#   PGHOST=127.0.0.1 PGUSER=gateway_app PGDATABASE=gateway ./db/migrate.sh
set -euo pipefail

if ! command -v psql >/dev/null 2>&1; then
    echo "psql (the PostgreSQL client) is required." >&2
    exit 1
fi

migrations_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/migrations" && pwd)"
psql_cmd=(psql --no-psqlrc --quiet --set ON_ERROR_STOP=1)

query() {
    "${psql_cmd[@]}" --tuples-only --no-align --command "$1"
}

registry_exists="$(query "SELECT to_regclass('ops.schema_migration') IS NOT NULL")"
applied=0

shopt -s nullglob
for file in "$migrations_dir"/*.sql; do
    version="$(basename "$file" .sql)"
    if [[ ! "$version" =~ ^[0-9]{3}_[a-z0-9_]+$ ]]; then
        echo "Migration file '$version.sql' must be named like 001_description.sql" >&2
        exit 1
    fi

    if [[ "$registry_exists" == "t" && "$(query "SELECT count(*) FROM ops.schema_migration WHERE version = '$version'")" == "1" ]]; then
        continue
    fi

    echo "Applying $version"
    "${psql_cmd[@]}" --file "$file" >/dev/null

    if [[ "$(query "SELECT count(*) FROM ops.schema_migration WHERE version = '$version'")" != "1" ]]; then
        echo "$version ran but did not record itself in ops.schema_migration" >&2
        exit 1
    fi

    registry_exists="t"
    applied=$((applied + 1))
done

echo "Migrations applied: $applied"
