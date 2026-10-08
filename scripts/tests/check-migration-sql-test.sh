#!/usr/bin/env bash
# Tests for scripts/check-migration-sql.sh, an allowlist: each case writes a script shaped like `dotnet ef migrations
# script --idempotent` output (one DO $EF$ block per statement, naming its MigrationId) and asserts the exit code and
# the line that names the finding. A refusal must be non-zero AND name what it refused, or it proves nothing.
# Run: bash scripts/tests/check-migration-sql-test.sh
set -uo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
bash -n "$repo/scripts/check-migration-sql.sh" || { echo "FAIL check-migration-sql.sh does not parse"; exit 1; }

expected_cases=62
cases=0
failed=0
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
: >"$work/none.txt"
q="'"

# block <MigrationId> <statement...>: one DO block as EF writes it.
block() {
  local id="$1"; shift
  # shellcheck disable=SC2016 # $EF$ is SQL dollar-quoting, not a shell variable
  printf 'DO $EF$\nBEGIN\n    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '\''%s'\'') THEN\n    %s\n    END IF;\nEND $EF$;\n\n' "$id" "$*"
}
script() { { printf 'START TRANSACTION;\n\n'; cat; printf 'COMMIT;\n'; } >"$work/s.sql"; }
scan() { bash "$repo/scripts/check-migration-sql.sh" "$work/s.sql" "${1:-$work/none.txt}" >"$work/out" 2>&1; }
# expect <status> <name> <0|1|2> [text the output must contain]
expect() {
  cases=$((cases + 1))
  local status="$1" name="$2" want="$3" text="${4:-}" ok=1
  [[ "$status" == "$want" ]] || ok=0
  if [[ -n "$text" ]] && ! grep -qF -- "$text" "$work/out"; then ok=0; fi
  if (( ok )); then echo "ok   $name"; else failed=$((failed + 1)); echo "FAIL $name (exit $status, expected $want, text '$text')"; cat "$work/out"; fi
}
refused() { echo "$1: not an additive migration shape"; }
locked() { echo "$1: refused, and it touches $2, which is append-only"; }

# 1. The shapes the committed migrations use, the append-only trigger on ColonyEvents included, pass.
{ block 20260101000000_Add 'CREATE TABLE "T" ("Id" uuid NOT NULL, "Name" character varying(200) NOT NULL, CONSTRAINT "PK_T" PRIMARY KEY ("Id"), CONSTRAINT "FK_T_O" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("AccountId") ON DELETE RESTRICT);'
  block 20260101000000_Add 'CREATE UNIQUE INDEX "IX_T_Name" ON "T" ("AccountId", "Name");'
  block 20260101000000_Add 'CREATE FUNCTION refuse() RETURNS trigger LANGUAGE plpgsql AS $$
    BEGIN
        RAISE EXCEPTION '"'"'ColonyEvents is append-only: % refused'"'"', TG_OP;
    END;
    $$;
    CREATE TRIGGER "T_append_only" BEFORE UPDATE OR DELETE ON "ColonyEvents" FOR EACH ROW EXECUTE FUNCTION refuse();
    CREATE TRIGGER "T_no_truncate" BEFORE TRUNCATE ON "ColonyEvents" FOR EACH STATEMENT EXECUTE FUNCTION refuse();'
  block 20260101000000_Add "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES (${q}20260101000000_Add${q}, ${q}10.0.12${q});"
} | script; scan; expect $? "additive tables, indexes and the append-only trigger pass" 0 "is additive"

block 20260102000000_Col 'ALTER TABLE "T" ADD "Notes" character varying(400);' | script; scan
expect $? "a nullable column passes" 0 "is additive"

block 20260102000000_Col 'ALTER TABLE "T" ADD "Count" integer NOT NULL DEFAULT 0;' | script; scan
expect $? "a NOT NULL column with a DEFAULT passes" 0 "is additive"

