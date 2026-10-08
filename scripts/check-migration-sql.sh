#!/usr/bin/env bash
# Refuses a generated staging migration script unless every statement in it is one of the shapes an additive
# migration takes (DEPLOYMENT.md §9, CONVENTIONS.md §14.1). It reads the idempotent script `dotnet ef
# migrations script --idempotent` writes, with scripts/migration-script.sh's seed and closing line, and is an
# ALLOWLIST: a tokeniser that knows string literals (E'' escapes included), quoted identifiers, ASCII-tagged dollar
# quotes and comments splits the script into statements, and each statement must match a known shape or it is
# refused. A dollar-quote tag with a non-ASCII character, and any non-ASCII character outside a literal or quoted
# identifier, is refused rather than read differently from PostgreSQL.
#
#   outside any migration   the history table's CREATE TABLE IF NOT EXISTS, START TRANSACTION, COMMIT, EF's DO $EF$
#                           block per statement (naming its MigrationId), the synthetic account's and its user's
#                           INSERT ... ON CONFLICT DO NOTHING, and the closing SELECT. Anything else is refused, with no override.
#   inside a migration      CREATE TABLE; CREATE [UNIQUE] INDEX, a filtered one's WHERE predicate included (no query in
#                           it); ALTER TABLE ... ADD [COLUMN] one column, nullable or
#                           with a DEFAULT; ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY / UNIQUE / PRIMARY KEY; EF's
#                           history INSERT for that same migration; a trigger function whose body only raises, and a
#                           BEFORE trigger that calls it (the append-only guard on ColonyEvents). Anything else - a drop,
#                           a rename, a type change, SET NOT NULL, a CHECK, an UPDATE or DELETE, dynamic SQL, a function
#                           that does work - is refused unless a reviewed line in scripts/sql/migration-overrides.txt,
#                           "<MigrationId> <reason>", names that migration.
#   never overridable       a refused statement that names ColonyEvents or Inspections, the append-only tables
#                           (ARCHITECTURE.md D-2): their history is corrected by a compensating event, not rewritten,
#                           and their triggers are not disabled or dropped. The match is by name, so a refused
#                           statement that could name a table without spelling it - dynamic SQL (EXECUTE), a U&
#                           escape, text built with || or format/quote_ident/chr - has no override either.
#
# What this is: a guard against a migration a PR author writes destroying or narrowing data by accident. What it is not:
# a security boundary against a malicious author. That boundary is review and the branch and environment rules.
#
# Usage: scripts/check-migration-sql.sh <script.sql> [overrides-file]
set -euo pipefail
sql="${1:?usage: check-migration-sql.sh <script.sql> [overrides-file]}"
overrides="${2:-$(dirname "$0")/sql/migration-overrides.txt}"
[[ -s "$sql" ]] || { echo "::error::check-migration-sql: '$sql' is missing or empty - nothing was generated to check." >&2; exit 2; }
[[ -f "$overrides" ]] || { echo "::error::check-migration-sql: overrides file '$overrides' is missing." >&2; exit 2; }

perl - "$sql" "$overrides" <<'PERL'
use strict; use warnings FATAL => q(all);  # an undefined value is a bug in this check: fail, never pass
my ($sql, $overrides) = @ARGV;

my %override;
open my $o, '<', $overrides or die "check-migration-sql: cannot read $overrides\n";
while (<$o>) {
  s/\r?\n\z//; next if /^\s*(#|$)/;
  my ($id, $reason) = /^\s*(\S+)\s+(\S.*)$/ or do { print STDERR "::error::check-migration-sql: override '$_' needs a reason: <MigrationId> <reason>\n"; exit 2 };
  $id =~ /^\d{14}_\w+$/ or do { print STDERR "::error::check-migration-sql: override '$id' is not a migration id (yyyyMMddHHmmss_Name): only a migration can be exempted\n"; exit 2 };
  $override{$id} = $reason;
}

open my $f, '<:raw', $sql or die "check-migration-sql: cannot read $sql\n";
my $text = do { local $/; <$f> };
$text =~ s/\r//g;
$text =~ s/\A\xEF\xBB\xBF//;

