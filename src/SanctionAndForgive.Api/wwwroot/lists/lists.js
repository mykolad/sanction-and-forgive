// The list browser: plain JS, no build step, reading POST /api/v1/lists/{code}/browse. Every value goes into the
// page as text (textContent), never as HTML, and only http(s) URLs become links. The filter text goes in the request
// body, never the URL; only the list code is kept in the address (?list=eu), so a link can open a list.
"use strict";

const PAGE = 50;
const TYPES = { Individual: "Person", Entity: "Entity", Unknown: "Not stated" };

const $ = (id) => document.getElementById(id);
const stateBox = $("state");
let offset = 0;
let total = 0;
// The filters that produced the page on screen: Previous and Next page through those, not through whatever has been
// typed into the form since (that takes "Show entries", from the first page).
let shown = null;
// Each request takes a new number; only the latest may change the page.
let current = 0;

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

function showState(text, isError) {
  stateBox.textContent = text || "";
  stateBox.classList.toggle("error", Boolean(isError));
}

function utc(value) {
  return value
    ? new Date(value).toLocaleString(undefined, { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "UTC", timeZoneName: "short" })
    : "never";
}

async function loadLists() {
  try {
    const response = await fetch("/api/v1/sources", { headers: { Accept: "application/json" } });
    if (response.status === 503) {
      showState("The lists are loading. This page fills in as soon as they're in memory…");
      setTimeout(loadLists, 3000);
      return;
    }
    const body = await response.json();
    const loaded = body.lists.filter((list) => list.state !== "NotImported");
    $("list").replaceChildren(...loaded.map((list) => el("option", { value: list.code }, `${list.name} (${list.recordCount.toLocaleString()})`)));
    const wanted = new URLSearchParams(location.search).get("list");
    if (wanted && loaded.some((list) => list.code === wanted)) $("list").value = wanted;
    showState("");
    browse(0, formFilters());
  } catch {
    showState("The service isn't answering. Try again in a moment.", true);
  }
}

function formFilters() {
  return { code: $("list").value, query: $("query").value.trim() || null, type: $("type").value || null };
}

async function browse(start, filters) {
  if (!filters.code) return;
  const request = ++current;
  history.replaceState(null, "", `/lists?list=${encodeURIComponent(filters.code)}`);
  showState("Loading…");
  try {
    const response = await fetch(`/api/v1/lists/${encodeURIComponent(filters.code)}/browse`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify({ query: filters.query, type: filters.type, offset: start, limit: PAGE }),
    });
    if (request !== current) return;
    if (response.status === 429) {
      showState("Too many requests from this address. Wait a minute and try again.", true);
      return;
    }
    if (response.status === 503) {
      $("results").hidden = true;
      showState("This list isn't loaded right now (it's loading, or its last load failed), so it can't be browsed. Try again in a few minutes.", true);
      return;
    }
    if (!response.ok) {
      showState(response.status === 400 ? "The filter is too long, or not valid." : "The service couldn't answer. Try again in a moment.", true);
      return;
    }
    const page = await response.json();
    // A newer request may have finished while this body was still arriving.
    if (request !== current) return;
    shown = filters;
    render(page);
    showState("");
  } catch {
    if (request === current) showState("The service isn't answering. Try again in a moment.", true);
  }
}

function render(page) {
  offset = page.offset;
  total = page.total;
  $("results").hidden = false;
  $("results-title").textContent = `${page.list.name}: ${page.total.toLocaleString()} ${page.total === 1 ? "entry" : "entries"}`;
  $("list-note").textContent = page.list.state === "Current"
    ? `As of ${utc(page.list.asOf)}, version ${page.list.version}. These are the entries a screening compares against.`
    : `This list is Not checked: its last successful check (${utc(page.list.asOf)}) is too old, so screenings don't search it. The entries below may be out of date.`;
  const rows = page.entries.flatMap((entry, i) => entryRows(entry, `entry-${page.offset + i}`));
  $("entries").tBodies[0].replaceChildren(...(rows.length ? rows : [el("tr", {}, el("td", { colspan: "4", class: "muted" }, "No entries match."))]));
  $("range").textContent = page.total ? `${(page.offset + 1).toLocaleString()}–${(page.offset + page.entries.length).toLocaleString()} of ${page.total.toLocaleString()}` : "";
  $("previous").disabled = page.offset === 0;
  $("next").disabled = page.offset + page.entries.length >= page.total;
}

function entryRows(entry, id) {
  const detail = el("tr", { id, class: "entry-detail", hidden: true }, el("td", { colspan: "4" }, details(entry)));
  const toggle = el("button", {
    type: "button", class: "entry-name", "aria-expanded": "false", "aria-controls": id,
    onclick: () => {
      const open = toggle.getAttribute("aria-expanded") !== "true";
      toggle.setAttribute("aria-expanded", String(open));
      detail.hidden = !open;
    },
  }, entry.primaryName);
  const aliases = entry.names.length - 1;
  return [
    el("tr", {},
      el("td", {}, toggle, aliases > 0 ? el("span", { class: "sub" }, `${aliases} other ${aliases === 1 ? "name" : "names"}`) : null),
      el("td", {}, TYPES[entry.type] ?? entry.type),
      el("td", {}, entry.programme ?? ""),
      el("td", { class: "id" }, entry.externalId)),
    detail,
  ];
}

function details(entry) {
  return el("div", { class: "detail" },
    el("dl", {},
      el("dt", {}, "Names"),
      el("dd", {}, el("ul", {}, entry.names.map((name) => el("li", {}, name.name, name.isWeak ? el("span", { class: "muted" }, " (the list marks this alias as low quality)") : null)))),
      el("dt", {}, "Addresses"),
      el("dd", {}, entry.addresses.length
        ? el("ul", {}, entry.addresses.map((address) => el("li", {}, address.raw, address.countryCode && !address.raw.includes(address.countryCode) ? ` (${address.countryCode})` : "")))
        : el("span", { class: "muted" }, "None listed")),
      el("dt", {}, "Programme"),
      el("dd", {}, entry.programme ?? el("span", { class: "muted" }, "None stated")),
      el("dt", {}, "Official record"),
      el("dd", {}, link(entry.sourceUrl, entry.sourceUrl) ?? el("span", { class: "muted" }, "The list gives no link per entry"))));
}

$("browse-form").addEventListener("submit", (event) => {
  event.preventDefault();
  browse(0, formFilters());
});
$("list").addEventListener("change", () => browse(0, formFilters()));
$("type").addEventListener("change", () => browse(0, formFilters()));
$("clear-button").addEventListener("click", () => {
  $("query").value = "";
  $("type").value = "";
  browse(0, formFilters());
});
$("previous").addEventListener("click", () => { if (shown) browse(Math.max(0, offset - PAGE), shown); });
$("next").addEventListener("click", () => { if (shown && offset + PAGE < total) browse(offset + PAGE, shown); });

loadLists();
