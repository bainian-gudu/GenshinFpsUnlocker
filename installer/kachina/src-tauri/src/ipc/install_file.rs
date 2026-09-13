use crate::{
    dfs::InsightItem,
    fs::{
        create_http_stream, create_local_stream, create_multi_http_stream, create_target_file,
        prepare_target, progressed_copy, progressed_hpatch, verify_hash,
    },
    utils::error::{IntoTAResult, TAResult},
};

use anyhow::Result;
use async_compression::tokio::bufread::ZstdDecoder as TokioZstdDecoder;
use futures::TryStreamExt;
use serde::{Deserialize, Serialize};
use std::sync::{Arc, Mutex};
use tokio::io::{AsyncReadExt, BufReader};
use tracing::{info, warn};

fn default_as_false() -> bool {
    false
}

// 根据 InstallFileArgs 检查是否需要解压的辅助函数
fn should_decompress_chunk(args: &InstallFileArgs) -> bool {
    match &args.mode {
        InstallFileMode::Direct { source } => match source {
            InstallFileSource::Url {
                skip_decompress, ..
            } => !skip_decompress,
            InstallFileSource::Local {
                skip_decompress, ..
            } => !skip_decompress,
        },
        InstallFileMode::Patch { source, .. } => match source {
            InstallFileSource::Url {
                skip_decompress, ..
            } => !skip_decompress,
            InstallFileSource::Local {
                skip_decompress, ..
            } => !skip_decompress,
        },
        InstallFileMode::HybridPatch { diff, .. } => match diff {
            InstallFileSource::Url {
                skip_decompress, ..
            } => !skip_decompress,
            InstallFileSource::Local {
                skip_decompress, ..
            } => !skip_decompress,
        },
    }
}

#[derive(Serialize, Deserialize, Debug)]
pub struct InstallResult {
    pub bytes_transferred: usize,
    pub insight: Option<InsightItem>,
}

#[derive(serde::Deserialize, serde::Serialize, Clone, Debug)]
#[serde(untagged)]
enum InstallFileSource {
    Url {
        url: String,
        offset: usize,
        size: usize,
        #[serde(default = "default_as_false")]
        skip_decompress: bool,
    },
    Local {
        offset: usize,
        size: usize,
        #[serde(default = "default_as_false")]
        skip_decompress: bool,
    },
}

#[derive(serde::Deserialize, serde::Serialize, Clone, Debug)]
#[serde(tag = "type")]
enum InstallFileMode {
    Direct {
        source: InstallFileSource,
    },
    Patch {
        source: InstallFileSource,
        diff_size: usize,
    },
    HybridPatch {
        diff: InstallFileSource,
        source: InstallFileSource,
    },
}

