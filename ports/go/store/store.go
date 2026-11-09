// Package store reads and writes the shared database (the schema the reference's EF migrations define).
package store

import (
	"context"
	"database/sql"
	"fmt"
	"strings"
	"time"

	mssql "github.com/microsoft/go-mssqldb"
	"github.com/microsoft/go-mssqldb/azuread"
)

// Open opens the database from an ADO.NET connection string (ConnectionStrings__Lists, as the reference reads it):
// SQL authentication for tests, the managed identity in Azure, the signed-in az user from tools/lists.ps1.
func Open(connectionString string) (*sql.DB, error) {
	driver, dsn := translate(connectionString)
	db, err := sql.Open(driver, dsn)
	if err != nil {
		return nil, err
	}
	db.SetMaxOpenConns(10)
	db.SetConnMaxIdleTime(5 * time.Minute)
	return db, nil
}

// translate turns ADO.NET keywords into go-mssqldb's: "Server=tcp:host,1433" into server and port, and the Entra
// authentication modes into fedauth with the azuread driver.
func translate(ado string) (string, string) {
	driver := "sqlserver"
	var parts []string
	for _, pair := range strings.Split(ado, ";") {
		key, value, ok := strings.Cut(pair, "=")
		if !ok {
			continue
		}
		key, value = strings.ToLower(strings.TrimSpace(key)), strings.TrimSpace(value)
		switch key {
		case "server", "data source", "address":
			value = strings.TrimPrefix(value, "tcp:")
			if host, port, found := strings.Cut(value, ","); found {
				parts = append(parts, "server="+host, "port="+port)
			} else {
				parts = append(parts, "server="+value)
			}
		case "authentication":
			driver = azuread.DriverName
			switch strings.ToLower(strings.ReplaceAll(value, " ", "")) {
			case "activedirectorymanagedidentity", "activedirectorymsi":
				parts = append(parts, "fedauth=ActiveDirectoryManagedIdentity")
			case "activedirectorydefault":
				parts = append(parts, "fedauth=ActiveDirectoryDefault")
			default:
				parts = append(parts, "fedauth="+strings.ReplaceAll(value, " ", ""))
			}
		case "initial catalog":
			parts = append(parts, "database="+value)
		case "user id", "uid", "user":
			parts = append(parts, "user id="+value)
		case "connect timeout", "connection timeout":
			parts = append(parts, "connection timeout="+value)
		case "trustservercertificate":
			parts = append(parts, "TrustServerCertificate="+strings.ToLower(value))
		case "encrypt":
			parts = append(parts, "encrypt="+strings.ToLower(value))
		default:
			parts = append(parts, key+"="+value)
		}
	}
	return driver, strings.Join(parts, ";")
}

var _ = mssql.Error{}

// SourceRow is a source with its active import's count and completion.
type SourceRow struct {
	ID                int
	Code              string
	Name              string
	URL               *string
	MaxAgeHours       *int
	ActiveImportID    *int
	LastCheckedAt     *time.Time
	SwitchedOffAt     *time.Time
	SwitchedOffReason *string
	RecordCount       int
	UpdatedAt         *time.Time
}

