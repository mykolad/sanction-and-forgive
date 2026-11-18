//! Japan's asset-freeze list (Ministry of Finance, CSV): JapanParser in the reference.

use std::collections::HashMap;
use std::io::BufRead;

use super::manual::csv_rows;
use super::parse::{
    birth_date_parts, clean, format_error, free_text_identifiers, join, parse_date, Builder, ParseError, ParsedSubject, SubjectType,
};

/// The law the list is kept under; each row adds its category number.
pub const JAPAN_PROGRAMME: &str = "Foreign Exchange and Foreign Trade Act";

const COLUMNS: [&str; 22] = [
    "区分",
    "番号",
    "告示日付",
    "個人・団体",
    "氏名（日本語）",
    "氏名（英語）",
    "別名・別称（日本語）",
    "別名・別称（英語）",
    "旧称（日本語）",
    "旧称（英語）",
    "確定に十分でない別名（日本語）",
    "確定に十分でない別名（英語）",
    "称号（英語）",
    "役職（英語）",
    "生年月日",
    "出生地（英語）",
    "国籍（英語）",
    "住所・所在地（国）（日本語）",
    "住所・所在地（都市その他の情報）（日本語）",
    "住所・所在地（国）（英語）",
    "住所・所在地（都市その他の情報）（英語）",
    "その他の情報",
];

pub fn parse_japan(input: &mut dyn BufRead) -> Result<Vec<ParsedSubject>, ParseError> {
    let mut data = Vec::new();
    input.read_to_end(&mut data)?;
    let rows = csv_rows(&String::from_utf8_lossy(&data))?;
    let has = |name: &str| rows.first().is_some_and(|header| header.iter().any(|cell| cell.trim() == name));
    if !has("番号") || !has("氏名（英語）") {
        return Err(format_error("No header row (区分, 番号, …, 氏名（英語）, …) in Japan's asset-freeze list."));
    }
    let mut columns: HashMap<&str, usize> = HashMap::new();
    for name in COLUMNS {
        let index = rows[0]
            .iter()
            .position(|cell| cell.trim() == name)
            .ok_or_else(|| format_error(format!("Japan's asset-freeze list has no \"{name}\" column.")))?;
        columns.insert(name, index);
    }

    let mut out = Vec::new();
    for row in &rows[1..] {
        let cell = |name: &str| row.get(columns[name]).and_then(|v| clean(Some(v)));
        if row.iter().all(|value| clean(Some(value)).is_none()) {
            continue;
        }
        let id = cell("番号").ok_or_else(|| format_error("A row in Japan's asset-freeze list has no 番号 (number)."))?;
        let kind = match cell("個人・団体").as_deref() {
            Some("個人") => SubjectType::Individual,
            Some("団体") => SubjectType::Entity,
            _ => SubjectType::Unknown,
        };
        let mut b = Builder::new(&id, kind);
        b.programmes = Some(match cell("区分") {
            Some(category) => format!("{JAPAN_PROGRAMME}, category {category}"),
            None => JAPAN_PROGRAMME.to_string(),
        });
        b.listed_on = parse_date(cell("告示日付").as_deref().and_then(|notice| notice.split(' ').next()), "%Y.%m.%d");
        b.remarks = join(
            "; ",
            &[
                cell("称号（英語）"),
                cell("役職（英語）"),
                cell("生年月日").map(|born| format!("born {born}")),
                cell("出生地（英語）"),
                cell("国籍（英語）").map(|nationality| format!("nationality {nationality}")),
                cell("その他の情報"),
            ],
        );
        // Identifiers and dates of birth (#68), in Japanese prose: "アフガニスタン旅券 P04581926（…に発行…）", "1941/8/20",
        // "1970年". The columns are optional: a file without them has none.
        let optional = |name: &str| rows[0].iter().position(|c| c.trim() == name).and_then(|i| row.get(i)).and_then(|v| clean(Some(v)));
        for (column, kind) in [("旅券番号", "Passport"), ("身分証番号", "ID card")] {
            for number in optional(column).map(|t| free_text_identifiers(&t)).unwrap_or_default() {
                b.add_identifier(Some(kind), Some(&number), None);
            }
        }
        for date in split(cell("生年月日")) {
            b.add_birth_date(japanese_date(&date));
        }
        for (i, name) in split(cell("氏名（英語）")).iter().enumerate() {
            b.add_name(Some(name), false, i == 0);
        }
        for column in ["氏名（日本語）", "別名・別称（英語）", "別名・別称（日本語）", "旧称（英語）", "旧称（日本語）"]
        {
            for name in split(cell(column)) {
                b.add_name(Some(&name), false, false);
            }
        }
        for column in ["確定に十分でない別名（英語）", "確定に十分でない別名（日本語）"] {
            for name in split(cell(column)) {
                b.add_name(Some(&name), true, false);
            }
        }
        let english = cell("住所・所在地（都市その他の情報）（英語）").is_some() || cell("住所・所在地（国）（英語）").is_some();
        let (city, country) = if english {
            ("住所・所在地（都市その他の情報）（英語）", "住所・所在地（国）（英語）")
        } else {
            ("住所・所在地（都市その他の情報）（日本語）", "住所・所在地（国）（日本語）")
        };
        let (cities, countries) = (split(cell(city)), split(cell(country)));
        if cities.len() == countries.len() {
            for (city, country) in cities.iter().zip(&countries) {
                b.add_address(join(", ", &[Some(city.clone()), Some(country.clone())]).as_deref(), None);
            }
        } else {
            for part in cities.iter().chain(&countries) {
                b.add_address(Some(part), None);
            }
        }
        out.extend(b.build());
    }
    Ok(out)
}

