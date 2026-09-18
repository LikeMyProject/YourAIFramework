using System;
using System.Collections.Generic;
using YourAI.Core.Json;

namespace YourFramework.Assets.Pack
{
    /// <summary>清单里的一个 Bundle 条目：名字 + 指纹（hash/crc/size）+ 标签 + 依赖。</summary>
    public sealed class ManifestBundle
    {
        /// <summary>Bundle 名（包内唯一，通常 = 构建时的 AssetBundle 名）。</summary>
        public string Name;

        /// <summary>内容指纹：内容变则变，版本差量比对的就是它。</summary>
        public string Hash;

        /// <summary>CRC32：下载落地后的完整性校验值。</summary>
        public uint Crc;

        /// <summary>字节数：下载进度、容量规划、缓存逐出都用它。</summary>
        public long Size;

        /// <summary>标签：按标签批量下载 / 预载（如 "preload"、"ui"）。</summary>
        public List<string> Tags;

        /// <summary>依赖的其它 bundle。加载顺序 = 拓扑序（被依赖者先）。</summary>
        public List<string> Dependencies;
    }

    /// <summary>清单里的一个资源条目：可寻址地址 → 所属 bundle。</summary>
    public sealed class ManifestAsset
    {
        /// <summary>可寻址地址（游戏代码 Load 用的 key）。</summary>
        public string Location;

        /// <summary>bundle 内部的资源名；缺省与 Location 相同。</summary>
        public string AssetName;

        /// <summary>所属 bundle 名。</summary>
        public string Bundle;

        /// <summary>标签（可选）。</summary>
        public List<string> Labels;
    }

    /// <summary>
    /// 资源包清单：一个包的全部 bundle + 可寻址资源 + 依赖关系，一次解析、只读使用。
    ///
    /// 设计取舍：
    /// 1. 清单是**构建期产物**，坏清单（悬空依赖、重名、地址重复、成环）一律 fail-fast
    ///    抛异常 —— 这是构建管线写错的 bug，不是运行期故障，不该拖到运行时才以
    ///    "加载失败" 的面目出现；
    /// 2. 解析后建立两张索引（bundle 名、地址），运行期查找 O(1) 零分配；
    /// 3. 成环检测用 Kahn 拓扑排序一次性完成：既能报出环上的 bundle 名，又顺带证明
    ///    清单可拓扑排序（加载顺序的前提）。
    /// </summary>
    public sealed class PackageManifest
    {
        private readonly List<ManifestBundle> _bundles = new List<ManifestBundle>();
        private readonly List<ManifestAsset> _assets = new List<ManifestAsset>();
        private readonly Dictionary<string, ManifestBundle> _byName =
            new Dictionary<string, ManifestBundle>(StringComparer.Ordinal);
        private readonly Dictionary<string, ManifestAsset> _byLocation =
            new Dictionary<string, ManifestAsset>(StringComparer.Ordinal);

        private PackageManifest(string packageName, string version)
        {
            PackageName = packageName;
            Version = version;
        }

        /// <summary>包名（多包工程里区分彼此）。</summary>
        public string PackageName { get; private set; }

        /// <summary>清单版本号（与 PackageVersion.Version 对齐）。</summary>
        public string Version { get; private set; }

        /// <summary>全部 bundle 条目，清单原始顺序。</summary>
        public List<ManifestBundle> Bundles { get { return _bundles; } }

        /// <summary>全部资源条目，清单原始顺序。</summary>
        public List<ManifestAsset> Assets { get { return _assets; } }

        /// <summary>全包字节总量（数字来自清单声明，不是实测）。</summary>
        public long TotalBytes { get; private set; }

        /// <summary>
        /// Parses a manifest. Every structural defect throws
        /// <see cref="ArgumentException"/> with the offending identity in the
        /// message, because a bad manifest is a build artifact bug.
        /// </summary>
        public static PackageManifest LoadJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentException("PackageManifest: json must not be null or empty.", "json");
            }

            JsonValue root;
            try
            {
                root = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("PackageManifest: invalid JSON: " + ex.Message, ex);
            }

            if (root == null || root.Kind != JsonKind.Object)
            {
                throw new ArgumentException("PackageManifest: root must be a JSON object.");
            }

            string packageName = RequiredString(root, "packageName", "(root)");
            string version = RequiredString(root, "version", "(root)");

