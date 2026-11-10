//! What the API reads and writes: the sources, the history, the held updates, the allow-list, a list's subjects, and
//! the admin's switch (ports/go/store/store.go and admin.go, the reference's repositories).

use chrono::{DateTime, FixedOffset, NaiveDate, Utc};
use tiberius::{Row, ToSql};

use super::db::{Connection, DbResult};

pub type Time = DateTime<Utc>;

pub(crate) fn text(row: &Row, i: usize) -> String {
    row.get::<&str, _>(i).unwrap_or_default().to_string()
}

pub(crate) fn optional_text(row: &Row, i: usize) -> Option<String> {
    row.get::<&str, _>(i).map(str::to_string)
}

pub(crate) fn int(row: &Row, i: usize) -> i32 {
    row.get::<i32, _>(i).unwrap_or_default()
}

pub(crate) fn optional_int(row: &Row, i: usize) -> Option<i32> {
    row.get::<i32, _>(i)
}

pub(crate) fn time(row: &Row, i: usize) -> Option<Time> {
    row.get::<DateTime<FixedOffset>, _>(i).map(|t| t.with_timezone(&Utc))
}

/// Runs a query with parameters (@P1, @P2, …) and returns its first result's rows.
pub async fn rows(conn: &mut Connection, sql: &str, params: &[&dyn ToSql]) -> DbResult<Vec<Row>> {
    Ok(conn.query(sql, params).await?.into_first_result().await?)
}

/// Runs a statement with parameters and returns the rows it affected.
pub async fn execute(conn: &mut Connection, sql: &str, params: &[&dyn ToSql]) -> DbResult<u64> {
    Ok(conn.execute(sql, params).await?.total())
}

pub async fn simple(conn: &mut Connection, sql: &str) -> DbResult<()> {
    conn.simple_query(sql).await?.into_results().await?;
    Ok(())
}

/// A source with its active import's count and completion.
#[derive(Debug, Clone)]
pub struct SourceRow {
    pub id: i32,
    pub code: String,
    pub name: String,
    pub url: Option<String>,
    pub max_age_hours: Option<i32>,
    pub active_import_id: Option<i32>,
    pub last_checked_at: Option<Time>,
    pub switched_off_at: Option<Time>,
    pub switched_off_reason: Option<String>,
    pub record_count: i32,
    pub updated_at: Option<Time>,
}

/// Every source ordered by code (SQL Server's collation, as the reference's query orders them).
pub async fn sources(conn: &mut Connection) -> DbResult<Vec<SourceRow>> {
    let rows = rows(
        conn,
        "SELECT s.Id, s.Code, s.Name, s.Url, s.MaxAgeHours, s.ActiveImportId, s.LastCheckedAt, s.SwitchedOffAt, s.SwitchedOffReason,
       COALESCE(i.RecordCount, 0), i.CompletedAt
FROM Sources s LEFT JOIN Imports i ON i.Id = s.ActiveImportId
ORDER BY s.Code",
        &[],
    )
    .await?;
    Ok(rows
        .iter()
        .map(|r| SourceRow {
            id: int(r, 0),
            code: text(r, 1),
            name: text(r, 2),
            url: optional_text(r, 3),
            max_age_hours: optional_int(r, 4),
            active_import_id: optional_int(r, 5),
            last_checked_at: time(r, 6),
            switched_off_at: time(r, 7),
            switched_off_reason: optional_text(r, 8),
            record_count: int(r, 9),
            updated_at: time(r, 10),
        })
        .collect())
}

/// One import as the history shows it.
#[derive(Debug, Clone)]
pub struct HistoryEntry {
    pub id: i32,
    pub source_code: String,
    pub status: String,
    pub started_at: Time,
    pub completed_at: Option<Time>,
    pub record_count: i32,
    pub message: Option<String>,
    pub active: bool,
    pub reviewed_at: Option<Time>,
    pub reviewed_by: Option<String>,
    pub added: Option<i32>,
    pub removed: Option<i32>,
    pub changed: Option<i32>,
}

pub(crate) const HISTORY_COLUMNS: &str = "i.Id, s.Code, i.Status, i.StartedAt, i.CompletedAt, i.RecordCount, i.Message,
       CASE WHEN s.ActiveImportId = i.Id THEN 1 ELSE 0 END, i.ReviewedAt, i.ReviewedBy, i.AddedCount, i.RemovedCount, i.ChangedCount";

pub(crate) fn history_entry(r: &Row) -> HistoryEntry {
    HistoryEntry {
        id: int(r, 0),
        source_code: text(r, 1),
        status: text(r, 2),
        started_at: time(r, 3).unwrap_or_default(),
        completed_at: time(r, 4),
        record_count: int(r, 5),
        message: optional_text(r, 6),
        active: int(r, 7) == 1,
        reviewed_at: time(r, 8),
        reviewed_by: optional_text(r, 9),
        added: optional_int(r, 10),
        removed: optional_int(r, 11),
        changed: optional_int(r, 12),
    }
}

