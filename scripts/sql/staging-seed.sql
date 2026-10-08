-- The one synthetic account staging acts for, and its one synthetic user (DEPLOYMENT.md §3). Synthetic data only
-- (NFR-11); never run on production. Since a separate change nothing configures the account: a signed-in caller acts for its
-- token's account_id - this account is the dev realm's synthetic owner's - as the user whose ExternalSubject is its sub.
-- This user has none until it is set (piece 4's invitation), and the rollback test drives older images by these ids.
-- Idempotent: it inserts each row once and never rewrites one that exists.
INSERT INTO "Accounts" ("AccountId", "Name")
VALUES ('a91a7c0e-5747-4e00-8000-000000000019', 'Synthetic Staging Apiary')
ON CONFLICT ("AccountId") DO NOTHING;
INSERT INTO "Users" ("UserId", "AccountId", "Name")
VALUES ('a91a7c0e-5747-4e00-8000-000000000042', 'a91a7c0e-5747-4e00-8000-000000000019', 'Synthetic Staging Beekeeper')
ON CONFLICT ("UserId") DO NOTHING;
