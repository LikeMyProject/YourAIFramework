using System;
using YourFramework.Core;

namespace YourFramework.Scripting
{
    /// <summary>脚本运行时的生命周期状态。</summary>
    public enum ScriptRuntimeState
    {
        /// <summary>还没初始化（构造出来了，环境还没建）。</summary>
        NotReady,

        /// <summary>可用：能装载代码块、能调入口。</summary>
        Ready,

        /// <summary>启动失败过：环境建不起来（缺原生库 / 建 Lua state 抛了）。原因在启动结果里。</summary>
        Faulted,

        /// <summary>已关停。再用它是程序 bug。</summary>
        Disposed
    }

    /// <summary>
    /// 一次脚本操作的结果。**失败也是值**：脚本语法错、入口抛异常、代码块缺席……都是运行期故障，
    /// 返回值带着原因，UI 有话可说；调用方不需要 try/catch。
    ///
    /// 与「合约违规」（拿 null 代码块来装、往已关停的运行时里塞东西）的分界线在
    /// <see cref="IScriptRuntime"/> 上：那类问题抛异常，因为它们是写错代码，不是运行时出了事。
    /// </summary>
    public struct ScriptOutcome
    {
        /// <summary>是否成功。</summary>
        public bool Succeeded;

        /// <summary>成功时的返回值（字符串化）。脚本没有返回值就是 null。</summary>
        public string Value;

        /// <summary>失败原因。Succeeded 为 false 时**保证非空**。</summary>
        public string Error;

        /// <summary>成功结果。</summary>
        public static ScriptOutcome Ok(string value)
        {
            ScriptOutcome outcome;
            outcome.Succeeded = true;
            outcome.Value = value;
            outcome.Error = null;
            return outcome;
        }

        /// <summary>
        /// 失败结果。原因为空时**不留白** —— 换成一句显式的占位，因为「失败但说不出为什么」
        /// 会让上层的错误 UI 退化成一片沉默，那比失败本身更难查。
        /// </summary>
        public static ScriptOutcome Fail(string error)
        {
            ScriptOutcome outcome;
            outcome.Succeeded = false;
            outcome.Value = null;
            outcome.Error = string.IsNullOrEmpty(error)
                ? "unspecified failure (a caller forgot the reason)"
                : error;
            return outcome;
        }

        /// <summary>诊断用一行。</summary>
        public override string ToString()
        {
            return Succeeded ? ("ok:" + (Value ?? "<null>")) : ("fail:" + Error);
        }
    }

    /// <summary>
    /// 脚本运行时预留接口：一个能装代码块、能调入口、能被逐帧泵的解释器。
    ///
    /// **这一层是自研的，而 XLua 只是它的一个实现。** 所以：
    /// - 内核零引擎依赖，也不认识任何 Lua 类型 —— 上层（补丁阶段、游戏逻辑）只对着本接口写；
    /// - 换实现不动上层：XLua、自研 Lua 绑定、甚至一个「纯 C# 假解释器」（测试与离线演示用）
    ///   都能塞进这个插槽；
    /// - IL2CPP 上热更代码的唯一落脚点就是这里：程序集装载在 IL2CPP 上被平台拒绝
    ///   （<c>Assembly.Load(byte[])</c> 抛 PlatformNotSupportedException），而**解释执行不受这条限制**，
    ///   于是「IL2CPP 也能热更」这件事等价于「有一个能跑的脚本运行时」。
    ///
    /// 合约：
    /// - 内容层面的问题（语法错、入口不存在、入口抛异常）→ 失败也是值，不抛；
    /// - 写错代码（null 代码块、空名字、往已关停的运行时里塞东西）→ 抛，且要抛得响；
    /// - <see cref="Pump"/> 在关停后是安全空转（关停与帧循环的竞态不是 bug）；
    /// - <see cref="Shutdown"/> 幂等，重复调用不抛（模块关停不能互相拖垮）。
    /// </summary>
    public interface IScriptRuntime
    {
        /// <summary>实现名（诊断用，例如 "xlua" / "toy"）。</summary>
        string Name { get; }

