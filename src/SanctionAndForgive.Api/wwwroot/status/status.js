// The list status page: plain JS, no build step, reading GET /api/v1/sources. Every value goes into the page as
// text (textContent), never as HTML, and only http(s) URLs become links.
"use strict";

const STATE = {
  Current: { symbol: "✓", label: "Searched" },
  Overdue: { symbol: "!", label: "Searched, out of date" },
  SwitchedOff: { symbol: "○", label: "Switched off" },
  NotChecked: { symbol: "–", label: "Not checked" },
};
const WHY = {
  TermsUnclear: "Terms don't allow this use yet",
  NeedsAccess: "Can't be fetched automatically",
  Active: "Not imported yet",
};

const $ = (id) => document.getElementById(id);

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

function link(url, text) {
  return url && /^https?:\/\//i.test(url) ? el("a", { href: url, rel: "noopener" }, text) : null;
}

function utc(value) {
  return new Date(value).toLocaleString(undefined, { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "UTC", timeZoneName: "short" });
}

function ago(value, now) {
  const minutes = Math.max(0, Math.round((now - new Date(value)) / 60000));
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h ago`;
  return `${Math.round(hours / 24)} days ago`;
}

function when(value, now) {
  if (!value) return el("span", { class: "muted" }, "never");
  return el("span", {}, ago(value, now), el("span", { class: "sub" }, utc(value)));
}

function links(list) {
  const parts = [link(list.homepage, "Official page"), link(list.fileUrl, "File"), link(list.termsUrl, "Terms")].filter(Boolean);
  return parts.flatMap((part, i) => (i ? [" · ", part] : [part]));
}

function listCell(list) {
  return el("td", {},
    list.state === "NotImported"
      ? el("span", { class: "list-title" }, list.name)
      : el("a", { class: "list-title", href: `/lists?list=${encodeURIComponent(list.code)}`, title: "Browse its entries" }, list.name),
    el("span", { class: "sub" }, list.code,
      list.status === "Manual" ? " · maintained by hand" : "",
      list.state !== "NotImported" && list.status === "TermsUnclear" ? " · imported by decision; its terms aren't cleared" : ""));
}

function searchedRow(list, now) {
  const state = STATE[list.state] ?? STATE.NotChecked;
  return el("tr", {},
    listCell(list),
    el("td", {},
      el("span", { class: `state-badge ${list.state}` }, el("span", { "aria-hidden": "true" }, state.symbol), " ", state.label),
      list.reason ? el("span", { class: "sub" }, list.reason) : null,
      list.held ? held(list) : null),
    el("td", { class: "num" }, list.recordCount.toLocaleString()),
    el("td", {}, when(list.lastCheckedAt, now), list.maxAgeHours ? el("span", { class: "sub" }, `allowed age ${list.maxAgeHours} h`) : null),
    el("td", {}, when(list.updatedAt, now)),
    el("td", { class: "links" }, links(list)));
}

// An update held for review: the list above still serves; a person confirms or rejects it.
function held(list) {
  return el("span", { class: "held" },
    el("span", { class: "state-badge Held" }, el("span", { "aria-hidden": "true" }, "!"), " Update held for review"),
    el("span", { class: "sub" },
      `${list.held.recordCount.toLocaleString()} records downloaded ${utc(list.held.downloadedAt)}, against ${list.recordCount.toLocaleString()} now. `,
      "Screenings keep using the list above (past its allowed age too, with a warning) until an administrator confirms or rejects the update."));
}

const RESULTS = {
  Active: "The list in use",
  Replaced: "Replaced by a later update",
  HeldForReview: "Held for review",
  Rejected: "Rejected",
  Failed: "Failed",
  Running: "Running",
};

// What an update changed against the list in use when it was downloaded: entries added, removed, and kept with any
// field, name or address different. Null counts weren't recorded (an update from before they were, or a failure).
function changes(entry) {
  if (entry.added === null || entry.added === undefined) {
    return entry.result === "Failed" || entry.result === "Running" ? "" : el("span", { class: "muted" }, "not recorded");
  }
  if (entry.added === 0 && entry.removed === 0 && entry.changed === 0) return el("span", { class: "muted" }, "no entry changed");
  const parts = [];
  if (entry.added) parts.push(el("span", { class: "change added" }, `+${entry.added.toLocaleString()} added`));
  if (entry.removed) parts.push(el("span", { class: "change removed" }, `−${entry.removed.toLocaleString()} removed`));
  if (entry.changed) parts.push(el("span", { class: "change changed" }, `${entry.changed.toLocaleString()} changed`));
  return el("span", { class: "changes" }, ...parts);
}

function historyRows(entries, now) {
  return entries.map((entry) => el("tr", {},
    el("td", {}, when(entry.startedAt, now)),
    el("td", {}, el("span", { class: `update-result ${entry.result}` }, RESULTS[entry.result] ?? entry.result), entry.reviewed ? el("span", { class: "sub" }, "after review") : null),
    el("td", { class: "num" }, entry.result === "Failed" ? "" : entry.recordCount.toLocaleString()),
    el("td", {}, changes(entry)),
    el("td", { class: "note" }, entry.note ?? "")));
}

function historyBlock(list, now) {
  const last = list.history.find((entry) => entry.result === "Active" || entry.result === "Replaced");
  const total = list.historyCount ?? list.history.length;
  const body = el("tbody", {}, historyRows(list.history, now));
  const more = total > list.history.length
    ? el("button", { type: "button", class: "secondary", onclick: (event) => showAll(list, body, event.currentTarget, now) }, `Show all ${total} updates`)
    : null;
  return el("details", { class: "history" },
    el("summary", {}, el("span", { class: "list-title" }, list.name),
      el("span", { class: "muted" }, list.history.length
        ? ` · ${total > list.history.length ? `the latest ${list.history.length} of ${total} updates` : `${total} ${total === 1 ? "update" : "updates"}`}${last ? `; current list from ${utc(list.updatedAt ?? last.completedAt)}` : ""}`
        : " · no updates recorded"),
      list.held ? el("span", { class: "state-badge Held" }, " ! held for review") : null),
    list.history.length
      ? el("div", { class: "table-scroll" }, el("table", { class: "sources" },
        el("thead", {}, el("tr", {}, el("th", { scope: "col" }, "When"), el("th", { scope: "col" }, "Result"), el("th", { scope: "col", class: "num" }, "Entries"), el("th", { scope: "col" }, "Changes"), el("th", { scope: "col" }, "Note"))),
        body))
      : el("p", { class: "muted" }, "Nothing recorded yet."),
    more ? el("p", {}, more) : null);
}

async function showAll(list, body, button, now) {
  button.disabled = true;
  button.textContent = "Loading…";
  try {
    const response = await fetch(`/api/v1/sources/${encodeURIComponent(list.code)}/history`, { headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error(String(response.status));
    const full = await response.json();
    body.replaceChildren(...historyRows(full.history, now));
    button.parentElement.remove();
  } catch {
    button.disabled = false;
    button.textContent = "That didn't work. Try again";
  }
}

function notImportedRow(list) {
  return el("tr", {},
    listCell(list),
    el("td", {}, el("span", { class: "state-badge NotImported" }, WHY[list.status] ?? list.status), el("span", { class: "sub" }, list.reason)),
    el("td", { class: "links" }, links(list)));
}

async function load() {
  const stateBox = $("state");
  try {
    const response = await fetch("/api/v1/sources", { headers: { Accept: "application/json" } });
    if (response.status === 503) {
      stateBox.textContent = "The lists are loading. This page fills in as soon as they're in memory…";
      setTimeout(load, 3000);
      return;
    }
    const body = await response.json();
    const now = new Date(body.now);
    const searched = body.lists.filter((list) => list.state !== "NotImported");
    const current = searched.filter((list) => list.state === "Current" || list.state === "Overdue");
    $("sources-table").tBodies[0].replaceChildren(...searched.map((list) => searchedRow(list, now)));
    $("not-imported-table").tBodies[0].replaceChildren(...body.lists.filter((list) => list.state === "NotImported").map(notImportedRow));
    $("history").replaceChildren(...searched.map((list) => historyBlock(list, now)));
    const records = current.reduce((sum, list) => sum + list.recordCount, 0);
    stateBox.textContent = `${current.length} of ${searched.length} lists searched now: ${records.toLocaleString()} records.`;
    stateBox.classList.remove("error");
  } catch {
    stateBox.textContent = "The service isn't answering. Try again in a moment.";
    stateBox.classList.add("error");
    return;
  }

  try {
    const status = await (await fetch("/api/v1/status", { headers: { Accept: "application/json" } })).json();
    $("version").textContent = `Version ${status.version}, matcher ${status.matcherVersion}.`;
  } catch {
    // The version line is optional.
  }
}

load();
