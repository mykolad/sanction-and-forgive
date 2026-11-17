// Screening a manifest: plain JS, no build step. A CSV file is read and parsed here in the browser; its rows go to
// POST /api/v1/screenings/stream in chunks (NDJSON), and the results come back as a table and a CSV file to download.
// Nothing is stored on the server and nothing goes into the URL. Every value goes into the page as text
// (textContent), never as HTML.
"use strict";

const MAX_ROWS = 5000;
const MAX_BYTES = 10 * 1024 * 1024;
// Records per stream request. Small enough that a chunk's answers fit in the connection's buffers while the browser
// is still sending it (a browser reads the response only once the request is sent), large enough to keep the stream
// endpoint's one-at-a-time permit busy.
const CHUNK = 100;
const FIELD_LIMIT = 300;
const SHOWN = 200;

const LABELS = { Match: "Match", Review: "Possible match", Allowed: "Possible match, allowed", NoMatch: "No match", NotChecked: "Not checked" };
const SYMBOLS = { Match: "●", Review: "▲", Allowed: "◇", NoMatch: "✓", NotChecked: "–" };
const OVERALL = { Match: "Possible match found", Review: "Review needed", NoMatch: "No match found", Incomplete: "Incomplete", Error: "Not screened" };
const KINDS = { Receiver: "Receiver", Company: "Company", Address: "Address" };

const $ = (id) => document.getElementById(id);
const stateBox = $("state");

// The file as read: column names and data rows (arrays of strings), and the separator it used.
let table = null;
// The run's results, by row: { row, reference, values: {receiver, company, address}, outcome, entries, errors }.
let results = [];
let headers = [];
let aborter = null;

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

// "1 row", "2 rows".
function rows(count, one, many) {
  return `${count.toLocaleString()} ${count === 1 ? one : many}`;
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

// ---- Reading the file ----

// The file's text: a byte order mark decides; otherwise UTF-8 if it is valid UTF-8, else Windows-1251 (the usual
// encoding of a Cyrillic spreadsheet saved as CSV). The encoding can also be chosen.
function decode(buffer, choice) {
  const bytes = new Uint8Array(buffer);
  if (choice !== "auto") return { text: new TextDecoder(choice).decode(bytes), encoding: choice };
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) return { text: new TextDecoder("utf-8").decode(bytes), encoding: "utf-8" };
  if (bytes[0] === 0xff && bytes[1] === 0xfe) return { text: new TextDecoder("utf-16le").decode(bytes), encoding: "utf-16le" };
  if (bytes[0] === 0xfe && bytes[1] === 0xff) return { text: new TextDecoder("utf-16be").decode(bytes), encoding: "utf-16be" };
  try {
    return { text: new TextDecoder("utf-8", { fatal: true }).decode(bytes), encoding: "utf-8" };
  } catch {
    return { text: new TextDecoder("windows-1251").decode(bytes), encoding: "windows-1251" };
  }
}

// The separator: the one of comma, semicolon and tab that occurs most in the first line (outside quotes).
function separatorOf(text) {
  const counts = { ",": 0, ";": 0, "\t": 0 };
  let quoted = false;
  for (const c of text) {
    if (c === '"') quoted = !quoted;
    else if (!quoted && (c === "\n" || c === "\r")) break;
    else if (!quoted && c in counts) counts[c] += 1;
  }
  const [best, count] = Object.entries(counts).sort((a, b) => b[1] - a[1])[0];
  return count > 0 ? best : ",";
}

// RFC 4180: fields separated by the separator, quoted with double quotes ("" inside a quoted field is one quote),
// records ending in CRLF or LF, a quoted field may hold either. Blank lines are left out.
function parseCsv(text, separator) {
  const rows = [];
  let row = [];
  let field = "";
  let quoted = false;
  let started = false;
  for (let i = 0; i < text.length; i += 1) {
    const c = text[i];
    if (quoted) {
      if (c === '"') {
        if (text[i + 1] === '"') {
          field += '"';
          i += 1;
        } else {
          quoted = false;
        }
      } else {
        field += c;
      }
      continue;
    }
    if (c === '"' && field.length === 0) {
      quoted = true;
      started = true;
    } else if (c === separator) {
      row.push(field);
      field = "";
      started = true;
    } else if (c === "\n" || c === "\r") {
      if (c === "\r" && text[i + 1] === "\n") i += 1;
      row.push(field);
      if (started || row.some((value) => value !== "")) rows.push(row);
      row = [];
      field = "";
      started = false;
    } else {
      field += c;
      started = true;
    }
  }
  if (started || field !== "" || row.length > 0) {
    row.push(field);
    if (row.some((value) => value !== "")) rows.push(row);
  }
  return rows;
}