block 20260102000000_Del "DELETE FROM \"T\" WHERE \"Id\" = ${q}00000000-0000-0000-0000-000000000001${q};" | script; scan
expect $? "a keyed DELETE is not an additive shape: it needs an override" 1 "$(refused 20260102000000_Del)"

# 2. Each destructive statement, refused and named, with the migration that carries it.
block 20260103000000_Drop 'DROP TABLE "T";' | script; scan
expect $? "DROP TABLE is refused" 1 "$(refused 20260103000000_Drop): DROP TABLE"

block 20260103000000_DropCol 'ALTER TABLE "T" DROP COLUMN "Name";' | script; scan
expect $? "DROP COLUMN is refused" 1 "$(refused 20260103000000_DropCol): ALTER TABLE \"T\" DROP COLUMN"

block 20260103000000_Trunc 'TRUNCATE "T";' | script; scan
expect $? "TRUNCATE is refused" 1 "$(refused 20260103000000_Trunc): TRUNCATE"

block 20260103000000_Del 'DELETE FROM "T";' | script; scan
expect $? "an unkeyed DELETE is refused, though the DO block's IF has a WHERE" 1 "$(refused 20260103000000_Del): DELETE FROM"

block 20260103000000_Type 'ALTER TABLE "T" ALTER COLUMN "Name" TYPE character varying(100);' | script; scan
expect $? "a column type change is refused" 1 "$(refused 20260103000000_Type)"

block 20260103000000_NotNull 'ALTER TABLE "T" ALTER COLUMN "Notes" SET NOT NULL;' | script; scan
expect $? "SET NOT NULL is refused" 1 "$(refused 20260103000000_NotNull)"

block 20260103000000_Rename 'ALTER TABLE "T" RENAME COLUMN "Name" TO "Title";' | script; scan
expect $? "a RENAME is refused" 1 "$(refused 20260103000000_Rename)"

block 20260103000000_Req 'ALTER TABLE "T" ADD "Weight" numeric(10,2) NOT NULL;' | script; scan
expect $? "a NOT NULL column with no DEFAULT is refused (an older image's insert fails)" 1 "$(refused 20260103000000_Req)"

block 20260103000000_Anon 'DO $$ BEGIN ALTER TABLE "T" ADD "Weight" integer NOT NULL; END $$;' | script; scan
expect $? "an anonymous DO \$\$ block is refused" 1 "$(refused 20260103000000_Anon): DO \$\$"

# 3. The append-only tables: refused, override or not.
block 20260104000000_Fix "UPDATE \"ColonyEvents\" SET \"Data\" = ${q}{}${q} WHERE \"EventId\" = ${q}00000000-0000-0000-0000-000000000001${q};" | script; scan
expect $? "an UPDATE on ColonyEvents is refused" 1 "$(locked 20260104000000_Fix ColonyEvents)"

block 20260104000000_Fix "DELETE FROM \"Inspections\" WHERE \"InspectionId\" = ${q}00000000-0000-0000-0000-000000000001${q};" | script
printf '20260104000000_Fix synthetic reason\n' >"$work/ov.txt"; scan "$work/ov.txt"
expect $? "a DELETE on Inspections is refused even with an override" 1 "$(locked 20260104000000_Fix Inspections)"

# 4. Overrides: a reviewed line exempts its own migration, and only that one.
block 20260105000000_Drop 'DROP TABLE "Old";' | script
printf '# a comment\n20260105000000_Drop the table has been unused since 1.2.0\n' >"$work/ov.txt"; scan "$work/ov.txt"
expect $? "an override lets its migration through, and says so" 0 "allowed by its reviewed override (the table has been unused since 1.2.0)"

{ block 20260105000000_Drop 'DROP TABLE "Old";'; block 20260106000000_Other 'DROP TABLE "Kept";'; } | script; scan "$work/ov.txt"
expect $? "an override does not cover another migration" 1 "$(refused 20260106000000_Other): DROP TABLE \"Kept\""

printf '20260105000000_Drop\n' >"$work/ov.txt"; block 20260105000000_Drop 'DROP TABLE "Old";' | script; scan "$work/ov.txt"
expect $? "an override with no reason is refused" 2 "needs a reason"

