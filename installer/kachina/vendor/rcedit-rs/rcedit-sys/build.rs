use cc::Build;

const SOURCE_FILES: [&str; 2] = ["src/rescle.cc", "src/librcedit.cpp"];
const HEADER_FILES: [&str; 1] = ["src/rescle.h"];

fn track_file_changes(file: &str) {
    println!("cargo:rerun-if-changed={}", file);
}

fn main() {
    SOURCE_FILES.iter().copied().for_each(track_file_changes);
    HEADER_FILES.iter().copied().for_each(track_file_changes);

    let current_dir = std::env::var("CARGO_MANIFEST_DIR").unwrap();

    Build::new()
        .cpp(true)
        .static_crt(true)
        // MSVC 默认按本地代码页读取源文件；项目注释使用 UTF-8，显式指定源文件编码。
        .flag_if_supported("/utf-8")
        .flag_if_supported("-std=c++11")
        .files(SOURCE_FILES.iter().map(|name| format!("{}/{}", current_dir, name)))
        .compile("rcedit");
}
