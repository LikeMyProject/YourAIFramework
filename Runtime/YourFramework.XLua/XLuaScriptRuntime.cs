using System;
using System.Collections.Generic;
using System.Text;
using YourFramework.Core;
using YourFramework.Scripting;
using XLua;

namespace YourFramework.XLua
{
    /// <summary>
    /// XLua 版的脚本运行时：<see cref="IScriptRuntime"/> 的第一个真实实现。
    ///
    /// 它在框架里的位置值得说清楚：**内核不认识 XLua，XLua 也不认识内核**。这一层只做四件事，
    /// 每一件都对应一个 XLua 的公开能力：
    ///
    /// 1. **装块** → <c>LuaEnv.DoString(text, chunkName)</c>。装载 = 编译并执行这一块，
    ///    与 Lua 的 require 语义一致：块执行完，它声明的入口点（全局函数）就已经在了。
    /// 2. **喂 require** → <c>LuaEnv.AddLoader(CustomLoader)</c>。补丁代码里的
    ///    <c>require("core.util")</c> 不该走后缀搜索去翻文件系统 —— 在 Android/iOS 上那些
    ///    .lua 根本不在文件系统里，而且**请求名来自下载下来的补丁，属于不可信输入**。
    ///    所以 loader 只认我们自己的代码块来源，并套同一道路径检查（拒绝绝对路径与「..」）。
    /// 3. **关掉不该有的门** → 见 <see cref="XLuaSandbox"/>：策略来自内核，
    ///    这里只负责把「哪些全局该消失」翻译成 Lua 能执行的东西，并**回读确认真的消失了**。
    /// 4. **滴答** → <c>LuaEnv.Tick()</c>。逐帧驱动的框架不靠后台线程，帧循环里滴答一下即可。
    ///
    /// 出错时的处理严格照抄内核合约：
    /// - 语法错、入口抛异常、块缺席 → 失败也是值，带解释器自己的原文（排查靠的就是那句原文）；
    /// - null 代码块、往已关停的实例里塞东西 → 抛异常：这是写错代码，不是运行时出了事；
    /// - 环境建不起来（缺原生库 / ctor 抛）与沙箱没关上 → 状态转 Faulted 且**拒绝装载**，
    ///   此后每次装载都返回同一个原因。理由很直接：一个没关门的解释器里跑下载来的补丁，
    ///   比不热更坏得多。
    /// </summary>
    public sealed class XLuaScriptRuntime : IScriptRuntime, IDisposable
    {
        /// <summary>XLua 里 chunkName 以 '@' 开头表示「这是文件名」，报错时按文件报而不是按字符串报。</summary>
        public const string ChunkNamePrefix = "@";

        private readonly string _name;
        private readonly IScriptChunkSource _chunkSource;
        private readonly List<string> _loaded = new List<string>();
        private readonly List<string> _unloadedByRollback = new List<string>();

        private LuaEnv _env;
        private ScriptRuntimeState _state = ScriptRuntimeState.NotReady;
        private string _startupFailure;
        private XLuaSandboxReport _sandboxReport;

        /// <summary>
        /// Builds the runtime. <paramref name="chunkSource"/> may be null (then Lua-side
        /// <c>require</c> falls back to XLua's default search and only C#-side loading works);
        /// a null sandbox policy is a wiring bug and throws — 「忘了关沙箱」不该是默认选项。
        /// </summary>
        public XLuaScriptRuntime(string name, IScriptChunkSource chunkSource, ScriptSandboxPolicy sandbox)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("XLuaScriptRuntime: name must not be empty.", "name");
            }

            if (sandbox == null)
            {
                throw new ArgumentNullException("sandbox");
            }

            _name = name;
            _chunkSource = chunkSource;

            try
            {
                _env = new LuaEnv();
            }
            catch (Exception ex)
            {
                // 环境建不起来是运行期故障（平台没有可用的原生库），不是写错代码：
                // 记下原因、转 Faulted，之后每次使用都返回这个原因。
                _state = ScriptRuntimeState.Faulted;
                _startupFailure = "creating the Lua environment failed: " + ex.GetType().Name + ": " + ex.Message;
                return;
            }

            _sandboxReport = XLuaSandbox.Apply(_env, sandbox);
            if (!_sandboxReport.Succeeded)
            {
                _state = ScriptRuntimeState.Faulted;
                _startupFailure = "sandbox could not be enforced: " + _sandboxReport;
                return;
            }

            if (_chunkSource != null)
            {
                _env.AddLoader(ResolveChunkBytes);
            }

