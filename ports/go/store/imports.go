package store

import (
	"context"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"sort"
	"strings"
	"time"
	"unicode/utf16"

	mssql "github.com/microsoft/go-mssqldb"
)

// The import rules (ImportStore.cs in the reference): each import a complete copy, written with bulk copy, made
// active in one conditional transaction; every import's row stays as the history; records kept for the active
// import, the one before it and one held for review; one import per source at a time (an application lock).

const (
	batchSize     = 5000
	maxExternalID = 200
	maxDrop       = 0.20
)

type ImportResult string

const (
	Imported      ImportResult = "Imported"
	Unchanged     ImportResult = "Unchanged"
	Rejected      ImportResult = "Rejected"
	HeldForReview ImportResult = "HeldForReview"
)

type ImportOutcome struct {
	Result        ImportResult
	RecordCount   int
	PreviousCount *int
	Message       string
	ImportID      *int
}

type ReviewResult string

const (
	ReviewConfirmed  ReviewResult = "Confirmed"
	ReviewRejected   ReviewResult = "Rejected"
	ReviewNotFound   ReviewResult = "NotFound"
	ReviewNotPending ReviewResult = "NotPending"
)

type mode int

const (
	modeFeed mode = iota
	modeForced
	modeManual
)

// SourceRecord is a source as the import rules need it.
type SourceRecord struct {
	ID             int
	Code           string
	Name           string
	URL            *string
	MaxAgeHours    *int
	ActiveImportID *int
	LastCheckedAt  *time.Time
}

// Imports runs the import rules against the database.
type Imports struct {
	DB  *sql.DB
	Now func() time.Time
}

type queryer interface {
	QueryContext(ctx context.Context, query string, args ...any) (*sql.Rows, error)
	QueryRowContext(ctx context.Context, query string, args ...any) *sql.Row
	ExecContext(ctx context.Context, query string, args ...any) (sql.Result, error)
}

func (s *Imports) EnsureSource(ctx context.Context, code, name string, url *string, maxAgeHours *int) (*SourceRecord, error) {
	existing, err := s.FindSource(ctx, code)
	if err != nil {
		return nil, err
	}
	if existing == nil {
		_, err = s.DB.ExecContext(ctx, `INSERT INTO Sources (Code, Name, Url, MaxAgeHours) VALUES (@code, @name, @url, @age)`,
			sql.Named("code", code), sql.Named("name", name), sql.Named("url", url), sql.Named("age", maxAgeHours))
	} else {
		_, err = s.DB.ExecContext(ctx, `UPDATE Sources SET Name = @name, Url = @url, MaxAgeHours = @age WHERE Id = @id`,
			sql.Named("name", name), sql.Named("url", url), sql.Named("age", maxAgeHours), sql.Named("id", existing.ID))
	}
	if err != nil {
		return nil, err
	}
	return s.FindSource(ctx, code)
}

func (s *Imports) FindSource(ctx context.Context, code string) (*SourceRecord, error) {
	return findSource(ctx, s.DB, `Code = @key`, code)
}

func findSource(ctx context.Context, q queryer, where string, key any) (*SourceRecord, error) {
	var r SourceRecord
	err := q.QueryRowContext(ctx, `SELECT Id, Code, Name, Url, MaxAgeHours, ActiveImportId, LastCheckedAt FROM Sources WHERE `+where, sql.Named("key", key)).
		Scan(&r.ID, &r.Code, &r.Name, &r.URL, &r.MaxAgeHours, &r.ActiveImportID, &r.LastCheckedAt)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, nil
	}
	return &r, err
}

// Import imports subjects as the source's new list (force: past the drop guard, and even when unchanged).
func (s *Imports) Import(ctx context.Context, source *SourceRecord, subjects []Subject, force bool) (*ImportOutcome, error) {
	m := modeFeed
	if force {
		m = modeForced
	}
	return s.locked(ctx, source, subjects, nil, m)
}

// Change is a manual change: change gets the active list under the source's lock, and its result becomes the list.
func (s *Imports) Change(ctx context.Context, source *SourceRecord, change func([]Subject) []Subject) (*ImportOutcome, error) {
	return s.locked(ctx, source, nil, change, modeManual)
}

