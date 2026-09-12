// ^(?:(dfs)\+)?(?:(hashed|packed|auto)\+)?(http(?:s)?:\/\/(?:.*?))$
export interface SourceItem {
  uri: string;
  id: string;
  name: string;
  hidden: boolean;
  icon?: string; // 可选的SVG图标字符串
}
/**
 * 用户协议：打包时由项目配置的 `agreementFile` 内联生成（见
 * src-tauri/src/builder/pack.rs 的 resolve_agreement），运行期直接读取，
 * 不依赖网络与外部文件。
 */
export type AgreementFormat = 'text' | 'markdown' | 'html';

export type AgreementConfig = {
  /** 链接与弹窗标题，默认「用户协议」 */
  title?: string;
  /** 正文渲染方式，默认 text */
  format?: AgreementFormat;
  /** 正文（text/markdown/html 源码） */
  content: string;
};

/**
 * 卸载时额外清理的注册表项：安装期写入、需要随卸载回收的内容，
 * 例如开机自启动 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
 * ARP 卸载项由卸载器按 regName 自动清理，无需在此声明。
 */
export type RegistryCleanupItem = {
  /** HKCU / HKLM / HKCR / HKU（大小写不敏感，也接受全称） */
  hive: string;
  /** 子键路径，如 `Software\Microsoft\Windows\CurrentVersion\Run` */
  key: string;
  /** 仅删除该键下的这一个值；省略则递归删除整个子键 */
  value?: string;
};

export type ProjectConfig = {
  source: string | SourceItem[];
  appName: string;
  publisher: string;
  regName: string;
  exeName: string;
  uninstallName: string;
  updaterName: string;
  programFilesPath: string;
  userDataPath: string[];
  ignoreFolderPath?: string[];
  extraUninstallPath: string[];
  title: string;
  description: string;
  windowTitle: string;
  // UAC 策略
  // prefer-admin: 除非用户安装在%User%、%AppData%、%Documents%、%Desktop%、%Downloads%目录，都请求UAC
  // prefer-user: 只在用户没有权限写入的目录请求UAC
  // force: 强制请求UAC
  uacStrategy: 'prefer-admin' | 'prefer-user' | 'force';
  runtimes?: string[];
  windowBorderless?: boolean;
  /** 用户协议（有内容时界面上的「用户协议」可点击弹窗查看） */
  agreement?: AgreementConfig;
  /** 卸载时额外清理的注册表项 */
  extraUninstallRegistry?: RegistryCleanupItem[];
  /**
   * 卸载时额外清理的快捷方式**文件名**（不是完整路径）。
   * 目录由卸载器用 shell API 解析（公共桌面 / 用户桌面 / 公共开始菜单 /
   * 用户开始菜单四侧都试），因此桌面被 OneDrive 重定向也能命中。
   * 删不掉只记日志，不会让卸载失败。
   */
  extraUninstallLnkNames?: string[];
};

export type InstallStat = {
  speedLastSize: number;
  lastTime: DOMHighResTimeStamp;
  speed: number;
};

export type DfsMetadataHashType = 'md5' | 'xxh';

export type DfsMetadataHashInfo = {
  file_name: string;
  size: number;
  md5?: string;
  xxh?: string;
  installer?: true;
};

export type DfsMetadataPatchInfo = {
  file_name: string;
  size: number;
  from: Omit<DfsMetadataHashInfo, 'file_name'>;
  to: Omit<DfsMetadataHashInfo, 'file_name'>;
};

export interface DfsUpdateTask extends DfsMetadataHashInfo {
  patch?: DfsMetadataPatchInfo;
  lpatch?: DfsMetadataPatchInfo;
  downloaded: number;
  running: boolean;
  old_hash?: string;
  unwritable: boolean;
  failed?: true;
  errorMessage?: string; // 用于存储合并下载中的单个文件错误信息
}

// 合并下载相关类型定义
export interface FileWithPosition extends DfsUpdateTask {
  dfsOffset: number;
  dfsSize: number;
}