# 5. Statements outside any migration: only the history table, transactions, the seed and the closing line.
{ printf 'START TRANSACTION;\nCOMMIT;\n'; printf 'DELETE FROM "Accounts";\n'; } >"$work/s.sql"; scan
expect $? "an unkeyed DELETE outside a DO block is refused" 1 "(script): a statement outside any migration"

# The seed outside the migrations: the committed seed passes; the organization seed from before the account-rooted
# schema, and a seed that rewrites an existing row, do not.
{ printf 'START TRANSACTION;\nCOMMIT;\n'; cat "$repo/scripts/sql/staging-seed.sql"; } >"$work/s.sql"; scan
expect $? "the committed staging seed passes outside any migration" 0 "is additive"
{ printf 'START TRANSACTION;\nCOMMIT;\n'; printf 'INSERT INTO "Organizations" ("OrganizationId", "Name") VALUES (%sa91a7c0e-5747-4e00-8000-000000000019%s, %sX%s) ON CONFLICT ("OrganizationId") DO NOTHING;\n' "$q" "$q" "$q" "$q"; } >"$work/s.sql"; scan
expect $? "the old organization seed is refused" 1 "(script): a statement outside any migration"
{ printf 'START TRANSACTION;\nCOMMIT;\n'; printf 'INSERT INTO "Accounts" ("AccountId", "Name") VALUES (%sa91a7c0e-5747-4e00-8000-000000000019%s, %sX%s) ON CONFLICT ("AccountId") DO UPDATE SET "Name" = %sY%s;\n' "$q" "$q" "$q" "$q" "$q" "$q"; } >"$work/s.sql"; scan
expect $? "an account seed that rewrites an existing row is refused" 1 "(script): a statement outside any migration"
{ printf 'START TRANSACTION;\nCOMMIT;\n'; printf 'INSERT INTO "Users" ("UserId", "AccountId", "Name") VALUES (%sa91a7c0e-5747-4e00-8000-000000000042%s, %sa91a7c0e-5747-4e00-8000-000000000019%s, %sX%s) ON CONFLICT ("UserId") DO UPDATE SET "AccountId" = %sa91a7c0e-5747-4e00-8000-000000000001%s;\n' "$q" "$q" "$q" "$q" "$q" "$q" "$q" "$q"; } >"$work/s.sql"; scan
expect $? "a user seed that moves an existing user to another account is refused" 1 "(script): a statement outside any migration"

# 6. Nothing to check is a failure, never a pass.
: >"$work/s.sql"; scan
expect $? "an empty script is refused" 2 "missing or empty"

printf -- '-- only a comment\n' >"$work/s.sql"; scan
expect $? "a script with no statements is refused" 2 "no statements found"

# 7. The bypasses of the earlier denylist, found in review, each refused by the allowlist.
block 20260107000000_Line 'CREATE TABLE "A" ("Id" int); DROP TABLE "B";' | script; scan
expect $? "a DROP after another statement on the same line is refused" 1 "$(refused 20260107000000_Line): DROP TABLE \"B\""

block 20260107000000_Lit "SELECT ${q}--${q}; DROP TABLE \"B\";" | script; scan
expect $? "-- inside a string literal hides nothing" 1 "$(refused 20260107000000_Lit): DROP TABLE \"B\""

block 20260107000000_Type 'ALTER TABLE "T" ALTER "Name" TYPE text;' | script; scan
expect $? "a type change without COLUMN is refused" 1 "$(refused 20260107000000_Type): ALTER TABLE \"T\" ALTER \"Name\" TYPE"

block 20260107000000_NotNull 'ALTER TABLE "T" ALTER "Name" SET NOT NULL;' | script; scan
expect $? "SET NOT NULL without COLUMN is refused" 1 "$(refused 20260107000000_NotNull): ALTER TABLE \"T\" ALTER \"Name\" SET NOT NULL"