// RecordFailure records a refresh that failed before it could import.
func (s *Imports) RecordFailure(ctx context.Context, source *SourceRecord, message string) error {
	now := s.Now()
	_, err := s.DB.ExecContext(ctx, `INSERT INTO Imports (SourceId, StartedAt, CompletedAt, Status, Message, RecordCount, HasData) VALUES (@source, @now, @now, 'Failed', @message, 0, 0)`,
		sql.Named("source", source.ID), sql.Named("now", now), sql.Named("message", truncate(message, 1000)))
	return err
}

type importRow struct {
	ID          int
	ContentHash *string
	RecordCount int
}

func (s *Imports) locked(ctx context.Context, source *SourceRecord, subjects []Subject, change func([]Subject) []Subject, m mode) (*ImportOutcome, error) {
	conn, err := s.DB.Conn(ctx)
	if err != nil {
		return nil, err
	}
	defer conn.Close()
	if err := acquireLock(ctx, conn, source.Code); err != nil {
		return nil, err
	}
	defer releaseLock(conn, source.Code)

	current, err := findSource(ctx, conn, `Id = @key`, source.ID)
	if err != nil {
		return nil, err
	}
	*source = *current
	if subjects == nil {
		active, err := activeSubjects(ctx, conn, source.ActiveImportID)
		if err != nil {
			return nil, err
		}
		subjects = change(active)
	}
	records := Deduplicate(subjects)
	for _, r := range records {
		if utf16Len(r.ExternalID) > maxExternalID {
			return nil, fmt.Errorf("an external id is longer than %d characters: %s…", maxExternalID, string([]rune(r.ExternalID)[:40]))
		}
	}
	hash := CanonicalHash(records)
	var active *importRow
	if source.ActiveImportID != nil {
		active = &importRow{}
		if err := conn.QueryRowContext(ctx, `SELECT Id, ContentHash, RecordCount FROM Imports WHERE Id = @id`, sql.Named("id", *source.ActiveImportID)).
			Scan(&active.ID, &active.ContentHash, &active.RecordCount); err != nil {
			return nil, err
		}
	}
	now := s.Now()
	held, err := heldImports(ctx, conn, source.ID)
	if err != nil {
		return nil, err
	}
	var previousCount *int
	if active != nil {
		previousCount = &active.RecordCount
	}

	if m != modeForced && active != nil && active.ContentHash != nil && *active.ContentHash == hash {
		if m == modeFeed {
			err := inTransaction(ctx, conn, func(tx *sql.Tx) error {
				if _, err := tx.ExecContext(ctx, `UPDATE Sources SET LastCheckedAt = @now WHERE Id = @id`, sql.Named("now", now), sql.Named("id", source.ID)); err != nil {
					return err
				}
				return supersedeHeld(ctx, tx, source.ID, nil, "superseded: the feed returned to the active list")
			})
			if err != nil {
				return nil, err
			}
			if err := deleteOldImports(ctx, conn, source.ID); err != nil {
				return nil, err
			}
		}
		return &ImportOutcome{Unchanged, active.RecordCount, previousCount, "Unchanged since the active import", nil}, nil
	}

	if len(records) == 0 {
		if _, err := conn.ExecContext(ctx, `INSERT INTO Imports (SourceId, StartedAt, CompletedAt, ContentHash, RecordCount, Status, Message, HasData) VALUES (@source, @now, @now, @hash, 0, 'Rejected', 'Zero records', 0)`,
			sql.Named("source", source.ID), sql.Named("now", now), sql.Named("hash", hash)); err != nil {
			return nil, err
		}
		return &ImportOutcome{Rejected, 0, previousCount, "Zero records; the active list is kept", nil}, nil
	}

	drop := ""
	if m == modeFeed && active != nil && float64(len(records)) < float64(active.RecordCount)*(1-maxDrop) {
		// The reference formats MaxDrop with P0 in the invariant culture: "20 %".
		drop = fmt.Sprintf("Records dropped from %d to %d, more than 20 %%: held for review", active.RecordCount, len(records))
	}
	if drop != "" && len(held) > 0 && held[0].ContentHash != nil && *held[0].ContentHash == hash {
		id := held[0].ID
		return &ImportOutcome{HeldForReview, len(records), previousCount, fmt.Sprintf("Still held for review (import %d); the active list is kept", id), &id}, nil
	}

	var basedOn *int
	if drop != "" {
		basedOn = &active.ID
	}
	var importID int
	if err := conn.QueryRowContext(ctx, `INSERT INTO Imports (SourceId, StartedAt, ContentHash, RecordCount, Status, HasData, BasedOnImportId) OUTPUT INSERTED.Id VALUES (@source, @now, @hash, @count, 'Running', 1, @basedOn)`,
		sql.Named("source", source.ID), sql.Named("now", now), sql.Named("hash", hash), sql.Named("count", len(records)), sql.Named("basedOn", basedOn)).Scan(&importID); err != nil {
		return nil, err
	}
	previousActive := source.ActiveImportID
	work := func() error {
		if err := writeSubjects(ctx, conn, source.ID, importID, records); err != nil {
			return err
		}
		var activeID *int
		if active != nil {
			activeID = &active.ID
		}
		countChanges(ctx, conn, importID, activeID)
		if drop != "" {
			completed := s.Now()
			return inTransaction(ctx, conn, func(tx *sql.Tx) error {
				if _, err := tx.ExecContext(ctx, `UPDATE Imports SET Status = 'PendingReview', CompletedAt = @at, Message = @message WHERE Id = @id`,
					sql.Named("at", completed), sql.Named("message", drop), sql.Named("id", importID)); err != nil {
					return err
				}
				return supersedeHeld(ctx, tx, source.ID, &importID, fmt.Sprintf("superseded by import %d", importID))
			})
		}
		return s.activate(ctx, conn, source, importID, previousActive, now, nil)
	}
	if err := work(); err != nil {
		// The activation may have committed before the failure surfaced: if so, the import is active and whole.
		background := context.Background()
		var persisted *int
		conn.QueryRowContext(background, `SELECT ActiveImportId FROM Sources WHERE Id = @id`, sql.Named("id", source.ID)).Scan(&persisted) //nolint:errcheck // best effort
		if persisted == nil || *persisted != importID {
			conn.ExecContext(background, `UPDATE Imports SET Status = 'Failed', CompletedAt = @at, Message = @message WHERE Id = @id`, //nolint:errcheck // best effort
				sql.Named("at", s.Now()), sql.Named("message", fmt.Sprintf("%T", err)), sql.Named("id", importID))
			deleteImportData(background, conn, importID)                                                             //nolint:errcheck // best effort
			conn.ExecContext(background, `UPDATE Imports SET HasData = 0 WHERE Id = @id`, sql.Named("id", importID)) //nolint:errcheck // best effort
			return nil, err
		}
	}
	if err := deleteOldImports(ctx, conn, source.ID); err != nil {
		return nil, err
	}
	if drop != "" {
		return &ImportOutcome{HeldForReview, len(records), previousCount, fmt.Sprintf("%s (import %d); the active list is kept", drop, importID), &importID}, nil
	}
	return &ImportOutcome{Imported, len(records), previousCount, "Imported", &importID}, nil
}