#[derive(serde::Deserialize, serde::Serialize, Clone, Debug)]
pub struct InstallFileArgs {
    mode: InstallFileMode,
    target: String,
    md5: Option<String>,
    xxh: Option<String>,
    clear_installer_index_mark: Option<bool>,
}
async fn create_stream_by_source(
    source: InstallFileSource,
) -> Result<(
    Box<dyn tokio::io::AsyncRead + Unpin + std::marker::Send>,
    Option<Arc<Mutex<InsightItem>>>,
)> {
    match source {
        InstallFileSource::Url {
            url,
            offset,
            size,
            skip_decompress,
        } => {
            let (stream, _content_length, insight_handle) =
                create_http_stream(&url, offset, size, skip_decompress).await?;
            Ok((stream, Some(insight_handle)))
        }
        InstallFileSource::Local {
            offset,
            size,
            skip_decompress,
        } => Ok((
            create_local_stream(offset, size, skip_decompress).await?,
            None,
        )),
    }
}
pub async fn ipc_install_file(
    args: InstallFileArgs,
    notify: impl Fn(serde_json::Value) + std::marker::Send + 'static,
) -> TAResult<serde_json::Value> {
    let target = args.target;
    let override_old_path = prepare_target(&target).await?;
    let progress_noti = move |downloaded: usize| {
        notify(serde_json::json!(downloaded));
    };
    match args.mode {
        InstallFileMode::Direct { source } => {
            let (stream, insight_handle) = create_stream_by_source(source).await?;
            let bytes_transferred = match crate::fs::progressed_copy(
                stream,
                create_target_file(&target).await?,
                progress_noti,
            )
            .await
            {
                Ok(bytes) => bytes,
                Err(e) => {
                    if let Some(handle) = &insight_handle {
                        if let Ok(mut insight) = handle.lock() {
                            insight.error = Some(e.to_string());
                        }
                        return Err(crate::utils::error::TACommandError::with_insight_handle(
                            e,
                            handle.clone(),
                        ));
                    } else {
                        return Err(crate::utils::error::TACommandError::new(e));
                    }
                }
            };

            // 获取最终的insight
            let final_insight = if let Some(handle) = insight_handle {
                if let Ok(insight) = handle.lock() {
                    Some(insight.clone())
                } else {
                    None
                }
            } else {
                None
            };

            if args.md5.is_some() || args.xxh.is_some() {
                // 如果需要清理安装器索引标记，先清理再进行哈希校验
                if args.clear_installer_index_mark.unwrap_or(false) || override_old_path.is_some() {
                    info!("Clearing installer index mark for: {}", target);
                    if let Err(e) = crate::installer::uninstall::clear_index_mark(
                        &std::path::PathBuf::from(&target),
                    )
                    .await
                    .into_ta_result()
                    {
                        warn!("Failed to clear index mark: {:?}", e);
                        return Err(e);
                    }
                    info!("Index mark cleared successfully");
                }
                verify_hash(&target, args.md5, args.xxh).await?;
            }

            let result = InstallResult {
                bytes_transferred,
                insight: final_insight,
            };
            serde_json::to_value(result).into_ta_result()
        }
        InstallFileMode::Patch { source, diff_size } => {
            let is_self_update = override_old_path.is_some();
            let (stream, insight_handle) = create_stream_by_source(source).await?;
            let (bytes_transferred, _) = progressed_hpatch(
                stream,
                &target,
                diff_size,
                progress_noti,
                override_old_path,
                None, // 传入None，因为现在insight由处理管理
            )
            .await?;

            // 获取最终的insight
            let final_insight = if let Some(handle) = insight_handle {
                if let Ok(insight) = handle.lock() {
                    Some(insight.clone())
                } else {
                    None
                }
            } else {
                None
            };

            if args.md5.is_some() || args.xxh.is_some() {
                // 如果需要清理安装器索引标记，先清理再进行哈希校验
                if args.clear_installer_index_mark.unwrap_or(false) || is_self_update {
                    info!("Clearing installer index mark for: {}", target);
                    if let Err(e) = crate::installer::uninstall::clear_index_mark(
                        &std::path::PathBuf::from(&target),
                    )
                    .await
                    .into_ta_result()
                    {
                        warn!("Failed to clear index mark: {:?}", e);
                        return Err(e);
                    }
                    info!("Index mark cleared successfully");
                }
                verify_hash(&target, args.md5, args.xxh).await?;
            }

            let result = InstallResult {
                bytes_transferred,
                insight: final_insight,
            };
            serde_json::to_value(result).into_ta_result()
        }
        InstallFileMode::HybridPatch { diff, source } => {
            // 先解压来源（本地文件，不需要 insight）
            let (source_stream, _) = create_stream_by_source(source).await?;
            let target_fs = create_target_file(&target).await?;
            let _source_bytes = progressed_copy(source_stream, target_fs, progress_noti).await?;

            // 然后应用补丁（仅将 diff 视为 URL）
            let size: usize = match diff {
                InstallFileSource::Url { size, .. } => size,
                InstallFileSource::Local { size, .. } => size,
            };
            let (diff_stream, insight_handle) = create_stream_by_source(diff).await?;
            let (diff_bytes, _) =
                progressed_hpatch(diff_stream, &target, size, |_| {}, None, None).await?;

            // 获取最终的insight
            let final_insight = if let Some(handle) = insight_handle {
                if let Ok(insight) = handle.lock() {
                    Some(insight.clone())
                } else {
                    None
                }
            } else {
                None
            };

            if args.md5.is_some() || args.xxh.is_some() {
                // 如果需要清理安装器索引标记，先清理再进行哈希校验
                if args.clear_installer_index_mark.unwrap_or(false) || override_old_path.is_some() {
                    info!("Clearing installer index mark for: {}", target);
                    if let Err(e) = crate::installer::uninstall::clear_index_mark(
                        &std::path::PathBuf::from(&target),
                    )
                    .await
                    .into_ta_result()
                    {
                        warn!("Failed to clear index mark: {:?}", e);
                        return Err(e);
                    }
                    info!("Index mark cleared successfully");
                }
                verify_hash(&target, args.md5, args.xxh).await?;
            }

            let result = InstallResult {
                bytes_transferred: diff_bytes, // 只统计差异文件的网络传输
                insight: final_insight,        // 只统计差异文件的网络统计
            };
            serde_json::to_value(result).into_ta_result()
        }
    }
}

