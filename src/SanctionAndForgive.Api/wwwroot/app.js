// Sanction & Forgive page: plain JS, no build step. It calls the same API as everyone else. Every value from the
// API or the user goes into the page as text (textContent), never as HTML.
"use strict";

const LABELS = { Match: "Match", Review: "Possible match", Allowed: "Possible match, allowed", NoMatch: "No match", NotChecked: "Not checked" };
const SYMBOLS = { Match: "●", Review: "▲", Allowed: "◇", NoMatch: "✓", NotChecked: "–" };
const OVERALL = { Match: "Possible match found", Review: "Review needed", NoMatch: "No match found", Incomplete: "Incomplete" };
const KIND = { Receiver: "Receiver", Company: "Company", Address: "Address" };

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
  if (!body.receiverName && !body.companyName && !body.address) {
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
    shown = { result, entered: { Receiver: body.receiverName, Company: body.companyName, Address: body.address }, at: new Date() };
    render(result);
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

  const table = $("result-table");
  const head = el("tr", {}, el("th", { scope: "col" }, "List"),
    result.entries.map((entry) => el("th", { scope: "col" }, KIND[entry.kind] || entry.kind, el("span", { class: "entry-input" }, entry.input))));
  table.tHead.replaceChildren(head);

  const body = table.tBodies[0];
  body.replaceChildren();
  for (const list of result.lists) {
    const row = el("tr", {}, el("td", { class: "list-name" }, list.name,
      el("span", { class: list.state === "Overdue" ? "as-of overdue" : "as-of" }, listAsOf(list))));
    if (list.state === "SwitchedOff") {
      // Not searched at all, so no cells: one note across the row (the summary gives the administrator's reason).
      row.append(el("td", { colspan: String(result.entries.length), class: "switched-off" }, "Not searched: switched off by an administrator"));
      body.append(row);
      continue;
    }
    for (const entry of result.entries) {
      const cell = entry.cells.find((candidate) => candidate.list === list.code);
      row.append(el("td", {}, cellButton(cell, entry, list)));
    }
    body.append(row);
  }

  $("disclaimer").textContent = result.disclaimer;
  $("result").scrollIntoView({ behavior: "smooth", block: "start" });
}

function cellButton(cell, entry, list) {
  // An allowed possible match (the entry is on the allow-list) keeps its percentage but isn't counted.
  const state = cell.state === "NotChecked" ? "NotChecked" : cell.allowed ? "Allowed" : cell.outcome;
  const who = cell.bestCandidate ? cell.bestCandidate.listedName : null;
  const label = cell.state === "NotChecked"
    ? `${list.name}: not checked`
    : `${list.name}, ${KIND[entry.kind]}: ${percentText(cell.percent)} match likelihood, ${LABELS[state]}${who ? ", " + who : ""}`;
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
    el("h3", {}, candidate.listedName),
    el("p", { class: "meta" }, `${list.name} · ${KIND[entry.kind]} “${entry.input}” · ${percentText(candidate.percent)} match likelihood (${LABELS[candidate.outcome]})`),
    cell.allowed && entry.allowedBy ? el("p", { class: "allowed-note" }, el("strong", {}, "Allowed by the allow-list: "),
      `“${entry.allowedBy.name}”: ${entry.allowedBy.reason}. This possible match doesn't raise the outcome; a person can still review it.`) : null,
    el("dl", {},
      el("dt", {}, "Name matched"), el("dd", {}, candidate.alias),
      el("dt", {}, "Type"), el("dd", {}, candidate.type === "Unknown" ? "Not stated by the list" : candidate.type),
      el("dt", {}, "Programme"), el("dd", {}, candidate.programme || "—"),
      el("dt", {}, "Listed addresses"), el("dd", {}, candidate.listedAddresses.length ? el("ul", {}, candidate.listedAddresses.map((address) => el("li", {}, address))) : "None listed"),
      el("dt", {}, "Address"), el("dd", {}, addressText(candidate.addressState, entry)),
      el("dt", {}, "Compared"), el("dd", {}, entry.compared),
      el("dt", {}, "Official record"), el("dd", {}, candidate.sourceUrl ? el("a", { href: candidate.sourceUrl, target: "_blank", rel: "noopener noreferrer" }, "Open the list's own record") : `${list.name}, id ${candidate.externalId}`)),
    el("p", {}, el("strong", {}, "How it aligned")),
    el("ul", {}, candidate.alignment.map((line) => el("li", {}, line))),
    cell.otherHits.length ? el("div", { class: "others" },
      el("p", {}, el("strong", {}, `Other possible matches on ${list.name}`)),
      el("ul", {}, cell.otherHits.map((hit) => el("li", {}, `${hit.listedName} — ${percentText(hit.percent)} (${LABELS[hit.outcome]})`)))) : null,
  ].filter(Boolean));
  detail.hidden = false;
}

// ---- The printed record ----