block 20260107000000_Rename 'ALTER TABLE "T" RENAME "Name" TO "Title";' | script; scan
expect $? "a RENAME without COLUMN is refused" 1 "$(refused 20260107000000_Rename): ALTER TABLE \"T\" RENAME"

block 20260107000000_Exec "DO \$\$ BEGIN EXECUTE ${q}DROP TABLE \"B\"${q}; END \$\$;" | script; scan
expect $? "dynamic SQL (EXECUTE) is refused, with no override" 1 "20260107000000_Exec: refused, and it uses dynamic SQL (EXECUTE); no override applies"

printf '20260107000000_Cte synthetic reason\n20260107000000_Merge synthetic reason\n' >"$work/ov.txt"
block 20260107000000_Cte 'WITH d AS (UPDATE "ColonyEvents" SET "Data" = NULL RETURNING 1) SELECT 1;' | script; scan "$work/ov.txt"
expect $? "an UPDATE on ColonyEvents inside a CTE is refused, override or not" 1 "$(locked 20260107000000_Cte ColonyEvents)"

block 20260107000000_Merge 'MERGE INTO "Inspections" i USING "T" t ON i."InspectionId" = t."Id" WHEN MATCHED THEN DELETE;' | script; scan "$work/ov.txt"
expect $? "a MERGE ... DELETE on Inspections is refused, override or not" 1 "$(locked 20260107000000_Merge Inspections)"

printf '20260107000000_Dyn synthetic reason
' >"$work/ov.txt"
block 20260107000000_Dyn "DO \$\$ BEGIN EXECUTE ${q}DELETE FROM ${q} || quote_ident(${q}ColonyEvents${q}); END \$\$;" | script; scan "$work/ov.txt"
expect $? "dynamic SQL naming ColonyEvents in a literal is refused, override or not" 1 "$(locked 20260107000000_Dyn ColonyEvents)"

block 20260107000000_Comment 'UPDATE/**/"ColonyEvents" SET "Data" = NULL;' | script; scan
expect $? "UPDATE/**/ on ColonyEvents is refused" 1 "$(locked 20260107000000_Comment ColonyEvents)"

block 20260107000000_Fn 'CREATE FUNCTION f() RETURNS void LANGUAGE sql AS $$ DELETE FROM "ColonyEvents" $$; SELECT f();' | script; scan
expect $? "a function body that the script then calls is refused" 1 "$(locked 20260107000000_Fn ColonyEvents)"

block 20260107000000_Work 'CREATE FUNCTION refuse() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN DELETE FROM "Other"; RAISE EXCEPTION '"'"'x'"'"'; END; $$;' | script; scan
expect $? "a trigger function that does work besides raising is refused" 1 "$(refused 20260107000000_Work): CREATE FUNCTION refuse()"

printf '20260107000000_Disable synthetic reason\n' >"$work/ov.txt"
block 20260107000000_Disable 'ALTER TABLE "ColonyEvents" DISABLE TRIGGER ALL;' | script; scan "$work/ov.txt"
expect $? "DISABLE TRIGGER on ColonyEvents is refused, override or not" 1 "$(locked 20260107000000_Disable ColonyEvents)"

printf '20260107000000_DropTrig synthetic reason\n' >"$work/ov.txt"
block 20260107000000_DropTrig 'DROP TRIGGER "ColonyEvents_append_only" ON "ColonyEvents";' | script; scan "$work/ov.txt"
expect $? "DROP TRIGGER on ColonyEvents is refused, override or not" 1 "$(locked 20260107000000_DropTrig ColonyEvents)"

printf '20260107000000_Trunc synthetic reason\n' >"$work/ov.txt"
block 20260107000000_Trunc 'TRUNCATE "ColonyEvents";' | script; scan "$work/ov.txt"
expect $? "TRUNCATE on ColonyEvents is refused, override or not" 1 "$(locked 20260107000000_Trunc ColonyEvents)"

