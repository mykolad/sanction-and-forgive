// Embeds the shared files from the .NET projects, their one source of truth (never copied by hand into this port):
// the matcher's dictionaries and the website's pages. The repository's layout is kept in the image's build stage.
use std::fs;
use std::path::{Path, PathBuf};

fn collect(root: &Path, dir: &Path, out: &mut Vec<(String, PathBuf)>) {
    let mut entries: Vec<_> = fs::read_dir(dir).unwrap_or_else(|e| panic!("{}: {e}", dir.display())).map(|e| e.unwrap().path()).collect();
    entries.sort();
    for path in entries {
        if path.is_dir() {
            collect(root, &path, out);
        } else {
            let name = path.strip_prefix(root).unwrap().to_string_lossy().replace('\\', "/");
            out.push((name, path.canonicalize().unwrap()));
        }
    }
}

fn table(name: &str, root: &Path, filter: impl Fn(&str) -> bool) -> String {
    let mut files = Vec::new();
    collect(root, root, &mut files);
    let mut code = format!("pub static {name}: &[(&str, &[u8])] = &[\n");
    for (relative, path) in files.into_iter().filter(|(relative, _)| filter(relative)) {
        println!("cargo:rerun-if-changed={}", path.display());
        code += &format!("    ({relative:?}, include_bytes!({:?})),\n", path.to_string_lossy());
    }
    code + "];\n"
}

fn main() {
    let manifest = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
    let repository = manifest.join("../..");
    let data = repository.join("src/SanctionAndForgive.Matching/Data");
    let pages = repository.join("src/SanctionAndForgive.Api/wwwroot");
    println!("cargo:rerun-if-changed={}", data.display());
    println!("cargo:rerun-if-changed={}", pages.display());
    let mut code = table("DICTIONARIES", &data, |name| name.ends_with(".json"));
    if pages.is_dir() {
        code += &table("PAGES", &pages, |_| true);
    } else {
        code += "pub static PAGES: &[(&str, &[u8])] = &[];\n";
    }
    fs::write(PathBuf::from(std::env::var("OUT_DIR").unwrap()).join("resources.rs"), code).unwrap();
}
