package store

import (
	"context"
	"database/sql"
	"strings"
	"time"
)

// SwitchResult says what switching a list did.
type SwitchResult string

const (
	Switched        SwitchResult = "Switched"
	SwitchUnchanged SwitchResult = "Unchanged"
	SwitchNotFound  SwitchResult = "NotFound"
)

// Switch switches a list off (with a reason) or on, in one conditional update (SourceSwitch.cs).
func Switch(ctx context.Context, db *sql.DB, code string, off bool, reason *string, by string, now time.Time) (SwitchResult, error) {
	var result sql.Result
	var err error
	if off {
		result, err = db.ExecContext(ctx, `UPDATE Sources SET SwitchedOffAt = @now, SwitchedOffBy = @by, SwitchedOffReason = @reason WHERE Code = @code AND SwitchedOffAt IS NULL`,
			sql.Named("now", now), sql.Named("by", by), sql.Named("reason", reason), sql.Named("code", code))
	} else {
		result, err = db.ExecContext(ctx, `UPDATE Sources SET SwitchedOffAt = NULL, SwitchedOffBy = NULL, SwitchedOffReason = NULL WHERE Code = @code AND SwitchedOffAt IS NOT NULL`,
			sql.Named("code", code))
	}
	if err != nil {
		return "", err
	}
	if n, _ := result.RowsAffected(); n > 0 {
		return Switched, nil
	}
	var exists int
	if err := db.QueryRowContext(ctx, `SELECT COUNT(*) FROM Sources WHERE Code = @code`, sql.Named("code", code)).Scan(&exists); err != nil {
		return "", err
	}
	if exists > 0 {
		return SwitchUnchanged, nil
	}
	return SwitchNotFound, nil
}

// AllowListAdd adds an entry unless existing finds the name among the entries in force, checked and inserted under one
// lock held to the end of the transaction (AllowListStore.AddAsync). Returns the new id, or the existing one.
func AllowListAdd(ctx context.Context, db *sql.DB, name, reason, addedBy string, now time.Time, existing func([]AllowListRecord) *int) (*int, *int, error) {
	name, reason = strings.TrimSpace(name), strings.TrimSpace(reason)
	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return nil, nil, err
	}
	defer tx.Rollback() //nolint:errcheck // committed below
	var lock int
	if err := tx.QueryRowContext(ctx, `DECLARE @result int; EXEC @result = sp_getapplock @Resource = N'allow-list', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; SELECT @result`).Scan(&lock); err != nil {
		return nil, nil, err
	}
	if lock < 0 {
		return nil, nil, context.DeadlineExceeded
	}
	inForce, _, err := AllowListCurrent(ctx, tx)
	if err != nil {
		return nil, nil, err
	}
	if id := existing(inForce); id != nil {
		return nil, id, nil
	}
	var id int
	if err := tx.QueryRowContext(ctx, `INSERT INTO AllowListEntries (Name, Reason, AddedBy, AddedAt) OUTPUT INSERTED.Id VALUES (@name, @reason, @by, @at)`,
		sql.Named("name", name), sql.Named("reason", reason), sql.Named("by", addedBy), sql.Named("at", now)).Scan(&id); err != nil {
		return nil, nil, err
	}
	return &id, nil, tx.Commit()
}

// AllowListRemove marks an entry removed (entries are never deleted); false if there's no such entry in force.
func AllowListRemove(ctx context.Context, db *sql.DB, id int, removedBy string, now time.Time) (bool, error) {
	result, err := db.ExecContext(ctx, `UPDATE AllowListEntries SET RemovedBy = @by, RemovedAt = @at WHERE Id = @id AND RemovedAt IS NULL`,
		sql.Named("by", removedBy), sql.Named("at", now), sql.Named("id", id))
	if err != nil {
		return false, err
	}
	n, _ := result.RowsAffected()
	return n == 1, nil
}
