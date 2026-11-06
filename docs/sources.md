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

**Database, against 2 GB.** Per full set of lists: subjects about 45 MB (remarks average about 200 characters), names
about 20 MB, addresses about 11 MB, indexes about half again: **about 115 MB**. Two imports per list: about 230 MB.
Briefly three during an import, before the oldest is deleted: about 350 MB. **Well inside 2 GB.** Phase 2 measures it.