# Splits SQL into statements at top-level semicolons, returning each as { text, mask }. A comment becomes a space; a
# string literal ('...', or E'...' with its backslash escapes), a quoted identifier ("...") and a dollar-quoted string
# ($tag$...$tag$, ASCII tags) is kept whole in the text, and in the mask is replaced by '' or "x", so nothing inside
# one ends a statement, hides the rest of a line or fakes a comma. Refused rather than guessed at: an unterminated
# literal, identifier, dollar quote or comment; a dollar-quote tag with a non-ASCII character; and any non-ASCII
# character outside a literal or quoted identifier (PostgreSQL would read it as part of a name, this lexer could not).
sub statements {
  my ($s) = @_;
  my @out; my ($cur, $mask) = ('', '');
  my $n = length $s; my $i = 0;
  while ($i < $n) {
    my $c = substr($s, $i, 1); my $d = substr($s, $i, 2);
    if ($d eq '--') { my $e = index($s, "\n", $i); $i = $e < 0 ? $n : $e; $cur .= ' '; $mask .= ' '; next; }
    if ($d eq '/*') {
      my $depth = 0;
      while ($i < $n) {
        if (substr($s, $i, 2) eq '/*') { $depth++; $i += 2; next; }
        if (substr($s, $i, 2) eq '*/') { $depth--; $i += 2; last if $depth == 0; next; }
        $i++;
      }
      die "unterminated comment\n" if $depth;
      $cur .= ' '; $mask .= ' '; next;
    }
    if ($c eq "'") {
      my $escapes = $cur =~ /(?:\A|[^A-Za-z0-9_])[Ee]\z/;
      my $j = $i + 1;
      while (1) {
        die "unterminated string literal\n" if $j >= $n;
        my $ch = substr($s, $j, 1);
        if ($escapes && $ch eq '\\') { $j += 2; next; }
        if ($ch eq "'") { if (substr($s, $j + 1, 1) eq "'") { $j += 2; next; } last; }
        $j++;
      }
      $cur .= substr($s, $i, $j - $i + 1); $mask .= "''"; $i = $j + 1; next;
    }
    if ($c eq '"') {
      my $j = $i + 1;
      while (1) {
        die "unterminated quoted identifier\n" if $j >= $n;
        if (substr($s, $j, 1) eq '"') { if (substr($s, $j + 1, 1) eq '"') { $j += 2; next; } last; }
        $j++;
      }
      $cur .= substr($s, $i, $j - $i + 1); $mask .= '"x"'; $i = $j + 1; next;
    }
    if ($c eq '$' && $cur !~ /[A-Za-z0-9_]\z/) {
      die "a dollar-quote tag with a non-ASCII character\n" if substr($s, $i + 1) =~ /\A[A-Za-z0-9_]*[^\x00-\x7F]/;
      if (substr($s, $i) =~ /\A(\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$)/) {
        my $tag = $1; my $e = index($s, $tag, $i + length $tag);
        die "unterminated dollar quote $tag\n" if $e < 0;
        # The body may be code (DO, a function) run now or later, so the mask keeps its own masked statements - or,
        # when it is not SQL at all, its raw text - for the lock to read.
        my $body = substr($s, $i + length $tag, $e - $i - length $tag);
        my @inner = eval { statements($body) };
        my $inner = $@ ? $body : join('; ', map { $_->{mask} } @inner);
        $cur .= substr($s, $i, $e + length($tag) - $i); $mask .= "\$\$ $inner \$\$"; $i = $e + length $tag; next;
      }
    }
    die "a non-ASCII character outside a quoted string or identifier\n" if ord($c) > 127;
    if ($c eq ';') { push @out, [$cur, $mask]; ($cur, $mask) = ('', ''); $i++; next; }
    $cur .= $c; $mask .= $c; $i++;
  }
  push @out, [$cur, $mask];
  my @r;
  for my $o (@out) {
    my ($t, $m) = @$o;
    for ($t, $m) { s/\s+/ /g; s/\A //; s/ \z//; }
    push @r, { text => $t, mask => $m } if length $t;
  }
  return @r;
}

# True when the masked text has a comma outside parentheses: a second action in the same ALTER TABLE.
sub top_level_comma {
  my ($m) = @_;
  my $depth = 0;
  for my $ch (split //, $m) { $depth++ if $ch eq '('; $depth-- if $ch eq ')'; return 1 if $ch eq ',' && $depth == 0; }
  return 0;
}

my $ident = qr/"(?:[^"]|"")+"/;
# Shapes are matched on the mask (literals and names replaced), except the two that must match exact text.
sub allowed_in_migration {
  my ($s, $m, $id) = @_;
  return 1 if $m =~ /\ACREATE TABLE "x" \(/;
  if ($m =~ /\ACREATE (?:UNIQUE )?INDEX "x" ON "x" (?:USING \w+ )?\(.*\)(?: WHERE (.+))?\z/s) {
    # A filtered index (EF's HasFilter): a read-only predicate, with no query inside it.
    return $m =~ /\bSELECT\b/i ? 0 : 1;
  }
  if ($m =~ /\AALTER TABLE "x" ADD (?:COLUMN )?(?!CONSTRAINT )"x" (.*)\z/s) {
    my $def = $1;
    return 0 if top_level_comma($def) || $def =~ /\b(?:CHECK|GENERATED)\b/i;
    return 0 if $def =~ /\bNOT NULL\b/i && $def !~ /\bDEFAULT\b/i;
    return 1;
  }
  if ($m =~ /\AALTER TABLE "x" ADD CONSTRAINT "x" (?:FOREIGN KEY|UNIQUE|PRIMARY KEY) (.*)\z/s) {
    return top_level_comma($1) ? 0 : 1;
  }
  return 1 if $s =~ /\AINSERT INTO "__EFMigrationsHistory" \("MigrationId", "ProductVersion"\) VALUES \('\Q$id\E', '[0-9A-Za-z.+-]+'\)\z/;
  return 1 if $s =~ /\ACREATE (?:OR REPLACE )?FUNCTION \w+\(\) RETURNS trigger LANGUAGE plpgsql AS \$\$ BEGIN RAISE EXCEPTION '(?:[^']|'')*'(?:, \w+)?; END; \$\$\z/;
  return 1 if $s =~ /\ACREATE TRIGGER $ident BEFORE (?:UPDATE|DELETE|TRUNCATE)(?: OR (?:UPDATE|DELETE|TRUNCATE))* ON $ident FOR EACH (?:ROW|STATEMENT) EXECUTE FUNCTION \w+\(\)\z/;
  return 0;
}
sub allowed_outside {
  my ($s) = @_;
  return 1 if $s eq 'START TRANSACTION' || $s eq 'COMMIT';
  return 1 if $s eq 'CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ( "MigrationId" character varying(150) NOT NULL, "ProductVersion" character varying(32) NOT NULL, CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId") )';
  return 1 if $s =~ /\AINSERT INTO "Accounts" \("AccountId", "Name"\) VALUES \('[0-9a-f-]{36}', '(?:[^']|'')*'\) ON CONFLICT \("AccountId"\) DO NOTHING\z/;
  return 1 if $s =~ /\AINSERT INTO "Users" \("UserId", "AccountId", "Name"\) VALUES \('[0-9a-f-]{36}', '[0-9a-f-]{36}', '(?:[^']|'')*'\) ON CONFLICT \("UserId"\) DO NOTHING\z/;
  return 1 if $s =~ /\ASELECT concat\('apiary-copilot-migration-', 'applied ', '[0-9A-Za-z.+-]+'\) AS migration\z/;
  return 0;
}
my $append_only = qr/(?<![\w"])"?(ColonyEvents|Inspections)"?(?![\w"])/;
# Why a refused statement can never be overridden, or nothing. The names are matched in the text, literals included;
# the rest in the mask, outside literals: each could name a table without spelling it.
sub locked {
  my ($s, $m) = @_;
  return "touches $1, which is append-only - correct its history with a compensating event" if $s =~ $append_only;
  return "uses dynamic SQL (EXECUTE)" if $m =~ /\bEXECUTE\b(?!\s+(?:FUNCTION|PROCEDURE)\b)/i;
  return "uses a U& escape" if $m =~ /\bU&["']/i;
  return "builds text with ||" if $m =~ /\|\|/;
  return "builds a name with " . lc($1) if $m =~ /\b(format|quote_ident|chr)\s*\(/i;
  return undef;
}
sub short { my $s = shift; return length $s > 160 ? substr($s, 0, 157) . '...' : $s; }

my @top = eval { statements($text) };
if ($@) { chomp(my $e = $@); print "::error::check-migration-sql: $sql cannot be read as SQL: $e\n"; exit 2; }
my ($findings, $checked, $blocks) = (0, 0, 0);
for my $top (@top) {
  my $st = $top->{text};
  $checked++;
  if ($st =~ /\ADO \$EF\$(.*)\$EF\$\z/s) {
    my ($id, $inner) = $1 =~ /\A ?BEGIN IF NOT EXISTS ?\( ?SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '(\d{14}_\w+)' ?\) THEN (.*) END IF; END ?\z/s;
    if (!defined $id) {
      print "::error::check-migration-sql: (unattributed): a DO block that is not EF's migration block - refused, no override applies: " . short($st) . "\n";
      $findings++; next;
    }
    $blocks++;
    my @inner = eval { statements($inner) };
    if ($@) { chomp(my $e = $@); print "::error::check-migration-sql: $id cannot be read as SQL: $e\n"; $findings++; next; }
    for my $one (@inner) {
      my ($s, $m) = ($one->{text}, $one->{mask});
      $checked++;
      next if allowed_in_migration($s, $m, $id);
      if (defined(my $why = locked($s, $m))) {
        print "::error::check-migration-sql: $id: refused, and it $why; no override applies: " . short($s) . "\n";
        $findings++; next;
      }
      if (exists $override{$id}) {
        print "::warning::check-migration-sql: $id: not an additive shape, allowed by its reviewed override ($override{$id}): " . short($s) . "\n";
        next;
      }
      print "::error::check-migration-sql: $id: not an additive migration shape: " . short($s) . " - make it additive, or add a reviewed override (scripts/sql/migration-overrides.txt)\n";
      $findings++;
    }
    next;
  }
  next if allowed_outside($st);
  print "::error::check-migration-sql: (script): a statement outside any migration that is not the history table, a transaction, the seed or the closing line - refused, no override applies: " . short($st) . "\n";
  $findings++;
}
if ($checked == 0) { print STDERR "::error::check-migration-sql: no statements found in $sql\n"; exit 2; }
if ($findings) { print "check-migration-sql: $findings finding(s) in $sql\n"; exit 1; }
print "check-migration-sql: $sql is additive ($checked statements, $blocks migration blocks)\n";
exit 0;
PERL