func heldImports(ctx context.Context, q queryer, sourceID int) ([]importRow, error) {
	rows, err := q.QueryContext(ctx, `SELECT Id, ContentHash, RecordCount FROM Imports WHERE SourceId = @id AND Status = 'PendingReview' ORDER BY Id DESC`, sql.Named("id", sourceID))
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []importRow
	for rows.Next() {
		var r importRow
		if err := rows.Scan(&r.ID, &r.ContentHash, &r.RecordCount); err != nil {
			return nil, err
		}
		out = append(out, r)
	}
	return out, rows.Err()
}

func activeSubjects(ctx context.Context, q queryer, activeID *int) ([]Subject, error) {
	if activeID == nil {
		return []Subject{}, nil
	}
	return readSubjectsFrom(ctx, q, *activeID)
}

type reviewStamp struct {
	reviewer string
	at       time.Time
	message  string
}

// activate is the flip, in one transaction: the source moves to the new import only if its active import is still the
// one this import started from, and the import completes.
func (s *Imports) activate(ctx context.Context, conn *sql.Conn, source *SourceRecord, importID int, expected *int, checkedAt time.Time, review *reviewStamp) error {
	now := s.Now()
	return inTransaction(ctx, conn, func(tx *sql.Tx) error {
		result, err := tx.ExecContext(ctx, `UPDATE Sources SET ActiveImportId = @import, LastCheckedAt = @checked
WHERE Id = @id AND (ActiveImportId = @expected OR (ActiveImportId IS NULL AND @expected IS NULL))`,
			sql.Named("import", importID), sql.Named("checked", checkedAt), sql.Named("id", source.ID), sql.Named("expected", expected))
		if err != nil {
			return err
		}
		if flipped, _ := result.RowsAffected(); flipped == 0 {
			return fmt.Errorf("another import of %s became active meanwhile; nothing was changed. Run it again", source.Code)
		}
		if _, err := tx.ExecContext(ctx, `UPDATE Imports SET Status = 'Completed', CompletedAt = @now WHERE Id = @id`, sql.Named("now", now), sql.Named("id", importID)); err != nil {
			return err
		}
		if review != nil {
			if _, err := tx.ExecContext(ctx, `UPDATE Imports SET ReviewedBy = @by, ReviewedAt = @at, Message = @message WHERE Id = @id`,
				sql.Named("by", review.reviewer), sql.Named("at", review.at), sql.Named("message", review.message), sql.Named("id", importID)); err != nil {
				return err
			}
		}
		return supersedeHeld(ctx, tx, source.ID, &importID, fmt.Sprintf("superseded by import %d", importID))
	})
}

