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
  $("screen-button").disabled = none || aborter !== null;
  if (none) showState("Choose at least one column to screen.");
  else if (aborter === null) showState("");
}

$("map-receiver").addEventListener("change", showPreview);
$("map-company").addEventListener("change", showPreview);

// While rows are being screened, nothing that decides them can change: the file, its reading, the columns.
function lock(locked) {
  for (const id of ["file", "encoding", "has-header", "map-receiver", "map-company"]) $(id).disabled = locked;
  for (const input of $("map-address").querySelectorAll("input")) input.disabled = locked;
}

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

// The NDJSON lines of a response as they arrive, each parsed. A body that breaks off, or ends inside a line, throws
// after the complete lines before it: their answers count.
async function* ndjson(response) {
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffered = "";
  for (;;) {
    const { done, value } = await reader.read();
    buffered += done ? decoder.decode() : decoder.decode(value, { stream: true });
    let end;
    while ((end = buffered.indexOf("\n")) >= 0) {
      const line = buffered.slice(0, end).trim();
      buffered = buffered.slice(end + 1);
      if (line !== "") yield JSON.parse(line);
    }
    if (done) {
      if (buffered.trim() !== "") throw new Error("The answer ended inside a line.");
      return;
    }
  }
}

// Sends one chunk of records, and whatever of it isn't answered again (the stream's limits, or a connection or answer
// that broke off), until every record has a result or an error. Busy and warming-up answers are waited out.
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
    let answered = 0;
    let end = null;
    let snapshot = -1;
    try {
      for await (const message of ndjson(response)) {
        if (message.type === "header") {
          headers.push(message);
          snapshot = headers.length - 1;
        } else if (message.type === "result" || message.type === "error") {
          const record = pending[message.line - 1];
          if (record) onAnswer(record, message, snapshot);
          answered = Math.max(answered, message.line);
        } else if (message.type === "end") {
          end = message;
        }
      }
    } catch (error) {
      if (error.name === "AbortError") throw error;
      // Broke off: the answers so far stand; the rest goes again below.
      end = null;
    }
    // The stream stopped early (its record or time limit: nextLine), or broke (no end line): send the rest again,
    // from the first line without an answer.
    const next = end ? (end.truncated && end.nextLine ? end.nextLine - 1 : pending.length) : answered;
    if (!end && ++failures > 3) throw new Error("The stream kept breaking off.");
    pending = pending.slice(next);
  }
}

async function screen() {
  if (aborter !== null || !table) return;
  const map = mapping();
  const batch = randomId();
  results = [];
  headers = [];
  const records = [];
  table.data.forEach((row, i) => {
    const values = valuesOf(row, map);
    const number = table.firstRow + i;
    const result = { row: number, reference: null, values, outcome: null, entries: [], errors: null, snapshot: -1, skipped: false };
    results.push(result);
    if (!values.receiver && !values.company && !values.address) {
      // Kept, so the results file has every row: marked skipped, never sent.
      result.skipped = true;
      return;
    }
    result.reference = `manifest-${batch}-${number}`;
    const body = { reference: result.reference };
    if (values.receiver) body.receiverName = values.receiver;
    if (values.company) body.companyName = values.company;
    if (values.address) body.address = values.address;
    records.push({ body, result });
  });

  aborter = new AbortController();
  lock(true);
  $("screen-button").disabled = true;
  $("cancel-button").hidden = false;
  $("result").hidden = true;
  $("progress-panel").hidden = false;
  const progress = $("progress");
  progress.max = Math.max(records.length, 1);
  let done = 0;
  const skipped = results.length - records.length;
  const started = Date.now();
  const report = () => {
    progress.value = done;
    const seconds = (Date.now() - started) / 1000;
    const rate = seconds > 0 ? done / seconds : 0;
    const left = rate > 0 ? Math.round((records.length - done) / rate) : null;
    $("progress-text").textContent = `${done.toLocaleString()} of ${records.length.toLocaleString()} rows screened`
      + (skipped ? ` (${rows(skipped, "row", "rows")} with nothing to screen skipped)` : "")
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
    showResults();
  } catch (error) {
    if (error.name === "AbortError") {
      showState(`Stopped after ${rows(done, "row", "rows")}. The results so far are below; the rest weren't screened.`);
    } else {
      showState(`${error.message} The results so far are below; screen the file again to finish it.`, true);
    }
    showResults();
  } finally {
    aborter = null;
    lock(false);
    $("cancel-button").hidden = true;
    $("progress-panel").hidden = true;
    showPreview();
  }
}

$("screen-button").addEventListener("click", screen);
$("cancel-button").addEventListener("click", () => {
  if (aborter) aborter.abort();
});

// ---- Results ----