pub async fn install_file_by_reader<C>(
    args: InstallFileArgs,
    reader: &mut C,
    notify: impl Fn(serde_json::Value) + std::marker::Send + 'static,
) -> Result<serde_json::Value>
where
    C: tokio::io::AsyncRead + Unpin + std::marker::Send,
{
    let target = args.target;
    let override_old_path = prepare_target(&target).await?;
    let progress_noti = move |downloaded: usize| {
        notify(serde_json::json!(downloaded));
    };
    match args.mode {
        InstallFileMode::Direct { .. } => {
            let res =
                progressed_copy(reader, create_target_file(&target).await?, progress_noti).await?;
            if args.md5.is_some() || args.xxh.is_some() {
                // 如果需要清理安装器索引标记，先清理再进行哈希校验
                if args.clear_installer_index_mark.unwrap_or(false) || override_old_path.is_some() {
                    info!("Clearing installer index mark for: {}", target);
                    if let Err(e) = crate::installer::uninstall::clear_index_mark(
                        &std::path::PathBuf::from(&target),
                    )
                    .await
                    {
                        warn!("Failed to clear index mark: {:?}", e);
                        return Err(e);
                    }
                    info!("Index mark cleared successfully");
                }
                verify_hash(&target, args.md5, args.xxh).await?;
            }
            Ok(serde_json::json!(res))
        }
        InstallFileMode::Patch { diff_size, .. } => {
            // 使用 progressed_copy 复制到本地缓冲区
            let mut buffer: Vec<u8> = vec![0; diff_size];
            progressed_copy(reader, &mut buffer, progress_noti).await?;
            let reader = std::io::Cursor::new(buffer);
            let is_self_update = override_old_path.is_some();
            let res =
                progressed_hpatch(reader, &target, diff_size, |_| {}, override_old_path, None)
                    .await?
                    .0;
            if args.md5.is_some() || args.xxh.is_some() {
                // 如果需要清理安装器索引标记，先清理再进行哈希校验
                if args.clear_installer_index_mark.unwrap_or(false) || is_self_update {
                    info!("Clearing installer index mark for: {}", target);
                    if let Err(e) = crate::installer::uninstall::clear_index_mark(
                        &std::path::PathBuf::from(&target),
                    )
                    .await
                    {
                        warn!("Failed to clear index mark: {:?}", e);
                        return Err(e);
                    }
                    info!("Index mark cleared successfully");
                }
                verify_hash(&target, args.md5, args.xxh).await?;
            }
            Ok(serde_json::json!(res))
        }
        InstallFileMode::HybridPatch { .. } => {
            // 此函数不支持 Hybrid patch
            Err(anyhow::anyhow!(
                "Hybrid patch is not supported in this function"
            ))
        }
    }
}