func supersedeHeld(ctx context.Context, tx *sql.Tx, sourceID int, exceptID *int, reason string) error {
	_, err := tx.ExecContext(ctx, `UPDATE Imports SET Status = 'Rejected', Message = SUBSTRING(COALESCE(Message, N'') + N'; ' + @reason, 1, 1000)
WHERE SourceId = @source AND Status = 'PendingReview' AND (@except IS NULL OR Id <> @except)`,
		sql.Named("reason", reason), sql.Named("source", sourceID), sql.Named("except", exceptID))
	return err
}

// deleteOldImports keeps the records of the active import, the one before it, and any held for review.
func deleteOldImports(ctx context.Context, conn *sql.Conn, sourceID int) error {
	var active *int
	if err := conn.QueryRowContext(ctx, `SELECT ActiveImportId FROM Sources WHERE Id = @id`, sql.Named("id", sourceID)).Scan(&active); err != nil {
		return err
	}
	var previous *int
	err := conn.QueryRowContext(ctx, `SELECT TOP 1 Id FROM Imports WHERE SourceId = @source AND Status = 'Completed' AND (@active IS NULL OR Id <> @active) ORDER BY Id DESC`,
		sql.Named("source", sourceID), sql.Named("active", active)).Scan(&previous)
	if err != nil && !errors.Is(err, sql.ErrNoRows) {
		return err
	}
	rows, err := conn.QueryContext(ctx, `SELECT Id FROM Imports WHERE SourceId = @source AND HasData = 1 AND (@active IS NULL OR Id <> @active)
AND (@previous IS NULL OR Id <> @previous) AND Status <> 'PendingReview'`, sql.Named("source", sourceID), sql.Named("active", active), sql.Named("previous", previous))
	if err != nil {
		return err
	}
	var old []int
	for rows.Next() {
		var id int
		if err := rows.Scan(&id); err != nil {
			rows.Close()
			return err
		}
		old = append(old, id)
	}
	rows.Close()
	for _, id := range old {
		if err := deleteImportData(ctx, conn, id); err != nil {
			return err
		}
		if _, err := conn.ExecContext(ctx, `UPDATE Imports SET HasData = 0 WHERE Id = @id`, sql.Named("id", id)); err != nil {
			return err
		}
	}
	return nil
}

// deleteImportData deletes an import's names, addresses and subjects in chunks.
func deleteImportData(ctx context.Context, conn *sql.Conn, importID int) error {
	for _, statement := range []string{
		`DELETE TOP (20000) n FROM SubjectNames n JOIN Subjects s ON s.Id = n.SubjectId WHERE s.ImportId = @import`,
		`DELETE TOP (20000) a FROM SubjectAddresses a JOIN Subjects s ON s.Id = a.SubjectId WHERE s.ImportId = @import`,
		`DELETE TOP (20000) FROM Subjects WHERE ImportId = @import`,
	} {
		for {
			result, err := conn.ExecContext(ctx, statement, sql.Named("import", importID))
			if err != nil {
				return err
			}
			if n, _ := result.RowsAffected(); n == 0 {
				break
			}
		}
	}
	return nil
}