/// Every import of every source (or of code), sources by code, each newest first.
pub async fn history(conn: &mut Connection, code: &str) -> DbResult<Vec<HistoryEntry>> {
    let sql = format!("SELECT {HISTORY_COLUMNS}\nFROM Imports i JOIN Sources s ON s.Id = i.SourceId\nWHERE @P1 = N'' OR s.Code = @P1\nORDER BY s.Code, i.Id DESC");
    Ok(rows(conn, &sql, &[&code]).await?.iter().map(history_entry).collect())
}

/// Every import held for review (with its records), newest first.
pub async fn held(conn: &mut Connection) -> DbResult<Vec<HistoryEntry>> {
    let sql = format!("SELECT {HISTORY_COLUMNS}\nFROM Imports i JOIN Sources s ON s.Id = i.SourceId\nWHERE i.Status = 'PendingReview' AND i.HasData = 1\nORDER BY i.Id DESC");
    Ok(rows(conn, &sql, &[]).await?.iter().map(history_entry).collect())
}

/// An allow-list entry as stored.
#[derive(Debug, Clone)]
pub struct AllowListRecord {
    pub id: i32,
    pub name: String,
    pub reason: String,
    pub added_by: String,
    pub added_at: Time,
    pub removed_by: Option<String>,
    pub removed_at: Option<Time>,
}

/// Every entry ever added, newest first.
pub async fn allow_list_history(conn: &mut Connection) -> DbResult<Vec<AllowListRecord>> {
    let rows = rows(conn, "SELECT Id, Name, Reason, AddedBy, AddedAt, RemovedBy, RemovedAt FROM AllowListEntries ORDER BY Id DESC", &[]).await?;
    Ok(rows
        .iter()
        .map(|r| AllowListRecord {
            id: int(r, 0),
            name: text(r, 1),
            reason: text(r, 2),
            added_by: text(r, 3),
            added_at: time(r, 4).unwrap_or_default(),
            removed_by: optional_text(r, 5),
            removed_at: time(r, 6),
        })
        .collect())
}

/// The entries in force (by id) and the version: additions plus removals.
pub async fn allow_list_current(conn: &mut Connection) -> DbResult<(Vec<AllowListRecord>, String)> {
    let all = allow_list_history(conn).await?;
    let removed = all.iter().filter(|r| r.removed_at.is_some()).count();
    let in_force: Vec<AllowListRecord> = all.iter().rev().filter(|r| r.removed_at.is_none()).cloned().collect();
    Ok((in_force, (all.len() + removed).to_string()))
}

