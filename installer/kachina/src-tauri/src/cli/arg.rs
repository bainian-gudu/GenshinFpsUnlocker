use std::path::PathBuf;

use clap::Subcommand;

#[derive(Debug, Clone, clap::Args, serde::Serialize)]
pub struct InstallArgs {
    #[clap(short = 'D', help = "Install directory")]
    pub target: Option<PathBuf>,
    #[clap(short = 'I', help = "Non-interactive install")]
    pub non_interactive: bool,
    #[clap(short = 'S', help = "Silent install")]
    pub silent: bool,
    #[clap(short = 'O', help = "Force online install")]
    pub online: bool,
    #[clap(short = 'U', help = "Uninstall")]
    pub uninstall: bool,
    // 覆盖安装来源
    #[clap(long, hide = true)]
    pub source: Option<String>,
    // DFS 附加数据
    #[clap(long, hide = true)]
    pub dfs_extras: Option<String>,
    // 相关实现：override mirrorc cdk
    #[clap(long, hide = true)]
    pub mirrorc_cdk: Option<String>,
}

#[derive(Debug, Clone, clap::Args)]
pub struct UacArgs {
    pub pipe_id: String,
}

#[derive(Subcommand, Clone, Debug)]
pub enum Command {
    #[clap(hide = true)]
    Install(InstallArgs),
    #[clap(hide = true)]
    InstallWebview2,
    #[clap(hide = true)]
    HeadlessUac(UacArgs),
    // clap 的 external_subcommand 只在解析阶段写入该字段，编译期看不到读取方
    #[clap(external_subcommand)]
    #[allow(dead_code)]
    Other(Vec<String>),
}
