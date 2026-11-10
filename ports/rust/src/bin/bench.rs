//! Bench: builds the lists exported by the Go bench (`go run ./cmd/bench -export lists.json`), screens the load test's
//! inputs, reports the time per screening, and writes every result in the canonical form (src/canonical.rs) to
//! compare with the Go port's. A development tool: it never runs in production.
//!
//!   cargo run --release --bin bench -- --lists lists.json --inputs ../../tests/load/inputs.csv --rounds 3 --dump rust.ndjson

use std::fs::File;
use std::io::{BufRead, BufReader, BufWriter, Write};
use std::sync::Arc;
use std::time::{Duration, Instant};

use sanction_and_forgive::canonical;
use sanction_and_forgive::matching::{
    AllowList, ListIndex, ListedAddress, ListedName, ListedSubject, Options, ScreenedList, Screener, ScreeningRequest, SubjectKind,
};
use serde_json::Value;

fn argument(name: &str) -> Option<String> {
    let args: Vec<String> = std::env::args().collect();
    args.iter().position(|a| a == name).and_then(|i| args.get(i + 1).cloned())
}

fn text(value: &Value) -> String {
    value.as_str().unwrap_or_default().to_string()
}

fn load(path: &str) -> Vec<ScreenedList> {
    let mut lists = Vec::new();
    for line in BufReader::new(File::open(path).expect("the lists")).lines() {
        let list: Value = serde_json::from_str(&line.unwrap()).unwrap();
        let subjects: Vec<ListedSubject> = list["subjects"]
            .as_array()
            .unwrap()
            .iter()
            .map(|s| ListedSubject {
                external_id: text(&s[0]),
                kind: match s[1].as_i64() {
                    Some(0) => SubjectKind::Individual,
                    Some(1) => SubjectKind::Entity,
                    _ => SubjectKind::Unknown,
                },
                primary_name: text(&s[2]),
                names: s[3]
                    .as_array()
                    .unwrap()
                    .iter()
                    .map(|n| ListedName { full_name: text(&n[0]), is_weak: n[1].as_bool().unwrap() })
                    .collect(),
                addresses: s[4].as_array().unwrap().iter().map(|a| ListedAddress { raw: text(&a[0]), country_code: text(&a[1]) }).collect(),
                programme: s[5].as_str().map(str::to_string),
                source_url: s[6].as_str().map(str::to_string),
            })
            .collect();
        lists.push(ScreenedList {
            code: text(&list["code"]),
            name: text(&list["name"]),
            index: Some(Arc::new(ListIndex::build(subjects))),
        });
    }
    lists
}

/// receiverName,companyName,address,kind: every field quoted or empty (load-inputs writes it that way).
fn read_inputs(path: &str) -> Vec<ScreeningRequest> {
    let mut requests = Vec::new();
    for line in BufReader::new(File::open(path).expect("the inputs")).lines().skip(1) {
        let line = line.unwrap();
        let mut fields = Vec::new();
        let mut chars = line.chars().peekable();
        let mut field = String::new();
        let mut quoted = false;
        while let Some(c) = chars.next() {
            match c {
                '"' if quoted && chars.peek() == Some(&'"') => {
                    field.push('"');
                    chars.next();
                }
                '"' => quoted = !quoted,
                ',' if !quoted => fields.push(std::mem::take(&mut field)),
                _ => field.push(c),
            }
        }
        fields.push(field);
        if fields.len() >= 3 {
            requests.push(ScreeningRequest {
                receiver_name: fields[0].clone(),
                company_name: fields[1].clone(),
                address: fields[2].clone(),
            });
        }
    }
    requests
}

fn main() {
    let lists_path = argument("--lists").expect("--lists <the Go bench's export>");
    let inputs = argument("--inputs").unwrap_or_else(|| "../../tests/load/inputs.csv".to_string());
    let rounds: usize = argument("--rounds").map(|r| r.parse().unwrap()).unwrap_or(3);

    let started = Instant::now();
    let lists = load(&lists_path);
    let aliases: usize = lists.iter().map(|l| l.index.as_ref().unwrap().alias_count()).sum();
    println!("{} lists, {aliases} aliases, built in {:.1} s", lists.len(), started.elapsed().as_secs_f64());

    let requests = read_inputs(&inputs);
    let screener = Screener::new(Options::default());
    let allow_list = AllowList::empty();
    if let Some(dump) = argument("--dump") {
        let mut out = BufWriter::new(File::create(dump).unwrap());
        for request in &requests {
            let result = screener.screen(request, &lists, &allow_list).unwrap();
            writeln!(out, "{}", canonical::result(&result)).unwrap();
        }
    }

    let mut times: Vec<Duration> = Vec::with_capacity(requests.len() * rounds);
    let started = Instant::now();
    for _ in 0..rounds {
        for request in &requests {
            let clock = Instant::now();
            screener.screen(request, &lists, &allow_list).unwrap();
            times.push(clock.elapsed());
        }
    }
    let total = started.elapsed();
    if times.is_empty() {
        return;
    }
    times.sort();
    let ms = |d: Duration| d.as_secs_f64() * 1000.0;
    println!(
        "{} screenings: {:.2} ms mean, {:.2} ms p50, {:.2} ms p95",
        times.len(),
        ms(total) / times.len() as f64,
        ms(times[times.len() / 2]),
        ms(times[times.len() * 95 / 100])
    );
}
