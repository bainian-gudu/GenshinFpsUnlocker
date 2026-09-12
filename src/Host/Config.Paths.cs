namespace GenshinFpsUnlocker.Host;

/// <summary><c>AppConfig</c> 的路径解析：候选读取位置、数据目录与旧版配置迁移。</summary>
internal sealed partial class AppConfig
{
    private static IEnumerable<string> EnumerateCandidateReadPaths()
    {
        var list = new List<string>
        {
            ConfigPath,
            BackupPath,
            TempPath,
        };

        // 残留的进程临时文件（不可在 try/catch 内 yield）
        try
        {
            var dir = AppPaths.DataDirectory;
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, ".config.*.tmp")
                             .OrderByDescending(File.GetLastWriteTimeUtc)
                             .Take(3))
                    list.Add(f);
            }
        }
        catch { /* ignore */ }

        var legacy = AppPaths.LegacyPortableConfigPath;
        if (legacy is not null) list.Add(legacy);

        return list;
    }

    private static void EnsureDataDirectory()
    {
        try
        {
            PathUtil.EnsureDir(AppPaths.DataDirectory);
        }
        catch (Exception ex)
        {
            AppLog.Warn("EnsureDataDirectory: " + ex.Message);
            // 回退：用户文档下的旁路（极少见 LocalAppData 不可写）
            try
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    AppPaths.ProductName);
                PathUtil.EnsureDir(fallback);
            }
            catch
            {
                throw new IOException("无法创建配置目录: " + AppPaths.DataDirectory, ex);
            }
        }
    }

    /// <summary>
    /// 若 AppData 尚无配置，而 exe 旁存在旧版 config.json，则复制一次到 AppData。
    /// 不删除旧文件，避免便携场景误伤。
    /// </summary>
    private static void MigrateLegacyConfigIfNeeded()
    {
        try
        {
            if (PathUtil.ExistsFile(ConfigPath)) return;
            var legacy = AppPaths.LegacyPortableConfigPath;
            if (legacy is null) return;

            PathUtil.EnsureDir(AppPaths.DataDirectory);
            File.Copy(legacy, ConfigPath, overwrite: false);
            try { File.Copy(legacy, BackupPath, overwrite: true); } catch { /* ignore */ }
            AppLog.Info("migrated portable config → " + ConfigPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn("config migrate: " + ex.Message);
        }
    }
}