/// A listed subject as stored, with its names and addresses.
#[derive(Debug, Clone, Default)]
pub struct Subject {
    pub id: i32,
    pub external_id: String,
    pub kind: u8,
    pub primary_name: String,
    pub programmes: Option<String>,
    pub listed_on: Option<NaiveDate>,
    pub remarks: Option<String>,
    pub source_url: Option<String>,
    pub names: Vec<SubjectName>,
    pub addresses: Vec<SubjectAddress>,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct SubjectName {
    pub full_name: String,
    pub is_weak: bool,
    pub script: u8,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct SubjectAddress {
    pub raw: String,
    pub country_code: Option<String>,
}

/// Import import_id's subjects in one snapshot transaction, if the source still points at it (cleanup never deletes
/// the active import, so then nothing of it can be gone). None when it moved on.
pub async fn active_subjects(conn: &mut Connection, code: &str, import_id: i32) -> DbResult<Option<Vec<Subject>>> {
    simple(conn, "SET TRANSACTION ISOLATION LEVEL SNAPSHOT; BEGIN TRANSACTION").await?;
    let result = async {
        let still = rows(conn, "SELECT COUNT(*) FROM Sources WHERE Code = @P1 AND ActiveImportId = @P2", &[&code, &import_id]).await?;
        if int(&still[0], 0) == 0 {
            return Ok(None);
        }
        read_subjects(conn, import_id).await.map(Some)
    }
    .await;
    let end = if result.is_ok() { "COMMIT" } else { "IF @@TRANCOUNT > 0 ROLLBACK" };
    simple(conn, &format!("{end}; SET TRANSACTION ISOLATION LEVEL READ COMMITTED")).await?;
    result
}

pub(crate) async fn read_subjects(conn: &mut Connection, import_id: i32) -> DbResult<Vec<Subject>> {
    let subject_rows =
        rows(conn, "SELECT Id, ExternalId, Type, PrimaryName, Programmes, ListedOn, Remarks, SourceUrl FROM Subjects WHERE ImportId = @P1 ORDER BY Id", &[&import_id])
            .await?;
    let mut subjects: Vec<Subject> = Vec::with_capacity(subject_rows.len());
    let mut by_id = std::collections::HashMap::with_capacity(subject_rows.len());
    for r in &subject_rows {
        by_id.insert(int(r, 0), subjects.len());
        subjects.push(Subject {
            id: int(r, 0),
            external_id: text(r, 1),
            kind: r.get::<u8, _>(2).unwrap_or_default(),
            primary_name: text(r, 3),
            programmes: optional_text(r, 4),
            listed_on: r.get::<NaiveDate, _>(5),
            remarks: optional_text(r, 6),
            source_url: optional_text(r, 7),
            ..Default::default()
        });
    }
    drop(subject_rows);
    for r in rows(
        conn,
        "SELECT n.SubjectId, n.FullName, n.IsWeak, n.Script FROM SubjectNames n JOIN Subjects s ON s.Id = n.SubjectId WHERE s.ImportId = @P1 ORDER BY n.SubjectId, n.Id",
        &[&import_id],
    )
    .await?
    {
        if let Some(&i) = by_id.get(&int(&r, 0)) {
            subjects[i].names.push(SubjectName { full_name: text(&r, 1), is_weak: r.get::<bool, _>(2).unwrap_or_default(), script: r.get::<u8, _>(3).unwrap_or_default() });
        }
    }
    for r in rows(
        conn,
        "SELECT a.SubjectId, a.Raw, a.CountryCode FROM SubjectAddresses a JOIN Subjects s ON s.Id = a.SubjectId WHERE s.ImportId = @P1 ORDER BY a.SubjectId, a.Id",
        &[&import_id],
    )
    .await?
    {
        if let Some(&i) = by_id.get(&int(&r, 0)) {
            subjects[i].addresses.push(SubjectAddress { raw: text(&r, 1), country_code: optional_text(&r, 2) });
        }
    }
    Ok(subjects)
}

/// What switching a list did.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SwitchResult {
    Switched,
    Unchanged,
    NotFound,
}

/// Switches a list off (with a reason) or on, in one conditional update (SourceSwitch.cs).
pub async fn switch(conn: &mut Connection, code: &str, off: bool, reason: Option<&str>, by: &str, now: Time) -> DbResult<SwitchResult> {
    let changed = if off {
        execute(
            conn,
            "UPDATE Sources SET SwitchedOffAt = @P1, SwitchedOffBy = @P2, SwitchedOffReason = @P3 WHERE Code = @P4 AND SwitchedOffAt IS NULL",
            &[&now.fixed_offset(), &by, &reason, &code],
        )
        .await?
    } else {
        execute(conn, "UPDATE Sources SET SwitchedOffAt = NULL, SwitchedOffBy = NULL, SwitchedOffReason = NULL WHERE Code = @P1 AND SwitchedOffAt IS NOT NULL", &[&code]).await?
    };
    if changed > 0 {
        return Ok(SwitchResult::Switched);
    }
    let exists = rows(conn, "SELECT COUNT(*) FROM Sources WHERE Code = @P1", &[&code]).await?;
    Ok(if int(&exists[0], 0) > 0 { SwitchResult::Unchanged } else { SwitchResult::NotFound })
}

/// Adds an entry unless existing finds the name among the entries in force, checked and inserted under one lock held
/// to the end of the transaction (AllowListStore.AddAsync). The new id, or the existing one.
pub async fn allow_list_add(
    conn: &mut Connection,
    name: &str,
    reason: &str,
    added_by: &str,
    now: Time,
    existing: impl FnOnce(&[AllowListRecord]) -> Option<i32>,
) -> DbResult<(Option<i32>, Option<i32>)> {
    let (name, reason) = (name.trim(), reason.trim());
    simple(conn, "BEGIN TRANSACTION").await?;
    let result = async {
        let lock = rows(
            conn,
            "DECLARE @result int; EXEC @result = sp_getapplock @Resource = N'allow-list', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; SELECT @result",
            &[],
        )
        .await?;
        if int(&lock[0], 0) < 0 {
            return Err("The allow-list is locked: try again.".into());
        }
        let (in_force, _) = allow_list_current(conn).await?;
        if let Some(id) = existing(&in_force) {
            return Ok((None, Some(id)));
        }
        let inserted = rows(
            conn,
            "INSERT INTO AllowListEntries (Name, Reason, AddedBy, AddedAt) OUTPUT INSERTED.Id VALUES (@P1, @P2, @P3, @P4)",
            &[&name, &reason, &added_by, &now.fixed_offset()],
        )
        .await?;
        Ok((Some(int(&inserted[0], 0)), None))
    }
    .await;
    simple(conn, if matches!(result, Ok((Some(_), _))) { "COMMIT" } else { "IF @@TRANCOUNT > 0 ROLLBACK" }).await?;
    result
}

/// Marks an entry removed (entries are never deleted); false if there's no such entry in force.
pub async fn allow_list_remove(conn: &mut Connection, id: i32, removed_by: &str, now: Time) -> DbResult<bool> {
    Ok(execute(conn, "UPDATE AllowListEntries SET RemovedBy = @P1, RemovedAt = @P2 WHERE Id = @P3 AND RemovedAt IS NULL", &[&removed_by, &now.fixed_offset(), &id]).await? == 1)
}
