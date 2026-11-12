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
        for (position, target) in pending {
            let import_id = target.active_import_id.unwrap();
            if let Some(index) = self.build(&mut conn, &target.code, import_id, target.record_count).await {
                let mut built = target;
                built.index = Some(Arc::new(index));
                lists[position] = Arc::new(built);
            }
            if self.catalog.is_ready() {
                self.catalog.replace(lists.clone());
            }
        }
        self.catalog.replace(lists);
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

    /// Loads the lists at startup, then every interval.
    /// Loads the lists at startup, then refreshes every refresh_every, and sooner whenever queries::CHANGES_SQL moved:
    /// it's read every check_every (zero: no check), so an administrator's change made on another replica applies here
    /// within seconds. The check keeps one connection open (a new one is a sign-in, with a token in Azure) and opens
    /// another only after a failure; a failing check is logged once, and once again when the database answers again.
    pub async fn run_refresh(self: Arc<Self>, refresh_every: std::time::Duration, check_every: std::time::Duration) {
        let wait = if check_every.is_zero() { refresh_every } else { check_every };
        let mut seen: Option<String> = None;
        let mut refreshed_at: Option<std::time::Instant> = None;
        let mut conn = None;
        let mut check_failing = false;
        loop {
            let due = refreshed_at.is_none_or(|at| at.elapsed() >= refresh_every);
            let mut changes = None;
            if !check_every.is_zero() {
                let read = async {
                    if conn.is_none() {
                        conn = Some(self.db.connect().await?);
                    }
                    queries::changes(conn.as_mut().expect("connected above")).await
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
            // Read before the refresh: a change made while it runs moves the value again, and the next check sees it.
            if due || (changes.is_some() && changes != seen) {
                if let Err(e) = self.refresh().await {
                    self.logger
                        .error("Refreshing the lists failed; the catalog keeps serving what it has", &[("error", e.to_string().into())]);
                }
                // Even after a failure: the next try is the next change or the next refresh_every, never every check.
                if changes.is_some() {
                    seen = changes;
                }
                refreshed_at = Some(std::time::Instant::now());
            }
            tokio::time::sleep(wait).await;
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
