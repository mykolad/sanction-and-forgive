# Demo walkthrough

About ten minutes, in this order, against the production site. Every input below was tried there on 2026-11-17 and
gave the answer shown; percentages can move by a point or two when the lists are updated. Steps 6 and 7 need an
administrator's sign-in (the owner's Entra account). The questions in italics are the ones each step usually raises.

Before you start: open the site, then **List status**, and check every list in the first table (the lists searched)
says ✓ *Searched* (one that says ! *Searched, out of date* is fine for the demo: step 8 shows what it means). The
second table, the lists not imported, is expected.

Press **Clear** before each example and leave the fields it doesn't name empty: the form keeps what was entered
after a screening (to change one field and screen again), so a company left from the step before still counts.

## 1. A listed company, exact

**Screen**, Company: `PJSC Rosneft Oil Company` → **Possible match found**: Match 99% on OFAC SDN, OFAC
consolidated, UK and New Zealand. Select the UK cell: the listed name, the alias matched, the programme, the official
record, and *How it aligned* word by word.

*Why "possible match" and not "this is them"?* The service matches names, nothing else: a person confirms it against
the official record, which the cell links to.

## 2. A typo and another legal form

Company: `Rosnieft Oil Company LLC` → **Review needed**: 75% on the same lists. The detail says which word aligned
approximately (`rosnieft ~ rosneft`) and that the legal form wasn't compared.

Then `Sovkomflot JSC` → **Match** 90-95% (EU, France, New Zealand, OFAC): `Sovkomflot` is `Sovcomflot` with one letter
changed, and *JSC* is a legal form like *PAO*, *АО*, *Ltd*.

*Why is the typo only 75% but Sovkomflot 95%?* An extra letter in the name's distinctive word is a typo, capped at a
possible match; c and k are read alike, since the romanisations of Совкомфлот write both, so Sovkomflot is a
spelling of Sovcomflot, not a typo of it.

## 3. Cyrillic, in Russian and Ukrainian

Company: `ПАО «Совкомфлот»` → **Match** 99% (EU, France). Then the Ukrainian `ПАТ «Совкомфлот»` → the same.
*ПАО* and *ПАТ* are the Russian and Ukrainian public joint-stock company; the name is read through both
transliteration tables.

*Do the lists have it in Cyrillic?* Some do (EU, UK); the others are matched through the Latin reading. Either way
it's found.

## 4. A common surname with initials

Receiver: `Ivanov A.` → **Review needed**: 60% on several lists (Aleksandr Alexandrovich Ivanov, Andrey Ivanov, …).

*Why not a match?* An initial agrees with a given name, but a common surname and an initial aren't enough to say
it's the listed person. That's exactly what a person must review, with a date of birth or an address.

Also: `Kim Chong Un` → **Match** 97% for *Jong Un KIM* (OFAC). Korean names are written in several romanisations
(Chong/Jong/Jeong); `Kim Jongun`, written as one word, matches too.

## 5. An ordinary company

Company: `Northwind Logistics GmbH`, Address: `Hauptstrasse 5, Hamburg, Germany` → **No match found in: EU, as of …;
…** with every list and its date.

*So it's cleared?* No: that sentence says these lists, on these dates, have no party close enough. It doesn't
cover ownership by a listed party, embargoes, or export controls on the goods; the disclaimer says so under every
result.

**Print or save the record** (under the result): the record a compliance file keeps, with the reference, the time,
what was entered, every list as of when, the versions and the disclaimer. Save it as a PDF from the print dialog.

## 6. The allow-list (administrator)

Company: `Volga Trade LLC`, Address: `Samara, Russia` → **Review needed**: 75% on five lists, all for other
companies (*Volgai Hajózási Rt.*, *OOO Volga Group*, a *Volna* plant). A reviewer decides it's a known customer.

**Allow-list** → add `Volga Trade LLC` with a reason; the page shows the possible matches and asks to confirm each.
Screen it again: the cells say *Possible match, allowed*, and the outcome is **No match found**.

*Could an allow-list hide a real match?* No: a Match is never allowed, and a name that is a Match on any list can't
be added at all. Remove the entry afterwards (it's kept as removed, not deleted).

## 7. Switch a list off (administrator)

**Admin** → switch off the UK list, with a reason. Screen `PJSC Rosneft Oil Company` again: the summary says *Not
searched: UK (switched off by an administrator: …)* and the UK row has no cells. Switch it back on; within a few
seconds the UK row answers again.

*Why would anyone switch a list off?* A list that's known to be wrong after an update, until it's fixed: it's a
person's decision, logged with their name, never automatic.

## 8. The status page

**List status**: every list with its record count, when it was last checked and last changed, the publisher's page,
the file and the terms; then the lists not imported and why (terms not cleared, no automatic download). *History* on
a list shows each update with how many records were added, removed or changed. An update that removed more than 20%
of a list is held for review and the old one keeps serving.

*What if a list can't be updated?* It's still searched as last downloaded, marked *Searched, out of date* here (the
API's state `Overdue`) after 72 hours, and every result says so in its summary.

## 9. A batch

**Manifest**: choose a CSV file with a few shipments (a column per receiver, company and address), check the columns,
**Screen the rows**: a summary, the rows that need a look, and a results file to download, a column per list.

From the command line, the stream endpoint behind it (one NDJSON line per shipment, answers as it goes):

```powershell
@'
{"reference":"DEMO-1","companyName":"PJSC Rosneft Oil Company"}
{"reference":"DEMO-2","companyName":"Northwind Logistics GmbH","address":"Hamburg, Germany"}
'@ | curl.exe -s -X POST https://<the site>/api/v1/screenings/stream -H "Content-Type: application/x-ndjson" --data-binary '@-'
```

The integration samples (`samples/`) and what to do with each answer (`docs/decisions.md`) are the follow-up for a
buyer's developers.