/// A date as the list writes one: "1941/8/20", "1970年", "1970年8月20日" (full-width digits too). None otherwise.
fn japanese_date(text: &str) -> Option<String> {
    use unicode_normalization::UnicodeNormalization;
    let text: String = text.nfkc().collect::<String>().replace(['年', '月'], "/").replace('日', "");
    let parts: Vec<&str> = text.trim().trim_end_matches('/').split('/').collect();
    if parts.is_empty() || parts.len() > 3 || parts.iter().any(|p| p.is_empty() || !p.chars().all(|c| c.is_ascii_digit())) {
        return None;
    }
    birth_date_parts(Some(parts[0]), parts.get(1).copied(), parts.get(2).copied())
}

/// A cell's list: split at semicolons (half or full width), each part without the "at the time of listing" note and
/// the commas left at its end.
fn split(cell: Option<String>) -> Vec<String> {
    let Some(cell) = cell else { return Vec::new() };
    cell.split([';', '；'])
        .filter_map(|raw| clean(Some(&raw.replace("(リスト掲載時点)", " ").replace("（リスト掲載時点）", " "))))
        .map(|part| part.trim_end_matches([',', '、', ' ']).to_string())
        .filter(|part| !part.is_empty())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::{japanese_date, split};

    #[test]
    fn dates_of_birth_read_as_the_list_writes_them() {
        assert_eq!(japanese_date("1941/8/20").as_deref(), Some("1941-08-20"));
        assert_eq!(japanese_date("1970年").as_deref(), Some("1970"));
        assert_eq!(japanese_date("１９７０年８月２０日").as_deref(), Some("1970-08-20"));
        assert_eq!(japanese_date("不明"), None);
    }

    #[test]
    fn a_cell_splits_at_either_semicolon_without_its_listing_note_or_trailing_commas() {
        assert_eq!(
            split(Some("Kuwait(リスト掲載時点); Kabul,；Darul Aman, Kabul,".to_string())),
            vec!["Kuwait", "Kabul", "Darul Aman, Kabul"]
        );
    }
}
