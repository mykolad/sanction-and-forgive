//! The shared database (the schema the reference's EF migrations define), reached from an ADO.NET connection string
//! (ConnectionStrings__Lists, as the reference reads it): SQL authentication for tests, the managed identity in Azure,
//! the signed-in az user from tools/lists.ps1. A connection per operation: screening never touches the database, so
//! only the refresh, the admin actions and the CLI do.

use std::time::Duration;

use tiberius::{AuthMethod, Client, Config, EncryptionLevel};
use tokio::net::TcpStream;
use tokio_util::compat::{Compat, TokioAsyncWriteCompatExt};

pub type Connection = Client<Compat<TcpStream>>;
pub type DbError = Box<dyn std::error::Error + Send + Sync>;
pub type DbResult<T> = Result<T, DbError>;

#[derive(Debug, Clone, PartialEq)]
enum Auth {
    Sql { user: String, password: String },
    ManagedIdentity { client_id: Option<String> },
    Default,
}

#[derive(Debug, Clone)]
pub struct Database {
    host: String,
    port: u16,
    database: Option<String>,
    auth: Auth,
    trust_certificate: bool,
    encrypt: bool,
    timeout: Duration,
}

const SQL_RESOURCE: &str = "https://database.windows.net/";

/// An ADO.NET connection string's keywords (lower case) and values: pairs separated by semicolons, a value quoted with
/// ' or " when it holds one (the quote doubled inside), == in a key for =, whitespace around keys and values trimmed.
fn pairs(ado: &str) -> Result<Vec<(String, String)>, String> {
    let chars: Vec<char> = ado.chars().collect();
    let mut out = Vec::new();
    let mut i = 0;
    while i < chars.len() {
        // The key, up to a single =.
        let mut key = String::new();
        loop {
            match chars.get(i) {
                None => break,
                Some('=') if chars.get(i + 1) == Some(&'=') => {
                    key.push('=');
                    i += 2;
                }
                Some('=') => {
                    i += 1;
                    break;
                }
                Some(';') if key.trim().is_empty() => {
                    i += 1;
                    key.clear();
                }
                Some(&c) => {
                    key.push(c);
                    i += 1;
                }
            }
        }
        let key = key.trim().to_lowercase();
        while chars.get(i).is_some_and(|c| c.is_whitespace()) {
            i += 1;
        }
        let mut value = String::new();
        match chars.get(i) {
            Some(&quote) if quote == '"' || quote == '\'' => {
                i += 1;
                loop {
                    match chars.get(i) {
                        None => return Err("A quoted value in the connection string isn't closed.".to_string()),
                        Some(&c) if c == quote && chars.get(i + 1) == Some(&quote) => {
                            value.push(quote);
                            i += 2;
                        }
                        Some(&c) if c == quote => {
                            i += 1;
                            break;
                        }
                        Some(&c) => {
                            value.push(c);
                            i += 1;
                        }
                    }
                }
                while chars.get(i).is_some_and(|c| c.is_whitespace()) {
                    i += 1;
                }
                if !matches!(chars.get(i), None | Some(';')) {
                    return Err("Something follows a quoted value in the connection string.".to_string());
                }
                i += 1;
            }
            _ => {
                while let Some(&c) = chars.get(i) {
                    i += 1;
                    if c == ';' {
                        break;
                    }
                    value.push(c);
                }
                value = value.trim().to_string();
            }
        }
        if !key.is_empty() {
            out.push((key, value));
        }
    }
    Ok(out)
}

impl Database {
    /// Reads the reference's keywords: Server (tcp:host,port), Database / Initial Catalog, User ID, Password,
    /// Authentication (Active Directory Managed Identity or Default), TrustServerCertificate, Encrypt, Connect Timeout.
    pub fn parse(ado: &str) -> Result<Database, String> {
        let mut db = Database {
            host: String::new(),
            port: 1433,
            database: None,
            auth: Auth::Sql { user: String::new(), password: String::new() },
            trust_certificate: false,
            encrypt: true,
            timeout: Duration::from_secs(15),
        };
        let (mut user, mut password, mut mode) = (String::new(), String::new(), None::<String>);
        for (key, value) in pairs(ado)? {
            let value = value.as_str();
            match key.as_str() {
                "server" | "data source" | "address" => {
                    let value = value.strip_prefix("tcp:").unwrap_or(value);
                    match value.split_once(',') {
                        Some((host, port)) => {
                            db.host = host.to_string();
                            db.port = port.trim().parse().map_err(|_| "the port in Server isn't a number".to_string())?;
                        }
                        None => db.host = value.to_string(),
                    }
                }
                "database" | "initial catalog" => db.database = Some(value.to_string()),
                "user id" | "uid" | "user" => user = value.to_string(),
                "password" | "pwd" => password = value.to_string(),
                "authentication" => mode = Some(value.replace(' ', "").to_lowercase()),
                "trustservercertificate" => db.trust_certificate = value.eq_ignore_ascii_case("true"),
                "encrypt" => db.encrypt = !value.eq_ignore_ascii_case("false"),
                "connect timeout" | "connection timeout" => {
                    db.timeout = Duration::from_secs(value.parse().unwrap_or(15));
                }
                _ => {}
            }
        }
        if db.host.is_empty() {
            return Err("No server in the connection string.".to_string());
        }
        db.auth = match mode.as_deref() {
            Some("activedirectorymanagedidentity") | Some("activedirectorymsi") => {
                Auth::ManagedIdentity { client_id: (!user.is_empty()).then_some(user) }
            }
            Some("activedirectorydefault") => Auth::Default,
            Some(other) if other != "sqlpassword" => return Err(format!("Authentication {other} isn't supported.")),
            _ => Auth::Sql { user, password },
        };
        Ok(db)
    }

