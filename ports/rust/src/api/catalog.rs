//! The lists in memory (ListCatalog and ListLoader in the reference): one snapshot of every source, swapped whole;
//! only the lists whose active import changed are rebuilt, one at a time.

use std::collections::HashMap;
use std::sync::{Arc, RwLock};

use chrono::Duration;
use tokio::sync::Mutex;

use crate::log::Logger;
use crate::matching::{AllowList, AllowListEntry, ListIndex, ListedAddress, ListedName, ListedSubject, SubjectKind};
use crate::store::db::{Database, DbResult};
use crate::store::queries::{self, HistoryEntry, Subject, Time};

/// When an administrator switched a list off, and why.
#[derive(Debug, Clone)]
pub struct SwitchedOff {
    pub at: Time,
    pub reason: String,
}

/// One list as the API holds it in memory.
#[derive(Clone)]
pub struct LoadedList {
    pub code: String,
    pub name: String,
    pub url: Option<String>,
    pub max_age_hours: Option<i32>,
    pub active_import_id: Option<i32>,
    pub last_checked_at: Option<Time>,
    pub updated_at: Option<Time>,
    pub record_count: i32,
    pub index: Option<Arc<ListIndex>>,
    pub switched_off: Option<SwitchedOff>,
}

impl LoadedList {
    /// Switched on, with an index. Fail closed.
    pub fn is_searched(&self) -> bool {
        self.switched_off.is_none() && self.index.is_some()
    }

    /// Searched, but a feed whose last successful check is older than its allowed age.
    pub fn is_overdue(&self, now: Time) -> bool {
        self.is_searched()
            && self
                .max_age_hours
                .is_some_and(|hours| self.last_checked_at.is_none_or(|checked| now - checked > Duration::hours(hours.into())))
    }

    /// Current, Overdue, NotChecked or SwitchedOff.
    pub fn state(&self, now: Time) -> &'static str {
        if self.switched_off.is_some() {
            "SwitchedOff"
        } else if !self.is_searched() {
            "NotChecked"
        } else if self.is_overdue(now) {
            "Overdue"
        } else {
            "Current"
        }
    }
}

#[derive(Default)]
struct Snapshot {
    lists: Vec<Arc<LoadedList>>,
    ready: bool,
}

/// One snapshot of the lists, swapped whole, plus the history, held updates and the allow-list.
pub struct Catalog {
    snapshot: RwLock<Arc<Snapshot>>,
    history: RwLock<Arc<HashMap<String, Vec<HistoryEntry>>>>,
    held: RwLock<Arc<HashMap<String, HistoryEntry>>>,
    allow_list: RwLock<Arc<AllowList>>,
}

impl Default for Catalog {
    fn default() -> Self {
        Catalog {
            snapshot: RwLock::new(Arc::new(Snapshot::default())),
            history: RwLock::new(Arc::new(HashMap::new())),
            held: RwLock::new(Arc::new(HashMap::new())),
            allow_list: RwLock::new(Arc::new(AllowList::empty())),
        }
    }
}

impl Catalog {
    pub fn is_ready(&self) -> bool {
        self.snapshot.read().unwrap().ready
    }

    pub fn lists(&self) -> Vec<Arc<LoadedList>> {
        self.snapshot.read().unwrap().lists.clone()
    }

    pub fn replace(&self, lists: Vec<Arc<LoadedList>>) {
        *self.snapshot.write().unwrap() = Arc::new(Snapshot { lists, ready: true });
    }

    pub fn history(&self) -> Arc<HashMap<String, Vec<HistoryEntry>>> {
        self.history.read().unwrap().clone()
    }

    pub fn held(&self) -> Arc<HashMap<String, HistoryEntry>> {
        self.held.read().unwrap().clone()
    }

    pub fn allow_list(&self) -> Arc<AllowList> {
        self.allow_list.read().unwrap().clone()
    }

    pub fn replace_allow_list(&self, list: AllowList) {
        *self.allow_list.write().unwrap() = Arc::new(list);
    }

    /// The loaded list with this code (ignoring case).
    pub fn find(&self, code: &str) -> Option<Arc<LoadedList>> {
        self.lists().into_iter().find(|l| l.code.eq_ignore_ascii_case(code))
    }
}