printf '(script) synthetic reason\n' >"$work/ov.txt"; block 20260105000000_Drop 'DROP TABLE "Old";' | script; scan "$work/ov.txt"
expect $? "an override named (script), or anything but a migration id, is refused" 2 "is not a migration id"

block 20260107000000_Check 'ALTER TABLE "T" ADD "Size" integer CHECK ("Size" > 0);' | script; scan
expect $? "a column added with a CHECK is refused (an older image's insert may fail it)" 1 "$(refused 20260107000000_Check)"

block 20260107000000_Two 'ALTER TABLE "T" ADD "A" integer, DROP COLUMN "B";' | script; scan
expect $? "a second action in the same ALTER TABLE is refused" 1 "$(refused 20260107000000_Two)"

{ printf 'START TRANSACTION;\n'; printf 'DO $$ BEGIN DROP TABLE "B"; END $$;\nCOMMIT;\n'; } >"$work/s.sql"; scan
expect $? "a DO block outside EF's shape is refused" 1 "(script): a statement outside any migration"

printf 'START TRANSACTION;\nSELECT %s;\n' "'unterminated" >"$work/s.sql"; scan
expect $? "an unterminated string literal is refused, not guessed at" 2 "unterminated string literal"

# 8. The bypasses found in a second review, each of which PostgreSQL then ran.
stmt="$(cat <<'SQL'
ALTER TABLE "T" ADD "c" text DEFAULT E'\'', DROP COLUMN "Data", ADD "d" text DEFAULT 'x';
SQL
)"
block 20260108000000_Esc "$stmt" | script; scan
expect $? "an E'' escape cannot hide a second ALTER action" 1 "$(refused 20260108000000_Esc)"

stmt="$(cat <<'SQL'
ALTER TABLE "T" ADD "c" text DEFAULT E'it\'s';
SQL
)"
block 20260108000000_EscOk "$stmt" | script; scan
expect $? "a single column with an E'' escaped DEFAULT still passes" 0 "is additive"

stmt="$(cat <<'SQL'
CREATE TABLE "x" ("a" text DEFAULT $é$'$é$); DROP TABLE "Inspections"; CREATE TABLE "y" ("b" text DEFAULT $é$'$é$);
SQL
)"
block 20260108000000_Tag "$stmt" | script; scan
expect $? "a dollar-quote tag with a non-ASCII character is refused, not misread" 1 "a dollar-quote tag with a non-ASCII character"

stmt="$(cat <<'SQL'
CREATE TABLE "x" ("a" text); ALTER TABLE Té$x$ ADD "b" text; DROP TABLE "Inspections"; $x$;
SQL
)"
block 20260108000000_Bare "$stmt" | script; scan
expect $? "a non-ASCII character outside a literal or identifier is refused" 1 "a non-ASCII character outside a quoted string or identifier"

printf '20260108000000_Uesc synthetic reason\n20260108000000_Concat synthetic reason\n20260108000000_Exec synthetic reason\n' >"$work/ov.txt"
stmt="$(cat <<'SQL'
ALTER TABLE U&"Colony\0045vents" DISABLE TRIGGER ALL;
SQL
)"
block 20260108000000_Uesc "$stmt" | script; scan "$work/ov.txt"
expect $? "a U& escaped name is refused, override or not" 1 "20260108000000_Uesc: refused, and it uses a U& escape"

stmt="$(cat <<'SQL'
DO $$ BEGIN PERFORM 'DELETE FROM "Colony' || 'Events"'; END $$;
SQL
)"
block 20260108000000_Concat "$stmt" | script; scan "$work/ov.txt"
expect $? "a name built with || is refused, override or not" 1 "20260108000000_Concat: refused, and it builds text with ||"

stmt="$(cat <<'SQL'
DO $$ BEGIN EXECUTE 'TRUNCATE "Other"'; END $$;
SQL
)"
block 20260108000000_Exec "$stmt" | script; scan "$work/ov.txt"
expect $? "dynamic SQL is refused, override or not, whatever it names" 1 "20260108000000_Exec: refused, and it uses dynamic SQL (EXECUTE)"

