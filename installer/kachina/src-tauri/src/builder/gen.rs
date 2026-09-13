use std::{collections::HashSet, path::Path};

use async_compression::tokio::bufread::ZstdEncoder;
use hdiff_sys::safe_create_single_patch;
use indicatif::{MultiProgress, ProgressBar, ProgressStyle};
use tokio::{io::AsyncWriteExt, task::JoinSet};

use crate::{
    cli::GenArgs,
    metadata::{deep_generate_metadata, deep_get_filelist},
    utils::{
        hash::run_hash,
        metadata::{InstallerInfo, Metadata, PatchInfo, PatchItem, RepoMetadata},
        progressed_read::ReadWithCallback,
    },
};

pub async fn gen_cli(args: GenArgs) {
    let pb_style = ProgressStyle::with_template("[{elapsed_precise}] {bar:20.cyan/blue} {msg} ")
        .unwrap()
        .progress_chars("##-");
    let pb_style_total =
        ProgressStyle::with_template("[{elapsed_precise}] {bar:20.cyan/blue} {pos}/{len} {msg} ")
            .unwrap()
            .progress_chars("##-");
    // 确保输出目录存在
    println!("Creating output directory...");
    let _ = tokio::fs::create_dir_all(&args.output_dir).await;
    // 计算更新器哈希
    let mut installer = None;
    if let Some(updater) = args.updater.as_ref() {
        println!("Hashing updater...");
        let hash = run_hash("xxh", updater.to_str().unwrap())
            .await
            .expect("failed to hash updater");
        let size = tokio::fs::metadata(updater)
            .await
            .expect("failed to get updater size")
            .len();
        installer = Some(InstallerInfo {
            size,
            md5: None,
            xxh: Some(hash),
        });
    }
    println!("Generating metadata...");
    let mut metadata = deep_generate_metadata(&args.input_dir)
        .await
        .expect("failed to generate metadata");
    if let Some(installer) = installer.as_ref() {
        // 从元数据中移除更新器
        metadata.retain(|x| x.xxh.as_ref().unwrap() != installer.xxh.as_ref().unwrap());
    }
    println!("Writting metadata to {:?}", args.output_metadata);
    let mut repometa = RepoMetadata {
        repo_name: args.repo,
        tag_name: args.tag,
        assets: None,
        hashed: Some(metadata.clone()),
        patches: None,
        installer,
        deletes: None,
        packing_info: None,
    };
    let metadata_str = serde_json::to_string(&repometa).expect("failed to serialize metadata");
    tokio::fs::write(&args.output_metadata, metadata_str)
        .await
        .expect("failed to write metadata");
    println!("Compressing files...");
    let multi_pg = MultiProgress::new();

    // 创建进度条跟踪总体状态
    let pb_main = multi_pg.add(ProgressBar::new(metadata.len() as u64));
    pb_main.set_style(pb_style_total.clone());
    pb_main.set_message("TOTAL");

    // 让主进度条立即渲染，不必等待
    // 第一个任务完成。
    pb_main.tick();

    // tokio::task::JoinSet
    // 设置 JoinSet 管理 future 的 join 句柄
    let mut set = JoinSet::new();

    let mut last_item = false;

    // 遍历下载列表，并
    // 为每个下载任务（do_stuff）创建后台任务
    // 创建的任务数不超过 MAX_CONCURRENT 限制
    for (index, file) in metadata.iter().enumerate() {
        let pb_main_ = pb_main.clone();
        if index == metadata.len() - 1 {
            last_item = true;
        }

        // 为每个下载创建进度条并设置样式
        // 使用 insert_before()，让 pb_main 保持在其他进度条下方
        let pb_task = multi_pg.insert_before(&pb_main, ProgressBar::new(file.size));
        pb_task.set_style(pb_style.clone());

        // 无论是否等待 future，都立即创建后台任务
        // https://docs.rs/tokio/latest/tokio/task/struct.JoinSet.html#method.spawn
        let file = file.clone();
        let output = args.output_dir.clone();
        let input: std::path::PathBuf = args.input_dir.clone();
        set.spawn(tokio::task::spawn_blocking(|| {
            // 为每个任务创建新的 tokio 运行时
            let rt = tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
                .unwrap();
            rt.block_on(async move {
                let display_name = file.file_name.clone().replace("\\", "/");
                // 将文件复制到输出目录
                let file_path = input.join(&file.file_name);
                let hash = if file.xxh.is_some() {
                    file.xxh.as_ref().unwrap()
                } else if file.md5.is_some() {
                    file.md5.as_ref().unwrap()
                } else {
                    panic!("file has no hash");
                };
                let output_path = output.join(hash);
                pb_task.set_message(format!("     {display_name:?}"));
                let task_ = pb_task.clone();
                let pb_main_ = pb_main_.clone();
                let reader = tokio::fs::File::open(file_path).await.unwrap();
                let reader = ReadWithCallback {
                    reader,
                    callback: move |chunk| {
                        task_.inc(chunk as u64);
                        pb_main_.tick();
                    },
                };
                let reader = tokio::io::BufReader::new(reader);
                let mut encoder = ZstdEncoder::with_quality(reader, async_compression::Level::Best);
                let mut writer = tokio::fs::File::create(output_path).await.unwrap();
                tokio::io::copy(&mut encoder, &mut writer)
                    .await
                    .expect("failed to compress file");
                if !console::Term::stdout().is_term() {
                    println!("Compressed {display_name:?}");
                }
                pb_task.finish_with_message(format!("DONE {display_name:?}"));
            });
        }));

        // 达到上限后等待运行中的任务完成
        // 等待 future（join_next().await）并获取执行结果
        // 此处结果是下载 ID（u64），详见 do_stuff 的签名
        while set.len() >= args.zstd_concurrency || last_item {
            match set.join_next().await {
                Some(res) => {
                    if let Err(e) = res {
                        eprintln!("Zstd Task Error: {e:?}");
                        std::process::exit(1);
                    }
                    let res = res.unwrap();
                    if let Err(e) = res {
                        eprintln!("Zstd Task Error: {e:?}");
                        std::process::exit(1);
                    }
                }
                None => {
                    break;
                }
            };
            pb_main.inc(1);
        }
    }
    pb_main.finish_with_message("Compression finished");
    // 压缩并复制安装器
    if let Some(installer) = repometa.installer.as_ref() {
        let output_path = args.output_dir.join(installer.xxh.as_ref().unwrap());
        println!("Compressing installer to {output_path:?}");
        let reader = tokio::fs::File::open(args.updater.as_ref().unwrap())
            .await
            .expect("failed to open installer");
        let reader = tokio::io::BufReader::new(reader);
        let mut encoder: ZstdEncoder<tokio::io::BufReader<tokio::fs::File>> =
            ZstdEncoder::with_quality_and_params(
                reader,
                async_compression::Level::Best,
                &[async_compression::zstd::CParameter::nb_workers(
                    num_cpus::get() as u32,
                )],
            );
        let mut writer = tokio::fs::File::create(output_path)
            .await
            .expect("failed to create file");
        tokio::io::copy(&mut encoder, &mut writer)
            .await
            .expect("failed to compress file");
    }
    // 检查差异
    if let Some(diff_vers) = args.diff_vers {
        let mut metadata_with_installer = metadata.clone();
        if let Some(installer) = repometa.installer.as_ref() {
            metadata_with_installer.push(Metadata {
                file_name: if let Some(name) = args.updater_name.as_ref() {
                    name.clone()
                } else if let Some(name) = args.updater.as_ref().unwrap().file_name() {
                    name.to_string_lossy().to_string()
                } else {
                    panic!("failed to get updater name");
                },
                size: installer.size,
                md5: installer.md5.clone(),
                xxh: installer.xxh.clone(),
            });
        }
        if !diff_vers.is_empty() {
            let mut ignore = ignore::gitignore::GitignoreBuilder::new("/");
            if let Some(diff_ignore) = args.diff_ignore {
                for ignore_file in diff_ignore.iter() {
                    ignore.add_line(None, ignore_file).unwrap();
                }
            }
            let ignore = ignore.build().unwrap();
            let mut diffs = Vec::new();
            let mut deletes = Vec::new();
            // 遍历差异版本
            for diff_ver in diff_vers.iter() {
                // 遍历当前元数据
                let multi_pg = MultiProgress::new();

                // 创建进度条跟踪总体状态
                let pb_main = multi_pg.add(ProgressBar::new(metadata_with_installer.len() as u64));
                pb_main.set_style(pb_style_total.clone());
                pb_main.set_message(format!("DIFF TOTAL {diff_ver}"));

                // 让主进度条立即渲染，不必等待
                // 第一个任务完成。
                pb_main.tick();

                // tokio::task::JoinSet
                // 设置 JoinSet 管理 future 的 join 句柄
                let mut set = JoinSet::new();

                let mut last_item = false;

                for (index, file) in metadata_with_installer.iter().enumerate() {
                    if index == metadata.len() - 1 {
                        last_item = true;
                    }

                    let input_dir = args.input_dir.clone();
                    let output_dir = args.output_dir.clone();
                    let diff_ver = diff_ver.clone();

                    // 无论是否等待 future，都立即创建后台任务
                    // https://docs.rs/tokio/latest/tokio/task/struct.JoinSet.html#method.spawn
                    let file = file.clone();
                    let ignore = ignore.clone();
                    set.spawn(async move {
                        if ignore
                            .matched_path_or_any_parents(&file.file_name, false)
                            .is_ignore()
                        {
                            println!("File {:?} ignored", file.file_name);
                            return None;
                        }
                        // 文件应大于 1 MB
                        if file.size < 1024 * 1024 {
                            println!("File {:?} too small, skipped", file.file_name);
                            return None;
                        }
                        // 检查文件是否存在于差异版本中
                        let diff_file = Path::new(&diff_ver).join(&file.file_name);
                        if !diff_file.exists() {
                            // 差异版本中未找到文件，跳过
                            println!("File {:?} not found in diff_ver, skipped", file.file_name);
                            return None;
                        }
                        // 找到文件，计算其哈希
                        let old_hash = run_hash("xxh", diff_file.to_str().unwrap())
                            .await
                            .expect("failed to hash diff file");
                        if old_hash == *file.xxh.as_ref().unwrap() {
                            // 哈希相同，跳过
                            println!("File {:?} hash same, skipped", file.file_name);
                            return None;
                        }
                        // 哈希不同，生成差异
                        let output_path = output_dir.join(format!(
                            "{}_{}.hdiff",
                            old_hash,
                            file.xxh.as_ref().unwrap()
                        ));
                        let compressed_path =
                            output_dir.join(format!("{}_{}", old_hash, file.xxh.as_ref().unwrap()));
                        println!("Generating diff for {diff_file:?} to {output_path:?}");
                        // 将旧数据和新数据读入内存
                        let old_data = tokio::fs::read(&diff_file)
                            .await
                            .expect("failed to read old data");
                        let new_data = tokio::fs::read(input_dir.join(&file.file_name))
                            .await
                            .expect("failed to read new data");
                        let output_file = std::fs::File::create(&output_path)
                            .expect("failed to create output file");
                        tokio::task::spawn_blocking(move || {
                            // 创建输出文件
                            safe_create_single_patch(&new_data, &old_data, output_file, 7)
                        })
                        .await
                        .expect("failed to create diff")
                        .expect("failed to create diff");
                        // 压缩差异文件
                        let reader = tokio::fs::File::open(&output_path)
                            .await
                            .expect("failed to open diff file");
                        let reader = tokio::io::BufReader::new(reader);
                        let mut encoder =
                            ZstdEncoder::with_quality(reader, async_compression::Level::Best);
                        let mut writer = tokio::fs::File::create(&compressed_path)
                            .await
                            .expect("failed to create compressed diff file");
                        tokio::io::copy(&mut encoder, &mut writer)
                            .await
                            .expect("failed to compress diff");
                        // 刷新写入器
                        writer.flush().await.expect("failed to flush writer");
                        // 关闭文件
                        drop(writer);
                        let diff_original_size = tokio::fs::metadata(&output_path)
                            .await
                            .expect("failed to get diff size")
                            .len();
                        // 删除未压缩的差异文件
                        tokio::fs::remove_file(&output_path)
                            .await
                            .expect("failed to remove uncompressed diff");
                        // 如果差异文件大小达到新文件的 50% 以上，删除差异并跳过
                        let diff_size = tokio::fs::metadata(&compressed_path)
                            .await
                            .expect("failed to get diff size")
                            .len();
                        let old_size = tokio::fs::metadata(&diff_file)
                            .await
                            .expect("failed to get old size")
                            .len();
                        if diff_size > (file.size / 2) {
                            tokio::fs::remove_file(&compressed_path)
                                .await
                                .expect("failed to remove diff");
                            println!("File {:?} diff too large, skipped", file.file_name);
                            return None;
                        }
                        Some(PatchInfo {
                            file_name: file.file_name.clone(),
                            size: diff_original_size,
                            from: PatchItem {
                                md5: None,
                                xxh: Some(old_hash.clone()),
                                size: old_size,
                            },
                            to: PatchItem {
                                md5: None,
                                xxh: Some(file.xxh.clone().unwrap()),
                                size: file.size,
                            },
                        })
                    });
                    while set.len() >= args.zstd_concurrency || last_item {
                        match set.join_next().await {
                            Some(res) => {
                                if let Err(e) = res {
                                    eprintln!("Diff Task Error: {e:?}");
                                    std::process::exit(1);
                                }
                                let res = res.unwrap();
                                if let Some(diff) = res {
                                    diffs.push(diff);
                                }
                            }
                            None => {
                                break;
                            }
                        };
                        pb_main.inc(1);
                    }
                }
                let diff_filelist = deep_get_filelist(&diff_ver.into())
                    .await
                    .expect("failed to get diff_ver file list");
                println!("Checking for deleted files in {diff_ver}...");
                for file in diff_filelist.iter() {
                    // 检查文件是否存在于当前元数据中
                    if !metadata_with_installer.iter().any(|x| x.file_name == *file) {
                        // 当前元数据中未找到文件，将其加入删除列表
                        println!("File {file:?} not found in current metadata, added to deletes");
                        deletes.push(file.clone());
                    }
                }
            }
            repometa.deletes = Some(deletes);
            // 生成打包优化信息（在移动 diffs 之前）
            let diff_vers_pathbuf: Vec<std::path::PathBuf> =
                diff_vers.iter().map(std::path::PathBuf::from).collect();
            let packing_info =
                generate_packing_info(&metadata_with_installer, &diffs, &diff_vers_pathbuf).await;

            repometa.patches = Some(diffs);
            repometa.packing_info = Some(packing_info);

            // 再次写入元数据
            let metadata_str =
                serde_json::to_string(&repometa).expect("failed to serialize metadata");
            tokio::fs::write(&args.output_metadata, metadata_str)
                .await
                .expect("failed to write metadata");
        }
    } else {
        // 即使没有差异版本，如果指定了diff_vers参数，也生成基础的packing_info
        println!("Generating packing info for first release...");
        let mut metadata_with_installer = metadata.clone();
        if let Some(installer) = repometa.installer.as_ref() {
            metadata_with_installer.push(Metadata {
                file_name: if let Some(name) = args.updater_name.as_ref() {
                    name.clone()
                } else if let Some(name) = args.updater.as_ref().unwrap().file_name() {
                    name.to_string_lossy().to_string()
                } else {
                    panic!("failed to get updater name");
                },
                size: installer.size,
                md5: installer.md5.clone(),
                xxh: installer.xxh.clone(),
            });
        }

        let empty_diffs = Vec::new();
        let empty_diff_vers = Vec::new();
        let packing_info =
            generate_packing_info(&metadata_with_installer, &empty_diffs, &empty_diff_vers).await;
        repometa.packing_info = Some(packing_info);

        // 再次写入元数据
        let metadata_str = serde_json::to_string(&repometa).expect("failed to serialize metadata");
        tokio::fs::write(&args.output_metadata, metadata_str)
            .await
            .expect("failed to write metadata");
    }
    println!("Done");
}

