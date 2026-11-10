//! Sanction & Forgive in Rust: a port of the .NET reference, checked by the conformance suite (docs/conformance.md).

/// mimalloc: fast, and it hands memory back instead of keeping a fragmented arena per thread as glibc's malloc does.
#[global_allocator]
static ALLOCATOR: mimalloc::MiMalloc = mimalloc::MiMalloc;

pub mod api;
pub mod canonical;
pub mod lists;
pub mod log;
pub mod matching;
pub mod store;
