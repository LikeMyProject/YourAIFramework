using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YourFramework.HotUpdate;
using YourFramework.Scripting;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// 脚本层速览：清单 → 装载（逐块）→ 入口 → 沙箱 → 失败回滚。
    ///
    /// 这一套解决的是**IL2CPP 上热更不了代码**这个硬问题：
    /// 程序集热更靠 <c>Assembly.Load(byte[])</c>，它在 JIT 平台（编辑器 / Android-Mono / Windows）
    /// 上是原生能力，在 IL2CPP 上会被平台直接拒绝；而**解释执行不受这条限制**。
    /// 于是「IL2CPP 也能热更代码」这件事，等价于「框架有一个能跑起来的脚本运行时」。
    ///
    /// 分工要说清楚，否则容易误以为框架内置了 Lua：
    ///   - 内核（本演示用的这一层）只定义接缝：代码块、清单、装载序、沙箱策略、失败姿态。
    ///     它**不认识任何脚本引擎**，也不依赖任何第三方包；
    ///   - 真正的解释器是适配层：<c>Runtime/YourFramework.XLua</c> 把 XLua 接到这套接缝上
    ///     （需要用户导入 XLua 并打开 <c>YOURFRAMEWORK_XLUA</c> 宏）。
    ///
    /// 本演示跑的是**玩具运行时**：不解释 Lua，只按 <c>function 名字</c> 登记入口，
    /// 好让「接缝怎么用、失败怎么折、回滚长什么样」在离线环境里能被看见。
    /// 换成 XLua 适配层时，下面这段演示代码一个字都不用改 —— 这正是接缝的意义。
    /// </summary>
    internal static class DemoScript
    {
        private static void Log(string line)
        {
            Debug.Log("[Demo22] " + line);
        }

        /// <summary>
        /// 玩具脚本运行时：把「装了什么、调了谁、卸了谁」记下来。
        ///
        /// 它刻意实现完整合约：内容问题返回失败值（不抛），合约问题抛（null 代码块、已关停）。
        /// 一个玩具如果对合约睁一只眼闭一只眼，演示就会教出错误的用法。
        /// </summary>
        private sealed class ToyScriptRuntime : IScriptRuntime
        {
            private readonly Dictionary<string, string> _chunks = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly List<string> _entries = new List<string>();
            private readonly List<string> _log = new List<string>();
            private ScriptRuntimeState _state = ScriptRuntimeState.Ready;

            public int PumpCount;

            public string Name { get { return "toy"; } }

            public ScriptRuntimeState State { get { return _state; } }

            /// <summary>调用流水（装载 / 卸载 / 调用入口），按发生顺序。</summary>
            public IList<string> Log { get { return _log; } }

            public ScriptOutcome LoadChunk(ScriptChunk chunk)
            {
                if (chunk == null)
                {
                    throw new ArgumentNullException("chunk");
                }

                if (_state == ScriptRuntimeState.Disposed)
                {
                    throw new InvalidOperationException("ToyScriptRuntime: 运行时已关停。");
                }

                // 玩具「语法检查」：源码里出现这个标记就当解释器拒绝了它。
                if (chunk.Text.Contains("SYNTAX_ERROR"))
                {
                    return ScriptOutcome.Fail("chunk '" + chunk.Name + "' failed to load: 第 1 行附近有语法错");
                }

                _chunks[chunk.Name] = chunk.Text;
                _log.Add("load:" + chunk.Name + "(" + chunk.Crc.ToString("X8") + ")");

                // 登记入口：扫 function <名字> 的声明。真解释器里这一步就是执行代码块。
                int cursor = 0;
                while (cursor < chunk.Text.Length)
                {
                    int start = chunk.Text.IndexOf("function ", cursor, StringComparison.Ordinal);
                    if (start < 0)
                    {
                        break;
                    }

                    start += "function ".Length;
                    int end = start;
                    while (end < chunk.Text.Length && (char.IsLetterOrDigit(chunk.Text[end]) || chunk.Text[end] == '_'))
                    {
                        end++;
                    }

                    string declared = chunk.Text.Substring(start, end - start);
                    if (declared.Length > 0 && !_entries.Contains(declared))
                    {
                        _entries.Add(declared);
                    }

                    cursor = end;
                }

                return ScriptOutcome.Ok(chunk.Name);
            }

            public ScriptOutcome UnloadChunk(string name)
            {
                if (string.IsNullOrEmpty(name))
                {
                    throw new ArgumentException("ToyScriptRuntime.UnloadChunk: name 不能为空。", "name");
                }

                if (!_chunks.Remove(name))
                {
                    return ScriptOutcome.Fail("chunk '" + name + "' was not loaded by this runtime");
                }

                _log.Add("unload:" + name);
                return ScriptOutcome.Ok(name);
            }

            public ScriptOutcome CallEntry(string entryName)
            {
                if (string.IsNullOrEmpty(entryName))
                {
                    throw new ArgumentException("ToyScriptRuntime.CallEntry: entryName 不能为空。", "entryName");
                }

                if (!_entries.Contains(entryName))
                {
                    return ScriptOutcome.Fail("入口 '" + entryName + "' 在脚本环境里不存在");
                }

                _log.Add("call:" + entryName);
                return ScriptOutcome.Ok("toy:" + entryName);
            }

            public void Pump(double deltaSeconds)
            {
                PumpCount++;
            }

            public void Shutdown()
            {
                _state = ScriptRuntimeState.Disposed;
                _chunks.Clear();
            }
        }

        /// <summary>一份三块的补丁清单：core.util 被 battle.damage 依赖，ui.hud 独立。</summary>
        private const string PatchManifestJson = @"{
  ""version"": ""1.4.0"",
  ""chunks"": [
    { ""name"": ""core.util"", ""kind"": ""module"", ""fileName"": ""core/util.lua"" },
    { ""name"": ""battle.damage"", ""fileName"": ""battle/damage.lua"",
      ""dependencies"": [""core.util""], ""entries"": [""YourFramework_Apply""] },
    { ""name"": ""ui.hud"", ""fileName"": ""ui/hud.lua"", ""entries"": [""YourFramework_Hud""] }
  ]
}";

        private const string UtilSource = "local M = {}\nfunction YourFramework_Util() return 'util' end\n";

        private const string DamageSource =
            "function YourFramework_Apply()\n  return 'damage patched'\nend\n";

        private const string HudSource = "function YourFramework_Hud() return 'hud' end\n";

        private static MemoryScriptChunkSource Source(bool withHud)
        {
            MemoryScriptChunkSource source = new MemoryScriptChunkSource();
            source.Add("core/util.lua", UtilSource);
            source.Add("battle/damage.lua", DamageSource);
            if (withHud)
            {
                source.Add("ui/hud.lua", HudSource);
            }

            return source;
        }

        public static IEnumerator ScriptLayer()
        {
            Log("脚本层：IL2CPP 上热更代码的唯一落脚点（解释执行不受程序集装载限制）。");

            // ---------- 第一幕：清单 ----------
            ScriptManifest manifest = ScriptManifest.LoadJson(PatchManifestJson);
            Log("  清单 " + manifest.Version + " 解析出 " + manifest.Count + " 块；自研清单的纪律是坏的立刻炸，"
                + "不留到运行期变成「装不上」。");

            ScriptLoadOrder order = new ScriptLoadOrder();
            List<string> plan = new List<string>();
            order.Resolve(manifest, new List<string> { "battle.damage", "ui.hud" }, plan);
            Log("  装载序（依赖在前）= " + string.Join(" -> ", plan.ToArray()));

            // ---------- 第二幕：沙箱 ----------
            ScriptSandboxPolicy strict = ScriptSandboxPolicy.CreateStrict();
            ScriptSandboxPolicy game = ScriptSandboxPolicy.CreateStrict();
            game.Allow(ScriptCapability.Modules);
            Log("  最严沙箱要摘掉 " + strict.DeniedGlobals.Count + " 个全局："
                + string.Join(" ", strict.DeniedGlobals.ToArray()));
            Log("  放行 require 之后剩下 " + game.DeniedGlobals.Count + " 个（补丁里要 require 自己的模块）；"
                + "补丁是下载来的代码，默认姿态必须是从零开始。");

            // ---------- 第三幕：装载与入口 ----------
            ToyScriptRuntime runtime = new ToyScriptRuntime();
            ScriptPatchStep apply = new ScriptPatchStep(
                manifest, new List<string> { "battle.damage", "ui.hud" }, Source(true), runtime);

            apply.Begin(new HotUpdateContext());
            while (apply.Pump() == HotUpdateStepState.Running)
            {
                runtime.Pump(0.016d); // 每帧滴答一下解释器：泵哲学，不靠后台线程
            }

            Log("  装载完成：状态=" + apply.State + "，装了 " + apply.LoadedCount + " 块，"
                + "调用 " + apply.EntriesAttempted + " 个入口；泵了 " + runtime.PumpCount + " 帧。");
            Log("  流水 = " + string.Join(" -> ", new List<string>(runtime.Log).ToArray()));

            ScriptOutcome called = runtime.CallEntry("YourFramework_Apply");
            Log("  补丁生效了吗？调用入口 YourFramework_Apply → " + called
                + "（失败也是值：调用方不需要 try/catch，UI 有话可说）");

            // ---------- 第四幕：失败与真回滚 ----------
            Log("  现在故意让 ui/hud.lua 缺席：前两块已经装进去了，所以这一次必须真的退回去。");
            ToyScriptRuntime failing = new ToyScriptRuntime();
            ScriptPatchStep failStep = new ScriptPatchStep(
                manifest, new List<string> { "battle.damage", "ui.hud" }, Source(false), failing);

            failStep.Begin(new HotUpdateContext());
            while (failStep.Pump() == HotUpdateStepState.Running)
            {
                // 泵到终态
            }

            Log("  失败阶段=" + failStep.FailureStage + "，原因=" + failStep.FailureReason);
            Log("  回滚=" + failStep.RolledBack + "，反序卸掉 = "
                + string.Join(" -> ", new List<string>(failStep.UnloadedNames).ToArray())
                + "；装载器侧流水 = " + string.Join(" -> ", new List<string>(failing.Log).ToArray()));
            Log("  Lua 能卸代码块，所以补丁期的失败是**真回滚**（回到补丁前的代码）。");
            Log("  入口一旦跑过就不再卸载 —— 那时函数已被闭包引用，卸掉只是自欺欺人，框架会如实上报。");

            runtime.Shutdown();
            yield break;
        }
    }
}