export interface MergedGroupInfo {
  files: DfsUpdateTask[];
  mergedRange: string;
  totalDownloadSize: number;
  totalEffectiveSize: number;
  wasteRatio: number;
  gaps: Array<{start: number, end: number}>;
}

export interface VirtualMergedFile extends DfsUpdateTask {
  _isMergedGroup: true;
  _mergedInfo: MergedGroupInfo;
  _fallbackFiles: DfsUpdateTask[];
}

export type InvokeGetDfsMetadataRes = {
  tag_name: string;
  hashed: Array<DfsMetadataHashInfo>;
  patches?: Array<DfsMetadataPatchInfo>;
  installer?: {
    size: number;
    md5?: string;
    xxh?: string;
  };
  deletes?: string[];
};

export type InvokeDeepReaddirWithMetadataRes = Array<{
  file_name: string;
  size: number;
  hash: string;
  unwritable: boolean;
}>;

export type InvokeGetDfsRes = {
  url?: string;
  tests?: Array<[string, string]>;
  source: string;
};

// DFS2 types
export type Dfs2Metadata = {
  resource_version: string;
  name: string;
  data: Dfs2Data | null;
};

export type Dfs2Data = {
  index: Record<string, Dfs2FileInfo>;
  metadata: InvokeGetDfsMetadataRes;
  installer_end: number;
};

export type Dfs2FileInfo = {
  name: string;
  offset: number;
  raw_offset: number;
  size: number;
};

export type Dfs2SessionResponse = {
  tries?: string[];
  sid?: string;
  challenge?: string;
  data?: string;
};

export type Dfs2ChunkResponse = {
  url: string;
};

export type Dfs2BatchChunkRequest = {
  chunks: string[];
};

export type Dfs2ChunkUrlResult = {
  url?: string;
  error?: string;
};

export type Dfs2BatchChunkResponse = {
  urls: Record<string, Dfs2ChunkUrlResult>;
};

export interface InsightItem {
  url: string;
  ttfb: number; // 首字节时间(ms)
  time: number; // 纯下载时间(ms) = 总时间 - TTFB
  size: number; // 实际下载字节数
  error?: string;
  range?: [number, number][]; // HTTP Range请求范围
  mode?: string; // 安装模式
}

export interface InstallResult {
  bytes_transferred: number;
  insight?: InsightItem;
}

export type Dfs2SessionInsights = {
  servers: InsightItem[];
};

export interface TAErrorData {
  message: string;
  insight?: InsightItem;
}

export class TAError extends Error {
  public readonly insight?: InsightItem;

  constructor(data: TAErrorData | string) {
    if (typeof data === 'string') {
      super(data);
    } else {
      super(data.message);
      this.insight = data.insight;
    }
  }

  static fromErrorData(data: TAErrorData): TAError {
    return new TAError(data);
  }
}

export type InvokeGetDirsRes = [string, string];

export type InvokeSelectDirRes = {
  path: string;
  state: 'Unwritable' | 'Writable' | 'Private';
  empty: boolean;
  upgrade: boolean;
} | null;

export interface Embedded {
  name: string;
  offset: number;
  raw_offset: number;
  size: number;
}

export interface InstallerConfig {
  install_path: string;
  install_path_exists: boolean;
  install_path_source:
    | 'CURRENT_DIR'
    | 'PARENT_DIR'
    | 'REG'
    | 'REG_FOLDED'
    | 'DEFAULT';
  is_uninstall: boolean;
  embedded_files: Embedded[] | null;
  embedded_index: Embedded[] | null;
  embedded_config: ProjectConfig | null;
  enbedded_metadata: InvokeGetDfsMetadataRes | null;
  embedded_image: string | null;
  exe_path: string;
  args: {
    target: string | null;
    non_interactive: boolean;
    silent: boolean;
    online: boolean;
    uninstall: boolean;
    source?: string;
    dfs_extras?: string;
    mirrorc_cdk?: string;
  };
  elevated: boolean;
}

export interface HttpGetResponse {
  status_code: number;
  headers: Record<string, string>;
  body: string;
  final_url: string;
}
