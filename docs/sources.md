# Sources

Every list the app screens against, what each feed holds, and whether its terms allow this use. The numbers come
from `probe` (`dotnet SanctionAndForgive.Cli.dll probe --source all`), run on 2026-11-06. Vessels and aircraft are
left out of the counts and the import, since the MVP doesn't screen them.

`Status` is `ListSources.All`'s `FeedStatus`: only `Active` feeds are imported automatically.

| Code | List | Format, size | Subjects (people / entities / unknown) | Names (weak, Cyrillic, other scripts) | Addresses | Status |
|---|---|---|---|---|---|---|
| `eu` | EU consolidated financial sanctions (FSF) | XML 1.1, 25.9 MB | 6,269 (4,490 / 1,779 / 0) | 30,804 (0, 5,177, 1,262) | 2,657 | Active |
| `ofac-sdn` | OFAC SDN | Enhanced XML, 110.7 MB | 17,643 (7,541 / 10,102 / 0) | 47,796 (4,478, 3,663, 2,141) | 22,225 | Active |
| `ofac-cons` | OFAC consolidated (non-SDN) | Enhanced XML, 3.9 MB | 486 (118 / 368 / 0) | 1,794 (78, 43, 75) | 580 | Active |
| `un` | UN Security Council consolidated | XML, 2.2 MB | 1,007 (733 / 274 / 0) | 4,125 (620, 16, 391) | 759 | **TermsUnclear** |
| `uk` | UK Sanctions List (FCDO) | XML, 22.1 MB | 5,737 (4,070 / 1,667 / 0) | 18,858 (603, 2,840, 895) | 3,859 | Active |
| `us-export` | US Consolidated Screening List: BIS and State only | JSON, 34.2 MB | 6,213 (0 / 0 / 6,213) | 11,576 (0, 0, 0) | 8,966 | Active |
| `ch-seco` | Switzerland SECO | XML, 43.1 MB | 6,766 (3,889 / 2,877 / 0) | 20,443 (656, 5,481, 939) | 3,702 | **TermsUnclear** |
| `ca-sema` | Canada autonomous sanctions (SEMA) | XML, 2.9 MB | 4,972 (3,522 / 1,450 / 0) | 8,808 (0, 2,856, 259) | 0 | **TermsUnclear** |
| `au-dfat` | Australia consolidated list | XLSX | not fetched | | | **NeedsAccess** |
| `ua-nsdc` | Ukraine NSDC State Register of Sanctions | JSON API | not fetched | | | **NeedsAccess** |
| `ua-sfms` | Ukraine SFMS terrorism list | XML, 1.8 MB | 1,014 (724 / 290 / 0) | 3,893 (627, 99, 0) | 771 | Active |
| `fr-tresor` | France national asset-freeze register (DG Trésor), from 2026-11-16 | JSON API, 15.0 MB | 8,187 (5,462 / 2,725 / 0) | 20,311 (134, 4,758, 438) | 3,491 | Active |
| `nl-terrorism` | Netherlands national terrorism list (Ministry of Foreign Affairs), from 2026-11-16 | ODS, 14 KB | 117 (114 / 3 / 0) | 120 (0, 0, 0) | 0 | Active |
| `nz-mfat` | New Zealand Russia Sanctions Register (MFAT), from 2026-11-16 | XLSX, 21.6 MB | 1,918 (1,389 / 529 / 0) | 2,896 (0, 0, 0) | 124 | Active |
| `jp-mof` | Japan asset-freeze list (Ministry of Finance), from 2026-11-16 | CSV, 3.2 MB | 2,897 (2,028 / 869 / 0) | 15,034 (345, 0, 6,648) | 1,707 | Active |
| `us-uflpa` | US UFLPA Entity List (DHS), from 2026-11-16 | HTML page, 138 KB | 192 (0 / 192 / 0) | 325 (0, 0, 0) | 0 | Active |
| **All fetched** | | 246 MB | **50,107** | **147,997** (7,062 weak, 20,175 Cyrillic) | 44,519 | |

The handover estimated 60-90k subjects. Without Australia and NSDC there are about 50k. NSDC is probably the
largest list, so the total is likely to land inside that range once it's added.

