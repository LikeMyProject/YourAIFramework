using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using YourAI.Core.Json;
using YourFramework.Assets.Pack;

namespace YourAI.Editor
{
    /// <summary>
    /// 自研资源包的构建器：把 <c>Assets/YourAI/AssetPackSource</c> 下的资源打成
    /// AssetBundle，并产出**自家格式**的清单与版本文件（PackageManifest /
    /// PackageVersion 认得的那两份 JSON）。
    ///
    /// 分组约定（简单到不用读文档也不会错）：
    /// - 一级子目录 = 一个 bundle，bundle 名即目录名（sanitize 后）；
    /// - 根目录下散落的资源归入 bundle "root"；
    /// - 资源的可寻址地址 = 相对源根的路径（去扩展名、小写、'/' 分隔），
    ///   例如 <c>ui/atlas/sword.png</c> → 地址 <c>ui/atlas/sword</c>；
    /// - bundle 内部的资源名 = 文件名**含扩展名**（Unity 的 LoadAsset 按这个找），
    ///   因此清单里的 assetName 与 location 不是同一个字符串，两者都写进清单。
    ///
    /// 配置：源根下可放 <c>assetpack.json</c>（{"packageName":"main","version":"1.0.0"}），
    /// 缺省则用 main / 1.0.0。依赖关系不由人写，直接问 Unity 构建出来的
    /// AssetBundleManifest（它才是真相），避免手写依赖表与打包结果不一致。
    /// </summary>
    public static class AssetPackBuilder
    {
        /// <summary>Where the builder reads assets from.</summary>
        public const string SourceRoot = "Assets/YourAI/AssetPackSource";

        /// <summary>Where bundles and manifests are written.</summary>
        public const string OutputRoot = "Build/AssetPack";

        private const string ConfigFile = "assetpack.json";
        private const string RootBundleName = "root";

        [MenuItem("YourAI/资源包/构建资源包（当前平台）", false, 1)]
        public static void BuildForActiveTarget()
        {
            if (!Directory.Exists(SourceRoot))
            {
                EditorUtility.DisplayDialog(
                    "没有资源包源目录",
                    "请先创建目录：\n\n" + SourceRoot
                        + "\n\n约定：一级子目录 = 一个 bundle，目录里的资源自动计入该 bundle。",
                    "知道了");
                return;
            }

            string packageName = "main";
            string version = "1.0.0";
            ReadConfig(ref packageName, ref version);

            List<AssetBundleBuild> builds = new List<AssetBundleBuild>();
            List<AssetSeed> assetSeeds = new List<AssetSeed>();
            List<string> bundleNames = new List<string>();
            try
            {
                Collect(builds, assetSeeds, bundleNames);
            }
            catch (Exception ex)
            {
                // Grouping problems (bundle name collisions) are setup bugs: say
                // exactly what clashes instead of failing deep inside BuildPipeline.
                Debug.LogError("AssetPack: " + ex.Message);
                EditorUtility.DisplayDialog("资源包分组有问题", ex.Message, "知道了");
                return;
            }

            if (builds.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "资源包为空",
                    SourceRoot + " 下没有可打包的资源（.meta 与配置文件不计）。",
                    "知道了");
                return;
            }

            Directory.CreateDirectory(OutputRoot);
            UnityEngine.AssetBundleManifest unityManifest;
            try
            {
                unityManifest = BuildPipeline.BuildAssetBundles(
                    OutputRoot, builds.ToArray(), BuildAssetBundleOptions.ChunkBasedCompression,
                    EditorUserBuildSettings.activeBuildTarget);
            }
            catch (Exception ex)
            {
                Debug.LogError("AssetPack: build failed: " + ex.GetType().Name + ": " + ex.Message);
                EditorUtility.DisplayDialog("构建失败", ex.Message, "知道了");
                return;
            }

            if (unityManifest == null)
            {
                EditorUtility.DisplayDialog("构建失败", "BuildPipeline 没有返回 AssetBundleManifest。", "知道了");
                return;
            }

