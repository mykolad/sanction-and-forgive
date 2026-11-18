// The admin page's table of destinations under broad restrictions (#67): shown when the API's status lists the
// "destinations" feature. Anyone sees the table in force (GET /api/v1/destinations); a lists administrator also sees
// every row ever added and edits them (GET and POST /api/v1/admin/destinations, …/{id}/remove, …/start), with the site's
// own header. Rows are never deleted: a change is a new row with the same code, which removes the old one. Uses el, $
// and me from admin.js. Every value goes in as text.
"use strict";

const KINDS = ["Country", "Region", "Settlement"];
const STATUSES = { Restricted: "Restricted", Partly: "Partly restricted", Outside: "Outside the restricted area" };
const ADMIN_HEADERS = { Accept: "application/json", "Content-Type": "application/json", "X-Requested-By": "sanction-and-forgive-admin" };
let destinationsShown = { rows: [], history: [], canStart: false, startingCount: 0 };

async function loadDestinations() {
  const section = $("destinations-section");
  let status;
  try {
    status = await (await fetch("/api/v1/status", { headers: { Accept: "application/json" } })).json();
  } catch {
    return;
  }
  if (!(status.features || []).includes("destinations")) return;
  section.hidden = false;

  const box = $("destinations");
  try {
    const response = await fetch("/api/v1/destinations", { headers: { Accept: "application/json" } });
    if (response.status === 503) {
      box.textContent = "The lists are loading…";
      setTimeout(loadDestinations, 3000);
      return;
    }
    const table = await response.json();
    destinationsShown = { rows: table.destinations, history: [], canStart: false, startingCount: 0, version: table.version, reviewOverdue: table.reviewOverdue };
    if (me?.isAdmin) {
      const answer = await fetch("/api/v1/admin/destinations", { headers: { Accept: "application/json" } });
      if (answer.ok) {
        const admin = await answer.json();
        // The rows in force from the database itself: another replica may not have read a change just made yet.
        Object.assign(destinationsShown, {
          rows: admin.rows.filter((r) => !r.removedAt).sort((a, b) => a.id - b.id),
          history: admin.rows, canStart: admin.canStart, startingCount: admin.startingCount,
        });
      }
    }
  } catch {
    box.textContent = "The service isn't answering. Try again in a moment.";
    return;
  }
  showDestinations();
}

function showDestinations() {
  const { rows, history, canStart, startingCount, reviewOverdue } = destinationsShown;
  const box = $("destinations");
  const status = el("p", { class: "review-status", "aria-live": "polite" });
  const actions = el("div", { class: "actions" });
  if (me?.isAdmin) {
    actions.append(el("button", { type: "button", class: "primary", onclick: () => showForm(null) }, "Add a row"));
    if (canStart) {
      actions.append(el("button", { type: "button", class: "secondary", onclick: () => start(status) }, `Load the ${startingCount} starting rows`));
    }
  }

  // Countries, then each region with its settlements under it.
  const byParent = new Map();
  for (const row of rows.filter((r) => r.kind === "Settlement")) {
    const list = byParent.get(row.parent || "") || [];
    list.push(row);
    byParent.set(row.parent || "", list);
  }
  const ordered = [
    ...rows.filter((r) => r.kind === "Country"),
    ...rows.filter((r) => r.kind === "Region").flatMap((region) => [region, ...(byParent.get(region.code) || [])]),
    ...(byParent.get("") || []),
  ];
  const listed = new Set(ordered);
  ordered.push(...rows.filter((r) => !listed.has(r)));

  const removed = history.filter((r) => r.removedAt);
  box.replaceChildren(...[
    reviewOverdue ? el("p", { class: "state error" }, `Due for review since ${reviewOverdue}: every result warns until the rows are checked again (edit each one with a new review date).`) : null,
    rows.length
      ? el("div", { class: "table-scroll" }, el("table", { class: "result-table destinations-table" },
        el("thead", {}, el("tr", {}, ["Place", "Status", "Restricted by", "As of", "Review by", ""].map((h) => el("th", { scope: "col" }, h)))),
        el("tbody", {}, ordered.map((row) => destinationRow(row, status)))))
      : el("p", { class: "muted" }, canStart
        ? "The table is empty, so no address raises a notice. Load the starting rows the owner approved, then edit them here."
        : "The table is empty, so no address raises a notice."),
    actions,
    el("div", { id: "destination-form" }),
    status,
    removed.length ? el("details", {}, el("summary", {}, `Removed and replaced rows (${removed.length})`),
      el("ul", {}, removed.map((r) => el("li", {}, `${r.name} (${r.code}, ${STATUSES[r.status] || r.status}): added by ${r.addedBy} ${utc(r.addedAt)}, removed by ${r.removedBy} ${utc(r.removedAt)}`)))) : null].filter(Boolean));
}

