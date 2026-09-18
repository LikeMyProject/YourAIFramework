using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using YourAI.Core.Json;

namespace YourFramework.Assets.Pack
{
    /// <summary>清单写入时的 bundle 条目（构建期的输入）。</summary>
    public sealed class BundleSeed
    {
        /// <summary>Bundle 名。</summary>
        public string Name;

        /// <summary>内容指纹；空串表示后端不提供（差量退化为 size+crc 比对）。</summary>
        public string Hash;

        /// <summary>CRC32。</summary>
        public uint Crc;

        /// <summary>字节数。</summary>
        public long Size;

        /// <summary>标签（可空）。</summary>
        public List<string> Tags;

        /// <summary>依赖的 bundle 名（可空）。</summary>
        public List<string> Dependencies;
    }

    /// <summary>清单写入时的资源条目（构建期的输入）。</summary>
    public sealed class AssetSeed
    {
        /// <summary>可寻址地址。</summary>
        public string Location;

        /// <summary>bundle 内部资源名；空则写 location（读回时缺省也是 location）。</summary>
        public string AssetName;

        /// <summary>所属 bundle 名。</summary>
        public string Bundle;

        /// <summary>标签（可空）。</summary>
        public List<string> Labels;
    }

    /// <summary>
    /// 清单写出器：把构建期的条目表写成 PackageManifest 认得的那份 JSON。
    ///
    /// 为什么写在 Foundation（而不是 Editor 里）：写与读是一对可检查项的契约，
    /// 往返测试（Write → LoadJson → 逐字段比对）必须在离线检查里跑得起来。
    /// 输出保持确定性成员顺序与不变文化数字格式，两次构建同内容必然同字节 ——
    /// 否则"内容没变"的差量判断会被无意义的文本抖动击穿。
    /// </summary>
    public static class ManifestWriter
    {
        /// <summary>Serialises a manifest. Null lists are written as empty arrays.</summary>
        public static string Write(
            string packageName, string version, IList<BundleSeed> bundles, IList<AssetSeed> assets)
        {
            if (string.IsNullOrEmpty(packageName))
            {
                throw new ArgumentException("ManifestWriter: packageName must not be empty.", "packageName");
            }

            if (string.IsNullOrEmpty(version))
            {
                throw new ArgumentException("ManifestWriter: version must not be empty.", "version");
            }

            if (bundles == null)
            {
                throw new ArgumentNullException("bundles");
            }

            if (assets == null)
            {
                throw new ArgumentNullException("assets");
            }

            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"packageName\":\"").Append(JsonParser.Escape(packageName))
                .Append("\",\"version\":\"").Append(JsonParser.Escape(version))
                .Append("\",\"bundles\":[");

            for (int i = 0; i < bundles.Count; i++)
            {
                BundleSeed bundle = bundles[i];
                if (bundle == null || string.IsNullOrEmpty(bundle.Name))
                {
                    throw new ArgumentException(
                        "ManifestWriter: bundles[" + i + "] must have a non-empty name.", "bundles");
                }

                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"name\":\"").Append(JsonParser.Escape(bundle.Name))
                    .Append("\",\"hash\":\"").Append(JsonParser.Escape(bundle.Hash ?? string.Empty))
                    .Append("\",\"crc\":").Append(bundle.Crc.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"size\":").Append(bundle.Size.ToString(CultureInfo.InvariantCulture));

                WriteStringArray(sb, "tags", bundle.Tags);
                WriteStringArray(sb, "dependencies", bundle.Dependencies);
                sb.Append('}');
            }

            sb.Append("],\"assets\":[");
            for (int i = 0; i < assets.Count; i++)
            {
                AssetSeed asset = assets[i];
                if (asset == null || string.IsNullOrEmpty(asset.Location))
                {
                    throw new ArgumentException(
                        "ManifestWriter: assets[" + i + "] must have a non-empty location.", "assets");
                }

                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"location\":\"").Append(JsonParser.Escape(asset.Location))
                    .Append("\",\"assetName\":\"").Append(JsonParser.Escape(
                        string.IsNullOrEmpty(asset.AssetName) ? asset.Location : asset.AssetName))
                    .Append("\",\"bundle\":\"").Append(JsonParser.Escape(asset.Bundle ?? string.Empty))
                    .Append('"');

                WriteStringArray(sb, "labels", asset.Labels);
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>Serialises a version file from bundle seeds (the fingerprint half of a manifest).</summary>
        public static string WriteVersion(string packageName, string version, IList<BundleSeed> bundles)
        {
            if (string.IsNullOrEmpty(packageName))
            {
                throw new ArgumentException("ManifestWriter: packageName must not be empty.", "packageName");
            }

            if (string.IsNullOrEmpty(version))
            {
                throw new ArgumentException("ManifestWriter: version must not be empty.", "version");
            }

            if (bundles == null)
            {
                throw new ArgumentNullException("bundles");
            }

            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"packageName\":\"").Append(JsonParser.Escape(packageName))
                .Append("\",\"version\":\"").Append(JsonParser.Escape(version))
                .Append("\",\"bundles\":[");

            for (int i = 0; i < bundles.Count; i++)
            {
                BundleSeed bundle = bundles[i];
                if (bundle == null || string.IsNullOrEmpty(bundle.Name))
                {
                    throw new ArgumentException(
                        "ManifestWriter: bundles[" + i + "] must have a non-empty name.", "bundles");
                }

                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"name\":\"").Append(JsonParser.Escape(bundle.Name))
                    .Append("\",\"hash\":\"").Append(JsonParser.Escape(bundle.Hash ?? string.Empty))
                    .Append("\",\"size\":").Append(bundle.Size.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"crc\":").Append(bundle.Crc.ToString(CultureInfo.InvariantCulture))
                    .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static void WriteStringArray(StringBuilder sb, string key, List<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return; // absent means "none": shorter files, identical semantics
            }

            sb.Append(",\"").Append(key).Append("\":[");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append('"').Append(JsonParser.Escape(values[i] ?? string.Empty)).Append('"');
            }

            sb.Append(']');
        }
    }
}