// A record of the screening for a compliance file, built from the result on the page: what was entered (from the
// page: the server doesn't keep it), against which lists as of when, what came back and why, the versions that
// reproduce it, and the disclaimer. The browser prints it, or saves it as a PDF; nothing is sent or stored.
function printRecord() {
  if (!shown) return;
  const { result, entered, at } = shown;
  const when = (date) => date.toLocaleString(undefined, { day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit", second: "2-digit", timeZoneName: "short" });
  const cellText = (cell) => {
    if (!cell) return "";
    if (cell.state === "NotChecked") return `${SYMBOLS.NotChecked} ${LABELS.NotChecked}`;
    const state = cell.allowed ? "Allowed" : cell.outcome;
    const who = cell.bestCandidate && cell.outcome !== "NoMatch" ? `: ${cell.bestCandidate.listedName}` : "";
    return `${SYMBOLS[state]} ${LABELS[state]} ${percentText(cell.percent)}${who}`;
  };
  const listByCode = new Map(result.lists.map((list) => [list.code, list]));
  const hits = result.entries.flatMap((entry) => entry.cells
    .filter((cell) => cell.bestCandidate && cell.state !== "NotChecked" && (cell.outcome === "Match" || cell.outcome === "Review"))
    .map((cell) => ({ entry, cell, list: listByCode.get(cell.list) })));

  const record = $("print-record");
  record.replaceChildren(
    el("h1", {}, "Sanctions list screening record"),
    el("p", { class: "print-sub" }, "Sanction & Forgive: a demo list check, not a legal determination"),
    el("dl", { class: "print-meta" },
      el("dt", {}, "Reference"), el("dd", {}, result.reference),
      el("dt", {}, "Screened"), el("dd", {}, `${when(at)} (${at.toISOString()})`),
      el("dt", {}, "Result"), el("dd", {}, el("strong", {}, OVERALL[result.outcome] || result.outcome), ". ", result.summary),
      ...["Receiver", "Company", "Address"].flatMap((kind) => [el("dt", {}, `${kind} entered`), el("dd", {}, entered[kind] || "—")])),
    el("h2", {}, "Lists searched"),
    el("table", { class: "print-table" },
      el("thead", {}, el("tr", {}, el("th", {}, "List"), el("th", {}, "As of"), result.entries.map((entry) => el("th", {}, KIND[entry.kind] || entry.kind)))),
      el("tbody", {}, result.lists.map((list) => el("tr", {},
        el("td", {}, list.name),
        el("td", {}, listAsOf(list)),
        list.state === "SwitchedOff"
          ? el("td", { colspan: String(result.entries.length) }, "Not searched: switched off by an administrator")
          : result.entries.map((entry) => el("td", {}, cellText(entry.cells.find((cell) => cell.list === list.code)))))))),
    hits.length
      ? el("section", {}, el("h2", {}, "Possible matches, and how the names aligned"),
        hits.map(({ entry, cell, list }) => {
          const candidate = cell.bestCandidate;
          return el("div", { class: "print-hit" },
            el("h3", {}, `${candidate.listedName}`),
            el("p", {}, `${list ? list.name : cell.list} · ${KIND[entry.kind] || entry.kind} · ${percentText(cell.percent)} match likelihood (${LABELS[cell.allowed ? "Allowed" : cell.outcome]})`),
            cell.allowed && entry.allowedBy ? el("p", {}, `Allowed by the allow-list: “${entry.allowedBy.name}”: ${entry.allowedBy.reason}.`) : null,
            el("dl", { class: "print-meta" },
              el("dt", {}, "Name matched"), el("dd", {}, candidate.alias),
              el("dt", {}, "Programme"), el("dd", {}, candidate.programme || "—"),
              el("dt", {}, "Address"), el("dd", {}, addressText(candidate.addressState, entry)),
              el("dt", {}, "Official record"), el("dd", {}, candidate.sourceUrl || `${list ? list.name : cell.list}, id ${candidate.externalId}`)),
            el("ul", {}, candidate.alignment.map((line) => el("li", {}, line))),
            cell.otherHits.length ? el("p", {}, `Other possible matches on this list: ${cell.otherHits.map((hit) => `${hit.listedName} (${percentText(hit.percent)})`).join("; ")}`) : null);
        }))
      : el("p", {}, "No possible match: no match found in the lists above, as of their dates."),
    el("h2", {}, "Versions"),
    el("p", {}, `Matcher ${result.matcherVersion} · allow-list ${result.allowListVersion} · lists: `,
      result.lists.map((list) => `${list.code} ${list.version === null || list.version === undefined ? "–" : list.version} (${list.state})`).join(", ")),
    el("p", { class: "print-disclaimer" }, result.disclaimer),
    el("p", { class: "print-sub" }, `Printed from ${location.origin}. Check any match against the official publication of the list it comes from.`));
  window.print();
}

function addressText(state, entry) {
  if (entry.kind === "Address") return "Compared on its own (address only)";
  return { Corroborated: "Agrees with a listed address", Conflicting: "In a different country from the listed addresses", NotCompared: "Not compared" }[state] || state;
}

form.addEventListener("submit", screen);
$("print-button").addEventListener("click", printRecord);
$("clear-button").addEventListener("click", () => {
  current++;
  if (pending) pending.abort();
  pending = null;
  $("screen-button").disabled = false;
  form.reset();
  shown = null;
  $("result").hidden = true;
  showState("");
  $("receiverName").focus();
});
loadStatus();
