using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using YourFramework.HotUpdate;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// 自研热更程序集流水线速览：清单与拓扑装载顺序 → 逐帧装载（一帧一个）→
    /// 完整性校验与"装载期失败 = 天然回滚" → 装载器拒绝（IL2CPP 那条路）→
    /// 入口点按 Order 执行 → AOT 白名单 link.xml → 插进 HotUpdateFlow 并让原因直达 UI。
    ///
    /// 与 15 号演示的分工：15 讲的是**编排**（阶段顺序/失败截停/终态），本个讲的是
    /// 编排里"程序集重载"那一格的**实现**。15 的 ReloadAssemblies 是个 mock，这里是真货。
    ///
    /// 演示里的字节来源与装载器是替身（真装 dll 会污染当前进程、也看不出过程），
    /// 但清单解析、拓扑排序、逐帧推进、完整性校验、入口点扫描与执行全是生产代码。
    /// </summary>
    internal static class DemoHotAssembly
    {
        /// <summary>演示入口点：真被扫描、真被调用，日志就是它写的。</summary>
        private sealed class DemoHotfixEntry : IHotfixEntry
        {
            public DemoHotfixEntry()
            {
            }

            public int Order { get { return 100; } }

            public void OnHotfixLoaded()
            {
                Debug.Log("[Demo19]    入口点 DemoHotfixEntry.OnHotfixLoaded() 被框架调用（Order=100）"
                    + " —— 热更真正生效的时刻就在这一行。");
            }
        }

        /// <summary>AOT 扫描的载体：把泛型实例化放在字段里，跟真实工程里的做法一样。</summary>
        private sealed class DemoAotCarrier
        {
            public List<Dictionary<int, string>> Basket;
            public Dictionary<string, float> Scores;
        }

        /// <summary>把本程序集当作"刚装载完的热更程序集"，好让入口点那条路真的走通。</summary>
        private sealed class SurrogateLoader : IAssemblyLoader
        {
            public Assembly Load(byte[] raw, string name, out string error)
            {
                error = null;
                return typeof(DemoHotAssembly).Assembly;
            }
        }

        private sealed class FullRefusingLoader : IAssemblyLoader
        {
            public Assembly Load(byte[] raw, string name, out string error)
            {
                // 这正是 IL2CPP 上 Assembly.Load(byte[]) 的真实结果：不是崩溃，是一条原因。
                error = "PlatformNotSupportedException: a byte-array assembly cannot be loaded on IL2CPP";
                return null;
            }
        }

        /// <summary>内存字节来源：演示里不想真去磁盘上找文件。</summary>
        private sealed class MemorySource : IAssemblyBytesSource
        {
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            public bool TryRead(string fileName, out byte[] bytes, out string error)
            {
                if (Files.TryGetValue(fileName, out bytes))
                {
                    error = null;
                    return true;
                }

                bytes = null;
                error = "not in the demo memory source: " + fileName;
                return false;
            }
        }

        private sealed class LogListener : IHotUpdateListener
        {
            public void OnStageStarted(string stageName, int stageIndex, int stageCount)
            {
                Debug.Log("[Demo19]    编排 → 阶段开始 " + stageName + " (" + (stageIndex + 1) + "/" + stageCount + ")");
            }

            public void OnStageDone(string stageName)
            {
                Debug.Log("[Demo19]    编排 → 阶段完成 " + stageName);
            }

            public void OnFailed(string stageName, string reason)
            {
                Debug.Log("[Demo19]    编排 → 失败于 " + stageName + "：" + reason);
            }

            public void OnSucceeded()
            {
                Debug.Log("[Demo19]    编排 → 全部阶段成功");
            }
        }

        public static IEnumerator HotAssemblies()
        {
            const string ManifestJson =
                "{\"version\":\"1.2.0\",\"assemblies\":["
                + "{\"name\":\"Core\",\"fileName\":\"Core.dll.bytes\",\"size\":4,\"crc\":1,"
                + "\"hash\":\"h-core\"},"
                + "{\"name\":\"Game\",\"fileName\":\"Game.dll.bytes\",\"size\":4,\"crc\":1,"
                + "\"dependencies\":[\"Core\"]},"
                + "{\"name\":\"UI\",\"fileName\":\"UI.dll.bytes\",\"size\":4,\"crc\":1,"
                + "\"dependencies\":[\"Core\"]},"
                + "{\"name\":\"Hot\",\"fileName\":\"hot/main.dll.bytes\",\"size\":4,\"crc\":1,"
                + "\"dependencies\":[\"Game\",\"UI\"]}"
                + "]}";

            // ---- 1. 清单：有什么、指纹是什么、谁依赖谁 ----
            AssemblyManifest manifest = AssemblyManifest.LoadJson(ManifestJson);
            Debug.Log("[Demo19] 清单 v" + manifest.Version + " 共 " + manifest.Count + " 个程序集，"
                + "声明总字节 " + manifest.TotalBytes + "。坏清单（重名/悬空依赖/成环/缺字段）解析期直接抛，"
                + "不留到运行期才以'装载失败'的面目出现。");
            yield return null;

            // ---- 2. 拓扑装载顺序：被依赖者先，root 在最后 ----
            AssemblyLoadOrder order = new AssemblyLoadOrder();
            List<string> plan = new List<string>();
            order.Resolve(manifest, new List<string> { "Hot" }, plan);
            Debug.Log("[Demo19] 只要 Hot，解析出的装载顺序 = " + string.Join(" → ", plan.ToArray())
                + "（Core 被 Game 与 UI 共用，只出现一次且排最前；Hot 这个 root 在最后）");
            yield return null;

            // ---- 3. 逐帧装载 + 入口点执行：一帧一个程序集 ----
            MemorySource source = new MemorySource();
            source.Files["Core.dll.bytes"] = new byte[] { 1, 2, 3, 4 };
            source.Files["Game.dll.bytes"] = new byte[] { 5, 6, 7, 8 };
            source.Files["UI.dll.bytes"] = new byte[] { 9, 10, 11, 12 };
            source.Files["hot/main.dll.bytes"] = new byte[] { 13, 14, 15, 16 };

            AssemblyReloadStep step = new AssemblyReloadStep(
                "ReloadAssemblies", manifest, new List<string> { "Hot" }, source, new SurrogateLoader(),
                new HotfixEntryRunner());

            step.Begin(null);
            int frames = 0;
            while (step.State == AssemblyReloadState.Loading && frames < 20)
            {
                step.Pump();
                frames++;
                Debug.Log("[Demo19]   第 " + frames + " 帧：已装载 " + step.LoadedCount
                    + "/" + plan.Count + " —— 每帧只推进一个程序集，单帧工作量有界，"
                    + "不会在加载界面冻住一帧。");
                yield return null;
            }

            Debug.Log("[Demo19] 装载完成：状态 " + step.State + "，入口点执行 " + step.EntriesAttempted
                + " 个，装载名册 = " + string.Join(",", new List<string>(step.LoadedNames).ToArray()));
            yield return null;

            // ---- 4. 完整性：声明 999 字节、实际 4 字节 → 一次干净的整批回滚 ----
            AssemblyManifest broken = AssemblyManifest.LoadJson(
                ManifestJson.Replace("\"name\":\"Hot\",\"fileName\":\"hot/main.dll.bytes\",\"size\":4",
                    "\"name\":\"Hot\",\"fileName\":\"hot/main.dll.bytes\",\"size\":999"));
            AssemblyReloadStep brokenStep = new AssemblyReloadStep(
                broken, new List<string> { "Hot" }, source, new SurrogateLoader());
            brokenStep.Begin(null);
            for (int i = 0; i < 20 && brokenStep.State == AssemblyReloadState.Loading; i++)
            {
                brokenStep.Pump();
            }

            Debug.Log("[Demo19] 完整性校验（长度）：阶段 " + brokenStep.FailureStage + "，原因 —— "
                + brokenStep.FailureReason);
            Debug.Log("[Demo19]   此刻 RolledBack=" + brokenStep.RolledBack + "、入口点执行 "
                + brokenStep.EntriesAttempted + " 个 —— .NET 卸载不了已装载的程序集，所以"
                + "\"回滚\"的真正含义是**入口点一个都没跑**，游戏还跑在旧代码上。这是干净的整批回滚。");
            yield return null;

            // ---- 5. 装载器拒绝：IL2CPP 那条路的真实形状 ----
            AssemblyReloadStep il2cpp = new AssemblyReloadStep(
                manifest, new List<string> { "Core" }, source, new FullRefusingLoader());
            il2cpp.Begin(null);
            for (int i = 0; i < 20 && il2cpp.State == AssemblyReloadState.Loading; i++)
            {
                il2cpp.Pump();
            }

            Debug.Log("[Demo19] 装载器拒绝（IL2CPP 上 Assembly.Load(byte[]) 就是这样）：阶段 "
                + il2cpp.FailureStage + "，原因 —— " + il2cpp.FailureReason);
            Debug.Log("[Demo19]   不崩、不吞：换成 HybridCLR 的实现塞进 IAssemblyLoader 插槽，"
                + "上层一个字节都不用改。");
            yield return null;

            // ---- 6. AOT 白名单：泛型定义 → link.xml ----
            HashSet<Type> definitions = new HashSet<Type>();
            List<string> problems = new List<string>();
            AotWhitelist.CollectGenericDefinitions(
                new Type[] { typeof(DemoAotCarrier) }, definitions, problems);
            string xml = AotWhitelist.BuildLinkXml(definitions);
            Debug.Log("[Demo19] AOT 白名单：扫出 " + definitions.Count + " 个泛型定义，"
                + "link.xml 前几行 ——\n" + FirstLines(xml, 5));
            Debug.Log("[Demo19]   诚实边界：反射只看得到字段/属性/方法签名里的实例化；"
                + "只在方法体内部 new 出来的要人在窗口的第二栏补种子。"
                + "纯 JIT 平台不需要 link.xml，IL2CPP 才需要。");
            yield return null;

            // ---- 7. 插进 HotUpdateFlow：详细原因直达监听者 ----
            HotUpdateFlow flow = new HotUpdateFlow(new LogListener());
            AssemblyReloadStep flowStep = new AssemblyReloadStep(
                manifest, new List<string> { "Hot" }, new MemorySource(), new SurrogateLoader());
            flow.Run(new IHotUpdateStep[] { flowStep });
            for (int i = 0; i < 20 && flow.IsRunning; i++)
            {
                flow.Pump();
            }

            Debug.Log("[Demo19] 编排预留接口：步骤失败时它自己的 FailureReason 会经 IHotUpdateStepFailure "
                + "原样上报，编排器不再吞成 \"step 'X' failed\" —— 玩家与日志都拿得到"
                + "\"哪个文件、哪一步、为什么\"。");
            Debug.Log("[Demo19] 热更流水线 = HotUpdateFlow（版本检查/下载/应用/重载的编排）"
                + " + AssemblyManifest（清单与拓扑序） + AssemblyReloadStep（逐帧装载与回滚点）"
                + " + IHotfixEntry（热更生效的入口） + AotWhitelist（IL2CPP 的泛型白名单）。");
        }

        private static string FirstLines(string text, int count)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int take = Math.Min(count, lines.Length);
            string joined = string.Join("\n", lines, 0, take);
            return joined.TrimEnd();
        }
    }
}