            _state = ScriptRuntimeState.Ready;
        }

        /// <summary>实现名。</summary>
        public string Name { get { return _name; } }

        /// <summary>当前状态。</summary>
        public ScriptRuntimeState State { get { return _state; } }

        /// <summary>启动失败的原因（State 为 Faulted 时非空）。</summary>
        public string StartupFailure { get { return _startupFailure; } }

        /// <summary>沙箱应用报告（发出的命令 + 回读结果）。环境没建起来时为 null。</summary>
        public XLuaSandboxReport SandboxReport { get { return _sandboxReport; } }

        /// <summary>已装载的块名（顺序即装载顺序）。</summary>
        public IList<string> LoadedChunkNames { get { return _loaded; } }

        /// <summary>被回滚卸下的块名。</summary>
        public IList<string> UnloadedChunkNames { get { return _unloadedByRollback; } }

        /// <summary>
        /// 底下的 LuaEnv。宿主需要直接读写 Lua 侧的值（注入绑定、读配置）时用；
        /// 拿到它就意味着绕过了本类的防护，所以只在环境就绪时非 null。
        /// </summary>
        public LuaEnv Environment { get { return _state == ScriptRuntimeState.Disposed ? null : _env; } }

        /// <summary>装一块：编译并执行。内容问题返回失败值。</summary>
        public ScriptOutcome LoadChunk(ScriptChunk chunk)
        {
            if (chunk == null)
            {
                throw new ArgumentNullException("chunk");
            }

            if (_state == ScriptRuntimeState.Disposed)
            {
                throw new InvalidOperationException(
                    "XLuaScriptRuntime.LoadChunk: the runtime is already shut down; loading into a disposed "
                    + "interpreter is a wiring bug (script layer shutdown ordering).");
            }

            if (_state != ScriptRuntimeState.Ready)
            {
                return ScriptOutcome.Fail("script runtime is not usable (" + _state + "): " + _startupFailure);
            }

            try
            {
                _env.DoString(chunk.Text, ChunkNamePrefix + chunk.Name);
            }
            catch (LuaException ex)
            {
                return ScriptOutcome.Fail("chunk '" + chunk.Name + "' failed to load: " + ex.Message);
            }
            catch (Exception ex)
            {
                return ScriptOutcome.Fail("chunk '" + chunk.Name + "' failed to load: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            if (!_loaded.Contains(chunk.Name))
            {
                _loaded.Add(chunk.Name);
            }

            return ScriptOutcome.Ok(chunk.Name);
        }

        /// <summary>
        /// 卸一块：清掉模块表项（<c>package.loaded[name]</c>）。
        ///
        /// **说清楚卸载的边界**：块执行时定义的全局函数不会被撤销 —— 这是 Lua 的语义，
        /// 不是实现的偷懒。所以「干净回滚」成立的真正条件是**入口点还没跑过**：
        /// 那时没有任何业务代码引用过那些函数，旧代码仍在生效，补丁等于没发生。
        /// 入口跑过之后再卸载只是把模块表项抹掉，函数对象还被闭包引用着，纯属自欺欺人，
        /// 所以 <see cref="ScriptPatchStep"/> 在入口期失败时根本不会调到这里。
        /// </summary>
        public ScriptOutcome UnloadChunk(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("XLuaScriptRuntime.UnloadChunk: name must not be empty.", "name");
            }

            if (_state == ScriptRuntimeState.Disposed)
            {
                throw new InvalidOperationException(
                    "XLuaScriptRuntime.UnloadChunk: the runtime is already shut down.");
            }

            if (_state != ScriptRuntimeState.Ready)
            {
                return ScriptOutcome.Fail("script runtime is not usable (" + _state + "): " + _startupFailure);
            }

            if (!_loaded.Contains(name))
            {
                return ScriptOutcome.Fail("chunk '" + name + "' was not loaded by this runtime");
            }

            try
            {
                _env.DoString("package.loaded[\"" + EscapeForLuaString(name) + "\"] = nil",
                    ChunkNamePrefix + "YourFramework.Unload");
            }
            catch (Exception ex)
            {
                return ScriptOutcome.Fail("chunk '" + name + "' could not be unloaded: "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            _loaded.Remove(name);
            _unloadedByRollback.Add(name);
            return ScriptOutcome.Ok(name);
        }

        /// <summary>调入口点（环境里的全局函数）。找不到入口是失败值，不是异常。</summary>
        public ScriptOutcome CallEntry(string entryName)
        {
            if (string.IsNullOrEmpty(entryName))
            {
                throw new ArgumentException("XLuaScriptRuntime.CallEntry: entryName must not be empty.", "entryName");
            }

            if (_state == ScriptRuntimeState.Disposed)
            {
                throw new InvalidOperationException(
                    "XLuaScriptRuntime.CallEntry: the runtime is already shut down.");
            }

            if (_state != ScriptRuntimeState.Ready)
            {
                return ScriptOutcome.Fail("script runtime is not usable (" + _state + "): " + _startupFailure);
            }

            LuaFunction function = _env.Global.Get<LuaFunction>(entryName);
            if (function == null)
            {
                return ScriptOutcome.Fail("entry point '" + entryName + "' does not exist in the script environment");
            }

            try
            {
                object[] result = function.Call();
                if (result == null || result.Length == 0)
                {
                    return ScriptOutcome.Ok(null);
                }

                return ScriptOutcome.Ok(result[0] == null ? null : Convert.ToString(result[0]));
            }
            catch (LuaException ex)
            {
                return ScriptOutcome.Fail("entry point '" + entryName + "' threw: " + ex.Message);
            }
            catch (Exception ex)
            {
                return ScriptOutcome.Fail("entry point '" + entryName + "' threw: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                // Global.Get<LuaFunction> 拿到的是一个 Lua 侧的引用包装，用完要还。
                function.Dispose();
            }
        }

        /// <summary>每帧滴答一次。关停后是安全空转（关停与帧循环的竞态不是 bug）。</summary>
        public void Pump(double deltaSeconds)
        {
            if (_state != ScriptRuntimeState.Ready)
            {
                return;
            }

            try
            {
                _env.Tick();
            }
            catch (Exception)
            {
                // Tick 内部是 XLua 自己的 GC/引用整理，抛出来也只说明它自己出了问题；
                // 泵循环不该被一个滴答炸掉，静默吞掉并继续下一帧是这里唯一合理的选择。
            }
        }

        /// <summary>关停。幂等。</summary>
        public void Shutdown()
        {
            if (_state == ScriptRuntimeState.Disposed)
            {
                return;
            }

            LuaEnv env = _env;
            _env = null;
            _state = ScriptRuntimeState.Disposed;
            _loaded.Clear();

            if (env != null)
            {
                try
                {
                    env.Dispose();
                }
                catch (Exception)
                {
                    // 关停不该因为一个释放失败把整条模块关停链拖下水。
                }
            }
        }

        /// <summary>IDisposable 只是 Shutdown 的顺手写法。</summary>
        public void Dispose()
        {
            Shutdown();
        }

        /// <summary>
        /// 喂给 Lua 侧 <c>require</c> 的字节来源。
        ///
        /// 请求名（<paramref name="filepath"/>）来自**下载下来的补丁代码**，属于不可信输入，
        /// 所以这里和文件来源套同一道检查：拒绝绝对路径、拒绝「..」。找不到返回 null，
        /// 让 Lua 自己报「module not found」—— 那是它本来就会说的话，我们不必另造一句。
        /// </summary>
        private byte[] ResolveChunkBytes(ref string filepath)
        {
            if (_chunkSource == null || string.IsNullOrEmpty(filepath))
            {
                return null;
            }

            string candidate = Normalize(filepath);
            string problem;
            if (!RelativePathGuard.TryValidate(candidate, out problem))
            {
                return null;
            }

            string text;
            string error;
            if (!_chunkSource.TryReadText(candidate, out text, out error))
            {
                // 名字可能带或不带 .lua 后缀，两个方向都试一次 —— Lua 的 searchpath 会带后缀来，
                // 而清单里的 fileName 带不带后缀由构建管线决定，这里不假设。
                if (!candidate.EndsWith(ScriptManifest.DefaultFileSuffix, StringComparison.Ordinal)
                    && _chunkSource.TryReadText(candidate + ScriptManifest.DefaultFileSuffix, out text, out error))
                {
                    return Encoding.UTF8.GetBytes(text);
                }

                return null;
            }

            return Encoding.UTF8.GetBytes(text);
        }

        /// <summary>把 Lua 传进来的名字归一成清单里的相对路径形态。</summary>
        private static string Normalize(string filepath)
        {
            string value = filepath.Replace('\\', '/');
            while (value.StartsWith("./", StringComparison.Ordinal))
            {
                value = value.Substring(2);
            }

            return value;
        }

        /// <summary>把名字里的引号与反斜杠转义，避免拼出一条会被截断的 Lua 语句。</summary>
        private static string EscapeForLuaString(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