func inTransaction(ctx context.Context, conn *sql.Conn, work func(*sql.Tx) error) error {
	tx, err := conn.BeginTx(ctx, nil)
	if err != nil {
		return err
	}
	if err := work(tx); err != nil {
		tx.Rollback() //nolint:errcheck // the work's error is the one to report
		return err
	}
	return tx.Commit()
}

func acquireLock(ctx context.Context, conn *sql.Conn, code string) error {
	var result int
	if err := conn.QueryRowContext(ctx, `DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @result`,
		sql.Named("resource", "import:"+code)).Scan(&result); err != nil {
		return err
	}
	if result < 0 {
		return fmt.Errorf("another import of %s is running", code)
	}
	return nil
}

func releaseLock(conn *sql.Conn, code string) {
	conn.ExecContext(context.Background(), `IF APPLOCK_MODE('public', @resource, 'Session') <> 'NoLock' EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'`, //nolint:errcheck // closing the connection releases it anyway
		sql.Named("resource", "import:"+code))
}

func writeSubjects(ctx context.Context, conn *sql.Conn, sourceID, importID int, records []Subject) error {
	if err := bulk(ctx, conn, "Subjects", []string{"ImportId", "SourceId", "ExternalId", "Type", "PrimaryName", "Programmes", "ListedOn", "Remarks", "SourceUrl"}, len(records), func(i int) []any {
		r := records[i]
		var listedOn any
		if r.ListedOn != nil {
			listedOn = *r.ListedOn
		}
		return []any{importID, sourceID, r.ExternalID, r.Type, truncate(r.PrimaryName, 500), truncatePtr(r.Programmes, 1000), listedOn, r.Remarks, truncatePtr(r.SourceURL, 1000)}
	}); err != nil {
		return err
	}
	ids := map[string]int{}
	rows, err := conn.QueryContext(ctx, `SELECT Id, ExternalId FROM Subjects WHERE ImportId = @id`, sql.Named("id", importID))
	if err != nil {
		return err
	}
	for rows.Next() {
		var id int
		var external string
		if err := rows.Scan(&id, &external); err != nil {
			rows.Close()
			return err
		}
		ids[external] = id
	}
	rows.Close()

	type nameRow struct {
		id   int
		name SubjectName
	}
	var names []nameRow
	type addressRow struct {
		id      int
		address SubjectAddress
	}
	var addresses []addressRow
	for _, r := range records {
		for _, n := range r.Names {
			names = append(names, nameRow{ids[r.ExternalID], n})
		}
		for _, a := range r.Addresses {
			addresses = append(addresses, addressRow{ids[r.ExternalID], a})
		}
	}
	if err := bulk(ctx, conn, "SubjectNames", []string{"SubjectId", "FullName", "IsWeak", "Script"}, len(names), func(i int) []any {
		return []any{names[i].id, truncate(names[i].name.FullName, 500), names[i].name.IsWeak, names[i].name.Script}
	}); err != nil {
		return err
	}
	return bulk(ctx, conn, "SubjectAddresses", []string{"SubjectId", "Raw", "CountryCode"}, len(addresses), func(i int) []any {
		return []any{addresses[i].id, truncate(addresses[i].address.Raw, 1000), addresses[i].address.CountryCode}
	})
}

func bulk(ctx context.Context, conn *sql.Conn, table string, columns []string, count int, row func(int) []any) error {
	for start := 0; start < count; start += batchSize {
		statement, err := conn.PrepareContext(ctx, mssql.CopyIn(table, mssql.BulkOptions{RowsPerBatch: batchSize}, columns...))
		if err != nil {
			return err
		}
		for i := start; i < min(count, start+batchSize); i++ {
			if _, err := statement.ExecContext(ctx, row(i)...); err != nil {
				statement.Close()
				return err
			}
		}
		if _, err := statement.ExecContext(ctx); err != nil {
			statement.Close()
			return err
		}
		statement.Close()
	}
	return nil
}