# 9. A filtered index (EF's HasFilter) is an additive shape; a query inside its predicate is not.
block 20260109000000_Filter 'CREATE UNIQUE INDEX "IX_T_Tag" ON "T" ("AccountId", "TagId") WHERE "TagId" IS NOT NULL;' | script; scan
expect $? "a filtered index passes" 0 "is additive"

block 20260109000000_FilterSel 'CREATE INDEX "IX_T_X" ON "T" ("Id") WHERE "Id" IN (SELECT "Id" FROM "U");' | script; scan
expect $? "a filtered index whose predicate holds a query is refused" 1 "$(refused 20260109000000_FilterSel)"

# 10. The lock's name-building arm: text built with format / quote_ident / chr could name a table without spelling it.
# The statements spell no append-only name, EXECUTE, U& or ||, so only that arm can lock them.
printf '20260110000000_Fmt synthetic reason\n20260110000000_Qi synthetic reason\n20260110000000_Chr synthetic reason\n' >"$work/ov.txt"
block 20260110000000_Fmt "DO \$\$ BEGIN PERFORM format(${q}%I${q}, ${q}T${q}); END \$\$;" | script; scan "$work/ov.txt"
expect $? "a name built with format() is refused, override or not" 1 "20260110000000_Fmt: refused, and it builds a name with format; no override applies"

block 20260110000000_Qi "DO \$\$ BEGIN PERFORM quote_ident(${q}T${q}); END \$\$;" | script; scan "$work/ov.txt"
expect $? "a name built with quote_ident() is refused, override or not" 1 "20260110000000_Qi: refused, and it builds a name with quote_ident; no override applies"

block 20260110000000_Chr "DO \$\$ BEGIN PERFORM chr(67); END \$\$;" | script; scan "$work/ov.txt"
expect $? "a name built with chr() is refused, override or not" 1 "20260110000000_Chr: refused, and it builds a name with chr; no override applies"

# 11. A filtered index's tail is a WHERE predicate and nothing else: any other tail is refused.
block 20260111000000_Tail 'CREATE INDEX "IX_T_X" ON "T" ("Id") TABLESPACE "other";' | script; scan
expect $? "an index with a tail that is not a WHERE predicate is refused" 1 "$(refused 20260111000000_Tail)"

# 12. Literals and quoted identifiers are masked before a shape or a lock reads the statement: what is only text
# inside one decides nothing. (Unmasked, each case below is refused or locked instead.)
block 20260112000000_Lit "ALTER TABLE \"T\" ADD \"Note\" text DEFAULT ${q}a, b CHECK GENERATED NOT NULL${q};" | script; scan
expect $? "a comma and keywords inside a DEFAULT literal do not refuse the column" 0 "is additive"

block 20260112000000_Ident 'ALTER TABLE "T" ADD "a, CHECK" integer;' | script; scan
expect $? "a comma and a keyword inside a quoted column name do not refuse the column" 0 "is additive"

block 20260112000000_Pred "CREATE INDEX \"IX_T_K\" ON \"T\" (\"Id\") WHERE \"Kind\" <> ${q}SELECT${q};" | script; scan
expect $? "SELECT inside a filtered index's literal is not a query" 0 "is additive"

printf '20260112000000_Quoted synthetic reason\n' >"$work/ov.txt"
block 20260112000000_Quoted "UPDATE \"T\" SET \"Name\" = ${q}a || format(b) EXECUTE U&\"c\"${q};" | script; scan "$work/ov.txt"
expect $? "||, format(), EXECUTE and U& inside a literal do not lock a statement an override allows" 0 "allowed by its reviewed override"

if [[ $cases -ne $expected_cases ]]; then echo "FAIL ran $cases cases, expected $expected_cases"; exit 1; fi
if [[ $failed -ne 0 ]]; then echo "$failed of $cases check-migration-sql.sh tests failed"; exit 1; fi
echo "all $cases check-migration-sql.sh tests passed"
