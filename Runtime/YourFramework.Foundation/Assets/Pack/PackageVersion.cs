using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using YourAI.Core.Json;

namespace YourFramework.Assets.Pack
{
    /// <summary>版本文件里一个 bundle 的指纹：只够判断"要不要重下"，不重复清单里的依赖信息。</summary>
    public sealed class VersionFingerprint
    {
        /// <summary>Bundle 名。</summary>
        public string Name;

        /// <summary>内容指纹。</summary>
        public string Hash;

        /// <summary>字节数。</summary>
        public long Size;

        /// <summary>CRC32。</summary>
        public uint Crc;
    }

    /// <summary>
    /// 版本文件：包名 + 版本号 + 全部 bundle 的指纹表。
    ///
    /// 与清单的分工：清单回答"有什么、依赖谁"（构建期一次成型，内容不随版本变），
    /// 版本文件回答"这一版每个 bundle 长什么样"（每次发版重写，体积小、可比对）。
    /// 热更第一现场就是拿本地版本文件与远端版本文件比差量，得出下载名单。
    /// 只有版本号没有指纹表的实现无法做增量，只能整包重下。
    /// </summary>
    public sealed class PackageVersion
    {
        private readonly List<VersionFingerprint> _bundles = new List<VersionFingerprint>();
        private readonly Dictionary<string, VersionFingerprint> _byName =
            new Dictionary<string, VersionFingerprint>(StringComparer.Ordinal);

        /// <summary>包名。</summary>
        public string PackageName { get; private set; }

        /// <summary>版本号（形如 1.2.0，比较用序数相等，不做语义化版本排序）。</summary>
        public string Version { get; private set; }

        /// <summary>指纹表，文件原始顺序。</summary>
        public List<VersionFingerprint> Bundles { get { return _bundles; } }

        /// <summary>全包字节总量（声明值）。</summary>
        public long TotalBytes { get; private set; }

        /// <summary>Parses a version file. Structural defects fail fast.</summary>
        public static PackageVersion LoadJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException("PackageVersion: json must not be null or empty.", "json");
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("PackageVersion: invalid JSON: " + ex.Message, ex);
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                throw new ArgumentException("PackageVersion: root must be a JSON object.");
            }

            PackageVersion version = new PackageVersion();
            version.PackageName = RequireString(root, "packageName");
            version.Version = RequireString(root, "version");

            JsonValue bundles = root["bundles"];
            if (bundles == null || bundles.Kind != JsonKind.Array)
            {
                throw new ArgumentException("PackageVersion: 'bundles' must be an array.");
            }

            for (int i = 0; i < bundles.Items.Count; i++)
            {
                JsonValue item = bundles.Items[i];
                if (item == null || item.Kind != JsonKind.Object)
                {
                    throw new ArgumentException("PackageVersion: bundles[" + i + "] must be an object.");
                }

                VersionFingerprint fingerprint = new VersionFingerprint();
                fingerprint.Name = RequireString(item, "name");
                if (version._byName.ContainsKey(fingerprint.Name))
                {
                    throw new ArgumentException(
                        "PackageVersion: duplicate bundle '" + fingerprint.Name + "'.");
                }

                JsonValue hash = item["hash"];
                fingerprint.Hash = hash.Kind == JsonKind.String ? hash.StringValue : string.Empty;

                JsonValue size = item["size"];
                fingerprint.Size = size.Kind == JsonKind.Number ? (long)size.NumberValue : 0;
                if (fingerprint.Size < 0)
                {
                    throw new ArgumentException(
                        "PackageVersion: bundle '" + fingerprint.Name + "' has a negative size.");
                }

                JsonValue crc = item["crc"];
                fingerprint.Crc = crc.Kind == JsonKind.Number ? (uint)crc.NumberValue : 0u;

                version._bundles.Add(fingerprint);
                version._byName.Add(fingerprint.Name, fingerprint);
                version.TotalBytes += fingerprint.Size;
            }