async function readFile() {
  const file = $("file").files[0];
  $("mapping-panel").hidden = true;
  $("result").hidden = true;
  showState("");
  table = null;
  if (!file) return;
  if (file.size > MAX_BYTES) {
    showState(`The file is ${(file.size / 1048576).toFixed(1)} MB: up to ${MAX_BYTES / 1048576} MB here. Split it, or use the stream API.`, true);
    return;
  }
  const { text, encoding } = decode(await file.arrayBuffer(), $("encoding").value);
  const clean = text.replace(/^﻿/, "");
  const separator = separatorOf(clean);
  const rows = parseCsv(clean, separator);
  if (rows.length === 0) {
    showState("The file has no rows.", true);
    return;
  }
  const width = Math.max(...rows.map((row) => row.length));
  const hasHeader = $("has-header").checked;
  const names = hasHeader
    ? Array.from({ length: width }, (_, i) => (rows[0][i] || "").trim() || `Column ${i + 1}`)
    : Array.from({ length: width }, (_, i) => `Column ${i + 1}`);
  const data = hasHeader ? rows.slice(1) : rows;
  if (data.length > MAX_ROWS) {
    showState(`The file has ${data.length.toLocaleString()} rows: up to ${MAX_ROWS.toLocaleString()} here. Split it, or use the stream API.`, true);
    return;
  }
  table = { names, data, separator, firstRow: hasHeader ? 2 : 1 };
  const separatorName = { ",": "commas", ";": "semicolons", "\t": "tabs" }[separator];
  $("file-note").textContent = `${data.length.toLocaleString()} rows, ${width} columns, separated by ${separatorName}, read as ${encoding.toUpperCase()}.`;
  showMapping();
}

// ---- Choosing the columns ----

const GUESSES = {
  company: /company|firm|organi[sz]ation|business|shipper|consignor|carrier|компан|фирм|организац|підприєм|організац|firma|unternehmen|spółka|şirket/i,
  receiver: /receiver|recipient|consignee|contact|person|name|получател|отримувач|одержувач|контакт|empfänger|odbiorca|alıcı/i,
  address: /address|street|city|town|country|post.?code|zip|region|адрес|улиц|город|страна|адреса|вулиц|місто|країн|adres|anschrift|straße|strasse|ort|land|ulica|miasto|kraj/i,
};

function guess(kind, taken) {
  const index = table.names.findIndex((name, i) => !taken.includes(i) && GUESSES[kind].test(name));
  return index;
}

function showMapping() {
  const options = (selected) => [
    el("option", { value: "-1", selected: selected === -1 }, "(not screened)"),
    ...table.names.map((name, i) => el("option", { value: String(i), selected: selected === i }, name)),
  ];
  const company = guess("company", []);
  const receiver = guess("receiver", [company]);
  $("map-company").replaceChildren(...options(company));
  $("map-receiver").replaceChildren(...options(receiver));
  const addressColumns = table.names.map((_, i) => i).filter((i) => i !== company && i !== receiver && GUESSES.address.test(table.names[i]));
  $("map-address").replaceChildren(...table.names.map((name, i) => el("label", { class: "check" },
    el("input", { type: "checkbox", value: String(i), checked: addressColumns.includes(i), onchange: showPreview }), name)));
  $("mapping-panel").hidden = false;
  showPreview();
}

function mapping() {
  return {
    receiver: Number($("map-receiver").value),
    company: Number($("map-company").value),
    address: [...$("map-address").querySelectorAll("input:checked")].map((input) => Number(input.value)),
  };
}

// A row's values as they'll be screened: trimmed, the address columns joined with commas.
function valuesOf(row, map) {
  const cell = (i) => (i >= 0 && i < row.length ? row[i].trim() : "");
  return {
    receiver: cell(map.receiver),
    company: cell(map.company),
    address: map.address.map(cell).filter((value) => value !== "").join(", "),
  };
}

