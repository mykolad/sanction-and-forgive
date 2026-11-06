// The list status page: plain JS, no build step, reading GET /api/v1/sources. Every value goes into the page as
// text (textContent), never as HTML, and only http(s) URLs become links.
"use strict";

const STATE = {
  Current: { symbol: "✓", label: "Searched" },
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
      list.reason ? el("span", { class: "sub" }, list.reason) : null),
    el("td", { class: "num" }, list.recordCount.toLocaleString()),
    el("td", {}, when(list.lastCheckedAt, now), list.maxAgeHours ? el("span", { class: "sub" }, `allowed age ${list.maxAgeHours} h`) : null),
    el("td", {}, when(list.updatedAt, now)),
    el("td", { class: "links" }, links(list)));
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
    const current = searched.filter((list) => list.state === "Current");
    $("sources-table").tBodies[0].replaceChildren(...searched.map((list) => searchedRow(list, now)));
    $("not-imported-table").tBodies[0].replaceChildren(...body.lists.filter((list) => list.state === "NotImported").map(notImportedRow));
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
