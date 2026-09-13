use tokio::io::AsyncSeekExt;

use crate::{
    cli::AppendArgs,
    pack::{write_file, PackFile},
};

pub async fn append_cli(args: AppendArgs) {
    // 文件数量应等于名称数量，或名称数量应为 0
    if args.file.len() != args.name.len() && !args.name.is_empty() {
        panic!("Files length must equal to names length, or names length must be 0");
    }
    // 以追加模式打开文件
    let mut output = tokio::fs::OpenOptions::new()
        .append(true)
        .open(&args.output)
        .await
        .expect("Failed to open output file");
    // 移动到文件末尾
    output
        .seek(std::io::SeekFrom::End(0))
        .await
        .expect("Failed to seek to the end of the file");
    // 遍历输入文件，获取对应名称；未提供时使用文件名
    for (i, file) in args.file.iter().enumerate() {
        let name = if !args.name.is_empty() {
            &args.name[i]
        } else {
            file.file_name().and_then(|s| s.to_str()).unwrap()
        };
        let input_stream = tokio::fs::File::open(file)
            .await
            .expect("Failed to open input file");
        let input_length = input_stream
            .metadata()
            .await
            .expect("Failed to get input file metadata")
            .len();
        // 将文件写入输出
        write_file(
            &mut output,
            &mut PackFile {
                name: name.to_string(),
                data: Box::new(input_stream),
                size: input_length.try_into().expect("File size too large"),
            },
        )
        .await
        .expect("Failed to write file");
        println!("Appended file: {name} ({input_length} bytes)");
    }
}
