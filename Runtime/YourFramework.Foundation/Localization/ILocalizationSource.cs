using System;

namespace YourFramework.Localization
{
    /// <summary>
    /// 外部取词源：本地化存储全链未命中时的可选后端（自研本地化包、
    /// 服务器下发词典等）。实现必须是可重入的纯查询——不加载资源、不落 IO
    /// 阻塞；取不到就返回 false，由存储继续走主键兜底。
    /// </summary>
    public interface ILocalizationSource
    {
        /// <summary>Human-readable source name (diagnostics).</summary>
        string Name { get; }

        /// <summary>
        /// Attempts to resolve the key under the requested language. Contract:
        /// no exceptions on miss (return false), text must be non-null when
        /// true, and the call must be cheap enough for a lookup path.
        /// </summary>
        bool TryGet(string language, string key, out string text);
    }
}