    pub async fn connect(&self) -> DbResult<Connection> {
        let auth = match &self.auth {
            Auth::Sql { user, password } => AuthMethod::sql_server(user, password),
            Auth::ManagedIdentity { client_id } => AuthMethod::AADToken(managed_identity_token(client_id.as_deref()).await?),
            Auth::Default => AuthMethod::AADToken(default_token().await?),
        };
        match self.connect_to(&self.host, self.port, auth.clone()).await {
            // Azure SQL's redirect connection policy: the gateway names the worker to connect to; connect there once
            // (the reference's SqlClient follows it the same way).
            Err(ConnectError::Sql(tiberius::error::Error::Routing { host, port })) => Ok(self.connect_to(&host, port, auth).await?),
            other => Ok(other?),
        }
    }

    async fn connect_to(&self, host: &str, port: u16, auth: AuthMethod) -> Result<Connection, ConnectError> {
        let mut config = Config::new();
        config.host(host);
        config.port(port);
        if let Some(database) = &self.database {
            config.database(database);
        }
        config.application_name("sanction-and-forgive");
        if self.trust_certificate {
            config.trust_cert();
        }
        config.encryption(if self.encrypt { EncryptionLevel::Required } else { EncryptionLevel::Off });
        config.authentication(auth);
        let tcp = tokio::time::timeout(self.timeout, TcpStream::connect(config.get_addr()))
            .await
            .map_err(|_| ConnectError::Other("Connecting to SQL Server timed out.".into()))?
            .map_err(|e| ConnectError::Other(e.into()))?;
        tcp.set_nodelay(true).map_err(|e| ConnectError::Other(e.into()))?;
        tokio::time::timeout(self.timeout, Client::connect(config, tcp.compat_write()))
            .await
            .map_err(|_| ConnectError::Other("Signing in to SQL Server timed out.".into()))?
            .map_err(ConnectError::Sql)
    }
}

/// Why a connection attempt failed: SQL Server's answer (a redirect among them), or anything else.
#[derive(Debug)]
enum ConnectError {
    Sql(tiberius::error::Error),
    Other(DbError),
}

impl std::fmt::Display for ConnectError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ConnectError::Sql(e) => e.fmt(f),
            ConnectError::Other(e) => e.fmt(f),
        }
    }
}

impl std::error::Error for ConnectError {}

/// A token for the database from the Container Apps identity endpoint (the user-assigned identity by client id).
async fn managed_identity_token(client_id: Option<&str>) -> DbResult<String> {
    let endpoint = std::env::var("IDENTITY_ENDPOINT").map_err(|_| "No managed identity here (IDENTITY_ENDPOINT isn't set).")?;
    let header = std::env::var("IDENTITY_HEADER").unwrap_or_default();
    let client_id = client_id.map(str::to_string).or_else(|| std::env::var("AZURE_CLIENT_ID").ok());
    let mut query = vec![("api-version", "2019-08-01".to_string()), ("resource", SQL_RESOURCE.to_string())];
    if let Some(id) = client_id {
        query.push(("client_id", id));
    }
    let response =
        reqwest::Client::new().get(endpoint).query(&query).header("X-IDENTITY-HEADER", header).send().await?.error_for_status()?;
    let body: serde_json::Value = response.json().await?;
    Ok(body["access_token"].as_str().ok_or("The identity endpoint gave no token.")?.to_string())
}

/// Active Directory Default: the managed identity where there is one, else the az CLI's signed-in user.
async fn default_token() -> DbResult<String> {
    if std::env::var("IDENTITY_ENDPOINT").is_ok() {
        return managed_identity_token(None).await;
    }
    let args = ["account", "get-access-token", "--resource", SQL_RESOURCE, "--query", "accessToken", "-o", "tsv"];
    let output = if cfg!(windows) {
        tokio::process::Command::new("cmd").arg("/C").arg("az").args(args).output().await?
    } else {
        tokio::process::Command::new("az").args(args).output().await?
    };
    if !output.status.success() {
        return Err("az account get-access-token failed: sign in with az login.".into());
    }
    Ok(String::from_utf8(output.stdout)?.trim().to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_references_connection_strings() {
        let local =
            Database::parse("Server=localhost,14333;Database=sanctions_local;User ID=sa;Password=p;TrustServerCertificate=True").unwrap();
        assert_eq!((local.host.as_str(), local.port, local.database.as_deref()), ("localhost", 14333, Some("sanctions_local")));
        assert_eq!(local.auth, Auth::Sql { user: "sa".into(), password: "p".into() });
        assert!(local.trust_certificate);

        let azure = Database::parse("Server=tcp:x.database.windows.net,1433;Database=db;Authentication=Active Directory Managed Identity;User Id=abc;Encrypt=True;Connect Timeout=60").unwrap();
        assert_eq!(azure.host, "x.database.windows.net");
        assert_eq!(azure.auth, Auth::ManagedIdentity { client_id: Some("abc".into()) });
        assert_eq!(azure.timeout, Duration::from_secs(60));

        let tools = Database::parse("Server=tcp:x,1433;Database=db;Authentication=Active Directory Default").unwrap();
        assert_eq!(tools.auth, Auth::Default);

        // Quoted values, a quote doubled inside one, and a key with == (Copilot's point on #37).
        let quoted = Database::parse("Server=h; User ID = 'sa' ;Password=\"a;b\"\"c\";Weird==Key=x;").unwrap();
        assert_eq!(quoted.auth, Auth::Sql { user: "sa".into(), password: "a;b\"c".into() });
        assert!(Database::parse("Server=h;Password=\"unclosed").is_err());
    }
}