/// Decides when run_refresh refreshes, from the time and the value of queries::CHANGES_SQL just read (None when the check
/// failed or is off), so it can be tested without a clock or a database. It refreshes:
/// - at the first call, and when refresh_every has passed since the last refresh (the backstop);
/// - at once for each new value, even while an earlier failure waits for its retry (an allow-list removal made on
///   another replica never waits behind a list that won't build). The value is read before the refresh, so a change made
///   while it runs gives a new value at the next check;
/// - after a failed refresh, once retry_wait has passed (5 s, doubling up to a minute): never at every check, and until
///   it works.
struct RefreshSchedule {
    refresh_every: std::time::Duration,
    check_every: std::time::Duration,
    attempted: Option<String>,
    refreshed_at: Option<std::time::Instant>,
    retry_at: Option<std::time::Instant>,
    retry_wait: std::time::Duration,
}

impl RefreshSchedule {
    fn new(refresh_every: std::time::Duration, check_every: std::time::Duration) -> Self {
        RefreshSchedule {
            refresh_every,
            check_every,
            attempted: None,
            refreshed_at: None,
            retry_at: None,
            retry_wait: std::time::Duration::ZERO,
        }
    }

    /// Whether to refresh now; when it says so, the refresh is for changes.
    fn begin(&mut self, now: std::time::Instant, changes: Option<String>) -> bool {
        let refresh = self.refreshed_at.is_none_or(|at| now.duration_since(at) >= self.refresh_every)
            || self.retry_at.is_some_and(|at| now >= at)
            || (changes.is_some() && changes != self.attempted);
        if refresh && changes.is_some() {
            self.attempted = changes;
        }
        refresh
    }

    /// Records how the refresh begin asked for went.
    fn finished(&mut self, now: std::time::Instant, succeeded: bool) {
        self.refreshed_at = Some(now);
        if succeeded {
            self.retry_at = None;
            self.retry_wait = std::time::Duration::ZERO;
        } else {
            self.retry_wait = next_retry_wait(self.retry_wait);
            self.retry_at = Some(now + self.retry_wait);
        }
    }

    /// How long to sleep before the next check: the check interval, or less when a retry falls sooner.
    fn next_wait(&self) -> std::time::Duration {
        if self.retry_at.is_some() {
            self.check_every.min(self.retry_wait)
        } else {
            self.check_every
        }
    }
}

/// The wait before trying a failed refresh again: 5 s, then twice the last, at most a minute.
fn next_retry_wait(last: std::time::Duration) -> std::time::Duration {
    if last.is_zero() {
        std::time::Duration::from_secs(5)
    } else {
        (last * 2).min(std::time::Duration::from_secs(60))
    }
}

/// Bounds one check for changes; one that takes longer counts as failed (and its connection is dropped).
const CHANGE_CHECK_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(10);

/// How many updates GET /api/v1/sources shows per list.
pub const HISTORY_SHOWN: usize = 10;

/// Loads the lists from SQL into the catalog.
pub struct Loader {
    pub db: Database,
    pub catalog: Arc<Catalog>,
    pub logger: Logger,
    gate: Mutex<()>,
}

impl Loader {
    pub fn new(db: Database, catalog: Arc<Catalog>, logger: Logger) -> Self {
        Loader { db, catalog, logger, gate: Mutex::new(()) }
    }

    /// Reloads the lists; one refresh at a time.
    pub async fn refresh(&self) -> DbResult<()> {
        let _gate = self.gate.lock().await;
        self.refresh_once().await
    }

    /// Reloads the allow-list only (after an edit), under the refresh gate. With without_id (a removal), that entry
    /// stops applying first.
    pub async fn reload_allow_list(&self, without_id: Option<i32>) -> bool {
        let _gate = self.gate.lock().await;
        if let Some(id) = without_id {
            self.catalog.replace_allow_list(self.catalog.allow_list().without(id));
        }
        match self.load_allow_list().await {
            Ok(()) => true,
            Err(e) => {
                self.logger
                    .warn("Reloading the allow-list after an edit failed; the next refresh retries", &[("error", e.to_string().into())]);
                false
            }
        }
    }