## Feed notes

- **EU:** the public token `dG9rZW4tMjAxNw` works (`LISTS_EU_TOKEN` overrides it). The file is `export/sanctionEntity`.
  `strong="false"` marks a weak alias, but no alias in today's file has it. The file's `generationDate` was
  2026-10-23 on 2026-11-06: either the list hadn't changed for two weeks or the public token serves a cached copy. Phase 2
  compares content hashes, so a stale copy would show up as "no change" for a long time; watch this.
- **OFAC:** the Consolidated file is `CONS_ENHANCED.XML` on the same service (confirmed on OFAC's consolidated page).
  The SDN file is 110 MB and parses in about 2 s once downloaded. Names come as `formattedFirstName` +
  `formattedLastName` per translation (Latin, Cyrillic, Arabic …); `isLowQuality` marks weak aliases.
- **US export (CSL):** 26,257 records. Only the 6,213 Commerce (BIS) and State records are kept; the Treasury ones
  repeat OFAC. Those records have **no type**, so they are `Unknown`: matching reads them as both a person and an
  entity. The CSL page notes that since 2026-10-01 ACN-designated persons are only on the SDN list, which is
  imported anyway.
- **SECO:** the whole-list file keeps de-listed targets and their history (newest modification first). A target
  whose first modification is `de-listed` is left out (about 790). Names come as parts with per-language spelling
  variants (Latin and Cyrillic); each variant becomes another full name. Addresses point at `place` elements at the
  end of the file, so this parser holds its ~7k subjects until the end.
- **Canada:** no id of its own (country / schedule / item instead), no addresses. Aliases are one comma-separated
  string with language labels (`Belarusian: …, Russian: …`). 736 ship records are left out.
- **SFMS:** mostly UN listings, with Ukrainian programme names. `type-entry` 1 = entity, 2 = person.
- **Australia:** `dfat.gov.au` resets the connection for an honestly identified non-browser client (the probe's
  user agent names the app). We don't disguise the client. The file's URL and format are unverified.
- **NSDC:** the API (`https://api-drs.nsdc.gov.ua/`, OpenAPI at `/swagger-json`, version 1.8.5) needs an API key
  (`x-cota-public-api-key` header or `cotaApiKey` query) for every data endpoint. `GET /v2/subjects?subjectType=…`
  returns the full list. Registration is at `https://drs.nsdc.gov.ua/`, which refuses automated fetches.

- **France (`fr-tresor`, 2026-11-16):** `GET https://gels-avoirs.dgtresor.gouv.fr/ApiPublic/api/v1/publication/derniere-publication-fichier-json`
  (an XML twin, a JSON schema and a Swagger page are listed on [data.gouv.fr](https://www.data.gouv.fr/datasets/registre-national-des-gels)).
  `{"Publications": {"DatePublication", "PublicationDetail": [...]}}`: each entry an `IdRegistre`, a `Nature`
  (`Personne physique`, `Personne morale`, `Navire`: the 39 vessels are left out), a `Nom`, and typed fields
  (`RegistreDetail`: `PRENOM`, `ALIAS` with a comment, `alias faible` for a weak one, `ADRESSE_PM` and `ADRESSE_PP`
  with the country in French, `FONDEMENT_JURIDIQUE`, `MOTIFS`, `NATIONALITE`, `DATE_DE_NAISSANCE`, identifiers).
  Countries are French names (`RUSSIE`, `ÉMIRATS ARABES UNIS`), which countries.json reads. It implements the EU's and
  the UN's listings as well as France's own, so most of it is on the EU list too; it's answered separately like
  every list. Each entry's page, `https://gels-avoirs.dgtresor.gouv.fr/Gels/RegistreDetail?idRegistre=<id>`, is its
  official record. Users have reported the API down at times (data.gouv.fr discussions): the download retries apply.
- **Netherlands (`nl-terrorism`, 2026-11-16):** the Ministry of Foreign Affairs' `eng-terrorismelijst.ods`, linked from
  [the national terrorism list page](https://www.government.nl/documents/2016/01/15/national-terrorism-list): one sheet,
  a title row, then `Surname`, `First name(s)`, `Alias`, `Date of Birth`, `Place of Birth`, `Date of ministerial
  decision` and `Link official notification` (`Stcrt 2015, 26627`, the Government Gazette notice). Columns are found by
  their header. Organisations have no first name and a place `n/a: established in …`. There is no id column: the
  notification is the id (one per listing). No addresses; the place of birth is kept in the remarks. The notices'
  pages (zoek.officielebekendmakingen.nl) answer an automated client with an error, so no record is linked.
  The file is read by `Spreadsheets.OdsRows` (zip and XML only).
- **New Zealand (`nz-mfat`, 2026-11-16):** MFAT's `Russia-Sanctions-Register.xlsx`, linked from
  [the register page](https://www.mfat.govt.nz/en/countries-and-regions/europe/ukraine/russian-invasion-of-ukraine/sanctions/russia-sanctions-register).
  Five sheets; only `Russia Sanctions Register` is read (the others: ships, trade measures padded to Excel's last row,
  exemptions). A legend, then the header on row 11: `Type`, `Unique Identifier` (`IND-1`, `ENT-1`, `BAN-1`, `AST-1`),
  names, `DOB`, `Title`, `Alias/Alternate Spellings` (semicolons), `Address`, citizenships, `Sanction Status`, dates as
  Excel serials, the measures (`Yes`/`No`), `General Rationale for Sanction`. `Individual` rows are people; `Entity`
  (512) and `Bank` (17) rows are entities, named in `First name`; the 7 `Asset` rows (categories of ships and aircraft)
  and the closing `Total` row are left out, as would be a row marked revoked, delisted or removed (none today). No
  per-record page. Read by `Spreadsheets.XlsxRows` (zip and XML only).
- **Japan (`jp-mof`, 2026-11-16):** the Ministry of Finance's `資産凍結等対象者一覧` (list of parties subject to asset
  freezes under the Foreign Exchange and Foreign Trade Act), published under a new dated name at each update
  (`shisantouketsuYYYYMMDD.csv`, also as `.xlsx`) and linked from
  [the list page](https://www.mof.go.jp/policy/international_policy/gaitame_kawase/gaitame/economic_sanctions/list.html).
  The catalogue's URL is that page and its `LinkPattern` (`/shisantouketsu[0-9]{8}\.csv$`) finds the file: the
  downloader fetches the page, takes the newest matching link on the page's own host, then downloads it; a page
  without one fails the import. CSV in UTF-8 with a byte order mark: 32 Japanese column names, every text in Japanese
  and English. `個人` (2,028) are people, `団体` (869) groups. The English name is primary (a few hold several, split
  at semicolons); the Japanese name, aliases (`別名・別称`) and former names (`旧称`) are aliases; `確定に十分でない別名`
  (aliases not enough to identify, 345) are weak. Lists in a cell are split at `;` or `；`. Addresses: the city and
  the country columns, paired when they have as many parts, in English when given, else in Japanese (384 rows);
  "(リスト掲載時点)" (at the time of listing) is dropped. Listed on is the notice date (`告示日付`, `2002.3.1`, amendments
  after it). The programme is the category number (`区分`, 1 to 44), which only the list page names. No per-record
  page. The 6,648 names in other scripts are the Japanese ones, mostly katakana, which no table transliterates yet.
- **US UFLPA Entity List (`us-uflpa`, 2026-11-16):** entities whose goods are presumed made with forced labour and
  barred from US import under the Uyghur Forced Labor Prevention Act, kept by DHS's Forced Labor Enforcement Task
  Force. Not in the Consolidated Screening List (its `source` values, checked 2026-11-16, have no UFLPA records), and
  published only as [the list page](https://www.dhs.gov/uflpa-entity-list) itself: three HTML tables, one per section
  of the Act (`Section 2(d)(2)(B)(i)`, `(ii)`, `(v)`, written before each), of a name and an effective date (`June 21,
  2022`), 210 rows. `HtmlTables` reads them with regular expressions (no HTML library). Aliases are written in the
  name: `(also known as A; B; and C)`, `(formerly known as …)`, `(including three aliases: …)`, `(and two aliases:
  …)`, or an acronym alone (`(XPCC)`); other parentheses are part of the name (`(Shanshan)`, `(Group)`). A trailing
  "and subsidiaries" or "and its subordinate and affiliated entities" goes to the remarks. 15 entities are listed
  under two sections, and two more differ only by a final full stop: one record each, 192 in all, with every section
  and the earliest date. No identifiers and no per-record page: the id is the name. A page whose tables lose their
  header, or a row that isn't a name and a date, fails the import.

## Scripts of the listed names

Measured on 2026-11-16 with `probe --source all --scripts` (#90): every name of every fetched list (primary names and
aliases, so a party often counts several times), by the Unicode scripts of its letters. A name in two scripts counts
under the one that isn't Latin; kana with or without kanji is "Japanese kana". `us-uflpa` (#99) is all Latin.

| List | Names | Latin | Cyrillic | Arabic | Han | Japanese kana | Hangul | Greek | Hebrew | Georgian, Armenian | Thai, Lao, Khmer, Myanmar, Ethiopic | Other |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `eu` | 30,804 | 24,366 | 5,177 | 773 | 32 |  | 87 | 351 | 16 |  |  | 2 |
| `ofac-sdn` | 47,832 | 42,028 | 3,663 | 1,498 | 575 |  | 50 | 9 | 2 | 4 |  | 3 |
| `ofac-cons` | 1,794 | 1,676 | 43 |  | 75 |  |  |  |  |  |  |  |
| `un` | 4,126 | 3,719 | 16 | 386 | 5 |  |  |  |  |  |  |  |
| `uk` | 18,890 | 15,146 | 2,848 | 744 | 66 | 1 | 6 | 3 | 29 | 22 | 23 | 2 |
| `us-export` | 11,576 | 11,576 |  |  |  |  |  |  |  |  |  |  |
| `ch-seco` | 20,901 | 14,305 | 5,643 | 849 | 93 |  | 6 |  |  | 1 | 4 |  |
| `ca-sema` | 9,288 | 6,036 | 2,988 | 233 | 13 |  |  |  | 17 | 1 |  |  |
| `fr-tresor` | 20,311 | 15,116 | 4,758 | 343 | 36 | 7 | 34 |  | 16 | 1 |  |  |
| `nl-terrorism` | 120 | 120 |  |  |  |  |  |  |  |  |  |  |
| `nz-mfat` | 2,896 | 2,896 |  |  |  |  |  |  |  |  |  |  |
| `jp-mof` | 15,034 | 8,395 |  | 1 | 227 | 6,411 |  |  |  |  |  |  |
| `ua-sfms` | 3,897 | 3,796 | 101 |  |  |  |  |  |  |  |  |  |
| **All** | **187,469** | **149,175** | **25,237** | **4,827** | **1,122** | **6,419** | **183** | **363** | **80** | **29** | **27** | **7** |

Names with Cyrillic letters, by the letters only some alphabets have (`Scripts.CyrillicAlphabet`; "Russian or
unmarked" uses none of them, so it holds Bulgarian too):

| List | Russian or unmarked | і (Ukrainian, Belarusian or Kazakh) | ў (Belarusian) | ґ є ї (Ukrainian) | ћ ђ (Serbian) | ј љ њ џ (Serbian or Macedonian) | ө ү (Kyrgyz, Mongolian or Kazakh) | ә ғ қ ң ұ һ (Kazakh) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `eu` | 4,633 | 294 | 179 | 69 | 1 | 1 |  |  |
| `ofac-sdn` | 3,452 | 132 | 62 | 11 | 5 | 1 |  |  |
| `ofac-cons` | 43 |  |  |  |  |  |  |  |
| `un` | 16 |  |  |  |  |  |  |  |
| `uk` | 2,688 | 82 | 55 | 17 | 3 |  | 2 | 1 |
| `ch-seco` | 5,243 | 150 | 182 | 66 | 1 | 1 |  |  |
| `ca-sema` | 2,856 | 63 | 32 | 35 |  | 1 |  | 1 |
| `fr-tresor` | 4,489 | 108 | 112 | 48 |  | 1 |  |  |
| `ua-sfms` | 68 | 23 |  | 10 |  |  |  |  |
| **All** | **23,488** | **852** | **622** | **256** | **10** | **5** | **2** | **2** |

What it says:

- **Latin is 80% of the names**, and most input will be Latin whatever the listed script: Latin details (#94) reach
  the most screenings.
- **Cyrillic is 13%**, read today through the Russian and Ukrainian tables. Belarusian (ў) is the next alphabet that
  matters: 622 names with ў and part of the 852 with і (#91). Kazakh, Kyrgyz, Serbian and Macedonian letters appear in
  fewer than 20 names together: not worth a table yet. The EU's "Russian or unmarked" includes its own Bulgarian
  translations of organisation names (`Египетски Al-Jihad`).
- **Arabic script is 2.6%** (4,827 names, OFAC SDN, EU, UK, SECO, UN): the largest group with no table (#92).
- **Japanese kana: 6,411 of the 6,419 names are `jp-mof`'s Japanese renderings of names the same record also gives
  in English**, so that list needs no table for screening. The other 8 (UK 1, France 7) are those lists' own aliases,
  each screened on its own: whether those parties also have a Latin name there is a separate check.
- **Han is 1,122 names, half of them OFAC SDN's** (#93); Hangul 183, Greek 363 (almost all the EU's Greek translations
  of organisation names, written with the micro sign for μ, which the measurement reads as Greek), Hebrew 80; the rest together under 70.
- About 3,500 names (3,474) mix Latin with another script: mostly the EU's and SECO's translations around a Latin name
  (`Οργάνωση Abu Nidal`) and Cyrillic company names with a Latin word.

Since #92 the Rust port, which production runs, compares Arabic-script names in their own script (`arabic.json`:
vowel marks and tatweel dropped; alef forms, hamza seats, ya and alef maqsura, ta marbuta, Persian kaf and ya unified;
Eastern digits read as ASCII), reads Arabic name particles in Latin script alike (Abd al-Rahman, Abdulrahman, Abdel
Rahman; al-Sayed, Elsayed; Nasr Allah, Nasrallah), and compares Chinese and Korean names in their own script
(`cjk.json`, #93: adjacent Han or Hangul words joined, company forms such as 股份有限公司 and 주식회사 split off,
traditional characters read as simplified with OpenCC's character table, Apache License 2.0). How such a name behaves
in the .NET app is recorded in
`tests/SanctionAndForgive.Tests/Matching/ScriptTests.cs`:
the same spelling matches in every script; Arabic vowel marks, tatweel and letter variants, Greek accents, a part of a
Chinese or Korean name, a space inside one, traditional characters for simplified ones, and a Latin spelling of a name
listed only in another script don't.

## Terms of reuse

Read on 2026-11-06. Not legal advice. The demo shows matches to a company, which is commercial use. It doesn't
resell the lists.

| Source | Commercial reuse | Attribution | Basis |
|---|---|---|---|
| EU | Yes: CC BY 4.0, the Commission's default for its content. | Credit, and indicate changes. | [Commission legal notice](https://commission.europa.eu/legal-notice_en) (Decision 2011/833/EU). The FSF page itself couldn't be fetched. |
| OFAC | Yes: a US federal government work, with no licence statement. | None required. Link each match to OFAC's record. | [Sanctions List Service](https://sanctionslist.ofac.treas.gov/Home/static/index.html) |
| UK | Yes: Open Government Licence v3.0. | "Contains public sector information licensed under the Open Government Licence v3.0." Don't imply endorsement. The OGL doesn't cover personal data in the information. | [OGL v3](https://www.nationalarchives.gov.uk/doc/open-government-licence/version/3/) |
| US export (trade.gov) | Yes. | Display: "This product uses the International Trade Administration's Data API but is not endorsed or certified by the International Trade Administration." Point users to the official source for any match. | [developer.trade.gov terms](https://developer.trade.gov/terms-of-service) |
| **UN** | **Unclear.** The UN's general site terms allow "personal, non-commercial use, without any right to … redistribute … or create derivative works". The list page itself couldn't be read. | | [UN terms of use](https://www.un.org/en/about-us/terms-of-use) |
| **SECO** | **Unclear.** The federal administration's terms: "Any reproduction requires the prior written consent of the copyright holder." | | [admin.ch terms](https://www.admin.ch/gov/en/start/terms-and-conditions.html). Contact: sanctions@seco.admin.ch |
| **Canada** | **Unclear.** "You may not reproduce materials on this site … for the purposes of commercial redistribution without prior written permission." Non-commercial reproduction is allowed. | | [canada.ca terms](https://www.canada.ca/en/transparency/terms.html) |
| Australia | Unknown: the site couldn't be reached. DFAT content is often CC BY 4.0. | | |
| NSDC | Unknown: the site refuses automated fetches. | | |
| SFMS | Unknown: the site refuses automated fetches. The file downloads. | | |
| France | Yes: Licence Ouverte 2.0 (Etalab), the dataset's licence on data.gouv.fr (read 2026-11-16). | Name the source and its date. The website's footer: "France: Registre national des gels, Direction générale du Trésor, reused under the Licence Ouverte 2.0." | [Licence Ouverte 2.0](https://www.etalab.gouv.fr/licence-ouverte-open-licence/) |
| Netherlands | Yes: government.nl declares CC0 1.0 for its content (read 2026-11-16). | None required. | [government.nl copyright](https://www.government.nl/copyright) |
| New Zealand | Yes: MFAT's site content is Crown copyright, licensed under CC BY 4.0 (read 2026-11-16). | Credit the Ministry. The website's footer: "New Zealand: Russia Sanctions Register, Ministry of Foreign Affairs and Trade, licensed under CC BY 4.0." | [MFAT copyright](https://www.mfat.govt.nz/en/copyright) |
| Japan | Yes: the Ministry of Finance's content is under the Public Data License 1.0 (PDL1.0) unless marked otherwise (its copyright page, read 2026-11-16). | Name the source; say if it was edited. The website's footer: "Japan: 資産凍結等対象者一覧 (list of parties subject to asset freezes), Ministry of Finance (財務省), used under the Public Data License 1.0; names and addresses are read for matching, not changed." | [MOF copyright and links](https://www.mof.go.jp/about_mof/notice/index.html), [PDL 1.0](https://www.digital.go.jp/resources/open_data/public_data_license_v1.0) |
| US UFLPA | Yes: a work of the US federal government, not subject to copyright in the US (17 U.S.C. 105). | None required. | [USA.gov: government works](https://www.usa.gov/government-copyright) |

The UN's listings are also on the EU and UK lists, which implement them. Leaving out the UN row loses its own as-of
date in the result, not coverage.

## Sizing

From the probe's character counts (names 3.94 M characters, addresses 2.32 M, other fields 11.7 M, mostly remarks),
assuming NSDC adds about 30k subjects and 60k names.

**Index memory, against 0.5 GiB.** About 80k subjects, 210k names and 75k addresses. Per name: its text (about 80 B as
a .NET string), token ids, and transliterated forms interned in a shared dictionary: about 300 B in all, so about
65 MB. Addresses: about 20 MB. Subjects (ids, primary name, programme, source URL; remarks stay in SQL): about 15 MB.
Token dictionary, fold keys and trigram postings: about 30 MB. **About 130 MB for every list**, plus about 40 MB
while the largest list (OFAC SDN) is rebuilt next to its old copy, plus ASP.NET Core's own 60-80 MB: **about 250 MB of
512 MiB.** Phase 3 measures it.

**Database, against 2 GB.** Measured in Phase 2 (local SQL Server 2022, the 9 fetched feeds plus a manual source,
**two imports each**): **114.5 MB used** (subjects 54.8 MB, names 26.5 MB, addresses 12.4 MB, indexes included).
Briefly a third import of one list exists during an import, before the oldest is deleted. NSDC will add perhaps
half as much again. **Well inside 2 GB**; the storage alert fires at 80%.
