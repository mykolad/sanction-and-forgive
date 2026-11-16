package lists

import (
	"bufio"
	"io"
	"strings"
)

// japanProgramme is the law the list is kept under; each row adds its category number.
const japanProgramme = "Foreign Exchange and Foreign Trade Act"

// parseJapan reads Japan's asset-freeze list (Ministry of Finance, CSV): JapanParser in the reference.
func parseJapan(reader io.Reader) ([]ParsedSubject, error) {
	rows, err := csvRows(bufio.NewReader(reader))
	if err != nil {
		return nil, err
	}
	has := func(row []string, name string) bool {
		for _, cell := range row {
			if strings.TrimSpace(cell) == name {
				return true
			}
		}
		return false
	}
	if len(rows) == 0 || !has(rows[0], "番号") || !has(rows[0], "氏名（英語）") {
		return nil, formatError("No header row (区分, 番号, …, 氏名（英語）, …) in Japan's asset-freeze list.")
	}
	columns := map[string]int{}
	for _, name := range []string{"区分", "番号", "告示日付", "個人・団体", "氏名（日本語）", "氏名（英語）", "別名・別称（日本語）", "別名・別称（英語）",
		"旧称（日本語）", "旧称（英語）", "確定に十分でない別名（日本語）", "確定に十分でない別名（英語）", "称号（英語）", "役職（英語）", "生年月日", "出生地（英語）",
		"国籍（英語）", "住所・所在地（国）（日本語）", "住所・所在地（都市その他の情報）（日本語）", "住所・所在地（国）（英語）", "住所・所在地（都市その他の情報）（英語）", "その他の情報"} {
		columns[name] = -1
		for i, cell := range rows[0] {
			if strings.TrimSpace(cell) == name {
				columns[name] = i
				break
			}
		}
		if columns[name] < 0 {
			return nil, formatError("Japan's asset-freeze list has no %q column.", name)
		}
	}

	var out []ParsedSubject
	for _, row := range rows[1:] {
		cell := func(name string) *string {
			if index := columns[name]; index < len(row) {
				return cleanString(row[index])
			}
			return nil
		}
		empty := true
		for _, value := range row {
			if cleanString(value) != nil {
				empty = false
				break
			}
		}
		if empty {
			continue
		}
		id := cell("番号")
		if id == nil {
			return nil, formatError("A row in Japan's asset-freeze list has no 番号 (number).")
		}
		kind := Unknown
		switch deref(cell("個人・団体")) {
		case "個人":
			kind = Individual
		case "団体":
			kind = Entity
		}
		b := newBuilder(*id, kind)
		if category := cell("区分"); category != nil {
			b.Programmes = s(japanProgramme + ", category " + *category)
		} else {
			b.Programmes = s(japanProgramme)
		}
		if notice := cell("告示日付"); notice != nil {
			b.ListedOn = ParseDate(s(strings.SplitN(*notice, " ", 2)[0]), "2006.1.2")
		}
		prefixed := func(prefix string, value *string) *string {
			if value == nil {
				return nil
			}
			return s(prefix + *value)
		}
		b.Remarks = Join("; ", cell("称号（英語）"), cell("役職（英語）"), prefixed("born ", cell("生年月日")), cell("出生地（英語）"),
			prefixed("nationality ", cell("国籍（英語）")), cell("その他の情報"))
		for i, name := range japanSplit(cell("氏名（英語）")) {
			b.AddName(s(name), false, i == 0)
		}
		for _, column := range []string{"氏名（日本語）", "別名・別称（英語）", "別名・別称（日本語）", "旧称（英語）", "旧称（日本語）"} {
			for _, name := range japanSplit(cell(column)) {
				b.AddName(s(name), false, false)
			}
		}
		for _, column := range []string{"確定に十分でない別名（英語）", "確定に十分でない別名（日本語）"} {
			for _, name := range japanSplit(cell(column)) {
				b.AddName(s(name), true, false)
			}
		}
		english := cell("住所・所在地（都市その他の情報）（英語）") != nil || cell("住所・所在地（国）（英語）") != nil
		cityColumn, countryColumn := "住所・所在地（都市その他の情報）（日本語）", "住所・所在地（国）（日本語）"
		if english {
			cityColumn, countryColumn = "住所・所在地（都市その他の情報）（英語）", "住所・所在地（国）（英語）"
		}
		cities, countries := japanSplit(cell(cityColumn)), japanSplit(cell(countryColumn))
		if len(cities) == len(countries) {
			for i := range cities {
				b.AddAddress(Join(", ", s(cities[i]), s(countries[i])), nil)
			}
		} else {
			for _, part := range append(cities, countries...) {
				b.AddAddress(s(part), nil)
			}
		}
		add(&out, b.Build())
	}
	return out, nil
}

// japanSplit is a cell's list: split at semicolons (half or full width), each part without the "at the time of
// listing" note and the commas left at its end.
func japanSplit(cell *string) []string {
	var parts []string
	if cell == nil {
		return parts
	}
	for _, raw := range strings.FieldsFunc(*cell, func(r rune) bool { return r == ';' || r == '；' }) {
		raw = strings.ReplaceAll(strings.ReplaceAll(raw, "(リスト掲載時点)", " "), "（リスト掲載時点）", " ")
		if cleaned := cleanString(raw); cleaned != nil {
			if part := strings.TrimRight(*cleaned, ", 、"); part != "" {
				parts = append(parts, part)
			}
		}
	}
	return parts
}