function cellState(cell) {
  return cell.state === "NotChecked" ? "NotChecked" : cell.allowed ? "Allowed" : cell.outcome;
}

function percentText(percent) {
  if (percent === null || percent === undefined) return "";
  return percent < 1 ? "<1%" : `${percent}%`;
}

// What a row found in one list: its entries' Match and Review cells (allowed ones too, so marked), worst first; else
// "No match" with the highest percentage, or "Not checked". Never "cleared": no match is only that, in that list as
// of its date. Null when the list has no cells (switched off).
function foundIn(result, code) {
  const cells = result.entries.flatMap((entry) => entry.cells.filter((cell) => cell.list === code).map((cell) => ({ entry, cell })));
  if (cells.length === 0) return null;
  const hits = cells.filter(({ cell }) => cell.state !== "NotChecked" && (cell.outcome === "Match" || cell.outcome === "Review"));
  const rank = { Match: 0, Review: 1 };
  hits.sort((a, b) => rank[a.cell.outcome] - rank[b.cell.outcome] || Number(a.cell.allowed) - Number(b.cell.allowed) || (b.cell.percent || 0) - (a.cell.percent || 0));
  if (hits.length > 0) {
    return hits.map(({ entry, cell }) => {
      const state = cellState(cell);
      const name = cell.best ? cell.best.listedName : "";
      return `${SYMBOLS[state]} ${LABELS[state]} ${percentText(cell.percent)}: ${KINDS[entry.kind] || entry.kind}${name ? `, ${name}` : ""}`;
    }).join("; ");
  }
  if (cells.some(({ cell }) => cell.state === "NotChecked")) return `${SYMBOLS.NotChecked} ${LABELS.NotChecked}`;
  const highest = Math.max(...cells.map(({ cell }) => cell.percent || 0));
  return `${SYMBOLS.NoMatch} ${LABELS.NoMatch} ${percentText(highest)}`;
}

// Whether a row has possible matches the allow-list allows (its outcome stays NoMatch).
function hasAllowed(result) {
  return result.entries.some((entry) => entry.cells.some((cell) => cell.allowed && cell.outcome === "Review"));
}

function listsOf(snapshot) {
  return snapshot >= 0 && headers[snapshot] ? headers[snapshot].lists : [];
}

// The snapshots the results were screened against, in order: usually one; more if a list was updated mid-run.
function usedSnapshots() {
  return [...new Set(results.map((result) => result.snapshot).filter((snapshot) => snapshot >= 0))].sort((a, b) => a - b);
}

// What a snapshot searched, as the single screening's summary words it: the no-match scope is only the lists
// searched; overdue, not-checked and switched-off lists are said so.
function coverage(snapshot) {
  const lists = listsOf(snapshot);
  const name = (list) => `${list.name}, as of ${utc(list.asOf)}`;
  return {
    searched: lists.filter((list) => list.state === "Current" || list.state === "Overdue").map(name),
    notices: [
      lists.some((list) => list.state === "Overdue")
        ? `Warning: couldn't be updated within their allowed age, so searched as last downloaded: ${lists.filter((list) => list.state === "Overdue").map(name).join("; ")}.`
        : null,
      lists.some((list) => list.state === "NotChecked") ? `Not checked: ${lists.filter((list) => list.state === "NotChecked").map((list) => list.name).join("; ")}.` : null,
      lists.some((list) => list.state === "SwitchedOff")
        ? `Not searched: ${lists.filter((list) => list.state === "SwitchedOff").map((list) => list.name).join("; ")} (switched off by an administrator).`
        : null,
    ].filter(Boolean),
  };
}

