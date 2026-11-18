// Sanction & Forgive page: plain JS, no build step. It calls the same API as everyone else. Every value from the
// API or the user goes into the page as text (textContent), never as HTML.
"use strict";

const LABELS = { Match: "Match", Review: "Possible match", Allowed: "Possible match, allowed", NoMatch: "No match", NotChecked: "Not checked" };
const SYMBOLS = { Match: "●", Review: "▲", Allowed: "◇", NoMatch: "✓", NotChecked: "–" };
const OVERALL = { Match: "Possible match found", Review: "Review needed", NoMatch: "No match found", Incomplete: "Incomplete" };
const KIND = { Receiver: "Receiver", Company: "Company", Address: "Address" };
// An address's destination notice (the "destinations" feature).
const DESTINATION = { restricted: "Destination under broad restrictions", possiblyRestricted: "The destination may be under broad restrictions" };
// A shipment's other parties (the "parties" feature, when the API offers it): the API's roles, and how they read.
const ROLES = { shipper: "Shipper", consignee: "Consignee", notifyParty: "Notify party", forwarder: "Forwarder", carrier: "Carrier", endUser: "End user", other: "Other party" };
const MAX_PARTIES = 10;
// Identifiers and dates of birth (the "identifiers" feature, when the API offers it).
let identifiersOffered = false;

// The identifiers typed in one field: commas or semicolons between them.
function identifiersIn(text) {
  return text.split(/[,;]/).map((value) => value.trim()).filter(Boolean);
}

const $ = (id) => document.getElementById(id);
const form = $("screen-form");
const stateBox = $("state");
let lists = [];
let warmUpTimer = null;
// Each screening (and each Clear) takes a new number; only the latest request may change the page.
let current = 0;
let pending = null;
// The result on the page, with what was entered for it and when it came back: what a printed record shows.
let shown = null;

