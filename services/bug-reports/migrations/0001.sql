CREATE TABLE IF NOT EXISTS reports (
 id TEXT PRIMARY KEY, secret_hash TEXT NOT NULL, content_hash TEXT NOT NULL,
 ip_key TEXT NOT NULL, hour INTEGER NOT NULL, day INTEGER NOT NULL,
 created INTEGER NOT NULL, body TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS reports_created ON reports(created);
CREATE INDEX IF NOT EXISTS reports_ip_hour ON reports(ip_key,hour);
CREATE INDEX IF NOT EXISTS reports_ip_day ON reports(ip_key,day);
CREATE INDEX IF NOT EXISTS reports_day ON reports(day);
CREATE TABLE IF NOT EXISTS outbox (
 report_id TEXT PRIMARY KEY REFERENCES reports(id) ON DELETE CASCADE,
 attempts INTEGER NOT NULL DEFAULT 0, next_attempt INTEGER NOT NULL,
 lease_until INTEGER NOT NULL DEFAULT 0, sent INTEGER, failed INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS outbox_pending ON outbox(sent,failed,next_attempt,lease_until);
CREATE TABLE IF NOT EXISTS budgets (key TEXT PRIMARY KEY, count INTEGER NOT NULL, expires INTEGER NOT NULL);
-- Trigger checks and insertion are one SQLite transaction, including the outbox.
CREATE TRIGGER IF NOT EXISTS report_quota BEFORE INSERT ON reports
 WHEN NOT EXISTS(SELECT 1 FROM reports WHERE id=NEW.id) AND (
   (SELECT COUNT(*) FROM reports WHERE day=NEW.day)>=50
   OR (SELECT COUNT(*) FROM reports WHERE ip_key=NEW.ip_key AND hour=NEW.hour)>=3
   OR (SELECT COUNT(*) FROM reports WHERE ip_key=NEW.ip_key AND day=NEW.day)>=10)
 BEGIN SELECT RAISE(ABORT,'REPORT_QUOTA'); END;
CREATE TRIGGER IF NOT EXISTS report_outbox AFTER INSERT ON reports
 BEGIN INSERT INTO outbox(report_id,next_attempt) VALUES(NEW.id,NEW.created); END;
