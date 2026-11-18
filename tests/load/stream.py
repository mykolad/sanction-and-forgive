"""A batch through the stream endpoint (POST /api/v1/screenings/stream), as a client sends one: N records built from
tests/load/inputs.csv (repeated, each with its own reference), sent as NDJSON while the answers are read; when a stream
stops at its limit (Screening:StreamMaxRecords or StreamMaxSeconds), the next one resumes from its nextLine. Prints,
per stream and in all, the records screened, errors, the time to the first result and records a second.
docs/load-test.md.

    python -I tests/load/stream.py https://<the app's address> 50000

Only the standard library. Sending and reading run together on one asyncio connection: a client that sends the whole
batch before reading (or reads and writes one TLS socket from two threads) stalls once the buffers between them fill.
Each record is screened and audited like a single screening (one audit line each), so a large batch logs a lot:
docs/load-test.md, "How to run it"."""
import asyncio
import csv
import json
import os
import ssl
import sys
import time
import urllib.parse


def records(count: int) -> list[bytes]:
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'inputs.csv')
    with open(path, encoding='utf-8', newline='') as file:
        rows = [row for row in csv.DictReader(file)]
    lines = []
    for i in range(count):
        row = rows[i % len(rows)]
        # load-: the operations workbook and the Incomplete alert tell load tests by this prefix (#81).
        record = {'reference': f'load-batch-{i + 1}'}
        for field in ('receiverName', 'companyName', 'address'):
            if row.get(field):
                record[field] = row[field]
        lines.append(json.dumps(record, ensure_ascii=False).encode('utf-8'))
    return lines


async def body_lines(reader: asyncio.StreamReader, chunked: bool):
    """The response body's lines, decoding chunked transfer encoding."""
    if not chunked:
        while line := await reader.readline():
            yield line
        return
    pending = b''
    while True:
        size = int((await reader.readline()).split(b';')[0].strip() or b'0', 16)
        if size == 0:
            break
        pending += await reader.readexactly(size)
        await reader.readexactly(2)
        *complete, pending = pending.split(b'\n')
        for line in complete:
            yield line
    if pending:
        yield pending


async def stream(base: urllib.parse.SplitResult, lines: list[bytes]) -> dict:
    """One stream: sends the lines while reading every answer line. Returns what its end line says, and timings."""
    secure = base.scheme == 'https'
    host, port = base.hostname, base.port or (443 if secure else 80)
    reader, writer = await asyncio.open_connection(host, port, ssl=ssl.create_default_context() if secure else None,
                                                   limit=1 << 20)
    body = b'\n'.join(lines) + b'\n'
    path = base.path.rstrip('/') + '/api/v1/screenings/stream'
    started = time.monotonic()
    writer.write((f'POST {path} HTTP/1.1\r\nHost: {host}\r\nContent-Type: application/x-ndjson\r\n'
                  f'Accept: application/x-ndjson\r\nContent-Length: {len(body)}\r\nConnection: close\r\n\r\n').encode())

    async def send() -> None:
        # A stream stopped at its limit stops reading: the rest can't be sent, which is expected (its end line says
        # where to resume).
        try:
            for start in range(0, len(body), 64 * 1024):
                writer.write(body[start:start + 64 * 1024])
                await writer.drain()
        except (ConnectionError, OSError):
            pass

    sending = asyncio.create_task(send())
    status = (await reader.readline()).decode('latin-1').split()
    headers = {}
    while (header := await reader.readline()) not in (b'\r\n', b'\n', b''):
        name, _, value = header.decode('latin-1').partition(':')
        headers[name.strip().lower()] = value.strip()
    if len(status) >= 2 and status[1] in ('429', '503'):
        # The replica's one stream is taken (another client's, or the previous stream's not yet released), or it's
        # busy or loading: nothing was screened, so the same records go again after Retry-After.
        sending.cancel()
        writer.close()
        retry_after = headers.get('retry-after', '1')
        return {'retry': float(retry_after) if retry_after.replace('.', '', 1).isdigit() else 1.0, 'status': status[1]}
    if len(status) < 2 or status[1] != '200':
        raise SystemExit(f'Stream: {" ".join(status)}')
    first = None
    results = errors = 0
    end = None
    async for raw in body_lines(reader, headers.get('transfer-encoding', '').lower() == 'chunked'):
        if not raw.strip():
            continue
        line = json.loads(raw)
        kind = line.get('type')
        if kind in ('result', 'error'):
            results += kind == 'result'
            errors += kind == 'error'
            first = first or time.monotonic() - started
        elif kind == 'end':
            end = line
    seconds = time.monotonic() - started
    sending.cancel()
    writer.close()
    if end is None:
        raise SystemExit(f'Stream ended without its end line after {results} results')
    return {'results': results, 'errors': errors, 'first': first, 'seconds': seconds, 'nextLine': end.get('nextLine')}


async def main() -> None:
    base = urllib.parse.urlsplit(sys.argv[1])
    count = int(sys.argv[2]) if len(sys.argv) > 2 else 50000
    lines = records(count)
    offset, streams, total_results, total_errors = 0, 0, 0, 0
    started = time.monotonic()
    while offset < len(lines):
        part = await stream(base, lines[offset:])
        if 'retry' in part:
            print(f"stream refused ({part['status']}): again in {part['retry']:g} s", flush=True)
            await asyncio.sleep(part['retry'])
            continue
        streams += 1
        total_results += part['results']
        total_errors += part['errors']
        rate = part['results'] / part['seconds'] if part['seconds'] else 0
        print(f"stream {streams}: {part['results']} screened, {part['errors']} errors in {part['seconds']:.1f} s "
              f"({rate:.1f}/s), first after {part['first'] or 0:.2f} s, next line {part['nextLine']}", flush=True)
        if not part['nextLine']:
            break
        offset += part['nextLine'] - 1
    seconds = time.monotonic() - started
    print(f'all: {total_results} screened, {total_errors} errors, {streams} streams, {seconds:.1f} s, '
          f'{total_results / seconds:.1f} records a second', flush=True)


if __name__ == '__main__':
    asyncio.run(main())