    /// Holds the refresh gate (an allow-list addition: the lists can't change between check and insert).
    pub async fn gate(&self) -> tokio::sync::MutexGuard<'_, ()> {
        self.gate.lock().await
    }

    /// Loads the allow-list now, for a caller that holds the gate.
    pub async fn load_allow_list_now(&self) -> bool {
        match self.load_allow_list().await {
            Ok(()) => true,
            Err(e) => {
                self.logger
                    .warn("Reloading the allow-list after an edit failed; the next refresh retries", &[("error", e.to_string().into())]);
                false
            }
        }
    }

    async fn load_allow_list(&self) -> DbResult<()> {
        let mut conn = self.db.connect().await?;
        let (entries, version) = queries::allow_list_current(&mut conn).await?;
        let list: Vec<AllowListEntry> = entries.into_iter().map(|e| AllowListEntry { id: e.id, name: e.name, reason: e.reason }).collect();
        self.catalog.replace_allow_list(AllowList::build(&list, &version));
        Ok(())
    }

    async fn refresh_once(&self) -> DbResult<()> {
        let mut conn = self.db.connect().await?;
        let sources = queries::sources(&mut conn).await?;
        let (entries, version) = queries::allow_list_current(&mut conn).await?;
        let allow: Vec<AllowListEntry> = entries.into_iter().map(|e| AllowListEntry { id: e.id, name: e.name, reason: e.reason }).collect();
        self.catalog.replace_allow_list(AllowList::build(&allow, &version));
        let history = queries::history(&mut conn, "").await?;
        let held_entries = queries::held(&mut conn).await?;
        let mut held: HashMap<String, HistoryEntry> = HashMap::new();
        for h in held_entries {
            held.entry(h.source_code.clone()).or_insert(h);
        }
        *self.catalog.held.write().unwrap() = Arc::new(held);
        let mut by_code: HashMap<String, Vec<HistoryEntry>> = HashMap::new();
        for h in history {
            by_code.entry(h.source_code.clone()).or_default().push(h);
        }
        *self.catalog.history.write().unwrap() = Arc::new(by_code);

        let current: HashMap<String, Arc<LoadedList>> = self.catalog.lists().into_iter().map(|l| (l.code.clone(), l)).collect();
        let mut lists: Vec<Arc<LoadedList>> = Vec::with_capacity(sources.len());
        let mut pending: Vec<(usize, LoadedList)> = Vec::new();
        for (i, source) in sources.iter().enumerate() {
            let switched_off =
                source.switched_off_at.map(|at| SwitchedOff { at, reason: source.switched_off_reason.clone().unwrap_or_default() });
            let mut target = LoadedList {
                code: source.code.clone(),
                name: source.name.clone(),
                url: source.url.clone(),
                max_age_hours: source.max_age_hours,
                active_import_id: source.active_import_id,
                last_checked_at: source.last_checked_at,
                updated_at: source.updated_at,
                record_count: source.record_count,
                index: None,
                switched_off: switched_off.clone(),
            };
            let existing = current.get(&source.code);
            if let Some(existing) = existing {
                if existing.active_import_id == source.active_import_id && (existing.index.is_some() || source.active_import_id.is_none()) {
                    target.index = existing.index.clone();
                    lists.push(Arc::new(target));
                    continue;
                }
            }
            match existing {
                None => lists.push(Arc::new(target.clone())),
                Some(existing) => {
                    let mut copy = (**existing).clone();
                    copy.switched_off = switched_off;
                    lists.push(Arc::new(copy));
                }
            }
            if source.active_import_id.is_some() {
                pending.push((i, target));
            }
        }
        // Only lists holds the old indexes now: each swap below can release the one it replaces.
        drop(current);
        // A build that fails keeps whatever served before, and the refresh reports it once everything else is
        // published: the change isn't applied yet, so run_refresh tries again shortly, never waiting for refresh_every.
        let mut not_built = Vec::new();
        for (position, target) in pending {
            let import_id = target.active_import_id.unwrap();
            if let Some(index) = self.build(&mut conn, &target.code, import_id, target.record_count).await {
                let mut built = target;
                built.index = Some(Arc::new(index));
                lists[position] = Arc::new(built);
            } else {
                not_built.push(target.code.clone());
            }
            if self.catalog.is_ready() {
                self.catalog.replace(lists.clone());
            }
        }
        self.catalog.replace(lists);
        if !not_built.is_empty() {
            return Err(format!(
                "Not built: {}; everything else is loaded, and the list's previous version serves until it's built",
                not_built.join(", ")
            )
            .into());
        }
        Ok(())
    }

    async fn build(&self, conn: &mut crate::store::db::Connection, code: &str, import_id: i32, expected: i32) -> Option<ListIndex> {
        let subjects = match queries::active_subjects(conn, code, import_id).await {
            Ok(subjects) => subjects,
            Err(e) => {
                self.logger.error(
                    "Building the index failed",
                    &[("code", code.into()), ("importId", import_id.into()), ("error", e.to_string().into())],
                );
                return None;
            }
        };
        let Some(subjects) = subjects.filter(|s| s.len() == expected as usize) else {
            self.logger.warn(
                "The import changed while loading; it stays Not checked until the next refresh",
                &[("code", code.into()), ("importId", import_id.into()), ("expected", expected.into())],
            );
            return None;
        };
        let listed: Vec<ListedSubject> = subjects.into_iter().map(to_listed).collect();
        let index = tokio::task::spawn_blocking(move || ListIndex::build(listed)).await.ok()?;
        self.logger
            .info("Built the index", &[("code", code.into()), ("importId", import_id.into()), ("aliases", index.alias_count().into())]);
        Some(index)
    }

    /// Loads the lists at startup, then refreshes as RefreshSchedule says: every refresh_every, at once whenever
    /// queries::CHANGES_SQL moved (read every check_every; zero: no check), so an administrator's change made on another
    /// replica applies here within seconds, and after a failure until it works. The check keeps one connection open (a
    /// new one is a sign-in, with a token in Azure) and opens another only after a failure; a failing check is logged
    /// once, and once again when the database answers again.
    pub async fn run_refresh(self: Arc<Self>, refresh_every: std::time::Duration, check_every: std::time::Duration) {
        let mut schedule = RefreshSchedule::new(refresh_every, if check_every.is_zero() { refresh_every } else { check_every });
        let mut conn = None;
        let mut check_failing = false;
        loop {
            let mut changes = None;
            if !check_every.is_zero() {
                let read = async {
                    if conn.is_none() {
                        conn = Some(self.db.connect().await?);
                    }
                    queries::changes(conn.as_mut().expect("connected above")).await
                };
                // Bounded: a database that takes the query and never answers must not stop the checks or the refresh.
                let read = async {
                    match tokio::time::timeout(CHANGE_CHECK_TIMEOUT, read).await {
                        Ok(result) => result,
                        Err(_) => Err("Checking the database for changes timed out.".into()),
                    }
                };
                match read.await {
                    Ok(value) => {
                        changes = Some(value);
                        if check_failing {
                            check_failing = false;
                            self.logger.info("Checking the database for changes works again", &[]);
                        }
                    }
                    Err(e) => {
                        conn = None;
                        if !check_failing {
                            check_failing = true;
                            self.logger.warn(
                                "Checking the database for changes failed; serving what was loaded, and checking again",
                                &[("error", e.to_string().into())],
                            );
                        }
                    }
                }
            }
            if schedule.begin(std::time::Instant::now(), changes) {
                let result = self.refresh().await;
                schedule.finished(std::time::Instant::now(), result.is_ok());
                if let Err(e) = result {
                    self.logger.error(
                        &format!(
                            "Refreshing the lists failed; the catalog keeps serving what it has, and tries again in {} s",
                            schedule.retry_wait.as_secs()
                        ),
                        &[("error", e.to_string().into())],
                    );
                }
            }
            tokio::time::sleep(schedule.next_wait()).await;
        }
    }
}

