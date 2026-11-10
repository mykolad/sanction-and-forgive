//! Logs as the reference writes them outside Development: one JSON object per line on standard output. Never the
//! entered text: callers pass references, codes and counts only.

use std::io::Write;

use serde_json::{Map, Value};

#[derive(Clone, Copy, Default)]
pub struct Logger;

impl Logger {
    fn write(&self, level: &str, message: &str, fields: &[(&str, Value)]) {
        let mut line = Map::new();
        line.insert("Timestamp".into(), Value::String(chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Nanos, true)));
        line.insert("LogLevel".into(), Value::String(level.into()));
        line.insert("Message".into(), Value::String(message.into()));
        for (key, value) in fields {
            line.insert((*key).into(), value.clone());
        }
        let mut out = std::io::stdout().lock();
        let _ = writeln!(out, "{}", Value::Object(line));
    }

    pub fn info(&self, message: &str, fields: &[(&str, Value)]) {
        self.write("INFO", message, fields);
    }

    pub fn warn(&self, message: &str, fields: &[(&str, Value)]) {
        self.write("WARN", message, fields);
    }

    pub fn error(&self, message: &str, fields: &[(&str, Value)]) {
        self.write("ERROR", message, fields);
    }
}