#[derive(serde::Deserialize, serde::Serialize, Clone, Debug)]
pub struct InstallMultiStreamArgs {
    url: String,
    range: String,
    chunks: Vec<InstallFileArgs>,
}
pub async fn ipc_install_multipart_stream(
    args: InstallMultiStreamArgs,
    notify: impl Fn(serde_json::Value) + std::marker::Send + 'static + Clone,
) -> TAResult<serde_json::Value> {
    let (http_stream, content_length, content_type, insight_handle) =
        create_multi_http_stream(&args.url, &args.range).await?;
    // 检查 content-type 是否为 multipart
    if content_type.starts_with("multipart/") {
        // 从 content-type 获取边界：multipart/byteranges；boundary=
        let boundary = content_type.split("boundary=").nth(1).ok_or_else(|| {
            crate::utils::error::TACommandError::new(anyhow::anyhow!(
                "Content-Type does not contain boundary"
            ))
        })?;
        let boundary = boundary.split(';').next().unwrap_or(boundary).trim();

        // 创建 multipart 读取器
        let mut multipart = multer::Multipart::new(http_stream, boundary);

        // 处理 multipart 流
        let mut mult_res = Vec::new();
        let mut chunk_index = 0usize;
        while let Some(mut field) = multipart.next_field().await.map_err(|e| {
            crate::utils::error::TACommandError::new(anyhow::anyhow!(
                "Multipart parsing error: {}",
                e
            ))
        })? {
            // 字段应包含 Content-Range
            let content_range = field
                .headers()
                .get("Content-Range")
                .and_then(|v| v.to_str().ok())
                .ok_or_else(|| {
                    crate::utils::error::TACommandError::new(anyhow::anyhow!(
                        "Field does not contain Content-Range"
                    ))
                })?;

            // 解析 content_range 并匹配对应分块
            // content_range 格式：bytes start-end/total
            let parts: Vec<&str> = content_range.split('/').collect();
            // 第一部分必须是范围
            if parts.is_empty() {
                return Err(crate::utils::error::TACommandError::new(anyhow::anyhow!(
                    "Invalid Content-Range format: {}",
                    content_range
                )));
            }
            let range = parts[0]
                .split("bytes ")
                .nth(1)
                .ok_or_else(|| {
                    crate::utils::error::TACommandError::new(anyhow::anyhow!(
                        "Content-Range does not contain range: {}",
                        content_range
                    ))
                })?
                .trim();
            let range_parts: Vec<&str> = range.split('-').collect();
            if range_parts.len() != 2 {
                return Err(crate::utils::error::TACommandError::new(anyhow::anyhow!(
                    "Invalid range format in Content-Range: {}",
                    content_range
                )));
            }
            let start: usize = range_parts[0].parse().map_err(|_| {
                crate::utils::error::TACommandError::new(anyhow::anyhow!(
                    "Invalid start range: {}",
                    content_range
                ))
            })?;
            let end: usize = range_parts[1].parse().map_err(|_| {
                crate::utils::error::TACommandError::new(anyhow::anyhow!(
                    "Invalid end range: {}",
                    content_range
                ))
            })?;

            // 将分块与对应范围匹配
            let chunk = args
                .chunks
                .iter()
                .find(|c| {
                    let source_size = get_chunk_size(c);
                    let source_pos = get_chunk_position(c);
                    let source_target = source_pos + source_size - 1;
                    start == source_pos && end == source_target
                })
                .ok_or_else(|| {
                    crate::utils::error::TACommandError::new(anyhow::anyhow!(
                        "No matching chunk found for range: {}",
                        content_range
                    ))
                })?;

            // 创建 增强 通知 回调 使用 分块 info
            let chunk_range = format!("{start}-{end}");
            let current_chunk_index = chunk_index;
            let chunk_notify = {
                let notify = notify.clone();
                let chunk_range = chunk_range.clone();
                move |progress: serde_json::Value| {
                    notify(serde_json::json!({
                        "progress": progress,
                        "chunk_index": current_chunk_index,
                        "chunk_range": chunk_range
                    }));
                }
            };

            // 获取分块的skip_decompress参数
            let should_decompress = should_decompress_chunk(chunk);

            // 读取字段数据
            let mut field_data = Vec::new();
            while let Some(chunk_bytes) = field.chunk().await.map_err(|e| {
                if let Ok(mut insight) = insight_handle.lock() {
                    insight.error = Some(e.to_string());
                }
                crate::utils::error::TACommandError::with_insight_handle(
                    anyhow::anyhow!("Field chunk read error: {}", e),
                    insight_handle.clone(),
                )
            })? {
                field_data.extend_from_slice(&chunk_bytes);
            }

            // 根据收集的字段数据创建读取器
            let reader = std::io::Cursor::new(field_data);

            // 根据参数决定是否解压缩并安装分块（install_file_by_reader 中禁用超时）
            let chunk_result = if should_decompress {
                let mut decompressed_reader = TokioZstdDecoder::new(reader);
                install_file_by_reader(chunk.clone(), &mut decompressed_reader, chunk_notify)
                    .await
                    .into_ta_result()
            } else {
                let mut raw_reader = reader;
                install_file_by_reader(chunk.clone(), &mut raw_reader, chunk_notify)
                    .await
                    .into_ta_result()
            };

            mult_res.push(chunk_result);

            chunk_index += 1;
        }
        // 获取最终的insight统计
        let final_insight = if let Ok(insight) = insight_handle.lock() {
            insight.clone()
        } else {
            InsightItem {
                url: args.url.clone(),
                ttfb: 0,
                time: 0,
                size: content_length as u32,
                error: Some("Failed to get insight".to_string()),
                range: vec![],
                mode: None,
            }
        };

        let response = serde_json::json!({
            "results": mult_res,
            "insight": final_insight
        });
        Ok(response)
    } else {
        // 服务器不支持 multipart range，可能只返回第一个分块
        if let Some(first_chunk) = args.chunks.first() {
            // 检查大小是否等于 content-length
            let source_size = get_chunk_size(first_chunk);
            let source_pos = get_chunk_position(first_chunk);
            if content_length == source_size as u64 {
                // 获取first_chunk的skip_decompress参数
                let should_decompress = should_decompress_chunk(first_chunk);

                // 继续处理第一个分块
                let stream = http_stream.map_err(std::io::Error::other);
                let reader = tokio_util::io::StreamReader::new(stream);

                // 为第一个分块创建包含分块信息的通知回调
                let chunk_notify = {
                    let notify = notify.clone();
                    move |progress: serde_json::Value| {
                        notify(serde_json::json!({
                            "progress": progress,
                            "chunk_index": 0,
                            "chunk_range": format!("{}-{}", source_pos, source_pos + source_size - 1)
                        }));
                    }
                };

                // 根据参数决定是否解压缩
                let res = if should_decompress {
                    let mut decompressed_reader = TokioZstdDecoder::new(reader);
                    install_file_by_reader(
                        first_chunk.clone(),
                        &mut decompressed_reader,
                        chunk_notify,
                    )
                    .await
                    .into_ta_result()
                } else {
                    let mut raw_reader = reader;
                    install_file_by_reader(first_chunk.clone(), &mut raw_reader, chunk_notify)
                        .await
                        .into_ta_result()
                };

                // 获取最终的insight统计
                let final_insight = if let Ok(insight) = insight_handle.lock() {
                    insight.clone()
                } else {
                    InsightItem {
                        url: args.url.clone(),
                        ttfb: 0,
                        time: 0,
                        size: content_length as u32,
                        error: Some("Failed to get insight".to_string()),
                        range: vec![],
                        mode: None,
                    }
                };

                let response = serde_json::json!({
                    "results": vec![res],
                    "insight": final_insight
                });
                Ok(response)
            } else {
                Err(crate::utils::error::TACommandError::new(anyhow::anyhow!(
                    "Server does not support multipart range, and cannot send the first chunk correctly (expected size: {}, got: {})",
                    source_size,
                    content_length
                )))
            }
        } else {
            Err(crate::utils::error::TACommandError::new(anyhow::anyhow!(
                "No chunks provided for multi-stream installation"
            )))
        }
    }
}