/// A subject from the database as the matcher reads it.
pub fn to_listed(s: Subject) -> ListedSubject {
    let kind = match s.kind {
        0 => SubjectKind::Individual,
        1 => SubjectKind::Entity,
        _ => SubjectKind::Unknown,
    };
    ListedSubject {
        external_id: s.external_id,
        kind,
        primary_name: s.primary_name,
        names: s.names.into_iter().map(|n| ListedName { full_name: n.full_name, is_weak: n.is_weak }).collect(),
        addresses: s
            .addresses
            .into_iter()
            .map(|a| ListedAddress { raw: a.raw, country_code: a.country_code.map(|c| c.trim().to_string()).unwrap_or_default() })
            .collect(),
        programme: s.programmes,
        source_url: s.source_url,
    }
}

#[cfg(test)]
mod tests {
    use super::{next_retry_wait, RefreshSchedule};
    use std::time::{Duration, Instant};

    // When the lists are refreshed (RefreshSchedule): scripted times and values of queries::CHANGES_SQL, with no clock
    // and no database. Checks every 2 s, the full refresh every 15 minutes, as in production.

    fn schedule() -> RefreshSchedule {
        RefreshSchedule::new(Duration::from_secs(15 * 60), Duration::from_secs(2))
    }

    fn at(start: Instant, seconds: u64) -> Instant {
        start + Duration::from_secs(seconds)
    }

    fn value(v: &str) -> Option<String> {
        Some(v.to_string())
    }

    /// A refresh asked for and finished at once, as it went.
    fn run(s: &mut RefreshSchedule, now: Instant, changes: Option<String>, succeeds: bool) -> bool {
        if !s.begin(now, changes) {
            return false;
        }
        s.finished(now, succeeds);
        true
    }