            PackageManifest manifest = new PackageManifest(packageName, version);
            manifest.ReadBundles(root["bundles"]);
            manifest.ReadAssets(root["assets"]);
            manifest.VerifyAcyclic();
            return manifest;
        }

        /// <summary>Looks a bundle up by name.</summary>
        public bool TryGetBundle(string name, out ManifestBundle bundle)
        {
            bundle = null;
            return name != null && _byName.TryGetValue(name, out bundle);
        }

        /// <summary>Looks an asset up by address.</summary>
        public bool TryGetAsset(string location, out ManifestAsset asset)
        {
            asset = null;
            return location != null && _byLocation.TryGetValue(location, out asset);
        }

        /// <summary>Whether the address exists (provider routing predicate).</summary>
        public bool ContainsLocation(string location)
        {
            return location != null && _byLocation.ContainsKey(location);
        }

        /// <summary>Dependencies of a bundle, or an empty list when it has none.</summary>
        public static List<string> DependenciesOf(ManifestBundle bundle)
        {
            return bundle.Dependencies ?? EmptyDependencies;
        }

        private static readonly List<string> EmptyDependencies = new List<string>();

        private void ReadBundles(JsonValue bundles)
        {
            if (bundles == null || bundles.Kind != JsonKind.Array)
            {
                throw new ArgumentException("PackageManifest: 'bundles' must be an array.");
            }

            for (int i = 0; i < bundles.Items.Count; i++)
            {
                JsonValue item = bundles.Items[i];
                if (item == null || item.Kind != JsonKind.Object)
                {
                    throw new ArgumentException("PackageManifest: bundles[" + i + "] must be an object.");
                }

                ManifestBundle bundle = new ManifestBundle();
                bundle.Name = RequiredString(item, "name", "bundles[" + i + "]");
                if (_byName.ContainsKey(bundle.Name))
                {
                    throw new ArgumentException(
                        "PackageManifest: duplicate bundle name '" + bundle.Name + "'.");
                }

                bundle.Hash = OptionalString(item, "hash", string.Empty);
                bundle.Crc = (uint)OptionalNumber(item, "crc", 0);
                bundle.Size = (long)OptionalNumber(item, "size", 0);
                if (bundle.Size < 0)
                {
                    throw new ArgumentException(
                        "PackageManifest: bundle '" + bundle.Name + "' has a negative size.");
                }

                bundle.Tags = OptionalStringList(item, "tags", "bundles[" + i + "]");
                bundle.Dependencies = OptionalStringList(item, "dependencies", "bundles[" + i + "]");

                // Self-dependency is a build bug that would silently turn into an
                // infinite load loop at runtime; refuse it at the door.
                List<string> dependencies = DependenciesOf(bundle);
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                for (int d = 0; d < dependencies.Count; d++)
                {
                    string dependency = dependencies[d];
                    if (string.Equals(dependency, bundle.Name, StringComparison.Ordinal))
                    {
                        throw new ArgumentException(
                            "PackageManifest: bundle '" + bundle.Name + "' depends on itself.");
                    }

                    if (!seen.Add(dependency))
                    {
                        throw new ArgumentException(
                            "PackageManifest: bundle '" + bundle.Name
                            + "' lists dependency '" + dependency + "' twice.");
                    }
                }

                _bundles.Add(bundle);
                _byName.Add(bundle.Name, bundle);
                TotalBytes += bundle.Size;
            }

            // Dangling references are only detectable once every name is known.
            for (int i = 0; i < _bundles.Count; i++)
            {
                List<string> dependencies = DependenciesOf(_bundles[i]);
                for (int d = 0; d < dependencies.Count; d++)
                {
                    if (!_byName.ContainsKey(dependencies[d]))
                    {
                        throw new ArgumentException(
                            "PackageManifest: bundle '" + _bundles[i].Name
                            + "' depends on unknown bundle '" + dependencies[d] + "'.");
                    }
                }
            }
        }

        private void ReadAssets(JsonValue assets)
        {
            if (assets == null || assets.Kind != JsonKind.Array)
            {
                throw new ArgumentException("PackageManifest: 'assets' must be an array.");
            }

            for (int i = 0; i < assets.Items.Count; i++)
            {
                JsonValue item = assets.Items[i];
                if (item == null || item.Kind != JsonKind.Object)
                {
                    throw new ArgumentException("PackageManifest: assets[" + i + "] must be an object.");
                }

                ManifestAsset asset = new ManifestAsset();
                asset.Location = RequiredString(item, "location", "assets[" + i + "]");
                if (_byLocation.ContainsKey(asset.Location))
                {
                    throw new ArgumentException(
                        "PackageManifest: duplicate asset address '" + asset.Location + "'.");
                }

                asset.Bundle = RequiredString(item, "bundle", "assets[" + i + "]");
                if (!_byName.ContainsKey(asset.Bundle))
                {
                    throw new ArgumentException(
                        "PackageManifest: asset '" + asset.Location
                        + "' belongs to unknown bundle '" + asset.Bundle + "'.");
                }

                asset.AssetName = OptionalString(item, "assetName", asset.Location);
                asset.Labels = OptionalStringList(item, "labels", "assets[" + i + "]");

                _assets.Add(asset);
                _byLocation.Add(asset.Location, asset);
            }
        }

        /// <summary>
        /// Kahn topological sort over the bundle graph. A cycle means the load
        /// order is undefined, so it is a build-artifact bug: fail fast and name
        /// the bundles sitting on the cycle.
        /// </summary>
        private void VerifyAcyclic()
        {
            Dictionary<string, int> inDegree = new Dictionary<string, int>(_bundles.Count, StringComparer.Ordinal);
            Dictionary<string, List<string>> dependents =
                new Dictionary<string, List<string>>(_bundles.Count, StringComparer.Ordinal);

            for (int i = 0; i < _bundles.Count; i++)
            {
                List<string> dependencies = DependenciesOf(_bundles[i]);
                inDegree[_bundles[i].Name] = dependencies.Count;
                for (int d = 0; d < dependencies.Count; d++)
                {
                    List<string> list;
                    if (!dependents.TryGetValue(dependencies[d], out list))
                    {
                        list = new List<string>();
                        dependents.Add(dependencies[d], list);
                    }

                    list.Add(_bundles[i].Name);
                }
            }

            List<string> ready = new List<string>();
            for (int i = 0; i < _bundles.Count; i++)
            {
                if (inDegree[_bundles[i].Name] == 0)
                {
                    ready.Add(_bundles[i].Name);
                }
            }

            int settled = 0;
            while (ready.Count > 0)
            {
                string name = ready[ready.Count - 1];
                ready.RemoveAt(ready.Count - 1);
                settled++;

                List<string> list;
                if (!dependents.TryGetValue(name, out list))
                {
                    continue;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    int left = inDegree[list[i]] - 1;
                    inDegree[list[i]] = left;
                    if (left == 0)
                    {
                        ready.Add(list[i]);
                    }
                }
            }

            if (settled == _bundles.Count)
            {
                return;
            }

            List<string> cyclic = new List<string>();
            for (int i = 0; i < _bundles.Count; i++)
            {
                if (inDegree[_bundles[i].Name] > 0)
                {
                    cyclic.Add(_bundles[i].Name);
                }
            }

            throw new ArgumentException(
                "PackageManifest: dependency cycle among bundle(s): " + string.Join(", ", cyclic.ToArray()));
        }

        private static string RequiredString(JsonValue owner, string key, string where)
        {
            JsonValue value = owner[key];
            string text = value.AsNonEmptyString();
            if (text == null)
            {
                throw new ArgumentException(
                    "PackageManifest: " + where + " is missing a non-empty string '" + key + "'.");
            }

            return text;
        }

        private static string OptionalString(JsonValue owner, string key, string fallback)
        {
            if (!owner.Has(key))
            {
                return fallback;
            }

            JsonValue value = owner[key];
            if (value.Kind == JsonKind.Null)
            {
                return fallback;
            }

            if (value.Kind != JsonKind.String)
            {
                throw new ArgumentException("PackageManifest: '" + key + "' must be a string.");
            }

            return value.StringValue;
        }

        private static double OptionalNumber(JsonValue owner, string key, double fallback)
        {
            if (!owner.Has(key))
            {
                return fallback;
            }

            JsonValue value = owner[key];
            if (value.Kind == JsonKind.Null)
            {
                return fallback;
            }

            if (value.Kind != JsonKind.Number)
            {
                throw new ArgumentException("PackageManifest: '" + key + "' must be a number.");
            }

            return value.NumberValue;
        }

        private static List<string> OptionalStringList(JsonValue owner, string key, string where)
        {
            if (!owner.Has(key))
            {
                return null;
            }

            JsonValue value = owner[key];
            if (value.Kind == JsonKind.Null)
            {
                return null;
            }

            if (value.Kind != JsonKind.Array)
            {
                throw new ArgumentException(
                    "PackageManifest: " + where + " field '" + key + "' must be an array of strings.");
            }

            List<string> list = new List<string>(value.Items.Count);
            for (int i = 0; i < value.Items.Count; i++)
            {
                JsonValue entry = value.Items[i];
                string text = entry == null ? null : entry.AsNonEmptyString();
                if (text == null)
                {
                    throw new ArgumentException(
                        "PackageManifest: " + where + " field '" + key + "' has an empty entry.");
                }

                list.Add(text);
            }

            return list;
        }
    }
}
