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

$("add-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const status = $("add-status");
  const name = $("name").value.trim();
  const reason = $("reason").value.trim();
  if (!name || !reason) {
    status.textContent = "A name and a reason are both needed.";
    return;
  }
  status.textContent = "Adding…";
  try {
    const response = await fetch("/api/v1/admin/allow-list", { method: "POST", headers: HEADERS, body: JSON.stringify({ name, reason }) });
    if (response.ok) {
      status.textContent = "Added: it applies to the next screening.";
      $("name").value = "";
      $("reason").value = "";
      load();
    } else if (response.status === 409) {
      status.textContent = (await response.json()).message;
    } else {
      status.textContent = response.status === 400 ? "Check the name and the reason (at most 300 and 500 characters)." : "That didn't work. Try again in a moment.";
    }
  } catch {
    status.textContent = "The service isn't answering. Try again in a moment.";
  }
});

start();