// ChangeCountSql is the reference's statement, verbatim (ImportStore.ChangeCountSql).
const ChangeCountSql = `
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
            WHERE s.ImportId = @new OR s.ImportId = @old),
        Counted AS (
            SELECT
                ISNULL(SUM(CASE WHEN o.ExternalId IS NULL THEN 1 ELSE 0 END), 0) AS Added,
                ISNULL(SUM(CASE WHEN n.ExternalId IS NULL THEN 1 ELSE 0 END), 0) AS Removed,
                ISNULL(SUM(CASE WHEN n.ExternalId IS NOT NULL AND o.ExternalId IS NOT NULL AND n.Signature <> o.Signature THEN 1 ELSE 0 END), 0) AS Changed
            FROM (SELECT ExternalId, Signature FROM Signatures WHERE ImportId = @new) n
            FULL OUTER JOIN (SELECT ExternalId, Signature FROM Signatures WHERE ImportId = @old) o
                ON o.ExternalId COLLATE Latin1_General_BIN2 = n.ExternalId COLLATE Latin1_General_BIN2 AND DATALENGTH(o.ExternalId) = DATALENGTH(n.ExternalId))
        UPDATE Imports SET AddedCount = Counted.Added, RemovedCount = Counted.Removed, ChangedCount = Counted.Changed
        FROM Counted
        WHERE Imports.Id = @new;
        `

// countChanges records what an import added, removed and changed; if counting fails, they stay null.
func countChanges(ctx context.Context, conn *sql.Conn, importID int, previous *int) {
	conn.ExecContext(ctx, ChangeCountSql, sql.Named("new", importID), sql.Named("old", previous)) //nolint:errcheck // the counts are history
}

var typeNames = [...]string{"Individual", "Entity", "Unknown"}
var scriptNames = [...]string{"Latin", "Cyrillic", "Other"}

func nameOf(names []string, value int) string {
	if value >= 0 && value < len(names) {
		return names[value]
	}
	return fmt.Sprint(value)
}

// CanonicalHash is the reference's: records sorted by external id, each with its content in a fixed order (unit
// separators, .NET's spellings of the enums and booleans), so the same records hash the same in every implementation.
func CanonicalHash(subjects []Subject) string {
	sorted := append([]Subject{}, subjects...)
	sort.SliceStable(sorted, func(a, b int) bool { return sorted[a].ExternalID < sorted[b].ExternalID })
	hash := sha256.New()
	str := func(v *string) string {
		if v == nil {
			return ""
		}
		return *v
	}
	for _, s := range sorted {
		listedOn := ""
		if s.ListedOn != nil {
			listedOn = s.ListedOn.Format("2006-01-02")
		}
		var b strings.Builder
		b.WriteString(strings.Join([]string{s.ExternalID, nameOf(typeNames[:], s.Type), s.PrimaryName, str(s.Programmes), listedOn, str(s.Remarks), str(s.SourceURL)}, "\u001f"))
		names := append([]SubjectName{}, s.Names...)
		sort.SliceStable(names, func(a, c int) bool {
			if names[a].FullName != names[c].FullName {
				return names[a].FullName < names[c].FullName
			}
			return !names[a].IsWeak && names[c].IsWeak
		})
		for _, n := range names {
			weak := "False"
			if n.IsWeak {
				weak = "True"
			}
			b.WriteString("\u001e" + strings.Join([]string{n.FullName, weak, nameOf(scriptNames[:], n.Script)}, "\u001f"))
		}
		addresses := append([]SubjectAddress{}, s.Addresses...)
		sort.SliceStable(addresses, func(a, c int) bool {
			if addresses[a].Raw != addresses[c].Raw {
				return addresses[a].Raw < addresses[c].Raw
			}
			return lessNullable(addresses[a].CountryCode, addresses[c].CountryCode)
		})
		for _, a := range addresses {
			b.WriteString("\u001d" + a.Raw + "\u001f" + str(a.CountryCode))
		}
		b.WriteString("\n")
		hash.Write([]byte(b.String()))
	}
	return hex.EncodeToString(hash.Sum(nil))
}

// lessNullable orders as .NET's ordinal comparer does: null first.
func lessNullable(a, b *string) bool {
	if a == nil || b == nil {
		return a == nil && b != nil
	}
	return *a < *b
}

func lessTime(a, b *time.Time) bool {
	if a == nil || b == nil {
		return a == nil && b != nil
	}
	return a.Before(*b)
}

