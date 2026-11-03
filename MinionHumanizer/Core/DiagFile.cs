using System;
using System.IO;
using System.Text;

namespace MinionHumanizer.Core;

/// <summary>
/// 把诊断信息写到**插件自己的文件**里，不依赖 Dalamud 日志。
///
/// <para>
/// 为什么需要它：<c>%APPDATA%\XIVLauncherCN\dalamud.log</c> 有 100MB 上限，
/// 一旦被别的插件刷满（实测被 VFXEditor 刷到 104MB 停写），
/// <b>插件日志就再也写不进去了</b>——此时"看日志排查"这条路直接断掉。
/// 自己的文件不受影响，而且可以被外部直接读取。
/// </para>
/// </summary>
public static class DiagFile
{
    private static string? _path;

    /// <summary>诊断文件路径。首次调用时才解析目录（那时 PluginInterface 才可用）。</summary>
    public static string Path => _path ??= ResolvePath();

    private static string ResolvePath()
    {
        try
        {
            // ConfigDirectory 是 DirectoryInfo（不是 string）。
            var dir = Plugin.PluginInterface.ConfigDirectory;
            if (dir is not null)
            {
                Directory.CreateDirectory(dir.FullName);
                return System.IO.Path.Combine(dir.FullName, "last-diag.txt");
            }
        }
        catch
        {
            // 拿不到配置目录就退回临时目录，不能因此让插件失败。
        }

        return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MinionHumanizer-diag.txt");
    }

    /// <summary>
    /// 写一个<b>独立命名</b>的诊断文件（不与 last-diag.txt 互相覆盖）。
    /// 逐帧追踪这类"跑完就没了"的数据必须用这个，否则会被定时诊断冲掉。
    /// </summary>
    public static bool WriteTo(string fileName, string content)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("==== MinionHumanizer ").Append(fileName)
              .Append(" @ ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine(" ====");
            sb.AppendLine($"版本：{Plugin.DisplayVersion}");
            sb.AppendLine(content);
            File.WriteAllText(SiblingPath(fileName), sb.ToString(), Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            try { Plugin.Log?.Warning(ex, "[MinionHumanizer] 诊断文件写入失败：{0}", fileName); } catch { }
            return false;
        }
    }

    /// <summary>与 last-diag.txt 同目录的某个文件名。</summary>
    public static string SiblingPath(string fileName)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        return System.IO.Path.Combine(string.IsNullOrEmpty(dir) ? "." : dir, fileName);
    }

    /// <summary>写一份带时间戳的诊断快照。写失败只记录，不抛——诊断不该让插件崩。</summary>
    public static bool Write(string content)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("==== MinionHumanizer 诊断 @ ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).AppendLine(" ====");
            sb.AppendLine($"版本：{Plugin.DisplayVersion}");
            sb.AppendLine(content);
            File.WriteAllText(Path, sb.ToString(), Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            try { Plugin.Log?.Warning(ex, "[MinionHumanizer] 诊断文件写入失败：{0}", Path); } catch { }
            return false;
        }
    }
}