function destinationRow(row, status) {
  const indent = row.kind === "Settlement" && row.parent ? "destination-child" : "";
  const buttons = me?.isAdmin ? el("td", { class: "actions" },
    el("button", { type: "button", class: "secondary", onclick: () => showForm(row) }, "Edit"),
    el("button", { type: "button", class: "secondary", onclick: (event) => askRemove(event.target.parentElement, row, status) }, "Remove")) : el("td", {});
  return el("tr", { "data-id": String(row.id) },
    el("td", { class: indent },
      el("strong", {}, row.name), ` ${row.kind.toLowerCase()}${row.country ? `, ${row.country}` : ""}${row.needsRegion ? ", only with its region" : ""}`,
      row.otherNames.length ? el("span", { class: "entry-input" }, `Also: ${row.otherNames.join(", ")}`) : null,
      row.legalSource ? el("span", { class: "entry-input" }, row.legalSource) : null),
    el("td", {}, STATUSES[row.status] || row.status),
    el("td", {}, row.restrictedBy || "—"),
    el("td", {}, row.asOf),
    el("td", {}, row.reviewBy),
    buttons);
}

function askRemove(cell, row, status) {
  cell.replaceChildren(
    el("span", {}, "Addresses there stop raising a notice. "),
    el("button", { type: "button", class: "secondary", onclick: () => remove(row, status) }, "Yes, remove"),
    el("button", { type: "button", class: "secondary", onclick: showDestinations }, "Cancel"));
}

async function send(path, body) {
  const response = await fetch(path, { method: "POST", headers: ADMIN_HEADERS, body: body === undefined ? undefined : JSON.stringify(body) });
  return { response, body: response.headers.get("content-type")?.includes("json") ? await response.json() : null };
}

function failure(response) {
  return response.status === 401 || response.status === 403 ? "Your sign-in doesn't allow this. Sign in again as a lists administrator."
    : "That didn't work. Try again in a moment.";
}

async function remove(row, status) {
  status.textContent = "Removing…";
  try {
    const { response, body } = await send(`/api/v1/admin/destinations/${row.id}/remove`);
    status.textContent = response.ok ? `Removed: ${row.name}.`
      : response.status === 404 ? "It isn't in the table any more."
      : response.status === 409 && body?.code === "inUse" ? `${body.message} (${body.settlements.join(", ")})`
      : failure(response);
    // The table is shown again only when it changed: a refusal stays on screen.
    if (response.ok || response.status === 404) setTimeout(loadDestinations, 800);
    else showButtons(row, status);
  } catch {
    status.textContent = "The service isn't answering. Try again in a moment.";
  }
}

// Puts a row's Edit and Remove buttons back after a refusal.
function showButtons(row, status) {
  const cell = [...$("destinations").querySelectorAll("tr")].find((tr) => tr.dataset.id === String(row.id))?.lastElementChild;
  if (cell) {
    cell.replaceChildren(
      el("button", { type: "button", class: "secondary", onclick: () => showForm(row) }, "Edit"),
      el("button", { type: "button", class: "secondary", onclick: (event) => askRemove(event.target.parentElement, row, status) }, "Remove"));
  }
}

async function start(status) {
  status.textContent = "Loading the starting rows…";
  try {
    const { response, body } = await send("/api/v1/admin/destinations/start");
    status.textContent = response.ok ? `Loaded ${body.added} rows.` : response.status === 409 ? "The table already has rows." : failure(response);
  } catch {
    status.textContent = "The service isn't answering. Try again in a moment.";
  }
  setTimeout(loadDestinations, 800);
}