function showResults() {
  const screened = results.filter((result) => !result.skipped && result.outcome !== null);
  const pending = results.filter((result) => !result.skipped && result.outcome === null).length;
  const skipped = results.filter((result) => result.skipped).length;
  const count = (outcome) => screened.filter((result) => result.outcome === outcome).length;
  const allowed = screened.filter((result) => result.outcome === "NoMatch" && hasAllowed(result)).length;
  // Match and Review first; otherwise anything not screened (stopped, failed, an error, a list not checked) makes the
  // batch incomplete: never a no-match badge for rows nobody screened.
  const worst = count("Match") ? "Match" : count("Review") ? "Review" : count("Incomplete") || count("Error") || pending ? "Incomplete" : "NoMatch";
  const snapshots = usedSnapshots();
  const latest = coverage(snapshots.length ? snapshots[snapshots.length - 1] : -1);
  const parts = [
    `${rows(screened.length, "row", "rows")} screened`,
    `${count("Match").toLocaleString()} with a match`,
    `${count("Review").toLocaleString()} to review`,
    count("Incomplete") ? `${count("Incomplete").toLocaleString()} incomplete (a list couldn't be checked)` : null,
    count("Error") ? `${count("Error").toLocaleString()} not screened (an error)` : null,
    pending ? `${rows(pending, "row", "rows")} not screened (stopped)` : null,
    skipped ? `${rows(skipped, "row", "rows")} with nothing to screen` : null,
  ].filter(Boolean);
  const noMatch = count("NoMatch");
  const summary = $("summary");
  summary.className = `summary ${worst}`;
  summary.replaceChildren(
    el("span", { class: "badge" }, OVERALL[worst]),
    el("span", {},
      `${parts.join(", ")}. `,
      noMatch
        ? `No match found for the other ${noMatch.toLocaleString()}${allowed ? ` (${allowed.toLocaleString()} of them only with possible matches the allow-list allows)` : ""} in: ${latest.searched.join("; ") || "no list"}. `
        : null,
      latest.notices.join(" "),
      snapshots.length > 1 ? " A list was updated while the file was screened: each row's list versions are in the results file." : null));

  const flagged = screened.filter((result) => result.outcome !== "NoMatch" || hasAllowed(result));
  $("flagged-title").textContent = flagged.length === 0
    ? "No row needs a look."
    : flagged.length > SHOWN ? `The first ${SHOWN} of ${flagged.length.toLocaleString()} rows that need a look (all are in the results file)` : "Rows that need a look";
  $("flagged").hidden = flagged.length === 0;
  $("flagged").querySelector("tbody").replaceChildren(...flagged.slice(0, SHOWN).map((result) => {
    const found = result.outcome === "Error"
      ? errorText(result)
      : listsOf(result.snapshot).map((list) => {
        const text = foundIn(result, list.code);
        return text && !text.startsWith(SYMBOLS.NoMatch) ? `${list.name}: ${text}` : null;
      }).filter(Boolean).join("\n");
    const label = result.outcome === "NoMatch" ? `${OVERALL.NoMatch} (allowed possible match)` : OVERALL[result.outcome] || result.outcome;
    return el("tr", {},
      el("td", {}, result.row),
      el("td", { class: "reference" }, result.reference),
      el("td", { class: `outcome ${result.outcome}` }, label),
      el("td", { class: "found" }, found));
  }));
  $("disclaimer").textContent = headers.length > 0 ? headers[headers.length - 1].disclaimer : "";
  $("result").hidden = screened.length === 0 && pending === 0;
}

function errorText(result) {
  return Object.entries(result.errors || {}).map(([field, messages]) => `${field || "Record"}: ${[].concat(messages).join(" ")}`).join("; ");
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
  const snapshots = usedSnapshots();
  // A column per list any snapshot used, in the latest's order, then any only an earlier one had.
  const columns = [];
  for (const snapshot of [...snapshots].reverse()) {
    for (const list of listsOf(snapshot)) if (!columns.some((column) => column.code === list.code)) columns.push(list);
  }
  const line = (fields) => fields.map((field) => csvField(field, separator)).join(separator);
  const output = [line(["Row", "Reference", "Result", "Receiver", "Company", "Address", ...columns.map((list) => `${list.name} (${list.code})`), "Lists as of"])];
  for (const result of results) {
    let outcome;
    if (result.skipped) outcome = "Skipped: nothing to screen";
    else if (result.outcome === null) outcome = "Not screened (stopped)";
    else if (result.outcome === "NoMatch" && hasAllowed(result)) outcome = `${OVERALL.NoMatch} (allowed possible match)`;
    else outcome = OVERALL[result.outcome] || result.outcome;
    const searched = new Set(listsOf(result.snapshot).map((list) => list.code));
    const cells = columns.map((list) => {
      if (result.skipped || result.outcome === null) return "";
      if (result.outcome === "Error") return errorText(result);
      if (!searched.has(list.code)) return "";
      return foundIn(result, list.code) || "Not searched (switched off)";
    });
    output.push(line([result.row, result.reference, outcome, result.values.receiver, result.values.company, result.values.address, ...cells,
      result.snapshot >= 0 ? `snapshot ${snapshots.indexOf(result.snapshot) + 1}` : ""]));
  }
  output.push("", line(["Lists as searched"]), line(["Snapshot", "List", "Code", "Version", "As of", "State", "Matcher version"]));
  snapshots.forEach((snapshot, i) => {
    for (const list of listsOf(snapshot)) {
      output.push(line([`snapshot ${i + 1}`, list.name, list.code, list.version, utc(list.asOf), list.state, headers[snapshot].matcherVersion]));
    }
  });
  output.push("", line(["Screened", new Date().toISOString()]));
  output.push(line([headers.length ? headers[headers.length - 1].disclaimer : ""]));
  // A byte order mark, so a spreadsheet reads the file as UTF-8.
  const blob = new Blob(["﻿" + output.join("\r\n") + "\r\n"], { type: "text/csv;charset=utf-8" });
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