function el(tag, attributes = {}, ...children) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) {
    if (value === null || value === undefined || value === false) continue;
    if (name === "class") node.className = value;
    else if (name.startsWith("on")) node.addEventListener(name.slice(2), value);
    else node.setAttribute(name, value === true ? "" : value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

// A name or address from a list or the user inside a sentence, isolated so a right-to-left one (Arabic, Persian,
// Hebrew) neither reorders the words around it nor is reordered by them (#92). Where one stands alone, dir="auto".
function isolate(text) {
  return `\u2068${text}\u2069`;
}

function percentText(percent) {
  if (percent === null || percent === undefined) return "";
  return percent < 1 ? "<1%" : `${percent}%`;
}

function asOf(value) {
  if (!value) return "never checked";
  const date = new Date(value);
  return "as of " + date.toLocaleString(undefined, { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "UTC", timeZoneName: "short" });
}

function showState(text, isError) {
  stateBox.textContent = text || "";
  stateBox.classList.toggle("error", Boolean(isError));
}

// When a list was last checked; an Overdue list (searched, but not updated within its allowed age) says so.
function listAsOf(list) {
  if (list.state === "NotChecked") return "not checked";
  if (list.state === "SwitchedOff") return "switched off";
  return list.state === "Overdue" ? `⚠ out of date: ${asOf(list.asOf)}` : asOf(list.asOf);
}

async function loadStatus() {
  try {
    const response = await fetch("/api/v1/status", { headers: { Accept: "application/json" } });
    const status = await response.json();
    lists = status.lists;
    $("parties").hidden = !(status.features || []).includes("parties");
    identifiersOffered = (status.features || []).includes("identifiers");
    document.querySelectorAll(".particulars").forEach((field) => { field.hidden = !identifiersOffered; });
    $("version").textContent = `Version ${status.version}, matcher ${status.matcherVersion}.`;
    $("sources").textContent = lists.length
      ? lists.map((list) => `${list.name} (${listAsOf(list)})`).join("; ") + "."
      : "No lists loaded yet.";
    if (status.state !== "ready") {
      showState("The lists are loading. Screening starts as soon as they're in memory…");
      clearTimeout(warmUpTimer);
      warmUpTimer = setTimeout(loadStatus, 3000);
    } else if (!stateBox.classList.contains("error")) {
      showState("");
      loadExamples();
    }
  } catch {
    showState("The service isn't answering. Try again in a moment.", true);
  }
}

async function loadExamples() {
  const box = $("examples");
  if (box.childElementCount) return;
  try {
    const response = await fetch("/api/v1/examples");
    if (!response.ok) return;
    for (const example of await response.json()) {
      box.append(el("button", {
        type: "button",
        title: `From the ${example.list} list`,
        onclick: () => {
          $("receiverName").value = example.receiverName || "";
          $("companyName").value = example.companyName || "";
          $("address").value = example.address || "";
          // An example is a name alone: identifiers or a date of birth entered for another must not go with it.
          for (const id of ["receiverIdentifiers", "receiverDateOfBirth", "companyIdentifiers"]) $(id).value = "";
          form.requestSubmit();
        },
      }, example.label));
    }
  } catch {
    // Examples are a convenience; the form works without them.
  }
}

async function screen(event) {
  event.preventDefault();
  // A reference per screening, as an API caller would send its transaction id: shown with the result, and how the
  // screening is found in the logs. Random, so it says nothing about what was entered.
  const body = {
    reference: "web-" + Date.now().toString(36) + "-" + Math.random().toString(36).slice(2, 8),
    receiverName: $("receiverName").value.trim() || null,
    companyName: $("companyName").value.trim() || null,
    address: $("address").value.trim() || null,
  };
  if (identifiersOffered) {
    const receiverIds = identifiersIn($("receiverIdentifiers").value);
    const companyIds = identifiersIn($("companyIdentifiers").value);
    const dateOfBirth = $("receiverDateOfBirth").value.trim();
    if (receiverIds.length) body.receiverIdentifiers = receiverIds;
    if (companyIds.length) body.companyIdentifiers = companyIds;
    if (dateOfBirth) body.receiverDateOfBirth = dateOfBirth;
  }
  const parties = $("parties").hidden ? [] : partiesEntered();
  if (parties.some((party) => !party.name && !party.address)) {
    showState("Enter each party's name, its address, or both (or remove it).", true);
    return;
  }
  if (parties.length) body.parties = parties;
  if (!body.receiverName && !body.companyName && !body.address && !parties.length) {
    showState("Enter a receiver, a company or an address.", true);
    return;
  }

  const button = $("screen-button");
  const request = ++current;
  if (pending) pending.abort();
  pending = new AbortController();
  button.disabled = true;
  showState("Screening…");
  try {
    const response = await fetch("/api/v1/screenings", {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify(body),
      signal: pending.signal,
    });
    if (request !== current) return;
    if (response.status === 503) {
      const state = await response.json().catch(() => null);
      if (request !== current) return;
      showState(state && state.state === "busy"
        ? "Too many screenings at once right now. Try again in a moment."
        : "The lists are still loading. Try again in a few seconds.", true);
      return;
    }
    if (response.status === 429) {
      showState("Too many screenings from this address in a minute. Wait a moment and try again.", true);
      return;
    }
    if (!response.ok) {
      const problem = await response.json().catch(() => null);
      if (request !== current) return;
      const errors = problem && problem.errors ? Object.values(problem.errors).flat().join(" ") : "";
      showState(errors || "That didn't work. Check the fields and try again.", true);
      return;
    }
    const result = await response.json();
    if (request !== current) return;
    showState("");
    shown = {
      result,
      entered: {
        Receiver: body.receiverName, Company: body.companyName, Address: body.address,
        "Receiver's ID numbers": (body.receiverIdentifiers || []).join(", "), "Receiver's date of birth": body.receiverDateOfBirth,
        "Company's ID numbers": (body.companyIdentifiers || []).join(", "),
      },
      parties: body.parties || [],
      at: new Date(),
    };
    render(result);
    buildRecord();
  } catch {
    if (request === current) showState("The service isn't answering. Try again in a moment.", true);
  } finally {
    if (request === current) {
      button.disabled = false;
      pending = null;
    }
  }
}

function render(result) {
  $("result").hidden = false;
  $("detail").hidden = true;

  const summary = $("summary");
  summary.className = `summary ${result.outcome}`;
  summary.replaceChildren(el("span", { class: "badge" }, OVERALL[result.outcome] || result.outcome), el("span", {}, result.summary,
    el("span", { class: "reference" }, `Reference ${result.reference}`)));

  // One table per party (the shipment's own entries first, untitled when there are no parties); the outcome above is
  // the shipment's.
  const groups = [];
  for (const entry of result.entries) {
    const key = entry.party || 0;
    let group = groups.find((candidate) => candidate.key === key);
    if (!group) groups.push(group = { key, role: entry.role, entries: [] });
    group.entries.push(entry);
  }
  groups.sort((a, b) => a.key - b.key);
  $("result-tables").replaceChildren(...groups.map((group) => el("div", { class: "table-scroll" },
    resultTable(result, group.entries, groups.length > 1
      ? group.key ? `Party ${group.key}: ${ROLES[group.role] || group.role}` : "The shipment's receiver, company and address"
      : null))));

  const notices = destinationNotices(result);
  $("destination-notices").hidden = !notices.length;
  $("destination-notices").replaceChildren(...notices.map(({ where, destination }) => el("p", { class: `destination ${destination.state}` },
    el("strong", {}, `⚠ ${DESTINATION[destination.state] || destination.state}`), ` ${where}: ${destination.name}. `,
    destinationText(destination))));

  $("disclaimer").textContent = result.disclaimer;
  $("result").scrollIntoView({ behavior: "smooth", block: "start" });
}

// A table of lists (rows) × entries (columns), with a caption when it's one party's of several.
function resultTable(result, entries, caption) {
  const body = el("tbody");
  for (const list of result.lists) {
    const row = el("tr", {}, el("td", { class: "list-name" }, list.name,
      el("span", { class: list.state === "Overdue" ? "as-of overdue" : "as-of" }, listAsOf(list))));
    if (list.state === "SwitchedOff") {
      // Not searched at all, so no cells: one note across the row (the summary gives the administrator's reason).
      row.append(el("td", { colspan: String(entries.length), class: "switched-off" }, "Not searched: switched off by an administrator"));
      body.append(row);
      continue;
    }
    for (const entry of entries) {
      const cell = entry.cells.find((candidate) => candidate.list === list.code);
      row.append(el("td", {}, cellButton(cell, entry, list)));
    }
    body.append(row);
  }
  // Within a party's table the party is in the caption: the columns say only the kind.
  const title = (entry) => caption && entry.party ? (entry.kind === "Receiver" ? "Person" : KIND[entry.kind] || entry.kind) : entryTitle(entry);
  return el("table", { class: "result-table" },
    caption ? el("caption", { class: "party-caption" }, caption) : null,
    el("thead", {}, el("tr", {}, el("th", { scope: "col" }, "List"),
      entries.map((entry) => el("th", { scope: "col" }, title(entry), el("span", { class: "entry-input", dir: "auto" }, entry.input))))),
    body);
}

// Each address entered in a destination under broad restrictions (#67), with which address it is.
function destinationNotices(result) {
  return result.entries.filter((entry) => entry.destination)
    .map((entry) => ({ where: entry.party ? entryTitle(entry) : "Address", destination: entry.destination }));
}

// What a notice says beyond its title: who restricts it, under what, as of when, and what to do.
function destinationText(destination) {
  const rest = destination.state === "restricted"
    ? "Check the rules for the goods."
    : "The address doesn't tell which side of the line it is: a person must check where it is.";
  return `Restricted by ${destination.restrictedBy}, as of ${destination.asOf} (${destination.legalSource.replace(/\.$/, "")}). ${rest}`;
}

// An entry's column title: its kind, and for a party its number and role ("Party 2, consignee: Company"); a party's
// name entered as a person's reads "Person" (the API's kind is Receiver).
function entryTitle(entry) {
  if (!entry.party) return KIND[entry.kind] || entry.kind;
  const kind = entry.kind === "Receiver" ? "Person" : KIND[entry.kind] || entry.kind;
  return `Party ${entry.party}, ${(ROLES[entry.role] || entry.role).toLowerCase()}: ${kind}`;
}

// The parties as entered, every row in order: the numbers in the result are the rows' numbers.
function partiesEntered() {
  return [...$("party-rows").children].map((row) => {
    const party = {
      role: row.querySelector(".party-role").value,
      kind: row.querySelector(".party-kind").value,
      name: row.querySelector(".party-name").value.trim() || null,
      address: row.querySelector(".party-address").value.trim() || null,
    };
    const ids = row.querySelector(".party-ids");
    if (ids && identifiersIn(ids.value).length) party.identifiers = identifiersIn(ids.value);
    const dateOfBirth = row.querySelector(".party-dob");
    // Sent whatever the kind: the API refuses a company's date of birth rather than the page dropping it unseen.
    if (dateOfBirth && dateOfBirth.value.trim()) party.dateOfBirth = dateOfBirth.value.trim();
    return party;
  });
}

function addParty() {
  const rows = $("party-rows");
  if (rows.children.length >= MAX_PARTIES) return;
  const number = rows.children.length + 1;
  const row = el("div", { class: "party", role: "group" },
    el("label", { class: "field" }, el("span", { class: "field-label" }, "Role"),
      el("select", { class: "party-role" }, Object.entries(ROLES).map(([value, label]) => el("option", { value }, label)))),
    el("label", { class: "field" }, el("span", { class: "field-label" }, "Name"),
      el("input", { class: "party-name", maxlength: "300", autocomplete: "off", spellcheck: "false" })),
    el("label", { class: "field" }, el("span", { class: "field-label" }, "Kind"),
      el("select", { class: "party-kind" }, el("option", { value: "company" }, "Company"), el("option", { value: "person" }, "Person"))),
    identifiersOffered ? el("label", { class: "field" }, el("span", { class: "field-label" }, "ID numbers", el("span", { class: "hint" }, "commas between")),
      el("input", { class: "party-ids", maxlength: "400", autocomplete: "off", spellcheck: "false" })) : null,
    identifiersOffered ? el("label", { class: "field" }, el("span", { class: "field-label" }, "Date of birth", el("span", { class: "hint" }, "a person's")),
      el("input", { class: "party-dob", maxlength: "10", autocomplete: "off", spellcheck: "false", placeholder: "YYYY-MM-DD" })) : null,
    el("label", { class: "field field-wide" }, el("span", { class: "field-label" }, "Address", el("span", { class: "hint" }, "any format")),
      el("textarea", { class: "party-address", maxlength: "300", rows: "1", spellcheck: "false" })),
    el("button", { type: "button", class: "secondary party-remove", onclick: () => { row.remove(); partiesChanged(); } }, "Remove"));
  // Roles in the usual order of a booking: the first party a shipper, the second a consignee, then notify party.
  row.querySelector(".party-role").value = Object.keys(ROLES)[Math.min(number - 1, 2)];
  rows.append(row);
  partiesChanged();
  row.querySelector(".party-name").focus();
}

function partiesChanged() {
  const rows = $("party-rows").children;
  [...rows].forEach((row, i) => {
    row.setAttribute("aria-label", `Party ${i + 1}`);
    row.querySelector(".party-remove").setAttribute("aria-label", `Remove party ${i + 1}`);
  });
  $("add-party").disabled = rows.length >= MAX_PARTIES;
}

function cellButton(cell, entry, list) {
  // An allowed possible match (the entry is on the allow-list) keeps its percentage but isn't counted.
  const state = cell.state === "NotChecked" ? "NotChecked" : cell.allowed ? "Allowed" : cell.outcome;
  const who = cell.bestCandidate ? isolate(cell.bestCandidate.listedName) : null;
  const label = cell.state === "NotChecked"
    ? `${list.name}: not checked`
    : `${list.name}, ${entryTitle(entry)}: ${percentText(cell.percent)} match likelihood, ${LABELS[state]}${who ? ", " + who : ""}`;
  return el("button", {
    type: "button",
    class: `cell ${state}`,
    "aria-label": label,
    "aria-pressed": "false",
    disabled: !cell.bestCandidate,
    onclick: (event) => showDetail(event.currentTarget, cell, entry, list),
  },
  el("span", { class: "percent" }, cell.state === "NotChecked" ? SYMBOLS.NotChecked : percentText(cell.percent)),
  el("span", {},
    el("span", { class: "label" }, `${SYMBOLS[state]} ${LABELS[state]}`),
    who ? el("span", { class: "who" }, who) : null));
}

function showDetail(button, cell, entry, list) {
  document.querySelectorAll(".cell[aria-pressed='true']").forEach((other) => other.setAttribute("aria-pressed", "false"));
  button.setAttribute("aria-pressed", "true");
  const candidate = cell.bestCandidate;
  const detail = $("detail");
  // replaceChildren would show a null as the text "null": only the parts there are.
  detail.replaceChildren(...[
    el("h3", { dir: "auto" }, candidate.listedName),
    el("p", { class: "meta" }, `${list.name} · ${entryTitle(entry)} “${entry.input}” · ${percentText(candidate.percent)} match likelihood (${LABELS[candidate.outcome]})`),
    cell.allowed && entry.allowedBy ? el("p", { class: "allowed-note" }, el("strong", {}, "Allowed by the allow-list: "),
      `“${entry.allowedBy.name}”: ${entry.allowedBy.reason}. This possible match doesn't raise the outcome; a person can still review it.`) : null,
    el("dl", {},
      el("dt", {}, "Name matched"), el("dd", { dir: "auto" }, candidate.alias),
      el("dt", {}, "Type"), el("dd", {}, candidate.type === "Unknown" ? "Not stated by the list" : candidate.type),
      el("dt", {}, "Programme"), el("dd", {}, candidate.programme || "—"),
      el("dt", {}, "Listed addresses"), el("dd", {}, candidate.listedAddresses.length ? el("ul", {}, candidate.listedAddresses.map((address) => el("li", { dir: "auto" }, address))) : "None listed"),
      candidate.listedIdentifiers ? el("dt", {}, "Listed identifiers") : null,
      candidate.listedIdentifiers ? el("dd", {}, el("ul", {}, candidate.listedIdentifiers.map((id) => el("li", {}, identifierText(id))))) : null,
      candidate.listedBirthDates ? el("dt", {}, "Listed dates of birth") : null,
      candidate.listedBirthDates ? el("dd", {}, candidate.listedBirthDates.join(", ")) : null,
      el("dt", {}, "Address"), el("dd", {}, addressText(candidate.addressState, entry)),
      el("dt", {}, "Compared"), el("dd", {}, entry.compared),
      el("dt", {}, "Official record"), el("dd", {}, candidate.sourceUrl ? el("a", { href: candidate.sourceUrl, target: "_blank", rel: "noopener noreferrer" }, "Open the list's own record") : `${list.name}, id ${candidate.externalId}`)),
    el("p", {}, el("strong", {}, "How it aligned")),
    el("ul", {}, candidate.alignment.map((line) => el("li", {}, line))),
    cell.otherHits.length ? el("div", { class: "others" },
      el("p", {}, el("strong", {}, `Other possible matches on ${list.name}`)),
      el("ul", {}, cell.otherHits.map((hit) => el("li", {}, el("bdi", {}, hit.listedName), ` — ${percentText(hit.percent)} (${LABELS[hit.outcome]})`)))) : null,
  ].filter(Boolean));
  detail.hidden = false;
}

// A listed identifier as the detail panel and the record show it: kind, value, issuing country.
function identifierText(id) {
  return `${id.kind}: ${id.value}${id.country ? ` (${id.country})` : ""}`;
}

// ---- The printed record ----

// A record of the screening for a compliance file, built from the result on the page: what was entered (from the
// page: the server doesn't keep it), against which lists as of when, what came back and why, the versions that
// reproduce it, and the disclaimer. Built with each result and again before any print (the button, or the browser's
// own Print), and emptied with the result; the browser prints it or saves it as a PDF. Nothing is sent or stored.
function buildRecord() {
  const record = $("print-record");
  if (!shown) {
    record.replaceChildren();
    document.body.classList.remove("has-record");
    return;
  }
  const { result, entered, at } = shown;
  const when = (date) => date.toLocaleString(undefined, { day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit", second: "2-digit", timeZoneName: "short" });
  const cellText = (cell) => {
    if (!cell) return "";
    if (cell.state === "NotChecked") return `${SYMBOLS.NotChecked} ${LABELS.NotChecked}`;
    const state = cell.allowed ? "Allowed" : cell.outcome;
    const who = cell.bestCandidate && cell.outcome !== "NoMatch" ? `: ${isolate(cell.bestCandidate.listedName)}` : "";
    return `${SYMBOLS[state]} ${LABELS[state]} ${percentText(cell.percent)}${who}`;
  };
  const listByCode = new Map(result.lists.map((list) => [list.code, list]));
  const hits = result.entries.flatMap((entry) => entry.cells
    .filter((cell) => cell.bestCandidate && cell.state !== "NotChecked" && (cell.outcome === "Match" || cell.outcome === "Review"))
    .map((cell) => ({ entry, cell, list: listByCode.get(cell.list) })));

  // One candidate in full: the best of a cell, or another possible match on the same list.
  const candidateBlock = (entry, cell, list, candidate, percent, outcome) => el("div", { class: "print-hit" },
    el("h3", { dir: "auto" }, candidate.listedName),
    el("p", {}, `${list ? list.name : cell.list} · ${entryTitle(entry)} · ${percentText(percent)} match likelihood (${LABELS[outcome]})`),
    cell.allowed && entry.allowedBy ? el("p", {}, `Allowed by the allow-list: “${entry.allowedBy.name}”: ${entry.allowedBy.reason}.`) : null,
    el("dl", { class: "print-meta" },
      el("dt", {}, "Name matched"), el("dd", { dir: "auto" }, candidate.alias),
      el("dt", {}, "Programme"), el("dd", {}, candidate.programme || "—"),
      el("dt", {}, "Listed addresses"), el("dd", {}, candidate.listedAddresses && candidate.listedAddresses.length ? candidate.listedAddresses.map(isolate).join("; ") : "None listed"),
      candidate.listedIdentifiers ? el("dt", {}, "Listed identifiers") : null,
      candidate.listedIdentifiers ? el("dd", {}, candidate.listedIdentifiers.map(identifierText).join("; ")) : null,
      candidate.listedBirthDates ? el("dt", {}, "Listed dates of birth") : null,
      candidate.listedBirthDates ? el("dd", {}, candidate.listedBirthDates.join(", ")) : null,
      el("dt", {}, "Address"), el("dd", {}, addressText(candidate.addressState, entry)),
      el("dt", {}, "Official record"), el("dd", {}, candidate.sourceUrl || `${list ? list.name : cell.list}, id ${candidate.externalId}`)),
    el("ul", {}, (candidate.alignment || []).map((line) => el("li", {}, line))));

  record.replaceChildren(...[
    el("h1", {}, "Sanctions list screening record"),
    el("p", { class: "print-sub" }, "Sanction & Forgive: a demo list check, not a legal determination"),
    el("dl", { class: "print-meta" },
      el("dt", {}, "Reference"), el("dd", {}, result.reference),
      el("dt", {}, "Screened"), el("dd", {}, `${when(at)} (${at.toISOString()})`),
      el("dt", {}, "Result"), el("dd", {}, el("strong", {}, OVERALL[result.outcome] || result.outcome), ". ", result.summary),
      ...["Receiver", "Company", "Address"].flatMap((kind) => [el("dt", {}, `${kind} entered`), el("dd", {}, entered[kind] || "—")]),
      ...["Receiver's ID numbers", "Receiver's date of birth", "Company's ID numbers"].filter((label) => entered[label])
        .flatMap((label) => [el("dt", {}, label), el("dd", {}, entered[label])]),
      // Each party as entered, numbered as the result numbers them.
      ...shown.parties.flatMap((party, i) => [
        el("dt", {}, `Party ${i + 1}, ${(ROLES[party.role] || party.role).toLowerCase()} (${party.kind === "person" ? "person" : "company"})`),
        el("dd", {}, [party.name, party.address, party.identifiers && `ID numbers ${party.identifiers.join(", ")}`, party.dateOfBirth && `born ${party.dateOfBirth}`].filter(Boolean).join(" · ") || "—")])),
    el("h2", {}, "Lists searched"),
    el("table", { class: "print-table" },
      el("thead", {}, el("tr", {}, el("th", {}, "List"), el("th", {}, "As of"), result.entries.map((entry) => el("th", {}, entryTitle(entry))))),
      el("tbody", {}, result.lists.map((list) => el("tr", {},
        el("td", {}, list.name),
        el("td", {}, listAsOf(list)),
        list.state === "SwitchedOff"
          ? el("td", { colspan: String(result.entries.length) }, "Not searched: switched off by an administrator")
          : result.entries.map((entry) => el("td", {}, cellText(entry.cells.find((cell) => cell.list === list.code)))))))),
    destinationNotices(result).length
      ? el("section", {}, el("h2", {}, "Destinations under broad restrictions"),
        destinationNotices(result).map(({ where, destination }) => el("p", {},
          el("strong", {}, DESTINATION[destination.state] || destination.state), `. ${where}: ${destination.name}. `, destinationText(destination))))
      : null,
    // Only what the lists found: whether anything was searched at all is the result's summary above, never inferred.
    hits.length
      ? el("section", {}, el("h2", {}, "Possible matches, and how the names aligned"),
        hits.flatMap(({ entry, cell, list }) => [
          candidateBlock(entry, cell, list, cell.bestCandidate, cell.percent, cell.allowed ? "Allowed" : cell.outcome),
          ...cell.otherHits.map((hit) => candidateBlock(entry, cell, list, hit, hit.percent, hit.outcome)),
        ]))
      : null,
    el("h2", {}, "Versions"),
    el("p", {}, `Matcher ${result.matcherVersion} · allow-list ${result.allowListVersion}`,
      result.destinationsVersion ? ` · destinations ${result.destinationsVersion}` : "", " · lists: ",
      result.lists.map((list) => `${list.code} ${list.version === null || list.version === undefined ? "–" : list.version} (${list.state})`).join(", ")),
    el("p", { class: "print-disclaimer" }, result.disclaimer),
    // The lists' licence notices (OGL, trade.gov's, the EU's…) travel with the data they cover: the page's own
    // attribution, copied (static markup of this page, never anything from the API or the user).
    el("section", {}, el("h2", {}, "Sources and licences"), attributionCopy()),
    el("p", { class: "print-sub" }, `Printed from ${location.origin}. Check any match against the official publication of the list it comes from.`)].filter(Boolean));
  document.body.classList.add("has-record");
}

function attributionCopy() {
  const copy = $("attribution").cloneNode(true);
  copy.removeAttribute("id");
  return copy;
}

function printRecord() {
  buildRecord();
  window.print();
}

// The browser's own Print (Ctrl+P) prints the record of the result on the page, or the page itself when there's none.
window.addEventListener("beforeprint", buildRecord);

function addressText(state, entry) {
  if (entry.kind === "Address") return "Compared on its own (address only)";
  return { Corroborated: "Agrees with a listed address", Conflicting: "In a different country from the listed addresses", NotCompared: "Not compared" }[state] || state;
}

form.addEventListener("submit", screen);
$("add-party").addEventListener("click", addParty);
$("print-button").addEventListener("click", printRecord);
$("clear-button").addEventListener("click", () => {
  current++;
  if (pending) pending.abort();
  pending = null;
  $("screen-button").disabled = false;
  form.reset();
  $("party-rows").replaceChildren();
  partiesChanged();
  shown = null;
  buildRecord();
  $("result").hidden = true;
  showState("");
  $("receiverName").focus();
});
loadStatus();