// 辅助 函数 到 提取 分块 大小 从 InstallFileArgs
fn get_chunk_size(args: &InstallFileArgs) -> usize {
    match &args.mode {
        InstallFileMode::Direct { source } => match source {
            InstallFileSource::Url { size, .. } | InstallFileSource::Local { size, .. } => *size,
        },
        InstallFileMode::Patch { diff_size, .. } => *diff_size,
        InstallFileMode::HybridPatch { diff, .. } => match diff {
            InstallFileSource::Url { size, .. } | InstallFileSource::Local { size, .. } => *size,
        },
    }
}

// 辅助 函数 到 提取 分块 位置 从 InstallFileArgs
fn get_chunk_position(args: &InstallFileArgs) -> usize {
    match &args.mode {
        InstallFileMode::Direct { source } => match source {
            InstallFileSource::Url { offset, .. } | InstallFileSource::Local { offset, .. } => {
                *offset
            }
        },
        InstallFileMode::Patch { source, .. } => match source {
            InstallFileSource::Url { offset, .. } | InstallFileSource::Local { offset, .. } => {
                *offset
            }
        },
        InstallFileMode::HybridPatch { diff, .. } => match diff {
            InstallFileSource::Url { offset, .. } | InstallFileSource::Local { offset, .. } => {
                *offset
            }
        },
    }
}

#[derive(Debug, Clone)]
struct ChunkWithPosition {
    position: usize,
    args: InstallFileArgs,
}