function showPreview() {
  const map = mapping();
  $("preview").querySelector("thead").replaceChildren(el("tr", {},
    el("th", { scope: "col" }, "Row"), el("th", { scope: "col" }, "Receiver"), el("th", { scope: "col" }, "Company"), el("th", { scope: "col" }, "Address")));
  $("preview").querySelector("tbody").replaceChildren(...table.data.slice(0, 5).map((row, i) => {
    const values = valuesOf(row, map);
    return el("tr", {}, el("td", {}, table.firstRow + i), el("td", {}, values.receiver), el("td", {}, values.company), el("td", {}, values.address));
  }));
  const none = map.receiver < 0 && map.company < 0 && map.address.length === 0;
  $("screen-button").disabled = none;
  if (none) showState("Choose at least one column to screen.");
  else showState("");
}

$("map-receiver").addEventListener("change", showPreview);
$("map-company").addEventListener("change", showPreview);

// ---- Screening ----

function randomId() {
  const bytes = new Uint8Array(4);
  crypto.getRandomValues(bytes);
  return [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

const pause = (ms, signal) => new Promise((resolve, reject) => {
  const timer = setTimeout(resolve, ms);
  signal.addEventListener("abort", () => {
    clearTimeout(timer);
    reject(new DOMException("Stopped", "AbortError"));
  }, { once: true });
});

function retryAfter(response) {
  const seconds = Number(response.headers.get("Retry-After"));
  return Number.isFinite(seconds) && seconds > 0 ? seconds * 1000 : 1000;
}

// Sends one chunk of records, and whatever of it isn't answered again (the stream's limits, or a broken
// connection), until every record has a result or an error. Busy and warming-up answers are waited out.
async function screenChunk(records, signal, onAnswer) {
  let pending = records;
  let failures = 0;
  while (pending.length > 0) {
    let response;
    try {
      response = await fetch("/api/v1/screenings/stream", {
        method: "POST",
        headers: { "Content-Type": "application/x-ndjson", Accept: "application/x-ndjson" },
        body: pending.map((record) => JSON.stringify(record.body)).join("\n") + "\n",
        signal,
      });
    } catch (error) {
      if (error.name === "AbortError") throw error;
      if (++failures > 3) throw new Error("The connection to the API failed.");
      await pause(2000 * failures, signal);
      continue;
    }
    if (response.status === 429 || response.status === 503) {
      showState(response.status === 503 ? "The lists are loading or the service is busy: waiting…" : "Another batch is running: waiting for it…");
      await pause(response.status === 503 ? Math.max(retryAfter(response), 3000) : retryAfter(response), signal);
      continue;
    }
    if (!response.ok) throw new Error(`The API answered ${response.status}.`);
    showState("");
    const lines = (await response.text()).split("\n").filter((line) => line.trim() !== "");
    let answered = 0;
    let end = null;
    for (const line of lines) {
      const message = JSON.parse(line);
      if (message.type === "header") headers.push(message);
      else if (message.type === "result" || message.type === "error") {
        const record = pending[message.line - 1];
        if (record) onAnswer(record, message, headers.length - 1);
        answered = Math.max(answered, message.line);
      } else if (message.type === "end") end = message;
    }
    // The stream stopped early (its record or time limit: nextLine), or broke (no end line): send the rest again,
    // from the first line without an answer.
    const next = end ? (end.truncated && end.nextLine ? end.nextLine - 1 : pending.length) : answered;
    if (!end && ++failures > 3) throw new Error("The stream kept breaking off.");
    pending = pending.slice(next);
  }
}

async function screen() {
  const map = mapping();
  const batch = randomId();
  results = [];
  headers = [];
  const records = [];
  let skipped = 0;
  table.data.forEach((row, i) => {
    const values = valuesOf(row, map);
    const number = table.firstRow + i;
    if (!values.receiver && !values.company && !values.address) {
      skipped += 1;
      return;
    }
    const reference = `manifest-${batch}-${number}`;
    const body = { reference };
    if (values.receiver) body.receiverName = values.receiver;
    if (values.company) body.companyName = values.company;
    if (values.address) body.address = values.address;
    const result = { row: number, reference, values, outcome: null, entries: [], errors: null, snapshot: -1 };
    results.push(result);
    records.push({ body, result });
  });

  aborter = new AbortController();
  $("screen-button").disabled = true;
  $("cancel-button").hidden = false;
  $("result").hidden = true;
  $("progress-panel").hidden = false;
  const progress = $("progress");
  progress.max = Math.max(records.length, 1);
  let done = 0;
  const started = Date.now();
  const report = () => {
    progress.value = done;
    const seconds = (Date.now() - started) / 1000;
    const rate = seconds > 0 ? done / seconds : 0;
    const left = rate > 0 ? Math.round((records.length - done) / rate) : null;
    $("progress-text").textContent = `${done.toLocaleString()} of ${records.length.toLocaleString()} rows screened`
      + (skipped ? ` (${rows(skipped, "empty row", "empty rows")} skipped)` : "")
      + (left !== null && done < records.length ? `, about ${left < 90 ? `${left} s` : `${Math.round(left / 60)} min`} left.` : ".");
  };
  report();
  try {
    for (let i = 0; i < records.length; i += CHUNK) {
      await screenChunk(records.slice(i, i + CHUNK), aborter.signal, (record, message, snapshot) => {
        if (record.result.outcome === null) done += 1;
        record.result.snapshot = snapshot;
        if (message.type === "error") {
          record.result.outcome = "Error";
          record.result.errors = message.errors;
        } else {
          record.result.outcome = message.outcome;
          record.result.entries = message.entries;
        }
        report();
      });
    }
    showResults(skipped, false);
  } catch (error) {
    if (error.name === "AbortError") {
      showState(`Stopped after ${done.toLocaleString()} rows. The results so far are below.`);
      showResults(skipped, true);
    } else {
      showState(`${error.message} The results so far are below; screen the file again to finish it.`, true);
      showResults(skipped, true);
    }
  } finally {
    aborter = null;
    $("screen-button").disabled = false;
    $("cancel-button").hidden = true;
    $("progress-panel").hidden = true;
  }
}

$("screen-button").addEventListener("click", () => {
  if (table) screen();
});
$("cancel-button").addEventListener("click", () => {
  if (aborter) aborter.abort();
});

// ---- Results ----

function cellState(cell) {
  return cell.state === "NotChecked" ? "NotChecked" : cell.allowed ? "Allowed" : cell.outcome;
}

// What a row found in one list: its entries' Match and Review cells, worst first; otherwise "No match" or "Not
// checked". Never "cleared": no match is only that, in that list as of its date.
function foundIn(result, code) {
  const cells = result.entries.flatMap((entry) => entry.cells.filter((cell) => cell.list === code).map((cell) => ({ entry, cell })));
  if (cells.length === 0) return null;
  const hits = cells.filter(({ cell }) => cell.state !== "NotChecked" && (cell.outcome === "Match" || cell.outcome === "Review"));
  const rank = { Match: 0, Review: 1 };
  hits.sort((a, b) => rank[a.cell.outcome] - rank[b.cell.outcome] || (b.cell.percent || 0) - (a.cell.percent || 0));
  if (hits.length > 0) {
    return hits.map(({ entry, cell }) => {
      const state = cellState(cell);
      const name = cell.best ? cell.best.listedName : "";
      return `${SYMBOLS[state]} ${LABELS[state]} ${cell.percent}%: ${KINDS[entry.kind] || entry.kind}${name ? `, ${name}` : ""}`;
    }).join("; ");
  }
  return cells.some(({ cell }) => cell.state === "NotChecked") ? `${SYMBOLS.NotChecked} ${LABELS.NotChecked}` : `${SYMBOLS.NoMatch} ${LABELS.NoMatch}`;
}

function listsOf(snapshot) {
  return snapshot >= 0 && headers[snapshot] ? headers[snapshot].lists : [];
}

function showResults(skipped, partial) {
  const screened = results.filter((result) => result.outcome !== null);
  const count = (outcome) => screened.filter((result) => result.outcome === outcome).length;
  const worst = count("Match") ? "Match" : count("Review") ? "Review" : count("Incomplete") || count("Error") ? "Incomplete" : "NoMatch";
  const lists = listsOf(headers.length - 1);
  const asOf = lists.filter((list) => list.state !== "SwitchedOff").map((list) => `${list.name}, as of ${utc(list.asOf)}`).join("; ");
  const parts = [
    `${rows(screened.length, "row", "rows")} screened${partial ? ` of ${results.length.toLocaleString()}` : ""}`,
    `${count("Match").toLocaleString()} with a match`,
    `${count("Review").toLocaleString()} to review`,
    count("Incomplete") ? `${count("Incomplete").toLocaleString()} incomplete (a list couldn't be checked)` : null,
    count("Error") ? `${count("Error").toLocaleString()} not screened (an error)` : null,
    skipped ? `${rows(skipped, "empty row", "empty rows")} skipped` : null,
  ].filter(Boolean);
  const noMatch = count("NoMatch");
  const summary = $("summary");
  summary.className = `summary ${worst}`;
  summary.replaceChildren(
    el("span", { class: "badge" }, OVERALL[worst]),
    el("span", {}, `${parts.join(", ")}. No match found for the other ${noMatch.toLocaleString()} in: ${asOf || "the lists"}.`,
      headers.length > 1 && new Set(headers.map((header) => header.lists.map((list) => `${list.code}:${list.version}`).join(","))).size > 1
        ? " A list was updated while the file was screened: each row's list versions are in the results file."
        : null));

  const flagged = screened.filter((result) => result.outcome !== "NoMatch");
  $("flagged-title").textContent = flagged.length === 0
    ? "No row needs a look."
    : flagged.length > SHOWN ? `The first ${SHOWN} of ${flagged.length.toLocaleString()} rows that need a look (all are in the results file)` : "Rows that need a look";
  $("flagged").hidden = flagged.length === 0;
  $("flagged").querySelector("tbody").replaceChildren(...flagged.slice(0, SHOWN).map((result) => {
    const found = result.outcome === "Error"
      ? Object.entries(result.errors || {}).map(([field, messages]) => `${field}: ${[].concat(messages).join(" ")}`).join("; ")
      : listsOf(result.snapshot).map((list) => {
        const text = foundIn(result, list.code);
        return text && !text.startsWith(SYMBOLS.NoMatch) ? `${list.name}: ${text}` : null;
      }).filter(Boolean).join("\n");
    return el("tr", {},
      el("td", {}, result.row),
      el("td", { class: "reference" }, result.reference),
      el("td", { class: `outcome ${result.outcome}` }, OVERALL[result.outcome] || result.outcome),
      el("td", { class: "found" }, found));
  }));
  $("disclaimer").textContent = headers.length > 0 ? headers[headers.length - 1].disclaimer : "";
  $("result").hidden = screened.length === 0;
}

// ---- The results file ----

// A CSV field: quoted when it holds the separator, a quote or a line break; a value that a spreadsheet would read as a
// formula (=, +, -, @, tab, carriage return first) gets a leading apostrophe, so a listed name can't run as one.
function csvField(value, separator) {
  let text = value === null || value === undefined ? "" : String(value);
  if (/^[=+\-@\t\r]/.test(text)) text = `'${text}`;
  return /["\r\n]/.test(text) || text.includes(separator) ? `"${text.replace(/"/g, '""')}"` : text;
}

function download() {
  const separator = table.separator;
  const lists = listsOf(headers.length - 1);
  const snapshots = new Set(results.map((result) => result.snapshot).filter((snapshot) => snapshot >= 0));
  const line = (fields) => fields.map((field) => csvField(field, separator)).join(separator);
  const rows = [line([
    "Row", "Reference", "Result", "Receiver", "Company", "Address",
    ...lists.map((list) => `${list.name} (${list.code})`),
    ...(snapshots.size > 1 ? ["List versions"] : []),
  ])];
  for (const result of results) {
    const outcome = result.outcome === null ? "Not screened (stopped)" : OVERALL[result.outcome] || result.outcome;
    const cells = result.outcome === "Error"
      ? lists.map(() => Object.entries(result.errors || {}).map(([field, messages]) => `${field}: ${[].concat(messages).join(" ")}`).join("; "))
      : lists.map((list) => (result.outcome === null ? "" : foundIn(result, list.code) || "Not searched"));
    rows.push(line([
      result.row, result.reference, outcome, result.values.receiver, result.values.company, result.values.address, ...cells,
      ...(snapshots.size > 1 ? [listsOf(result.snapshot).map((list) => `${list.code} ${list.version}`).join(" ")] : []),
    ]));
  }
  const header = headers[headers.length - 1];
  rows.push("", line(["Lists as searched"]), line(["List", "Code", "Version", "As of", "State"]));
  for (const list of lists) rows.push(line([list.name, list.code, list.version, utc(list.asOf), list.state]));
  rows.push("", line(["Matcher version", header ? header.matcherVersion : ""]), line(["Screened", new Date().toISOString()]));
  rows.push(line([header ? header.disclaimer : ""]));
  // A byte order mark, so a spreadsheet reads the file as UTF-8.
  const blob = new Blob(["﻿" + rows.join("\r\n") + "\r\n"], { type: "text/csv;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const a = el("a", { href: url, download: `screening-results-${new Date().toISOString().slice(0, 10)}.csv` });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

$("download-button").addEventListener("click", download);
$("file").addEventListener("change", readFile);
$("encoding").addEventListener("change", readFile);
$("has-header").addEventListener("change", readFile);
$("max-rows").textContent = MAX_ROWS.toLocaleString();
