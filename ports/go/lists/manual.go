package lists

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"strings"
	"time"
)

var manualColumns = []string{"external_id", "type", "name", "aliases", "address", "country", "programme", "listed_on", "remarks"}

// ReadManualCsv reads manual records: UTF-8 CSV with a header (ManualCsvReader.cs in the reference).
func ReadManualCsv(reader io.Reader) ([]ParsedSubject, error) {
	rows, err := csvRows(bufio.NewReader(reader))
	if err != nil {
		return nil, err
	}
	if len(rows) == 0 {
		return nil, formatError("The CSV is empty: a header is required.")
	}
	header := make([]string, len(rows[0]))
	for i, column := range rows[0] {
		header[i] = strings.ToLower(strings.TrimFunc(column, isWhiteSpace))
	}
	index := map[string]int{}
	for _, column := range manualColumns {
		index[column] = -1
		for i, h := range header {
			if h == column {
				index[column] = i
				break
			}
		}
	}
	for _, required := range []string{"external_id", "type", "name"} {
		if index[required] < 0 {
			return nil, formatError("The header has no %s column.", required)
		}
	}
	var subjects []ParsedSubject
	for line := 1; line < len(rows); line++ {
		row := rows[line]
		blank := true
		for _, field := range row {
			blank = blank && strings.TrimFunc(field, isWhiteSpace) == ""
		}
		if blank {
			continue
		}
		if len(row) > len(header) {
			return nil, formatError("Line %d has %d fields but the header has %d: quote any value containing a comma.", line+1, len(row), len(header))
		}
		field := func(column string) *string {
			if i := index[column]; i >= 0 && i < len(row) {
				return cleanString(row[i])
			}
			return nil
		}
		externalID := field("external_id")
		if externalID == nil {
			return nil, formatError("Line %d: external_id is required.", line+1)
		}
		name := field("name")
		if name == nil {
			return nil, formatError("Line %d: name is required.", line+1)
		}
		var kind SubjectType
		switch t := field("type"); {
		case t != nil && strings.ToLower(*t) == "individual":
			kind = Individual
		case t != nil && strings.ToLower(*t) == "entity":
			kind = Entity
		default:
			return nil, formatError("Line %d: type must be Individual or Entity, not '%s'.", line+1, strings.ToLower(deref(t)))
		}
		country := field("country")
		if country != nil {
			upper := strings.ToUpper(*country)
			country = &upper
			if len(upper) != 2 || !isUpperASCII(upper[0]) || !isUpperASCII(upper[1]) {
				return nil, formatError("Line %d: country must be a two-letter ISO code, not '%s'.", line+1, upper)
			}
		}
		listedOn := field("listed_on")
		date := ParseDate(listedOn, "2006-01-02")
		if listedOn != nil && date == nil {
			return nil, formatError("Line %d: listed_on must be yyyy-MM-dd, not '%s'.", line+1, *listedOn)
		}
		names := []ParsedName{{*name, false, DetectScript(*name)}}
		seen := map[string]bool{}
		for _, alias := range strings.Split(deref(field("aliases")), "|") {
			if a := cleanString(alias); a != nil && *a != *name && !seen[*a] {
				seen[*a] = true
				names = append(names, ParsedName{*a, false, DetectScript(*a)})
			}
		}
		var addresses []ParsedAddress
		if address := field("address"); address != nil || country != nil {
			raw := country
			if address != nil {
				raw = address
			}
			addresses = append(addresses, ParsedAddress{*raw, country})
		}
		subjects = append(subjects, ParsedSubject{*externalID, kind, *name, names, addresses, field("programme"), date, field("remarks"), nil})
	}
	return subjects, nil
}