pub async fn ipc_install_multichunk_stream(
    args: InstallMultiStreamArgs,
    notify: impl Fn(serde_json::Value) + std::marker::Send + 'static + Clone,
) -> TAResult<serde_json::Value> {
    // 提取 分块 positions 从 InstallFileArgs
    let mut chunks_with_positions: Vec<ChunkWithPosition> = Vec::new();

    for chunk in &args.chunks {
        let position = get_chunk_position(chunk);
        chunks_with_positions.push(ChunkWithPosition {
            position,
            args: chunk.clone(),
        });
    }

    // 按位置排序分块，确保流式处理顺序
    chunks_with_positions.sort_by_key(|chunk| chunk.position);

    let mut results: Vec<TAResult<serde_json::Value>> = Vec::new();
    let mut stream_position = 0usize;
    let (insight_stream, _content_length, _content_type, insight_handle) =
        create_multi_http_stream(&args.url, &args.range).await?;

    // 将 HTTP 流转换为 AsyncRead
    let stream = insight_stream.map_err(std::io::Error::other);
    let mut reader = tokio_util::io::StreamReader::new(stream);

    for (chunk_index, chunk_info) in chunks_with_positions.iter().enumerate() {
        let chunk_size = get_chunk_size(&chunk_info.args);
        let chunk_offset = chunk_info.position;

        // 创建 增强 通知 回调 使用 分块 info
        let chunk_range = format!("{}-{}", chunk_offset, chunk_offset + chunk_size - 1);
        let chunk_notify = {
            let notify = notify.clone();
            let chunk_range = chunk_range.clone();
            move |progress: serde_json::Value| {
                notify(serde_json::json!({
                    "progress": progress,
                    "chunk_index": chunk_index,
                    "chunk_range": chunk_range
                }));
            }
        };

        // 跳过字节，直到到达分块位置
        if stream_position < chunk_info.position {
            let skip_bytes = chunk_info.position - stream_position;
            let mut buffer = vec![0u8; 8192]; // 8KB 缓冲区
            let mut remaining = skip_bytes;

            while remaining > 0 {
                let to_read = std::cmp::min(buffer.len(), remaining);
                let bytes_read = reader.read(&mut buffer[..to_read]).await.map_err(|e| {
                    if let Ok(mut insight) = insight_handle.lock() {
                        insight.error = Some(e.to_string());
                    }
                    crate::utils::error::TACommandError::with_insight_handle(
                        anyhow::anyhow!("Failed to skip bytes: {}", e),
                        insight_handle.clone(),
                    )
                })?;

                if bytes_read == 0 {
                    return Err(crate::utils::error::TACommandError::with_insight_handle(
                        anyhow::anyhow!("Unexpected EOF while skipping bytes"),
                        insight_handle.clone(),
                    ));
                }

                remaining -= bytes_read;
            }

            stream_position = chunk_offset;
        }

        // 处理分块
        let should_decompress = should_decompress_chunk(&chunk_info.args);

        // 先将分块数据读入内存缓冲区
        let mut chunk_buffer = vec![0u8; chunk_size];
        reader.read_exact(&mut chunk_buffer).await.map_err(|e| {
            if let Ok(mut insight) = insight_handle.lock() {
                insight.error = Some(e.to_string());
            }
            crate::utils::error::TACommandError::with_insight_handle(
                anyhow::anyhow!("Failed to read chunk data: {}", e),
                insight_handle.clone(),
            )
        })?;

        let chunk_reader = std::io::Cursor::new(chunk_buffer);

        // 处理 分块 直接 不使用 超时 监控 (NetworkInsightStream handles 它)
        let chunk_result = if should_decompress {
            let buf_reader = BufReader::new(chunk_reader);
            let mut decompressed_reader = TokioZstdDecoder::new(buf_reader);
            install_file_by_reader(
                chunk_info.args.clone(),
                &mut decompressed_reader,
                chunk_notify,
            )
            .await
            .into_ta_result()
        } else {
            let mut raw_reader = chunk_reader;
            install_file_by_reader(chunk_info.args.clone(), &mut raw_reader, chunk_notify)
                .await
                .into_ta_result()
        };

        // 处理分块结果，发生错误时更新 insight
        let final_result = chunk_result.inspect_err(|e| {
            if let Ok(mut insight) = insight_handle.lock() {
                insight.error = Some(e.to_string());
            }
        });

        results.push(final_result);
        stream_position += chunk_size;
    }

    // 获取最终的insight统计
    let final_insight = if let Ok(insight) = insight_handle.lock() {
        insight.clone()
    } else {
        InsightItem {
            url: args.url.clone(),
            ttfb: 0,
            time: 0,
            size: 0,
            error: Some("Failed to get insight".to_string()),
            range: vec![],
            mode: None,
        }
    };

    let response = serde_json::json!({
        "results": results,
        "insight": final_insight
    });
    Ok(response)
}