async fn generate_packing_info(
    metadata_with_installer: &[Metadata],
    patches: &[PatchInfo],
    diff_vers: &[std::path::PathBuf],
) -> Vec<Vec<String>> {
    let mut packing_info = vec![
        Vec::new(), // [0] 大文件
        Vec::new(), // [1] 没有更新的小文件
        Vec::new(), // [2] 有变化或新增的小文件
        Vec::new(), // [3] 小补丁
        Vec::new(), // [4] 大补丁
    ];

    // 收集变化文件的哈希集合
    let mut changed_hashes = HashSet::new();
    let mut new_hashes = HashSet::new();

    if !diff_vers.is_empty() {
        // 分析每个文件的变化状态
        for file in metadata_with_installer {
            let hash = file.xxh.as_ref().unwrap();

            // 检查文件是否存在于旧版本
            let mut found_in_old = false;
            let mut hash_changed = false;

            for diff_ver in diff_vers {
                let old_file_path = diff_ver.join(&file.file_name);
                if old_file_path.exists() {
                    found_in_old = true;
                    let old_hash_result = run_hash("xxh", old_file_path.to_str().unwrap()).await;
                    if let Ok(old_hash) = old_hash_result {
                        if old_hash != *hash {
                            hash_changed = true;
                        }
                    }
                    break;
                }
            }

            if !found_in_old {
                new_hashes.insert(hash.clone());
            } else if hash_changed {
                changed_hashes.insert(hash.clone());
            }
        }
    } else {
        // 没有差异版本，所有文件都是新增
        for file in metadata_with_installer {
            new_hashes.insert(file.xxh.as_ref().unwrap().clone());
        }
    }

    // 分类原始文件
    for file in metadata_with_installer {
        let hash = file.xxh.as_ref().unwrap();

        if file.size > 1024 * 1024 {
            packing_info[0].push(hash.clone()); // 大文件
        } else if new_hashes.contains(hash) || changed_hashes.contains(hash) {
            packing_info[2].push(hash.clone()); // 有变化的小文件
        } else {
            packing_info[1].push(hash.clone()); // 没有更新的小文件
        }
    }

    // 分类补丁文件
    for patch in patches {
        let patch_name = format!(
            "{}_{}",
            patch.from.xxh.as_ref().unwrap(),
            patch.to.xxh.as_ref().unwrap()
        );
        if patch.size > 1024 * 1024 {
            packing_info[4].push(patch_name); // 大补丁
        } else {
            packing_info[3].push(patch_name); // 小补丁
        }
    }

    println!("Packing info generated:");
    println!("  Large files: {}", packing_info[0].len());
    println!("  Unchanged small files: {}", packing_info[1].len());
    println!("  Changed small files: {}", packing_info[2].len());
    println!("  Small patches: {}", packing_info[3].len());
    println!("  Large patches: {}", packing_info[4].len());

    packing_info
}
