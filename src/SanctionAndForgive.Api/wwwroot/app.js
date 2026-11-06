// Sanction & Forgive page: plain JS, no build step. It calls the same API as everyone else. Every value from the
// API or the user goes into the page as text (textContent), never as HTML.
"use strict";

const LABELS = { Match: "Match", Review: "Possible match", NoMatch: "No match", NotChecked: "Not checked" };
const SYMBOLS = { Match: "●", Review: "▲", NoMatch: "✓", NotChecked: "–" };
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

async function loadStatus() {
  try {
    const response = await fetch("/api/v1/status", { headers: { Accept: "application/json" } });
    const status = await response.json();
    lists = status.lists;
    $("version").textContent = `Version ${status.version}, matcher ${status.matcherVersion}.`;
    $("sources").textContent = lists.length
      ? lists.map((list) => `${list.name} (${list.state === "Current" ? asOf(list.asOf) : "not checked"})`).join("; ") + "."
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
      showState("The lists are still loading. Try again in a few seconds.", true);
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
    const row = el("tr", {}, el("td", { class: "list-name" }, list.name, el("span", { class: "as-of" }, list.state === "Current" ? asOf(list.asOf) : "not checked")));
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
  const state = cell.state === "NotChecked" ? "NotChecked" : cell.outcome;
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
  detail.replaceChildren(
    el("h3", {}, candidate.listedName),
    el("p", { class: "meta" }, `${list.name} · ${KIND[entry.kind]} “${entry.input}” · ${percentText(candidate.percent)} match likelihood (${LABELS[candidate.outcome]})`),
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
      el("ul", {}, cell.otherHits.map((hit) => el("li", {}, `${hit.listedName} — ${percentText(hit.percent)} (${LABELS[hit.outcome]})`)))) : null);
  detail.hidden = false;
}

function addressText(state, entry) {
  if (entry.kind === "Address") return "Compared on its own (address only)";
  return { Corroborated: "Agrees with a listed address", Conflicting: "In a different country from the listed addresses", NotCompared: "Not compared" }[state] || state;
}

form.addEventListener("submit", screen);
$("clear-button").addEventListener("click", () => {
  current++;
  if (pending) pending.abort();
  pending = null;
  $("screen-button").disabled = false;
  form.reset();
  $("result").hidden = true;
  showState("");
  $("receiverName").focus();
});
loadStatus();