// Deduplicate keeps one record per external id: the one that sorts first by its fields is the base, the others' names
// and addresses join it. Ids keep their first appearance's order.
func Deduplicate(subjects []Subject) []Subject {
	var order []string
	groups := map[string][]Subject{}
	for _, s := range subjects {
		if _, ok := groups[s.ExternalID]; !ok {
			order = append(order, s.ExternalID)
		}
		groups[s.ExternalID] = append(groups[s.ExternalID], s)
	}
	result := make([]Subject, 0, len(order))
	for _, id := range order {
		group := groups[id]
		sort.SliceStable(group, func(a, b int) bool {
			x, y := group[a], group[b]
			switch {
			case x.PrimaryName != y.PrimaryName:
				return x.PrimaryName < y.PrimaryName
			case !equalNullable(x.Programmes, y.Programmes):
				return lessNullable(x.Programmes, y.Programmes)
			case !equalTime(x.ListedOn, y.ListedOn):
				return lessTime(x.ListedOn, y.ListedOn)
			case !equalNullable(x.Remarks, y.Remarks):
				return lessNullable(x.Remarks, y.Remarks)
			case !equalNullable(x.SourceURL, y.SourceURL):
				return lessNullable(x.SourceURL, y.SourceURL)
			}
			return x.Type < y.Type
		})
		first := group[0]
		first.Names = append([]SubjectName{}, first.Names...)
		first.Addresses = append([]SubjectAddress{}, first.Addresses...)
		for _, other := range group[1:] {
			for _, name := range other.Names {
				found := false
				for i := range first.Names {
					if first.Names[i].FullName == name.FullName {
						first.Names[i].IsWeak = first.Names[i].IsWeak && name.IsWeak
						found = true
						break
					}
				}
				if !found {
					first.Names = append(first.Names, name)
				}
			}
			for _, address := range other.Addresses {
				found := false
				for _, existing := range first.Addresses {
					if existing.Raw == address.Raw && equalNullable(existing.CountryCode, address.CountryCode) {
						found = true
						break
					}
				}
				if !found {
					first.Addresses = append(first.Addresses, address)
				}
			}
		}
		result = append(result, first)
	}
	return result
}

func equalNullable(a, b *string) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	return *a == *b
}

func equalTime(a, b *time.Time) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	return a.Equal(*b)
}

func utf16Len(s string) int { return len(utf16.Encode([]rune(s))) }

// truncate cuts to length UTF-16 units, as the reference's value[..length].
func truncate(value string, length int) string {
	units := utf16.Encode([]rune(value))
	if len(units) <= length {
		return value
	}
	return string(utf16.Decode(units[:length]))
}

func truncatePtr(value *string, length int) *string {
	if value == nil {
		return nil
	}
	t := truncate(*value, length)
	return &t
}

// Review makes a held import active (confirm) or rejects it and deletes its records, under the source's lock.
func (s *Imports) Review(ctx context.Context, importID int, confirm bool, reviewer string) (ReviewResult, error) {
	var sourceID int
	var code string
	err := s.DB.QueryRowContext(ctx, `SELECT s.Id, s.Code FROM Imports i JOIN Sources s ON s.Id = i.SourceId WHERE i.Id = @id`, sql.Named("id", importID)).Scan(&sourceID, &code)
	if errors.Is(err, sql.ErrNoRows) {
		return ReviewNotFound, nil
	}
	if err != nil {
		return "", err
	}
	conn, err := s.DB.Conn(ctx)
	if err != nil {
		return "", err
	}
	defer conn.Close()
	if err := acquireLock(ctx, conn, code); err != nil {
		return "", err
	}
	defer releaseLock(conn, code)
	source, err := findSource(ctx, conn, `Id = @key`, sourceID)
	if err != nil {
		return "", err
	}
	var status string
	var hasData bool
	var message *string
	var basedOn *int
	var startedAt time.Time
	if err := conn.QueryRowContext(ctx, `SELECT Status, HasData, Message, BasedOnImportId, StartedAt FROM Imports WHERE Id = @id`, sql.Named("id", importID)).
		Scan(&status, &hasData, &message, &basedOn, &startedAt); err != nil {
		return "", err
	}
	if status != "PendingReview" || !hasData {
		return ReviewNotPending, nil
	}
	now := s.Now()
	if !confirm {
		return ReviewRejected, s.rejectHeld(ctx, conn, importID, message, "rejected on review", now, &reviewer)
	}
	if !equalInt(basedOn, source.ActiveImportID) {
		return ReviewNotPending, s.rejectHeld(ctx, conn, importID, message, "superseded: the list changed after it was held", now, nil)
	}
	stamp := &reviewStamp{truncate(reviewer, 256), now, truncate(deref(message)+"; confirmed on review", 1000)}
	if err := s.activate(ctx, conn, source, importID, source.ActiveImportID, startedAt, stamp); err != nil {
		return "", err
	}
	return ReviewConfirmed, deleteOldImports(ctx, conn, source.ID)
}

