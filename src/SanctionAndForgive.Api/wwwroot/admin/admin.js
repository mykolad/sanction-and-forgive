// The admin page: plain JS, no build step. Sign-in is the app's own (Microsoft Entra ID): /.auth/login/aad and
// /.auth/logout, and GET /api/v1/admin/me says who is signed in. Reviewing posts to /api/v1/admin/imports/{id}/...,
// switching a list to /api/v1/admin/lists/{code}/switch-off|switch-on, with the site's own header (X-Requested-By),
// which another site can't send. Every value goes in as text.
"use strict";

const $ = (id) => document.getElementById(id);
let me = null;

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
  return url && /^(https?:\/\/|\/)/i.test(url) ? el("a", { href: url, rel: "noopener" }, text) : null;
}

function utc(value) {
  return new Date(value).toLocaleString(undefined, { day: "numeric", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "UTC", timeZoneName: "short" });
}

async function loadAccount() {
  try {
    me = await (await fetch("/api/v1/admin/me", { headers: { Accept: "application/json" } })).json();
  } catch {
    $("account").textContent = "The service isn't answering. Try again in a moment.";
    return;
  }

  const account = $("account");
  if (!me.enabled) {
    account.replaceChildren(el("p", {}, "Sign-in isn't set up on this deployment, so nothing here can be changed from the web. Held updates are reviewed with ", el("code", {}, "tools/lists.ps1 review"), "."));
  } else if (!me.signedIn) {
    account.replaceChildren(el("p", {}, "Sign in with your Microsoft account to review updates. Only accounts given the Lists administrator role can."),
      el("p", {}, el("a", { class: "button primary", href: me.signInUrl }, "Sign in")));
  } else {
    account.replaceChildren(el("p", {}, "Signed in as ", el("strong", {}, me.name), me.isAdmin ? ", a lists administrator." : ", without the lists administrator role: you can see held updates but not review them."),
      el("p", {}, link(me.signOutUrl, "Sign out")));
  }

  loadHeld();
}

async function loadHeld() {
  const box = $("held");
  const switches = $("switches");
  let body;
  try {
    const response = await fetch("/api/v1/sources", { headers: { Accept: "application/json" } });
    if (response.status === 503) {
      box.textContent = switches.textContent = "The lists are loading…";
      setTimeout(loadHeld, 3000);
      return;
    }
    body = await response.json();
  } catch {
    box.textContent = switches.textContent = "The service isn't answering. Try again in a moment.";
    return;
  }

  const loaded = body.lists.filter((list) => list.state !== "NotImported");
  switches.replaceChildren(...(loaded.length ? loaded.map(switchRow) : [el("p", { class: "muted" }, "No lists are loaded.")]));
  const held = body.lists.filter((list) => list.held);
  box.replaceChildren(...(held.length ? held.map(heldCard) : [el("p", { class: "muted" }, "No updates are held for review.")]));
}

const STATES = { Current: "Searched", Overdue: "Searched, out of date", NotChecked: "Not checked (not loaded)", SwitchedOff: "Switched off" };

// One row per loaded list: its state, and (for an administrator) a switch. Switching off needs a reason.
function switchRow(list) {
  const status = el("p", { class: "review-status", "aria-live": "polite" });
  const actions = el("div", { class: "actions" });
  const row = el("article", { class: "switch-row" },
    el("h3", {}, list.name, " ", el("span", { class: `state-badge ${list.state}` }, STATES[list.state] ?? list.state)),
    list.state === "SwitchedOff" && list.reason ? el("p", { class: "muted" }, list.reason) : null,
    actions,
    status);
  if (me?.isAdmin) showSwitch(actions, status, list);
  return row;
}

function showSwitch(actions, status, list) {
  if (list.state === "SwitchedOff") {
    actions.replaceChildren(el("button", { type: "button", class: "primary", onclick: () => switchList(actions, status, list, false, null) }, "Switch on: search it again"));
    return;
  }

  actions.replaceChildren(el("button", { type: "button", class: "secondary", onclick: () => askReason(actions, status, list) }, "Switch off: stop searching it"));
}

// Two steps, like a review: the first click asks why, the second switches it off.
function askReason(actions, status, list) {
  const id = `reason-${list.code}`;
  const reason = el("input", { id, type: "text", maxlength: "500", placeholder: "Every result will show this" });
  const confirm = () => {
    if (!reason.value.trim()) {
      status.textContent = "Say why: every result shows the reason while it's off.";
      reason.focus();
      return;
    }
    switchList(actions, status, list, true, reason.value.trim());
  };
  const cancel = () => {
    status.textContent = "";
    showSwitch(actions, status, list);
  };
  actions.replaceChildren(
    el("label", { for: id }, "Why "), reason,
    el("button", { type: "button", class: "secondary", onclick: confirm }, "Yes, switch it off"),
    el("button", { type: "button", class: "secondary", onclick: cancel }, "Cancel"));
  reason.focus();
}