            return version;
        }

        /// <summary>Serialises back to the file format (deterministic member order).</summary>
        public string ToJson()
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"packageName\":\"").Append(JsonParser.Escape(PackageName ?? string.Empty))
                .Append("\",\"version\":\"").Append(JsonParser.Escape(Version ?? string.Empty))
                .Append("\",\"bundles\":[");

            for (int i = 0; i < _bundles.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                VersionFingerprint fingerprint = _bundles[i];
                sb.Append("{\"name\":\"").Append(JsonParser.Escape(fingerprint.Name ?? string.Empty))
                    .Append("\",\"hash\":\"").Append(JsonParser.Escape(fingerprint.Hash ?? string.Empty))
                    .Append("\",\"size\":").Append(fingerprint.Size.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"crc\":").Append(fingerprint.Crc.ToString(CultureInfo.InvariantCulture))
                    .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>Fingerprint lookup by bundle name.</summary>
        public bool TryGet(string bundle, out VersionFingerprint fingerprint)
        {
            fingerprint = null;
            return bundle != null && _byName.TryGetValue(bundle, out fingerprint);
        }

        private static string RequireString(JsonValue owner, string key)
        {
            string text = owner[key].AsNonEmptyString();
            if (text == null)
            {
                throw new ArgumentException(
                    "PackageVersion: missing a non-empty string '" + key + "'.");
            }

            return text;
        }
    }

    /// <summary>差量结果：该下什么、能删什么、总共要下多少字节。</summary>
    public sealed class PackageDiffResult
    {
        /// <summary>远端有、本地没有的 bundle。</summary>
        public readonly List<string> Added = new List<string>();

        /// <summary>两边都有但指纹不同的 bundle（要重下）。</summary>
        public readonly List<string> Changed = new List<string>();

        /// <summary>指纹一致的 bundle（不用动）。</summary>
        public readonly List<string> Unchanged = new List<string>();

        /// <summary>本地有、远端已移除的 bundle（缓存可清理）。</summary>
        public readonly List<string> Removed = new List<string>();

        /// <summary>本次需要下载的总字节数（Added + Changed）。</summary>
        public long DownloadBytes;

        /// <summary>本次需要下载的 bundle 个数。</summary>
        public int DownloadCount { get { return Added.Count + Changed.Count; } }

        /// <summary>本地已是最新：无需下载、无需清理。</summary>
        public bool IsUpToDate
        {
            get { return Added.Count == 0 && Changed.Count == 0 && Removed.Count == 0; }
        }

        /// <summary>给 UI 用的一句话（失败/进度都据它有话可说）。</summary>
        public string Explain()
        {
            if (IsUpToDate)
            {
                return "已是最新版本";
            }

            return "新增 " + Added.Count + " 个、更新 " + Changed.Count + " 个、移除 "
                + Removed.Count + " 个，需下载 " + DownloadBytes + " 字节";
        }
    }

    /// <summary>
    /// 版本差量：本地版本文件 × 远端版本文件 → 下载名单。
    ///
    /// 语义：
    /// - local 为 null 表示全新安装（远端全部算新增）；
    /// - 指纹（Hash 优先，空 Hash 则比 Size+Crc）不同即 Changed；
    /// - Removed 只是"本地可清理"的建议，不参与下载字节统计。
    /// </summary>
    public static class PackageDiff
    {
        /// <summary>Computes the diff between a local version (may be null) and the remote one.</summary>
        public static PackageDiffResult Compute(PackageVersion local, PackageVersion remote)
        {
            if (remote == null)
            {
                throw new ArgumentNullException("remote");
            }

            PackageDiffResult result = new PackageDiffResult();

            for (int i = 0; i < remote.Bundles.Count; i++)
            {
                VersionFingerprint remoteFingerprint = remote.Bundles[i];
                VersionFingerprint localFingerprint;
                if (local == null || !local.TryGet(remoteFingerprint.Name, out localFingerprint))
                {
                    result.Added.Add(remoteFingerprint.Name);
                    result.DownloadBytes += remoteFingerprint.Size;
                    continue;
                }

                if (SameContent(localFingerprint, remoteFingerprint))
                {
                    result.Unchanged.Add(remoteFingerprint.Name);
                }
                else
                {
                    result.Changed.Add(remoteFingerprint.Name);
                    result.DownloadBytes += remoteFingerprint.Size;
                }
            }

            if (local != null)
            {
                for (int i = 0; i < local.Bundles.Count; i++)
                {
                    VersionFingerprint localFingerprint = local.Bundles[i];
                    VersionFingerprint remoteFingerprint;
                    if (!remote.TryGet(localFingerprint.Name, out remoteFingerprint))
                    {
                        result.Removed.Add(localFingerprint.Name);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Same-content test. Hash is authoritative when both sides carry one;
        /// otherwise size + CRC stand in, which is weaker but still catches every
        /// realistic rebuild.
        /// </summary>
        public static bool SameContent(VersionFingerprint left, VersionFingerprint right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            bool leftHasHash = !string.IsNullOrEmpty(left.Hash);
            bool rightHasHash = !string.IsNullOrEmpty(right.Hash);
            if (leftHasHash && rightHasHash)
            {
                return string.Equals(left.Hash, right.Hash, StringComparison.Ordinal);
            }

            return left.Size == right.Size && left.Crc == right.Crc;
        }
    }
}