func deref(v *string) string {
	if v == nil {
		return ""
	}
	return *v
}

func equalInt(a, b *int) bool {
	if a == nil || b == nil {
		return a == nil && b == nil
	}
	return *a == *b
}

// rejectHeld marks the import Rejected first, then deletes its records: an interrupted deletion leaves a rejected
// import, never a held one that could still be confirmed with part of its records.
func (s *Imports) rejectHeld(ctx context.Context, conn *sql.Conn, importID int, message *string, reason string, now time.Time, reviewer *string) error {
	text := truncate(deref(message)+"; "+reason, 1000)
	var err error
	if reviewer != nil {
		_, err = conn.ExecContext(ctx, `UPDATE Imports SET Status = 'Rejected', Message = @message, ReviewedAt = @at, ReviewedBy = @by WHERE Id = @id`,
			sql.Named("message", text), sql.Named("at", now), sql.Named("by", truncate(*reviewer, 256)), sql.Named("id", importID))
	} else {
		_, err = conn.ExecContext(ctx, `UPDATE Imports SET Status = 'Rejected', Message = @message WHERE Id = @id`, sql.Named("message", text), sql.Named("id", importID))
	}
	if err != nil {
		return err
	}
	if err := deleteImportData(ctx, conn, importID); err != nil {
		return err
	}
	_, err = conn.ExecContext(ctx, `UPDATE Imports SET HasData = 0 WHERE Id = @id`, sql.Named("id", importID))
	return err
}

// CurrentLogin is who this connection is signed in as: the reviewer the CLI records.
func (s *Imports) CurrentLogin(ctx context.Context) string {
	var login *string
	s.DB.QueryRowContext(ctx, `SELECT SUSER_SNAME()`).Scan(&login) //nolint:errcheck // "unknown" below
	if login == nil {
		return "unknown"
	}
	return *login
}

// SourceStatus is a source with its active import, for the CLI's status.
type SourceStatus struct {
	Code              string
	RecordCount       *int
	ImportedAt        *time.Time
	LastCheckedAt     *time.Time
	MaxAgeHours       *int
	LastImportStatus  *string
	LastImportMessage *string
}

func (s *Imports) Status(ctx context.Context) ([]SourceStatus, error) {
	rows, err := s.DB.QueryContext(ctx, `SELECT s.Code, a.RecordCount, a.CompletedAt, s.LastCheckedAt, s.MaxAgeHours, l.Status, l.Message
FROM Sources s
LEFT JOIN Imports a ON a.Id = s.ActiveImportId
OUTER APPLY (SELECT TOP 1 Status, Message FROM Imports i WHERE i.SourceId = s.Id ORDER BY i.Id DESC) l
ORDER BY s.Code`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []SourceStatus
	for rows.Next() {
		var st SourceStatus
		if err := rows.Scan(&st.Code, &st.RecordCount, &st.ImportedAt, &st.LastCheckedAt, &st.MaxAgeHours, &st.LastImportStatus, &st.LastImportMessage); err != nil {
			return nil, err
		}
		out = append(out, st)
	}
	return out, rows.Err()
}

// HistoryPerSource lists each source's latest imports (or code's), newest first, perSource each.
func HistoryPerSource(ctx context.Context, db *sql.DB, code string, perSource int) ([]HistoryEntry, error) {
	rows, err := db.QueryContext(ctx, `SELECT `+historyColumns+`
FROM Sources s CROSS APPLY (SELECT TOP (@per) * FROM Imports x WHERE x.SourceId = s.Id ORDER BY x.Id DESC) i
WHERE @code = N'' OR s.Code = @code
ORDER BY s.Code, i.Id DESC`, sql.Named("per", perSource), sql.Named("code", code))
	if err != nil {
		return nil, err
	}
	return scanHistory(rows)
}
