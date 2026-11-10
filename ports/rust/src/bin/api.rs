//! The API: the Rust port of SanctionAndForgive.Api, configured by the reference's names (docs/conformance.md).

use std::net::SocketAddr;
use std::sync::Arc;
use std::time::Duration;

use sanction_and_forgive::api::catalog::{Catalog, Loader};
use sanction_and_forgive::api::screening::ScreeningService;
use sanction_and_forgive::api::server::{router, AppState, Config};
use sanction_and_forgive::log::Logger;
use sanction_and_forgive::matching::{Options, Screener};
use sanction_and_forgive::store::db::Database;

fn setting(name: &str, fallback: &str) -> String {
    std::env::var(name).ok().filter(|v| !v.is_empty()).unwrap_or_else(|| fallback.to_string())
}

fn number<T: std::str::FromStr>(name: &str, fallback: T) -> T {
    std::env::var(name).ok().and_then(|v| v.parse().ok()).unwrap_or(fallback)
}

#[tokio::main]
async fn main() {
    let logger = Logger;
    let connection = setting("ConnectionStrings__Lists", "");
    if connection.is_empty() {
        logger.error("No connection string: set ConnectionStrings__Lists.", &[]);
        std::process::exit(2);
    }
    let db = match Database::parse(&connection) {
        Ok(db) => db,
        Err(e) => {
            logger.error("Reading the connection string failed", &[("error", e.into())]);
            std::process::exit(2);
        }
    };
    let refresh = Some(number("Lists__RefreshMinutes", 15.0_f64)).filter(|v| *v > 0.0).unwrap_or(15.0);
    let config = Config {
        version: setting("App__Version", "dev"),
        revision: setting("CONTAINER_APP_REVISION", ""),
        rate_limit_per_minute: number("RateLimiting__PerMinute", 120),
        stream_max_records: number("Screening__StreamMaxRecords", 50_000),
        stream_max_seconds: number("Screening__StreamMaxSeconds", 200),
        concurrent_streams: number("Screening__ConcurrentStreams", 1),
        sign_in_enabled: setting("Admin__SignInEnabled", "").eq_ignore_ascii_case("true"),
    };

    let catalog = Arc::new(Catalog::default());
    let loader = Arc::new(Loader::new(db.clone(), catalog.clone(), logger));
    let screenings = Arc::new(ScreeningService { catalog: catalog.clone(), screener: Screener::new(Options::default()), logger });
    let state = Arc::new(AppState::new(config, catalog, screenings, loader.clone(), db));
    tokio::spawn(loader.run_refresh(Duration::from_secs_f64(refresh * 60.0)));

    let port = setting("PORT", "8080");
    let listener = match tokio::net::TcpListener::bind(format!("[::]:{port}")).await {
        Ok(listener) => listener,
        // No IPv6 here: IPv4 only.
        Err(_) => tokio::net::TcpListener::bind(format!("0.0.0.0:{port}")).await.expect("the port"),
    };
    logger.info(&format!("Now listening on: http://[::]:{port}"), &[]);
    let shutdown = async {
        let ctrl_c = tokio::signal::ctrl_c();
        #[cfg(unix)]
        {
            let mut term = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate()).expect("signals");
            tokio::select! { _ = ctrl_c => {}, _ = term.recv() => {} }
        }
        #[cfg(not(unix))]
        let _ = ctrl_c.await;
    };
    if let Err(e) =
        axum::serve(listener, router(state).into_make_service_with_connect_info::<SocketAddr>()).with_graceful_shutdown(shutdown).await
    {
        logger.error("The server stopped", &[("error", e.to_string().into())]);
        std::process::exit(1);
    }
}