// The form for a new row, or for a change to row (a new row with its code, which replaces it).
function showForm(row) {
  const box = $("destination-form");
  const field = (label, input, hint) => el("label", { class: "field" }, el("span", { class: "field-label" }, label, hint ? el("span", { class: "hint" }, ` ${hint}`) : null), input);
  const select = (name, options, value) => el("select", { name }, options.map(([v, text]) => el("option", { value: v, selected: v === value }, text)));
  const regions = destinationsShown.rows.filter((r) => r.kind === "Region");
  const today = new Date().toISOString().slice(0, 10);
  const inputs = {
    code: el("input", { name: "code", maxlength: "64", value: row?.code || "", placeholder: "ua-crimea", spellcheck: "false" }),
    kind: select("kind", KINDS.map((k) => [k, k]), row?.kind || "Settlement"),
    name: el("input", { name: "name", maxlength: "200", value: row?.name || "" }),
    otherNames: el("textarea", { name: "otherNames", rows: "3", spellcheck: "false" }),
    country: el("input", { name: "country", maxlength: "2", value: row?.country || "", placeholder: "UA", spellcheck: "false" }),
    parent: select("parent", [["", "—"], ...regions.map((r) => [r.code, `${r.name} (${r.code})`])], row?.parent || ""),
    needsRegion: el("input", { type: "checkbox", name: "needsRegion", checked: !!row?.needsRegion }),
    status: select("status", Object.entries(STATUSES), row?.status || "Restricted"),
    restrictedBy: el("input", { name: "restrictedBy", maxlength: "200", value: row?.restrictedBy || "", placeholder: "EU, UK, US" }),
    legalSource: el("textarea", { name: "legalSource", rows: "2", maxlength: "1000" }),
    // A change keeps the row's dates: a new check of the sources is a new As of (and Review by), entered as such.
    asOf: el("input", { type: "date", name: "asOf", value: row?.asOf || today }),
    reviewBy: el("input", { type: "date", name: "reviewBy", value: row?.reviewBy || "" }),
  };
  inputs.otherNames.value = (row?.otherNames || []).join("\n");
  inputs.legalSource.value = row?.legalSource || "";
  const note = el("p", { class: "review-status", "aria-live": "polite" });
  const submit = async (confirmed) => {
    const body = {
      code: inputs.code.value, kind: inputs.kind.value, name: inputs.name.value,
      otherNames: inputs.otherNames.value.split("\n").map((n) => n.trim()).filter(Boolean),
      country: inputs.country.value, parent: inputs.parent.value || null, needsRegion: inputs.needsRegion.checked,
      status: inputs.status.value, restrictedBy: inputs.restrictedBy.value, legalSource: inputs.legalSource.value,
      asOf: inputs.asOf.value, reviewBy: inputs.reviewBy.value || null, confirmed,
    };
    note.textContent = "Saving…";
    try {
      const { response, body: answer } = await send("/api/v1/admin/destinations", body);
      if (response.ok) {
        note.textContent = answer.replaced ? "Saved: it replaces the row with the same code." : "Added.";
        box.replaceChildren();
        setTimeout(loadDestinations, 800);
      } else if (response.status === 409 && answer?.code === "inUse") {
        note.textContent = `${answer.message} (${answer.settlements.join(", ")})`;
      } else if (response.status === 409 && answer?.code === "confirm") {
        note.replaceChildren(el("span", {}, answer.message), el("ul", {}, answer.conflicts.map((c) => el("li", {}, c))),
          el("button", { type: "button", class: "primary", onclick: () => submit(true) }, "Yes, save it anyway"));
      } else if (response.status === 400 && answer?.errors) {
        note.replaceChildren(el("ul", {}, Object.entries(answer.errors).map(([name, messages]) => el("li", {}, `${name || "Row"}: ${messages.join(" ")}`))));
      } else {
        note.textContent = failure(response);
      }
    } catch {
      note.textContent = "The service isn't answering. Try again in a moment.";
    }
  };
  box.replaceChildren(el("form", { class: "panel destination-form", onsubmit: (event) => { event.preventDefault(); submit(false); } },
    el("h3", {}, row ? `Change ${row.name}` : "Add a row"),
    el("p", { class: "section-note" }, "A row with the code of one in the table replaces it; the old one is kept as history. Check the legal source against the official text."),
    el("div", { class: "fields" },
      field("Code", inputs.code, "stable, lower case"),
      field("Kind", inputs.kind),
      field("Name", inputs.name, "as the notice shows it"),
      field("Country", inputs.country, "ISO code: the country itself, or the one it lies in"),
      field("Status", inputs.status),
      field("Region", inputs.parent, "a settlement's"),
      el("label", { class: "field" }, el("span", { class: "field-label" }, "Only with its region"), el("span", {}, inputs.needsRegion, " a name that repeats elsewhere: recognised only when the address names this region or none")),
      field("Restricted by", inputs.restrictedBy),
      field("As of", inputs.asOf, "when the sources were checked"),
      field("Review by", inputs.reviewBy, "at most 12 months after As of; 12 months when empty"),
      el("label", { class: "field field-wide" }, el("span", { class: "field-label" }, "Other names ", el("span", { class: "hint" }, "one per line, every script and spelling")), inputs.otherNames),
      el("label", { class: "field field-wide" }, el("span", { class: "field-label" }, "Legal source"), inputs.legalSource)),
    el("div", { class: "actions" },
      el("button", { type: "submit", class: "primary" }, "Save"),
      el("button", { type: "button", class: "secondary", onclick: () => box.replaceChildren() }, "Cancel")),
    note));
  inputs.name.focus();
}

// admin.js calls loadDestinations once the account is known; if that happened before this script ran, start here.
if (me !== null) loadDestinations();