        /// <summary>当前状态。</summary>
        ScriptRuntimeState State { get; }

        /// <summary>
        /// Loads one chunk into the runtime. Content failures (syntax error, runtime error)
        /// come back as a failed outcome with the interpreter's own message.
        /// </summary>
        ScriptOutcome LoadChunk(ScriptChunk chunk);

        /// <summary>
        /// Unloads a previously loaded chunk by name. Not-loaded is a failed outcome, not a
        /// throw: during a rollback 「本来就没装上」 is a legitimate state, not a bug.
        /// </summary>
        ScriptOutcome UnloadChunk(string name);

        /// <summary>Calls a global function (a hot-patch entry point). Missing entry is a failed outcome.</summary>
        ScriptOutcome CallEntry(string entryName);

        /// <summary>Advances one frame (LuaEnv.Tick and friends). Safe no-op after shutdown.</summary>
        void Pump(double deltaSeconds);

        /// <summary>Shuts the runtime down. Idempotent.</summary>
        void Shutdown();
    }

    /// <summary>
    /// 把脚本运行时接进模块系统的薄壳：每帧泵一次，关停时按序释放。
    ///
    /// 一个壳而不是让每个实现各自实现 IModule：宿主该关心的是「脚本运行时在跑」，
    /// 不该关心它是谁；而适配层（XLua / 其它）因此不必知道模块系统长什么样 ——
    /// 换运行时、或同时挂两个运行时，都不需要改适配层。
    ///
    /// delta 的来路：<see cref="IModule.Pump"/> 不带参数（模块系统只承诺「该泵了」），
    /// 所以这里给一个带 delta 的重载 —— 有帧时长的宿主直接走 <see cref="Pump(double)"/>，
    /// 只用模块系统驱动的宿主拿到 0。对「每帧滴答一下」的解释器（LuaEnv.Tick 按调用次数
    /// 自己排 GC）0 恰好够用；哪天真需要帧时长，把宿主改成调那个重载即可，接口不动。
    /// </summary>
    public sealed class ScriptRuntimeModule : IModule
    {
        private readonly IScriptRuntime _runtime;
        private double _deltaSeconds;

        /// <summary>包一个运行时。null 是接错线，抛。</summary>
        public ScriptRuntimeModule(IScriptRuntime runtime)
            : this("Script", runtime)
        {
        }

        /// <summary>包一个运行时并指定模块名。</summary>
        public ScriptRuntimeModule(string moduleName, IScriptRuntime runtime)
        {
            if (string.IsNullOrEmpty(moduleName))
            {
                throw new ArgumentException("ScriptRuntimeModule: moduleName must not be empty.", "moduleName");
            }

            if (runtime == null)
            {
                throw new ArgumentNullException("runtime");
            }

            Name = moduleName;
            _runtime = runtime;
        }

        /// <summary>被包的运行时（宿主可能还要直接拿它做点事）。</summary>
        public IScriptRuntime Runtime { get { return _runtime; } }

        /// <summary>模块名。</summary>
        public string Name { get; private set; }

        /// <summary>
        /// 模块序：50。落在资源/网络之后（补丁要读文件、可能要下载），热更编排（60）之前 ——
        /// 热更阶段要往运行时里装块，运行时得先活着。
        /// </summary>
        public int InitOrder { get { return 50; } }

        /// <summary>模块 plumbing。运行时自己在构造时建环境，这里无事可做。</summary>
        public void Init(ModuleCenter host)
        {
        }

        /// <summary>模块系统驱动的每帧泵（无帧时长，沿用上一次的值）。</summary>
        public void Pump()
        {
            _runtime.Pump(_deltaSeconds);
        }

        /// <summary>带帧时长的泵：有帧时长的宿主走这条。</summary>
        public void Pump(double deltaSeconds)
        {
            _deltaSeconds = deltaSeconds;
            _runtime.Pump(deltaSeconds);
        }

        /// <summary>关停。</summary>
        public void Shutdown()
        {
            _runtime.Shutdown();
        }
    }
}