async function switchList(actions, status, list, off, reason) {
  actions.replaceChildren();
  status.textContent = off ? "Switching it off…" : "Switching it on…";
  try {
    const response = await fetch(`/api/v1/admin/lists/${encodeURIComponent(list.code)}/${off ? "switch-off" : "switch-on"}`, {
      method: "POST",
      headers: { Accept: "application/json", "Content-Type": "application/json", "X-Requested-By": "sanction-and-forgive-admin" },
      body: off ? JSON.stringify({ reason }) : undefined,
    });
    const body = response.ok ? await response.json() : null;
    const later = body && !body.reloaded ? " It applies within 15 minutes." : "";
    status.textContent = response.ok
      ? (off ? `Switched off: screenings don't search ${list.name}.${later}` : `Switched on: screenings search ${list.name} again.${later}`)
      : response.status === 409 ? (off ? "It's already off." : "It's already on.")
      : response.status === 401 || response.status === 403 ? "Your sign-in doesn't allow this. Sign in again as a lists administrator."
      : response.status === 400 ? "Say why, in at most 500 characters."
      : "That didn't work. Try again in a moment.";
    if (response.ok || response.status === 409) setTimeout(loadHeld, 1500);
    else showSwitch(actions, status, list);
  } catch {
    status.textContent = "The service isn't answering. Try again in a moment.";
    showSwitch(actions, status, list);
  }
}

function heldCard(list) {
  const drop = list.recordCount ? Math.round(100 * (1 - list.held.recordCount / list.recordCount)) : 0;
  const status = el("p", { class: "review-status", "aria-live": "polite" });
  const actions = el("div", { class: "actions" });
  const card = el("article", { class: "held-card" },
    el("h3", {}, list.name),
    el("p", {}, `Downloaded ${utc(list.held.downloadedAt)}: ${list.held.recordCount.toLocaleString()} records, against ${list.recordCount.toLocaleString()} in the list searched now (${drop}% fewer).`),
    list.held.reason ? el("p", { class: "muted" }, list.held.reason) : null,
    list.held.added !== null && list.held.added !== undefined
      ? el("p", {}, `Against the list it was compared with (the list in use when it was downloaded): ${list.held.added.toLocaleString()} entries added, ${list.held.removed.toLocaleString()} removed, ${list.held.changed.toLocaleString()} changed.`)
      : null,
    el("p", {}, link(list.homepage, "The publisher's page"), list.fileUrl ? " · " : "", link(list.fileUrl, "the file"), " · ", link(`/lists?list=${encodeURIComponent(list.code)}`, "browse the list searched now")),
    actions,
    status);
  if (me?.isAdmin) showActions(actions, status, list);
  return card;
}

// Two steps for each: a click asks, a second click does it.
function showActions(actions, status, list) {
  actions.replaceChildren(
    el("button", { type: "button", class: "primary", onclick: () => ask(actions, status, list, true) }, "Confirm: make it the active list"),
    el("button", { type: "button", class: "secondary", onclick: () => ask(actions, status, list, false) }, "Reject: discard it"));
}

function ask(actions, status, list, confirm) {
  actions.replaceChildren(
    el("span", {}, confirm
      ? `The ${list.held.recordCount.toLocaleString()}-record update becomes the active list; screenings use it while it's current (List status shows it).`
      : "The update will be deleted; the current list stays."),
    el("button", { type: "button", class: confirm ? "primary" : "secondary", onclick: () => review(actions, status, list, confirm) }, confirm ? "Yes, confirm" : "Yes, reject"),
    el("button", { type: "button", class: "secondary", onclick: () => showActions(actions, status, list) }, "Cancel"));
}

async function review(actions, status, list, confirm) {
  actions.replaceChildren();
  status.textContent = confirm ? "Confirming, then reloading the list…" : "Rejecting…";
  try {
    const response = await fetch(`/api/v1/admin/imports/${list.held.importId}/${confirm ? "confirm" : "reject"}`, {
      method: "POST",
      headers: { Accept: "application/json", "X-Requested-By": "sanction-and-forgive-admin" },
    });
    const body = response.ok ? await response.json() : null;
    const later = body && !body.reloaded ? " The lists reload within 15 minutes." : "";
    status.textContent = response.ok
      ? (confirm ? `Confirmed: it's the active list. List status shows whether it's searched now.${later}` : `Rejected: the update is deleted.${later}`)
      : response.status === 409 ? "It isn't held any more: someone reviewed it, or a later check replaced it."
      : response.status === 401 || response.status === 403 ? "Your sign-in doesn't allow this. Sign in again as a lists administrator."
      : "That didn't work. Try again in a moment.";
    if (response.ok || response.status === 409) setTimeout(loadHeld, 1500);
    else showActions(actions, status, list);
  } catch {
    status.textContent = "The service isn't answering. Try again in a moment.";
    showActions(actions, status, list);
  }
}

loadAccount();