            List<BundleSeed> bundleSeeds = new List<BundleSeed>();
            long totalBytes = 0;
            for (int i = 0; i < bundleNames.Count; i++)
            {
                string bundleName = bundleNames[i];
                string bundlePath = Path.Combine(OutputRoot, bundleName);

                BundleSeed seed = new BundleSeed();
                seed.Name = bundleName;
                seed.Hash = unityManifest.GetAssetBundleHash(bundleName).ToString();

                FileInfo info = new FileInfo(bundlePath);
                seed.Size = info.Exists ? info.Length : 0;
                seed.Crc = info.Exists ? FileCrc32(bundlePath) : 0u;

                string[] dependencies = unityManifest.GetAllDependencies(bundleName);
                if (dependencies != null && dependencies.Length > 0)
                {
                    seed.Dependencies = new List<string>(dependencies);
                }

                bundleSeeds.Add(seed);
                totalBytes += seed.Size;
            }

            string manifestJson = ManifestWriter.Write(packageName, version, bundleSeeds, assetSeeds);
            string versionJson = ManifestWriter.WriteVersion(packageName, version, bundleSeeds);

            string manifestPath = Path.Combine(OutputRoot, packageName + ".manifest.json");
            string versionPath = Path.Combine(OutputRoot, packageName + ".version.json");
            File.WriteAllText(manifestPath, manifestJson, new UTF8Encoding(false));
            File.WriteAllText(versionPath, versionJson, new UTF8Encoding(false));

            AssetDatabase.Refresh();

            StringBuilder summary = new StringBuilder();
            summary.Append("包名：").Append(packageName)
                .Append("\n版本：").Append(version)
                .Append("\nBundle：").Append(bundleSeeds.Count).Append(" 个")
                .Append("\n资源：").Append(assetSeeds.Count).Append(" 个")
                .Append("\n总量：").Append(totalBytes.ToString(CultureInfo.InvariantCulture)).Append(" 字节")
                .Append("\n\n清单：").Append(manifestPath)
                .Append("\n版本：").Append(versionPath);

            Debug.Log("AssetPack: " + summary.ToString().Replace("\n", " | "));
            EditorUtility.DisplayDialog("资源包构建完成", summary.ToString(), "好");
        }

        [MenuItem("YourAI/资源包/打开输出目录", false, 2)]
        public static void OpenOutputRoot()
        {
            if (!Directory.Exists(OutputRoot))
            {
                EditorUtility.DisplayDialog("还没有输出", "先构建一次资源包。", "知道了");
                return;
            }

            EditorUtility.RevealInFinder(OutputRoot);
        }

        [MenuItem("YourAI/资源包/在源目录新建示例结构", false, 3)]
        public static void CreateSampleLayout()
        {
            string examples = Path.Combine(SourceRoot, "examples");
            Directory.CreateDirectory(examples);
            File.WriteAllText(
                Path.Combine(examples, "readme.txt"),
                "把资源放进 SourceRoot 的一级子目录：每个子目录 = 一个 bundle。\n"
                    + "本文件在构建时会被当作资源打包（演示用），不需要可随时删除。\n",
                new UTF8Encoding(false));

            string configPath = Path.Combine(SourceRoot, ConfigFile);
            if (!File.Exists(configPath))
            {
                File.WriteAllText(
                    configPath,
                    "{\n  \"packageName\": \"main\",\n  \"version\": \"1.0.0\"\n}\n",
                    new UTF8Encoding(false));
            }

            AssetDatabase.Refresh();
            EditorUtility.RevealInFinder(SourceRoot);
        }

