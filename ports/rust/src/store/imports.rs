//! The import rules (ImportStore.cs in the reference; ports/go/store/imports.go): each import a complete copy, made
//! active in one conditional transaction; every import's row stays as the history; records kept for the active import,
//! the one before it and one held for review; one import per source at a time (an application lock).

use chrono::NaiveDate;
use sha2::{Digest, Sha256};
use tiberius::ToSql;

use super::db::{Connection, Database, DbResult};
use super::queries::{self, execute, int, optional_int, optional_text, rows, simple, text, time, HistoryEntry, Subject, SubjectAddress, SubjectName, Time};

const MAX_EXTERNAL_ID: usize = 200;
const MAX_DROP: f64 = 0.20;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ImportResult {
    Imported,
    Unchanged,
    Rejected,
    HeldForReview,
}

impl ImportResult {
    pub fn as_str(&self) -> &'static str {
        match self {
            ImportResult::Imported => "Imported",
            ImportResult::Unchanged => "Unchanged",
            ImportResult::Rejected => "Rejected",
            ImportResult::HeldForReview => "HeldForReview",
        }
    }
}

#[derive(Debug, Clone)]
pub struct ImportOutcome {
    pub result: ImportResult,
    pub record_count: usize,
    pub previous_count: Option<i32>,
    pub message: String,
    pub import_id: Option<i32>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ReviewResult {
    Confirmed,
    Rejected,
    NotFound,
    NotPending,
}

impl ReviewResult {
    pub fn as_str(&self) -> &'static str {
        match self {
            ReviewResult::Confirmed => "Confirmed",
            ReviewResult::Rejected => "Rejected",
            ReviewResult::NotFound => "NotFound",
            ReviewResult::NotPending => "NotPending",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Mode {
    Feed,
    Forced,
    Manual,
}

/// A source as the import rules need it.
#[derive(Debug, Clone)]
pub struct SourceRecord {
    pub id: i32,
    pub code: String,
    pub name: String,
    pub url: Option<String>,
    pub max_age_hours: Option<i32>,
    pub active_import_id: Option<i32>,
    pub last_checked_at: Option<Time>,
}

/// Runs the import rules against the database.
pub struct Imports {
    pub db: Database,
}

fn now() -> Time {
    chrono::Utc::now()
}

async fn find_source(conn: &mut Connection, where_clause: &str, key: &dyn ToSql) -> DbResult<Option<SourceRecord>> {
    let found = rows(conn, &format!("SELECT Id, Code, Name, Url, MaxAgeHours, ActiveImportId, LastCheckedAt FROM Sources WHERE {where_clause}"), &[key]).await?;
    Ok(found.first().map(|r| SourceRecord {
        id: int(r, 0),
        code: text(r, 1),
        name: text(r, 2),
        url: optional_text(r, 3),
        max_age_hours: optional_int(r, 4),
        active_import_id: optional_int(r, 5),
        last_checked_at: time(r, 6),
    }))
}

struct ImportRow {
    id: i32,
    content_hash: Option<String>,
    record_count: i32,
}

struct ReviewStamp {
    reviewer: String,
    at: Time,
    message: String,
}

impl Imports {
    pub async fn ensure_source(&self, code: &str, name: &str, url: Option<&str>, max_age_hours: Option<i32>) -> DbResult<SourceRecord> {
        let mut conn = self.db.connect().await?;
        match find_source(&mut conn, "Code = @P1", &code).await? {
            None => execute(&mut conn, "INSERT INTO Sources (Code, Name, Url, MaxAgeHours) VALUES (@P1, @P2, @P3, @P4)", &[&code, &name, &url, &max_age_hours]).await?,
            Some(existing) => execute(&mut conn, "UPDATE Sources SET Name = @P1, Url = @P2, MaxAgeHours = @P3 WHERE Id = @P4", &[&name, &url, &max_age_hours, &existing.id]).await?,
        };
        Ok(find_source(&mut conn, "Code = @P1", &code).await?.expect("just written"))
    }

    pub async fn find_source(&self, code: &str) -> DbResult<Option<SourceRecord>> {
        let mut conn = self.db.connect().await?;
        find_source(&mut conn, "Code = @P1", &code).await
    }

    /// Imports subjects as the source's new list (force: past the drop guard, and even when unchanged).
    pub async fn import(&self, source: &mut SourceRecord, subjects: Vec<Subject>, force: bool) -> DbResult<ImportOutcome> {
        self.locked(source, Some(subjects), None, if force { Mode::Forced } else { Mode::Feed }).await
    }

    /// A manual change: change gets the active list under the source's lock, and its result becomes the list.
    pub async fn change(&self, source: &mut SourceRecord, change: impl FnOnce(Vec<Subject>) -> Vec<Subject>) -> DbResult<ImportOutcome> {
        self.locked(source, None, Some(Box::new(change)), Mode::Manual).await
    }

    /// Records a refresh that failed before it could import.
    pub async fn record_failure(&self, source: &SourceRecord, message: &str) -> DbResult<()> {
        let mut conn = self.db.connect().await?;
        let at = now().fixed_offset();
        execute(
            &mut conn,
            "INSERT INTO Imports (SourceId, StartedAt, CompletedAt, Status, Message, RecordCount, HasData) VALUES (@P1, @P2, @P2, 'Failed', @P3, 0, 0)",
            &[&source.id, &at, &truncate(message, 1000)],
        )
        .await?;
        Ok(())
    }

    #[allow(clippy::type_complexity)]
    async fn locked(
        &self,
        source: &mut SourceRecord,
        subjects: Option<Vec<Subject>>,
        change: Option<Box<dyn FnOnce(Vec<Subject>) -> Vec<Subject> + '_>>,
        mode: Mode,
    ) -> DbResult<ImportOutcome> {
        let mut conn = self.db.connect().await?;
        acquire_lock(&mut conn, &source.code).await?;
        let result = self.locked_work(&mut conn, source, subjects, change, mode).await;
        release_lock(&mut conn, &source.code).await;
        result
    }

    #[allow(clippy::type_complexity)]
    async fn locked_work(
        &self,
        conn: &mut Connection,
        source: &mut SourceRecord,
        subjects: Option<Vec<Subject>>,
        change: Option<Box<dyn FnOnce(Vec<Subject>) -> Vec<Subject> + '_>>,
        mode: Mode,
    ) -> DbResult<ImportOutcome> {
        *source = find_source(conn, "Id = @P1", &source.id).await?.ok_or("The source went away.")?;
        let subjects = match subjects {
            Some(subjects) => subjects,
            None => {
                let active = match source.active_import_id {
                    Some(id) => queries::read_subjects(conn, id).await?,
                    None => Vec::new(),
                };
                (change.expect("a change"))(active)
            }
        };
        let records = deduplicate(subjects);
        if let Some(r) = records.iter().find(|r| r.external_id.encode_utf16().count() > MAX_EXTERNAL_ID) {
            let start: String = r.external_id.chars().take(40).collect();
            return Err(format!("an external id is longer than {MAX_EXTERNAL_ID} characters: {start}…").into());
        }
        let hash = canonical_hash(&records);
        let active = match source.active_import_id {
            Some(id) => {
                let found = rows(conn, "SELECT Id, ContentHash, RecordCount FROM Imports WHERE Id = @P1", &[&id]).await?;
                found.first().map(|r| ImportRow { id: int(r, 0), content_hash: optional_text(r, 1), record_count: int(r, 2) })
            }
            None => None,
        };
        let started = now();
        let held = held_imports(conn, source.id).await?;
        let previous_count = active.as_ref().map(|a| a.record_count);

        if mode != Mode::Forced && active.as_ref().is_some_and(|a| a.content_hash.as_deref() == Some(hash.as_str())) {
            let active = active.unwrap();
            if mode == Mode::Feed {
                in_transaction(conn, async |conn| {
                    execute(conn, "UPDATE Sources SET LastCheckedAt = @P1 WHERE Id = @P2", &[&started.fixed_offset(), &source.id]).await?;
                    supersede_held(conn, source.id, None, "superseded: the feed returned to the active list").await
                })
                .await?;
                delete_old_imports(conn, source.id).await?;
            }
            return Ok(ImportOutcome {
                result: ImportResult::Unchanged,
                record_count: active.record_count as usize,
                previous_count,
                message: "Unchanged since the active import".into(),
                import_id: None,
            });
        }

        if records.is_empty() {
            execute(
                conn,
                "INSERT INTO Imports (SourceId, StartedAt, CompletedAt, ContentHash, RecordCount, Status, Message, HasData) VALUES (@P1, @P2, @P2, @P3, 0, 'Rejected', 'Zero records', 0)",
                &[&source.id, &started.fixed_offset(), &hash],
            )
            .await?;
            return Ok(ImportOutcome {
                result: ImportResult::Rejected,
                record_count: 0,
                previous_count,
                message: "Zero records; the active list is kept".into(),
                import_id: None,
            });
        }

        let drop = match &active {
            Some(a) if mode == Mode::Feed && (records.len() as f64) < a.record_count as f64 * (1.0 - MAX_DROP) => {
                // The reference formats MaxDrop with P0 in the invariant culture: "20 %".
                Some(format!("Records dropped from {} to {}, more than 20 %: held for review", a.record_count, records.len()))
            }
            _ => None,
        };
        if drop.is_some() && held.first().is_some_and(|h| h.content_hash.as_deref() == Some(hash.as_str())) {
            let id = held[0].id;
            return Ok(ImportOutcome {
                result: ImportResult::HeldForReview,
                record_count: records.len(),
                previous_count,
                message: format!("Still held for review (import {id}); the active list is kept"),
                import_id: Some(id),
            });
        }

        let based_on = if drop.is_some() { active.as_ref().map(|a| a.id) } else { None };
        let inserted = rows(
            conn,
            "INSERT INTO Imports (SourceId, StartedAt, ContentHash, RecordCount, Status, HasData, BasedOnImportId) OUTPUT INSERTED.Id VALUES (@P1, @P2, @P3, @P4, 'Running', 1, @P5)",
            &[&source.id, &started.fixed_offset(), &hash, &(records.len() as i32), &based_on],
        )
        .await?;
        let import_id = int(&inserted[0], 0);
        let previous_active = source.active_import_id;
        let work = async {
            write_subjects(conn, source.id, import_id, &records).await?;
            count_changes(conn, import_id, active.as_ref().map(|a| a.id)).await;
            if let Some(drop) = &drop {
                let completed = now().fixed_offset();
                in_transaction(conn, async |conn| {
                    execute(conn, "UPDATE Imports SET Status = 'PendingReview', CompletedAt = @P1, Message = @P2 WHERE Id = @P3", &[&completed, drop, &import_id]).await?;
                    supersede_held(conn, source.id, Some(import_id), &format!("superseded by import {import_id}")).await
                })
                .await
            } else {
                activate(conn, source, import_id, previous_active, started, None).await
            }
        }
        .await;
        if let Err(e) = work {
            // The activation may have committed before the failure surfaced: if so, the import is active and whole.
            let _ = simple(conn, "IF @@TRANCOUNT > 0 ROLLBACK").await;
            let persisted = rows(conn, "SELECT ActiveImportId FROM Sources WHERE Id = @P1", &[&source.id]).await.ok().and_then(|r| r.first().and_then(|r| optional_int(r, 0)));
            if persisted != Some(import_id) {
                let _ = execute(conn, "UPDATE Imports SET Status = 'Failed', CompletedAt = @P1, Message = @P2 WHERE Id = @P3", &[&now().fixed_offset(), &"error", &import_id]).await;
                let _ = delete_import_data(conn, import_id).await;
                let _ = execute(conn, "UPDATE Imports SET HasData = 0 WHERE Id = @P1", &[&import_id]).await;
                return Err(e);
            }
        }
        delete_old_imports(conn, source.id).await?;
        Ok(match drop {
            Some(drop) => ImportOutcome {
                result: ImportResult::HeldForReview,
                record_count: records.len(),
                previous_count,
                message: format!("{drop} (import {import_id}); the active list is kept"),
                import_id: Some(import_id),
            },
            None => ImportOutcome { result: ImportResult::Imported, record_count: records.len(), previous_count, message: "Imported".into(), import_id: Some(import_id) },
        })
    }

    /// Makes a held import active (confirm) or rejects it and deletes its records, under the source's lock.
    pub async fn review(&self, import_id: i32, confirm: bool, reviewer: &str) -> DbResult<ReviewResult> {
        let mut conn = self.db.connect().await?;
        let found = rows(&mut conn, "SELECT s.Id, s.Code FROM Imports i JOIN Sources s ON s.Id = i.SourceId WHERE i.Id = @P1", &[&import_id]).await?;
        let Some(row) = found.first() else { return Ok(ReviewResult::NotFound) };
        let (source_id, code) = (int(row, 0), text(row, 1));
        acquire_lock(&mut conn, &code).await?;
        let result = review_locked(&mut conn, source_id, import_id, confirm, reviewer).await;
        release_lock(&mut conn, &code).await;
        result
    }

    /// Who this connection is signed in as: the reviewer the CLI records.
    pub async fn current_login(&self) -> String {
        let Ok(mut conn) = self.db.connect().await else { return "unknown".into() };
        rows(&mut conn, "SELECT SUSER_SNAME()", &[]).await.ok().and_then(|r| r.first().and_then(|r| optional_text(r, 0))).unwrap_or_else(|| "unknown".into())
    }

    pub async fn status(&self) -> DbResult<Vec<SourceStatus>> {
        let mut conn = self.db.connect().await?;
        let found = rows(
            &mut conn,
            "SELECT s.Code, a.RecordCount, a.CompletedAt, s.LastCheckedAt, s.MaxAgeHours, l.Status, l.Message
FROM Sources s
LEFT JOIN Imports a ON a.Id = s.ActiveImportId
OUTER APPLY (SELECT TOP 1 Status, Message FROM Imports i WHERE i.SourceId = s.Id ORDER BY i.Id DESC) l
ORDER BY s.Code",
            &[],
        )
        .await?;
        Ok(found
            .iter()
            .map(|r| SourceStatus {
                code: text(r, 0),
                record_count: optional_int(r, 1),
                imported_at: time(r, 2),
                last_checked_at: time(r, 3),
                max_age_hours: optional_int(r, 4),
                last_import_status: optional_text(r, 5),
                last_import_message: optional_text(r, 6),
            })
            .collect())
    }

    /// Each source's latest imports (or code's), newest first, per_source each.
    pub async fn history_per_source(&self, code: &str, per_source: i32) -> DbResult<Vec<HistoryEntry>> {
        let mut conn = self.db.connect().await?;
        let sql = format!(
            "SELECT {}\nFROM Sources s CROSS APPLY (SELECT TOP (@P1) * FROM Imports x WHERE x.SourceId = s.Id ORDER BY x.Id DESC) i\nWHERE @P2 = N'' OR s.Code = @P2\nORDER BY s.Code, i.Id DESC",
            queries::HISTORY_COLUMNS
        );
        Ok(rows(&mut conn, &sql, &[&per_source, &code]).await?.iter().map(queries::history_entry).collect())
    }

    pub async fn held(&self) -> DbResult<Vec<HistoryEntry>> {
        let mut conn = self.db.connect().await?;
        queries::held(&mut conn).await
    }
}

/// A source with its active import, for the CLI's status.
#[derive(Debug, Clone)]
pub struct SourceStatus {
    pub code: String,
    pub record_count: Option<i32>,
    pub imported_at: Option<Time>,
    pub last_checked_at: Option<Time>,
    pub max_age_hours: Option<i32>,
    pub last_import_status: Option<String>,
    pub last_import_message: Option<String>,
}

async fn review_locked(conn: &mut Connection, source_id: i32, import_id: i32, confirm: bool, reviewer: &str) -> DbResult<ReviewResult> {
    let source = find_source(conn, "Id = @P1", &source_id).await?.ok_or("The source went away.")?;
    let found = rows(conn, "SELECT Status, HasData, Message, BasedOnImportId, StartedAt FROM Imports WHERE Id = @P1", &[&import_id]).await?;
    let row = found.first().ok_or("The import went away.")?;
    let (status, has_data, message, based_on, started_at) =
        (text(row, 0), row.get::<bool, _>(1).unwrap_or_default(), optional_text(row, 2), optional_int(row, 3), time(row, 4).unwrap_or_default());
    if status != "PendingReview" || !has_data {
        return Ok(ReviewResult::NotPending);
    }
    let at = now();
    if !confirm {
        reject_held(conn, import_id, message.as_deref(), "rejected on review", at, Some(reviewer)).await?;
        return Ok(ReviewResult::Rejected);
    }
    if based_on != source.active_import_id {
        reject_held(conn, import_id, message.as_deref(), "superseded: the list changed after it was held", at, None).await?;
        return Ok(ReviewResult::NotPending);
    }
    let stamp = ReviewStamp {
        reviewer: truncate(reviewer, 256),
        at,
        message: truncate(&format!("{}; confirmed on review", message.unwrap_or_default()), 1000),
    };
    activate(conn, &source, import_id, source.active_import_id, started_at, Some(stamp)).await?;
    delete_old_imports(conn, source.id).await?;
    Ok(ReviewResult::Confirmed)
}

/// Marks the import Rejected first, then deletes its records: an interrupted deletion leaves a rejected import, never a
/// held one that could still be confirmed with part of its records.
async fn reject_held(conn: &mut Connection, import_id: i32, message: Option<&str>, reason: &str, at: Time, reviewer: Option<&str>) -> DbResult<()> {
    let text = truncate(&format!("{}; {reason}", message.unwrap_or_default()), 1000);
    match reviewer {
        Some(by) => {
            execute(
                conn,
                "UPDATE Imports SET Status = 'Rejected', Message = @P1, ReviewedAt = @P2, ReviewedBy = @P3 WHERE Id = @P4",
                &[&text, &at.fixed_offset(), &truncate(by, 256), &import_id],
            )
            .await?
        }
        None => execute(conn, "UPDATE Imports SET Status = 'Rejected', Message = @P1 WHERE Id = @P2", &[&text, &import_id]).await?,
    };
    delete_import_data(conn, import_id).await?;
    execute(conn, "UPDATE Imports SET HasData = 0 WHERE Id = @P1", &[&import_id]).await?;
    Ok(())
}

async fn held_imports(conn: &mut Connection, source_id: i32) -> DbResult<Vec<ImportRow>> {
    Ok(rows(conn, "SELECT Id, ContentHash, RecordCount FROM Imports WHERE SourceId = @P1 AND Status = 'PendingReview' ORDER BY Id DESC", &[&source_id])
        .await?
        .iter()
        .map(|r| ImportRow { id: int(r, 0), content_hash: optional_text(r, 1), record_count: int(r, 2) })
        .collect())
}

async fn in_transaction(conn: &mut Connection, work: impl AsyncFnOnce(&mut Connection) -> DbResult<()>) -> DbResult<()> {
    simple(conn, "BEGIN TRANSACTION").await?;
    match work(conn).await {
        Ok(()) => simple(conn, "COMMIT").await,
        Err(e) => {
            let _ = simple(conn, "IF @@TRANCOUNT > 0 ROLLBACK").await;
            Err(e)
        }
    }
}

/// The flip, in one transaction: the source moves to the new import only if its active import is still the one this
/// import started from, and the import completes.
async fn activate(conn: &mut Connection, source: &SourceRecord, import_id: i32, expected: Option<i32>, checked_at: Time, review: Option<ReviewStamp>) -> DbResult<()> {
    let completed = now().fixed_offset();
    in_transaction(conn, async |conn| {
        let flipped = execute(
            conn,
            "UPDATE Sources SET ActiveImportId = @P1, LastCheckedAt = @P2 WHERE Id = @P3 AND (ActiveImportId = @P4 OR (ActiveImportId IS NULL AND @P4 IS NULL))",
            &[&import_id, &checked_at.fixed_offset(), &source.id, &expected],
        )
        .await?;
        if flipped == 0 {
            return Err(format!("another import of {} became active meanwhile; nothing was changed. Run it again", source.code).into());
        }
        execute(conn, "UPDATE Imports SET Status = 'Completed', CompletedAt = @P1 WHERE Id = @P2", &[&completed, &import_id]).await?;
        if let Some(review) = &review {
            execute(
                conn,
                "UPDATE Imports SET ReviewedBy = @P1, ReviewedAt = @P2, Message = @P3 WHERE Id = @P4",
                &[&review.reviewer, &review.at.fixed_offset(), &review.message, &import_id],
            )
            .await?;
        }
        supersede_held(conn, source.id, Some(import_id), &format!("superseded by import {import_id}")).await
    })
    .await
}

async fn supersede_held(conn: &mut Connection, source_id: i32, except_id: Option<i32>, reason: &str) -> DbResult<()> {
    execute(
        conn,
        "UPDATE Imports SET Status = 'Rejected', Message = SUBSTRING(COALESCE(Message, N'') + N'; ' + @P1, 1, 1000)
WHERE SourceId = @P2 AND Status = 'PendingReview' AND (@P3 IS NULL OR Id <> @P3)",
        &[&reason, &source_id, &except_id],
    )
    .await?;
    Ok(())
}

/// Keeps the records of the active import, the one before it, and any held for review.
async fn delete_old_imports(conn: &mut Connection, source_id: i32) -> DbResult<()> {
    let active = rows(conn, "SELECT ActiveImportId FROM Sources WHERE Id = @P1", &[&source_id]).await?.first().and_then(|r| optional_int(r, 0));
    let previous = rows(
        conn,
        "SELECT TOP 1 Id FROM Imports WHERE SourceId = @P1 AND Status = 'Completed' AND (@P2 IS NULL OR Id <> @P2) ORDER BY Id DESC",
        &[&source_id, &active],
    )
    .await?
    .first()
    .map(|r| int(r, 0));
    let old: Vec<i32> = rows(
        conn,
        "SELECT Id FROM Imports WHERE SourceId = @P1 AND HasData = 1 AND (@P2 IS NULL OR Id <> @P2) AND (@P3 IS NULL OR Id <> @P3) AND Status <> 'PendingReview'",
        &[&source_id, &active, &previous],
    )
    .await?
    .iter()
    .map(|r| int(r, 0))
    .collect();
    for id in old {
        delete_import_data(conn, id).await?;
        execute(conn, "UPDATE Imports SET HasData = 0 WHERE Id = @P1", &[&id]).await?;
    }
    Ok(())
}

/// Deletes an import's names, addresses and subjects in chunks.
async fn delete_import_data(conn: &mut Connection, import_id: i32) -> DbResult<()> {
    for statement in [
        "DELETE TOP (20000) n FROM SubjectNames n JOIN Subjects s ON s.Id = n.SubjectId WHERE s.ImportId = @P1",
        "DELETE TOP (20000) a FROM SubjectAddresses a JOIN Subjects s ON s.Id = a.SubjectId WHERE s.ImportId = @P1",
        "DELETE TOP (20000) FROM Subjects WHERE ImportId = @P1",
    ] {
        while execute(conn, statement, &[&import_id]).await? > 0 {}
    }
    Ok(())
}

async fn acquire_lock(conn: &mut Connection, code: &str) -> DbResult<()> {
    let resource = format!("import:{code}");
    let result = rows(
        conn,
        "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @P1, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @result",
        &[&resource],
    )
    .await?;
    if int(&result[0], 0) < 0 {
        return Err(format!("another import of {code} is running").into());
    }
    Ok(())
}

async fn release_lock(conn: &mut Connection, code: &str) {
    let resource = format!("import:{code}");
    // Closing the connection releases it anyway.
    let _ = execute(conn, "IF APPLOCK_MODE('public', @P1, 'Session') <> 'NoLock' EXEC sp_releaseapplock @Resource = @P1, @LockOwner = 'Session'", &[&resource]).await;
}

/// Rows in one INSERT: SQL Server takes at most 2,100 parameters and 1,000 rows a statement.
fn rows_per_statement(columns: usize) -> usize {
    (2000 / columns).min(1000)
}

/// Inserts rows in multi-row statements (the reference bulk-copies; the rows end up the same).
async fn insert_rows(conn: &mut Connection, table: &str, columns: &[&str], values: &[Vec<Box<dyn ToSql + '_>>]) -> DbResult<()> {
    for chunk in values.chunks(rows_per_statement(columns.len())) {
        let mut sql = format!("INSERT INTO {table} ({}) VALUES ", columns.join(", "));
        let mut params: Vec<&dyn ToSql> = Vec::with_capacity(chunk.len() * columns.len());
        for (r, row) in chunk.iter().enumerate() {
            if r > 0 {
                sql.push_str(", ");
            }
            sql.push('(');
            for (c, value) in row.iter().enumerate() {
                if c > 0 {
                    sql.push_str(", ");
                }
                sql.push_str(&format!("@P{}", params.len() + 1));
                params.push(value.as_ref());
            }
            sql.push(')');
        }
        execute(conn, &sql, &params).await?;
    }
    Ok(())
}

async fn write_subjects(conn: &mut Connection, source_id: i32, import_id: i32, records: &[Subject]) -> DbResult<()> {
    let subject_rows: Vec<Vec<Box<dyn ToSql>>> = records
        .iter()
        .map(|r| {
            vec![
                Box::new(import_id) as Box<dyn ToSql>,
                Box::new(source_id),
                Box::new(r.external_id.clone()),
                Box::new(r.kind),
                Box::new(truncate(&r.primary_name, 500)),
                Box::new(r.programmes.as_deref().map(|p| truncate(p, 1000))),
                Box::new(r.listed_on),
                Box::new(r.remarks.clone()),
                Box::new(r.source_url.as_deref().map(|u| truncate(u, 1000))),
            ]
        })
        .collect();
    insert_rows(conn, "Subjects", &["ImportId", "SourceId", "ExternalId", "Type", "PrimaryName", "Programmes", "ListedOn", "Remarks", "SourceUrl"], &subject_rows).await?;
    let ids: std::collections::HashMap<String, i32> =
        rows(conn, "SELECT Id, ExternalId FROM Subjects WHERE ImportId = @P1", &[&import_id]).await?.iter().map(|r| (text(r, 1), int(r, 0))).collect();
    let mut names: Vec<Vec<Box<dyn ToSql>>> = Vec::new();
    let mut addresses: Vec<Vec<Box<dyn ToSql>>> = Vec::new();
    for r in records {
        let id = ids.get(&r.external_id).copied().unwrap_or_default();
        for n in &r.names {
            names.push(vec![Box::new(id), Box::new(truncate(&n.full_name, 500)), Box::new(n.is_weak), Box::new(n.script)]);
        }
        for a in &r.addresses {
            addresses.push(vec![Box::new(id), Box::new(truncate(&a.raw, 1000)), Box::new(a.country_code.clone())]);
        }
    }
    insert_rows(conn, "SubjectNames", &["SubjectId", "FullName", "IsWeak", "Script"], &names).await?;
    insert_rows(conn, "SubjectAddresses", &["SubjectId", "Raw", "CountryCode"], &addresses).await
}

/// The reference's statement, verbatim (ImportStore.ChangeCountSql), its parameters numbered.
const CHANGE_COUNT_SQL: &str = "
        WITH Signatures AS (
            SELECT s.ImportId, s.ExternalId,
                HASHBYTES('SHA2_256', CONCAT(
                    CONVERT(nvarchar(3), s.Type), NCHAR(31),
                    s.PrimaryName, NCHAR(31),
                    ISNULL(N'+' + s.Programmes, N'-'), NCHAR(31),
                    ISNULL(N'+' + CONVERT(nvarchar(10), s.ListedOn, 23), N'-'), NCHAR(31),
                    ISNULL(N'+' + s.Remarks, N'-'), NCHAR(31),
                    ISNULL(N'+' + s.SourceUrl, N'-'), NCHAR(30),
                    (SELECT STRING_AGG(CONVERT(nvarchar(max), CONCAT(n.FullName, NCHAR(31), CONVERT(nvarchar(1), n.IsWeak), NCHAR(31), CONVERT(nvarchar(3), n.Script))), NCHAR(30))
                        WITHIN GROUP (ORDER BY n.FullName COLLATE Latin1_General_BIN2, n.IsWeak, n.Script)
                     FROM SubjectNames n WHERE n.SubjectId = s.Id), NCHAR(29),
                    (SELECT STRING_AGG(CONVERT(nvarchar(max), CONCAT(a.Raw, NCHAR(31), ISNULL(N'+' + a.CountryCode, N'-'))), NCHAR(29))
                        WITHIN GROUP (ORDER BY a.Raw COLLATE Latin1_General_BIN2, a.CountryCode COLLATE Latin1_General_BIN2)
                     FROM SubjectAddresses a WHERE a.SubjectId = s.Id))) AS Signature
            FROM Subjects s
            WHERE s.ImportId = @P1 OR s.ImportId = @P2),
        Counted AS (
            SELECT
                ISNULL(SUM(CASE WHEN o.ExternalId IS NULL THEN 1 ELSE 0 END), 0) AS Added,
                ISNULL(SUM(CASE WHEN n.ExternalId IS NULL THEN 1 ELSE 0 END), 0) AS Removed,
                ISNULL(SUM(CASE WHEN n.ExternalId IS NOT NULL AND o.ExternalId IS NOT NULL AND n.Signature <> o.Signature THEN 1 ELSE 0 END), 0) AS Changed
            FROM (SELECT ExternalId, Signature FROM Signatures WHERE ImportId = @P1) n
            FULL OUTER JOIN (SELECT ExternalId, Signature FROM Signatures WHERE ImportId = @P2) o
                ON o.ExternalId COLLATE Latin1_General_BIN2 = n.ExternalId COLLATE Latin1_General_BIN2 AND DATALENGTH(o.ExternalId) = DATALENGTH(n.ExternalId))
        UPDATE Imports SET AddedCount = Counted.Added, RemovedCount = Counted.Removed, ChangedCount = Counted.Changed
        FROM Counted
        WHERE Imports.Id = @P1;
        ";

/// Records what an import added, removed and changed; if counting fails, they stay null.
async fn count_changes(conn: &mut Connection, import_id: i32, previous: Option<i32>) {
    let _ = execute(conn, CHANGE_COUNT_SQL, &[&import_id, &previous]).await;
}

const TYPE_NAMES: [&str; 3] = ["Individual", "Entity", "Unknown"];
const SCRIPT_NAMES: [&str; 3] = ["Latin", "Cyrillic", "Other"];

fn name_of(names: &[&str], value: u8) -> String {
    names.get(value as usize).map(|n| n.to_string()).unwrap_or_else(|| value.to_string())
}

/// The reference's content hash: records sorted by external id, each with its content in a fixed order (unit
/// separators, .NET's spellings of the enums and booleans), so the same records hash the same in every implementation.
pub fn canonical_hash(subjects: &[Subject]) -> String {
    let mut sorted: Vec<&Subject> = subjects.iter().collect();
    sorted.sort_by(|a, b| a.external_id.cmp(&b.external_id));
    let mut hash = Sha256::new();
    let s = |v: &Option<String>| v.clone().unwrap_or_default();
    for subject in sorted {
        let listed_on = subject.listed_on.map(|d| d.format("%Y-%m-%d").to_string()).unwrap_or_default();
        let mut line = [
            subject.external_id.clone(),
            name_of(&TYPE_NAMES, subject.kind),
            subject.primary_name.clone(),
            s(&subject.programmes),
            listed_on,
            s(&subject.remarks),
            s(&subject.source_url),
        ]
        .join("\u{1f}");
        let mut names: Vec<&SubjectName> = subject.names.iter().collect();
        names.sort_by(|a, b| a.full_name.cmp(&b.full_name).then(a.is_weak.cmp(&b.is_weak)));
        for n in names {
            line += &format!("\u{1e}{}\u{1f}{}\u{1f}{}", n.full_name, if n.is_weak { "True" } else { "False" }, name_of(&SCRIPT_NAMES, n.script));
        }
        let mut addresses: Vec<&SubjectAddress> = subject.addresses.iter().collect();
        addresses.sort_by(|a, b| a.raw.cmp(&b.raw).then_with(|| a.country_code.cmp(&b.country_code)));
        for a in addresses {
            line += &format!("\u{1d}{}\u{1f}{}", a.raw, s(&a.country_code));
        }
        line.push('\n');
        hash.update(line.as_bytes());
    }
    hex::encode(hash.finalize())
}

/// One record per external id: the one that sorts first by its fields is the base, the others' names and addresses
/// join it. Ids keep their first appearance's order.
pub fn deduplicate(subjects: Vec<Subject>) -> Vec<Subject> {
    let mut order: Vec<String> = Vec::new();
    let mut groups: std::collections::HashMap<String, Vec<Subject>> = std::collections::HashMap::new();
    for s in subjects {
        if !groups.contains_key(&s.external_id) {
            order.push(s.external_id.clone());
        }
        groups.entry(s.external_id.clone()).or_default().push(s);
    }
    let mut result = Vec::with_capacity(order.len());
    for id in order {
        let mut group = groups.remove(&id).unwrap();
        // Option's order puts None first, as .NET's ordinal comparer puts null first.
        group.sort_by(|x, y| {
            x.primary_name
                .cmp(&y.primary_name)
                .then_with(|| x.programmes.cmp(&y.programmes))
                .then_with(|| x.listed_on.cmp(&y.listed_on))
                .then_with(|| x.remarks.cmp(&y.remarks))
                .then_with(|| x.source_url.cmp(&y.source_url))
                .then_with(|| x.kind.cmp(&y.kind))
        });
        let mut others = group.into_iter();
        let mut first = others.next().unwrap();
        for other in others {
            for name in other.names {
                match first.names.iter_mut().find(|n| n.full_name == name.full_name) {
                    Some(existing) => existing.is_weak = existing.is_weak && name.is_weak,
                    None => first.names.push(name),
                }
            }
            for address in other.addresses {
                if !first.addresses.iter().any(|a| a.raw == address.raw && a.country_code == address.country_code) {
                    first.addresses.push(address);
                }
            }
        }
        result.push(first);
    }
    result
}

/// Cuts to length UTF-16 units, as the reference's value[..length].
pub fn truncate(value: &str, length: usize) -> String {
    let units: Vec<u16> = value.encode_utf16().collect();
    if units.len() <= length {
        return value.to_string();
    }
    String::from_utf16_lossy(&units[..length])
}

#[allow(dead_code)]
fn date(value: &str) -> Option<NaiveDate> {
    NaiveDate::parse_from_str(value, "%Y-%m-%d").ok()
}