// Sources lists every source ordered by code (SQL Server's collation, as the reference's query orders them).
func Sources(ctx context.Context, db *sql.DB) ([]SourceRow, error) {
	rows, err := db.QueryContext(ctx, `
SELECT s.Id, s.Code, s.Name, s.Url, s.MaxAgeHours, s.ActiveImportId, s.LastCheckedAt, s.SwitchedOffAt, s.SwitchedOffReason,
       COALESCE(i.RecordCount, 0), i.CompletedAt
FROM Sources s LEFT JOIN Imports i ON i.Id = s.ActiveImportId
ORDER BY s.Code`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []SourceRow
	for rows.Next() {
		var r SourceRow
		if err := rows.Scan(&r.ID, &r.Code, &r.Name, &r.URL, &r.MaxAgeHours, &r.ActiveImportID, &r.LastCheckedAt, &r.SwitchedOffAt, &r.SwitchedOffReason, &r.RecordCount, &r.UpdatedAt); err != nil {
			return nil, err
		}
		out = append(out, r)
	}
	return out, rows.Err()
}

// HistoryEntry is one import as the history shows it.
type HistoryEntry struct {
	ID          int
	SourceCode  string
	Status      string
	StartedAt   time.Time
	CompletedAt *time.Time
	RecordCount int
	Message     *string
	Active      bool
	ReviewedAt  *time.Time
	ReviewedBy  *string
	Added       *int
	Removed     *int
	Changed     *int
}

const historyColumns = `i.Id, s.Code, i.Status, i.StartedAt, i.CompletedAt, i.RecordCount, i.Message,
       CASE WHEN s.ActiveImportId = i.Id THEN 1 ELSE 0 END, i.ReviewedAt, i.ReviewedBy, i.AddedCount, i.RemovedCount, i.ChangedCount`

func scanHistory(rows *sql.Rows) ([]HistoryEntry, error) {
	defer rows.Close()
	var out []HistoryEntry
	for rows.Next() {
		var h HistoryEntry
		var active int
		if err := rows.Scan(&h.ID, &h.SourceCode, &h.Status, &h.StartedAt, &h.CompletedAt, &h.RecordCount, &h.Message, &active, &h.ReviewedAt, &h.ReviewedBy, &h.Added, &h.Removed, &h.Changed); err != nil {
			return nil, err
		}
		h.Active = active == 1
		out = append(out, h)
	}
	return out, rows.Err()
}

// History lists every import of every source (or of code), sources by code, each newest first.
func History(ctx context.Context, db *sql.DB, code string) ([]HistoryEntry, error) {
	rows, err := db.QueryContext(ctx, `SELECT `+historyColumns+`
FROM Imports i JOIN Sources s ON s.Id = i.SourceId
WHERE @code = N'' OR s.Code = @code
ORDER BY s.Code, i.Id DESC`, sql.Named("code", code))
	if err != nil {
		return nil, err
	}
	return scanHistory(rows)
}

// Held lists every import held for review (with its records), newest first.
func Held(ctx context.Context, db *sql.DB) ([]HistoryEntry, error) {
	rows, err := db.QueryContext(ctx, `SELECT `+historyColumns+`
FROM Imports i JOIN Sources s ON s.Id = i.SourceId
WHERE i.Status = 'PendingReview' AND i.HasData = 1
ORDER BY i.Id DESC`)
	if err != nil {
		return nil, err
	}
	return scanHistory(rows)
}

// AllowListRecord is an allow-list entry as stored.
type AllowListRecord struct {
	ID        int
	Name      string
	Reason    string
	AddedBy   string
	AddedAt   time.Time
	RemovedBy *string
	RemovedAt *time.Time
}

// AllowListHistory lists every entry ever added, newest first.
func AllowListHistory(ctx context.Context, q querier) ([]AllowListRecord, error) {
	rows, err := q.QueryContext(ctx, `SELECT Id, Name, Reason, AddedBy, AddedAt, RemovedBy, RemovedAt FROM AllowListEntries ORDER BY Id DESC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []AllowListRecord
	for rows.Next() {
		var r AllowListRecord
		if err := rows.Scan(&r.ID, &r.Name, &r.Reason, &r.AddedBy, &r.AddedAt, &r.RemovedBy, &r.RemovedAt); err != nil {
			return nil, err
		}
		out = append(out, r)
	}
	return out, rows.Err()
}

// AllowListCurrent returns the entries in force (by id) and the version: additions plus removals.
func AllowListCurrent(ctx context.Context, q querier) ([]AllowListRecord, string, error) {
	all, err := AllowListHistory(ctx, q)
	if err != nil {
		return nil, "", err
	}
	removed := 0
	var inForce []AllowListRecord
	for i := len(all) - 1; i >= 0; i-- {
		if all[i].RemovedAt != nil {
			removed++
		} else {
			inForce = append(inForce, all[i])
		}
	}
	return inForce, fmt.Sprint(len(all) + removed), nil
}

type querier interface {
	QueryContext(ctx context.Context, query string, args ...any) (*sql.Rows, error)
}

// Subject is a listed subject as stored, with its names and addresses.
type Subject struct {
	ID          int
	ExternalID  string
	Type        int
	PrimaryName string
	Programmes  *string
	ListedOn    *time.Time
	Remarks     *string
	SourceURL   *string
	Names       []SubjectName
	Addresses   []SubjectAddress
}

type SubjectName struct {
	FullName string
	IsWeak   bool
	Script   int
}

type SubjectAddress struct {
	Raw         string
	CountryCode *string
}

// ActiveSubjects reads import importID's subjects in one snapshot transaction, if the source still points at it
// (cleanup never deletes the active import, so then nothing of it can be gone). Nil when it moved on.
func ActiveSubjects(ctx context.Context, db *sql.DB, code string, importID int) ([]Subject, error) {
	tx, err := db.BeginTx(ctx, &sql.TxOptions{Isolation: sql.LevelSnapshot})
	if err != nil {
		return nil, err
	}
	defer tx.Rollback() //nolint:errcheck // read-only
	var still int
	if err := tx.QueryRowContext(ctx, `SELECT COUNT(*) FROM Sources WHERE Code = @code AND ActiveImportId = @id`, sql.Named("code", code), sql.Named("id", importID)).Scan(&still); err != nil {
		return nil, err
	}
	if still == 0 {
		return nil, nil
	}
	subjects, err := readSubjects(ctx, tx, importID)
	if err != nil {
		return nil, err
	}
	return subjects, tx.Commit()
}

func readSubjects(ctx context.Context, tx *sql.Tx, importID int) ([]Subject, error) {
	rows, err := tx.QueryContext(ctx, `SELECT Id, ExternalId, Type, PrimaryName, Programmes, ListedOn, Remarks, SourceUrl FROM Subjects WHERE ImportId = @id ORDER BY Id`, sql.Named("id", importID))
	if err != nil {
		return nil, err
	}
	var subjects []Subject
	byID := map[int]int{}
	for rows.Next() {
		var s Subject
		if err := rows.Scan(&s.ID, &s.ExternalID, &s.Type, &s.PrimaryName, &s.Programmes, &s.ListedOn, &s.Remarks, &s.SourceURL); err != nil {
			rows.Close()
			return nil, err
		}
		byID[s.ID] = len(subjects)
		subjects = append(subjects, s)
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return nil, err
	}

	names, err := tx.QueryContext(ctx, `SELECT n.SubjectId, n.FullName, n.IsWeak, n.Script FROM SubjectNames n JOIN Subjects s ON s.Id = n.SubjectId WHERE s.ImportId = @id ORDER BY n.SubjectId, n.Id`, sql.Named("id", importID))
	if err != nil {
		return nil, err
	}
	for names.Next() {
		var subjectID int
		var n SubjectName
		if err := names.Scan(&subjectID, &n.FullName, &n.IsWeak, &n.Script); err != nil {
			names.Close()
			return nil, err
		}
		subjects[byID[subjectID]].Names = append(subjects[byID[subjectID]].Names, n)
	}
	names.Close()

	addresses, err := tx.QueryContext(ctx, `SELECT a.SubjectId, a.Raw, a.CountryCode FROM SubjectAddresses a JOIN Subjects s ON s.Id = a.SubjectId WHERE s.ImportId = @id ORDER BY a.SubjectId, a.Id`, sql.Named("id", importID))
	if err != nil {
		return nil, err
	}
	for addresses.Next() {
		var subjectID int
		var a SubjectAddress
		if err := addresses.Scan(&subjectID, &a.Raw, &a.CountryCode); err != nil {
			addresses.Close()
			return nil, err
		}
		subjects[byID[subjectID]].Addresses = append(subjects[byID[subjectID]].Addresses, a)
	}
	addresses.Close()
	return subjects, nil
}
