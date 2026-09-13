fn main() {
    cc::Build::new()
        // MSVC 默认按本地代码页读取源文件；项目注释使用 UTF-8，显式指定源文件编码。
        .flag_if_supported("/utf-8")
        .file("HPatch/patch.c")
        .compile("hpatch");
}