        private static void ReadConfig(ref string packageName, ref string version)
        {
            string path = Path.Combine(SourceRoot, ConfigFile);
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                JsonValue root = JsonParser.Parse(File.ReadAllText(path, Encoding.UTF8));
                string configuredName = root["packageName"].AsNonEmptyString();
                string configuredVersion = root["version"].AsNonEmptyString();
                if (configuredName != null)
                {
                    packageName = configuredName;
                }

                if (configuredVersion != null)
                {
                    version = configuredVersion;
                }
            }
            catch (Exception ex)
            {
                // A broken config is a setup bug, but it must not block a build:
                // fall back to defaults and say so loudly.
                Debug.LogWarning("AssetPack: " + ConfigFile + " is unreadable ("
                    + ex.GetType().Name + ": " + ex.Message + "); using defaults.");
            }
        }

        private static void Collect(
            List<AssetBundleBuild> builds, List<AssetSeed> assetSeeds, List<string> bundleNames)
        {
            // Root-level files first: they belong to the "root" bundle.
            List<string> rootAssets = CollectFiles(SourceRoot, false);
            AddBundle(builds, assetSeeds, bundleNames, RootBundleName, rootAssets, string.Empty);

            string[] directories = Directory.GetDirectories(SourceRoot);
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i];
                string folderName = Path.GetFileName(directory);
                string bundleName = Sanitize(folderName);
                if (string.IsNullOrEmpty(bundleName))
                {
                    continue;
                }

                // Two folders can sanitise to the same bundle name ("UI" and "ui").
                // Silently renaming one would make addresses lie about where they
                // came from, so stop and name both folders instead.
                if (bundleNames.Contains(bundleName))
                {
                    throw new InvalidOperationException(
                        "文件夹 '" + folderName + "' 映射到 bundle '" + bundleName
                        + "'，该名字已被占用（bundle 名会转小写并清洗非法字符），请重命名其中一个文件夹。");
                }

                List<string> files = CollectFiles(directory, true);
                AddBundle(builds, assetSeeds, bundleNames, bundleName, files, folderName + "/");
            }
        }

        private static void AddBundle(
            List<AssetBundleBuild> builds, List<AssetSeed> assetSeeds, List<string> bundleNames,
            string bundleName, List<string> files, string locationPrefix)
        {
            if (files.Count == 0)
            {
                return;
            }

            AssetBundleBuild build = new AssetBundleBuild();
            build.assetBundleName = bundleName;
            build.assetNames = files.ToArray();
            builds.Add(build);
            bundleNames.Add(bundleName);

            for (int i = 0; i < files.Count; i++)
            {
                string assetPath = files[i];
                string fileName = Path.GetFileName(assetPath);

                AssetSeed seed = new AssetSeed();
                seed.Bundle = bundleName;
                seed.AssetName = fileName; // Unity resolves assets by file name, extension included
                seed.Location = MakeLocation(locationPrefix, assetPath);
                assetSeeds.Add(seed);
            }
        }

        /// <summary>
        /// Address convention: source-root-relative path, extension dropped,
        /// lower-cased, forward slashes. Deterministic, so the same file always
        /// yields the same address across machines.
        /// </summary>
        private static string MakeLocation(string locationPrefix, string assetPath)
        {
            string fileName = Path.GetFileNameWithoutExtension(assetPath);
            string location = locationPrefix + fileName;
            return location.Replace('\\', '/').ToLowerInvariant();
        }

        private static List<string> CollectFiles(string directory, bool recursive)
        {
            List<string> files = new List<string>();
            string[] found = recursive
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                : Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);

            for (int i = 0; i < found.Length; i++)
            {
                string path = found[i].Replace('\\', '/');
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (path.EndsWith("/" + ConfigFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the rules file is not content
                }

                files.Add(path);
            }

            return files;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(name.Length);
            string lowered = name.ToLowerInvariant();
            for (int i = 0; i < lowered.Length; i++)
            {
                char c = lowered[i];
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                {
                    sb.Append(c);
                }
                else
                {
                    sb.Append('_');
                }
            }

            return sb.ToString();
        }

        private static uint FileCrc32(string path)
        {
            byte[] buffer = new byte[64 * 1024];
            uint crc = Crc32.Begin();
            using (FileStream stream = File.OpenRead(path))
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    crc = Crc32.Update(crc, buffer, 0, read);
                }
            }

            return Crc32.Finish(crc);
        }
    }
}