    #[test]
    fn the_first_check_refreshes_and_an_unchanged_value_doesnt_again() {
        let (start, mut s) = (Instant::now(), schedule());
        assert!(run(&mut s, at(start, 0), value("a"), true));
        assert!(!s.begin(at(start, 2), value("a")));
        assert!(!s.begin(at(start, 4), value("a")));
    }

    #[test]
    fn a_new_value_refreshes_at_once() {
        let (start, mut s) = (Instant::now(), schedule());
        run(&mut s, at(start, 0), value("a"), true);
        assert!(s.begin(at(start, 2), value("b")));
    }

    #[test]
    fn a_change_made_during_a_refresh_is_refreshed_for_at_the_next_check() {
        let (start, mut s) = (Instant::now(), schedule());
        run(&mut s, at(start, 0), value("a"), true);
        assert!(s.begin(at(start, 2), value("b")));
        s.finished(at(start, 3), true);
        assert!(s.begin(at(start, 5), value("c")));
    }

    #[test]
    fn a_failed_refresh_is_tried_again_after_its_wait_not_at_every_check_until_it_works() {
        let (start, mut s) = (Instant::now(), schedule());
        run(&mut s, at(start, 0), value("a"), true);
        assert!(run(&mut s, at(start, 2), value("b"), false));
        assert_eq!(s.retry_wait, Duration::from_secs(5));
        assert!(!s.begin(at(start, 4), value("b")));
        assert!(!s.begin(at(start, 6), value("b")));
        assert!(run(&mut s, at(start, 7), value("b"), false));
        assert_eq!(s.retry_wait, Duration::from_secs(10));
        assert!(!s.begin(at(start, 15), value("b")));
        assert!(run(&mut s, at(start, 17), value("b"), true));
        assert_eq!(s.retry_wait, Duration::ZERO);
        assert!(!s.begin(at(start, 19), value("b")));
        assert!(!s.begin(at(start, 40), value("b")));
    }

    #[test]
    fn a_new_value_doesnt_wait_for_an_earlier_failures_retry() {
        let (start, mut s) = (Instant::now(), schedule());
        run(&mut s, at(start, 0), value("a"), true);
        run(&mut s, at(start, 2), value("b"), false);
        assert!(s.begin(at(start, 4), value("c")));
    }

    #[test]
    fn a_failed_check_refreshes_only_for_a_retry_or_the_backstop() {
        let (start, mut s) = (Instant::now(), schedule());
        run(&mut s, at(start, 0), value("a"), true);
        assert!(!s.begin(at(start, 2), None));
        run(&mut s, at(start, 2), value("b"), false);
        assert!(!s.begin(at(start, 4), None));
        assert!(s.begin(at(start, 7), None));
    }

    #[test]
    fn with_nothing_changing_the_backstop_refreshes_every_15_minutes() {
        let (start, mut s) = (Instant::now(), schedule());
        run(&mut s, at(start, 0), value("a"), true);
        assert!(!s.begin(at(start, 15 * 60 - 1), value("a")));
        assert!(s.begin(at(start, 15 * 60), value("a")));
    }

    #[test]
    fn it_sleeps_the_check_interval_or_less_when_a_retry_falls_sooner() {
        let (start, mut with_check) = (Instant::now(), schedule());
        run(&mut with_check, at(start, 0), value("a"), true);
        assert_eq!(with_check.next_wait(), Duration::from_secs(2));
        run(&mut with_check, at(start, 2), value("b"), false);
        assert_eq!(with_check.next_wait(), Duration::from_secs(2));
        let mut without_check = RefreshSchedule::new(Duration::from_secs(15 * 60), Duration::from_secs(15 * 60));
        run(&mut without_check, at(start, 0), None, false);
        assert_eq!(without_check.next_wait(), Duration::from_secs(5));
        assert!(without_check.begin(at(start, 5), None));
    }

    /// A refresh that failed is tried again after a wait that grows: never at every check, never more than a minute apart.
    #[test]
    fn a_failed_refresh_is_tried_again_after_5s_then_twice_as_long_at_most_a_minute() {
        let mut wait = Duration::ZERO;
        let waits: Vec<u64> = (0..7)
            .map(|_| {
                wait = next_retry_wait(wait);
                wait.as_secs()
            })
            .collect();
        assert_eq!(waits, [5, 10, 20, 40, 60, 60, 60]);
    }
}
