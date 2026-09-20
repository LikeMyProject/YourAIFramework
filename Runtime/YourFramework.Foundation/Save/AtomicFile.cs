using System.IO;

namespace YourFramework.Save
{
    /// <summary>
    /// 落盘的最后一厘米：把刚写好的 .tmp 顶上目标文件。
    ///
    /// 这里必须用操作系统的替换操作，**不能用 <c>File.Copy(tmp, path, true)</c>**。
    /// <c>File.Copy</c> 覆盖已有文件时是“打开目标 -> 截断 -> 逐字节写”，进程恰好死在
    /// 中间，目标就成了半截文件；<c>File.Replace</c> 走 Windows 的 ReplaceFile / Unix 的
    /// rename，是“要么旧、要么新”的原子动作，失败时目标原样不动。
    ///
    /// <paramref name="backup"/> 不为 null 且目标存在时，旧内容会被挪到那个位置。
    /// 这一步只在替换成功后才发生，所以备份永远不会先于新档消失 —— 反过来做
    /// （先把当前档挪走、再写新档、再覆盖回来）会在中间留出一段“盘上没有档”的窗口。
    /// </summary>
    internal static class AtomicFile
    {
        /// <summary>用 <paramref name="tmp"/> 替换 <paramref name="path"/>，旧内容可选地落到 <paramref name="backup"/>。</summary>
        internal static void Replace(string tmp, string path, string backup)
        {
            if (!File.Exists(path))
            {
                // 首次落盘：没有旧档要备份，同目录改名本身就是原子的。
                File.Move(tmp, path);
                return;
            }

            if (backup == null)
            {
                File.Replace(tmp, path, null);
                return;
            }

            // 某些实现要求备份位为空，先清掉。
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Replace(tmp, path, backup);
        }
    }
}
