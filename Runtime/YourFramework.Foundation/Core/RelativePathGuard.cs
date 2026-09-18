using System;
using System.IO;

namespace YourFramework.Core
{
    /// <summary>
    /// 相对路径防护：清单是从网上来的，它里面的文件名**不可全信**。
    ///
    /// 这是热更程序集与脚本块共用的同一道检查：拒绝绝对路径、拒绝任何一段「..」，
    /// 一个被篡改的清单不能借文件名读到缓存目录之外的任意文件。
    ///
    /// 之所以单独成件而不是各写一遍：这道检查是**安全属性**，两处各写一遍就等于两个
    /// 地方可能各漏一次；而且它是可以离线检查到字节的纯函数（不含任何 IO）。
    /// </summary>
    public static class RelativePathGuard
    {
        /// <summary>
        /// 校验一个清单里的文件名。合法返回 true；不合法返回 false 并把**为什么**写进
        /// <paramref name="problem"/>（调用方在前面补一句「这是什么文件名」，拼成完整原因）。
        ///
        /// 空路径按不合法处理 —— 「没写文件名」和「写了非法文件名」对调用方是同一种处置。
        /// </summary>
        public static bool TryValidate(string relativePath, out string problem)
        {
            problem = null;

            if (string.IsNullOrEmpty(relativePath))
            {
                problem = "must not be empty";
                return false;
            }

            if (Path.IsPathRooted(relativePath) || HasParentSegment(relativePath))
            {
                problem = "'" + relativePath + "' must be a relative path without '..'"
                    + " (a manifest is not allowed to escape the cache directory)";
                return false;
            }

            return true;
        }

        /// <summary>路径里是否含「..」段。正反斜杠都算段分隔符（清单可能在任何平台生成）。</summary>
        public static bool HasParentSegment(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string normalized = path.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == "..")
                {
                    return true;
                }
            }

            return false;
        }
    }
}
