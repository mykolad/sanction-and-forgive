//! A screening result as plain JSON arrays, the same from every port's bench (ports/go/cmd/bench writes the same):
//! comparing two dumps compares every field the API shows, before there's an API to compare.

use serde_json::{json, Value};

use crate::matching::{ScreeningResult, SubjectMatch};

fn subject(m: &SubjectMatch) -> Value {
    let words: Vec<Value> = m.words.iter().map(|w| json!([w.entered, w.listed, w.how])).collect();
    json!([m.subject.external_id, m.percent, m.outcome.as_str(), m.alias, m.alignment, m.address_state.as_str(), words, m.notes])
}

pub fn result(r: &ScreeningResult) -> Value {
    let entries: Vec<Value> = r
        .entries
        .iter()
        .map(|e| {
            let cells: Vec<Value> = e
                .cells
                .iter()
                .map(|c| {
                    let others: Vec<Value> = c.others.iter().map(subject).collect();
                    json!([c.list, c.checked, c.percent, c.outcome.map(|o| o.as_str()), c.best.as_ref().map(subject), others, c.allowed])
                })
                .collect();
            json!([e.kind.as_str(), e.input, e.compared, e.allowed_by.as_ref().map(|a| a.id), cells])
        })
        .collect();
    json!([r.outcome.as_str(), r.matcher_version, entries, r.allow_list_version])
}
