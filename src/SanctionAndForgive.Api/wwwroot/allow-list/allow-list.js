// The allow-list page: plain JS, no build step. Administrators only (GET /api/v1/admin/allow-list needs the
// Lists.Admin role); changes post with the site's own header. Every value goes in as text, never HTML.
"use strict";

const $ = (id) => document.getElementById(id);
const HEADERS = { Accept: "application/json", "Content-Type": "application/json", "X-Requested-By": "sanction-and-forgive-admin" };

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

function utc(value) {
  return new Date(value).toLocaleString(undefined, { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "UTC", timeZoneName: "short" });
}

async function start() {
  let me;
  try {
    me = await (await fetch("/api/v1/admin/me", { headers: { Accept: "application/json" } })).json();
  } catch {
    $("state").textContent = "The service isn't answering. Try again in a moment.";
    return;
  }

  const state = $("state");
  if (!me.enabled) {
    state.textContent = "Sign-in isn't set up on this deployment, so the allow-list can't be seen or changed from the web.";
  } else if (!me.signedIn) {
    state.replaceChildren("Only administrators can see the allow-list. ", el("a", { href: me.signInUrl.replace("/admin", "/allow-list") }, "Sign in"));
  } else if (!me.isAdmin) {
    state.textContent = `Signed in as ${me.name}, without the lists administrator role: the allow-list is for administrators only.`;
  } else {
    state.textContent = `Signed in as ${me.name}.`;
    $("editor").hidden = false;
    $("entries-panel").hidden = false;
    load();
  }
}

async function load() {
  const response = await fetch("/api/v1/admin/allow-list", { headers: { Accept: "application/json" } });
  if (!response.ok) {
    $("state").textContent = "The allow-list couldn't be read. Try again in a moment.";
    return;
  }
  const body = await response.json();
  const rows = body.entries.map(row);
  $("entries").tBodies[0].replaceChildren(...(rows.length ? rows : [el("tr", {}, el("td", { colspan: "5", class: "muted" }, "No entries yet."))]));
}

function row(entry) {
  const removed = Boolean(entry.removedAt);
  const action = el("td", {});
  if (!removed) {
    action.append(el("button", { type: "button", class: "secondary", onclick: () => confirmRemove(action, entry) }, "Remove"));
  }
  return el("tr", { class: removed ? "removed" : null },
    el("td", {}, el("span", { class: "list-title" }, entry.name)),
    el("td", {}, entry.reason),
    el("td", {}, utc(entry.addedAt), el("span", { class: "sub" }, `by ${entry.addedBy}`)),
    el("td", {}, removed ? el("span", {}, "Removed", el("span", { class: "sub" }, `${utc(entry.removedAt)} by ${entry.removedBy}`)) : "In force"),
    action);
}

// Two steps: a click asks, a second click removes.
function confirmRemove(cell, entry) {
  cell.replaceChildren(
    el("button", { type: "button", class: "primary", onclick: () => remove(cell, entry) }, "Yes, remove"),
    " ",
    el("button", { type: "button", class: "secondary", onclick: () => load() }, "Cancel"));
}

async function remove(cell, entry) {
  cell.textContent = "Removing…";
  const response = await fetch(`/api/v1/admin/allow-list/${entry.id}/remove`, { method: "POST", headers: HEADERS });
  if (!response.ok) cell.textContent = "That didn't work.";
  load();
}

$("add-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const name = $("name").value.trim();
  const reason = $("reason").value.trim();
  if (!name || !reason) {
    $("add-status").textContent = "A name and a reason are both needed.";
    return;
  }
  add(name, reason, []);
});

// A confirmation is for the name and reason it was shown for: editing either asks again, and an answer still on its
// way for the old values is dropped (each request has a number; editing moves it on).
let attempt = 0;
for (const id of ["name", "reason"]) $(id).addEventListener("input", () => { attempt++; $("confirm").hidden = true; });

// The first try sends no confirmations. If the name is a possible match for listed parties, the service lists them
// and adds nothing: they're shown, and "Add anyway" sends their keys. If a list changed meanwhile and brought a new
// possible match, the service lists them again.
async function add(name, reason, confirmed) {
  const status = $("add-status");
  const mine = ++attempt;
  $("confirm").hidden = true;
  status.textContent = "Adding…";
  try {
    const response = await fetch("/api/v1/admin/allow-list", { method: "POST", headers: HEADERS, body: JSON.stringify({ name, reason, confirmed }) });
    // The whole answer first, so an edit made while it was still arriving counts too.
    const body = response.ok || response.status === 409 || response.status === 503 ? await response.json() : null;
    if (mine !== attempt) {
      // Edited meanwhile: what's in the form now hasn't been checked. Say only what became of the earlier values.
      status.textContent = response.ok ? `Added “${name}”, as entered before your edit.` : "";
      if (response.ok) load();
      return;
    }
    if (response.ok) {
      status.textContent = body.reloaded ? "Added: it applies to the next screening." : "Added: it applies within 15 minutes, at the next refresh.";
      $("name").value = "";
      $("reason").value = "";
      load();
    } else if (body) {
      if (body.code === "confirm") {
        status.textContent = "";
        showPossibleMatches(name, reason, body);
      } else {
        status.textContent = body.message;
      }
    } else {
      status.textContent = response.status === 400 ? "Check the name and the reason (at most 300 and 500 characters)." : "That didn't work. Try again in a moment.";
    }
  } catch {
    status.textContent = "The service isn't answering. Try again in a moment.";
  }
}

function showPossibleMatches(name, reason, body) {
  const panel = $("confirm");
  const items = body.possibleMatches.map((hit) => el("li", {},
    el("strong", {}, hit.listedName),
    ` — ${hit.listName}, ${hit.percent}%`,
    el("span", { class: "sub" }, [
      hit.alias !== hit.listedName ? `alias “${hit.alias}”` : null,
      hit.programme ? `programme ${hit.programme}` : null,
    ].filter(Boolean).join("; "))));
  panel.replaceChildren(
    el("h3", { tabindex: "-1" }, `“${name}” is a possible match on the lists`),
    el("p", {}, body.message),
    el("ul", { class: "possible-matches" }, items),
    el("div", { class: "actions" },
      el("button", { type: "button", class: "primary", onclick: () => add(name, reason, body.possibleMatches.map((hit) => hit.key)) }, "Add anyway"),
      " ",
      el("button", { type: "button", class: "secondary", onclick: () => { panel.hidden = true; } }, "Cancel")));
  panel.hidden = false;
  panel.querySelector("h3").focus();
}

start();