func csvRows(reader *bufio.Reader) ([][]string, error) {
	var rows [][]string
	var row []string
	var field strings.Builder
	quoted, any := false, false
	for {
		c, _, err := reader.ReadRune()
		if errors.Is(err, io.EOF) {
			break
		}
		if err != nil {
			return nil, err
		}
		any = true
		if quoted {
			if c == '"' {
				if next, _, err := reader.ReadRune(); err == nil && next == '"' {
					field.WriteRune('"')
				} else {
					if err == nil {
						reader.UnreadRune() //nolint:errcheck // just read
					}
					quoted = false
				}
			} else {
				field.WriteRune(c)
			}
			continue
		}
		switch {
		case c == '"' && field.Len() == 0:
			quoted = true
		case c == ',':
			row = append(row, field.String())
			field.Reset()
		case c == '\r':
		case c == '\n':
			row = append(row, field.String())
			field.Reset()
			rows = append(rows, row)
			row, any = nil, false
		case c == '\uFEFF' && len(rows) == 0 && len(row) == 0 && field.Len() == 0:
			// A byte order mark: the reference's reader detects and drops it.
		default:
			field.WriteRune(c)
		}
	}
	if quoted {
		return nil, formatError("The CSV ends inside a quoted field: a closing quote is missing.")
	}
	if any || field.Len() > 0 || len(row) > 0 {
		row = append(row, field.String())
		rows = append(rows, row)
	}
	return rows, nil
}

// --- Downloading (FeedDownloader.cs) ---

// DownloadRetry: after a passing fault, wait each delay in turn, never starting an attempt past the budget.
type DownloadRetry struct {
	Delays []time.Duration
	Budget time.Duration
}

// DefaultRetry: five retries, 15 s doubling to 4 minutes, within 10 minutes of the first attempt.
var DefaultRetry = DownloadRetry{[]time.Duration{15 * time.Second, 30 * time.Second, time.Minute, 2 * time.Minute, 4 * time.Minute}, 10 * time.Minute}

// Downloader downloads a feed to a temporary file, identifying itself honestly.
type Downloader struct {
	Client  *http.Client
	Timeout time.Duration
	Retry   DownloadRetry
	Version string
	Log     io.Writer
}

type statusError struct{ code int }

func (e *statusError) Error() string {
	return fmt.Sprintf("Response status code does not indicate success: %d", e.code)
}

func (d *Downloader) Download(ctx context.Context, source, url string) (string, error) {
	started := time.Now()
	for attempt := 1; ; attempt++ {
		path, err := d.attempt(ctx, url)
		if err == nil {
			return path, nil
		}
		if !passing(err) || attempt > len(d.Retry.Delays) {
			return "", err
		}
		delay := d.Retry.Delays[attempt-1]
		if time.Since(started)+delay > d.Retry.Budget {
			return "", err
		}
		// Worded without "failed" or "rejected": the import alert looks for those.
		fmt.Fprintf(d.Log, "%s: download attempt %d of %d got %s; trying again in %d s.\n", source, attempt, len(d.Retry.Delays)+1, describe(err), int(delay.Seconds()))
		select {
		case <-ctx.Done():
			return "", ctx.Err()
		case <-time.After(delay):
		}
		if time.Since(started) > d.Retry.Budget {
			return "", err
		}
	}
}

func (d *Downloader) attempt(ctx context.Context, url string) (string, error) {
	ctx, cancel := context.WithTimeout(ctx, d.Timeout)
	defer cancel()
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return "", err
	}
	request.Header.Set("User-Agent", "sanction-and-forgive/"+d.Version+" (+https://github.com/mykolad/sanction-and-forgive)")
	response, err := d.Client.Do(request)
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode > 299 {
		return "", &statusError{response.StatusCode}
	}
	file, err := os.CreateTemp("", "feed-*")
	if err != nil {
		return "", err
	}
	if _, err := io.Copy(file, response.Body); err != nil {
		file.Close()
		os.Remove(file.Name())
		return "", err
	}
	return file.Name(), file.Close()
}

func passing(err error) bool {
	var status *statusError
	if errors.As(err, &status) {
		return status.code >= 500 || status.code == 408 || status.code == 429
	}
	var netErr net.Error
	return errors.As(err, &netErr) || errors.Is(err, context.DeadlineExceeded) || errors.Is(err, io.ErrUnexpectedEOF)
}

func describe(err error) string {
	var status *statusError
	if errors.As(err, &status) {
		return fmt.Sprintf("%d %s", status.code, strings.ReplaceAll(http.StatusText(status.code), " ", ""))
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return "no complete answer in time"
	}
	return fmt.Sprintf("%T", err)
}
