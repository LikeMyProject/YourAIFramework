using System;
using System.Collections.Generic;

namespace YourFramework.Core
{
    /// <summary>
    /// One framework module: a unit of framework functionality with an explicit
    /// lifecycle. The centre drives it; the module never drives the centre.
    ///
    /// Lifecycle contract, in the order the centre guarantees it:
    ///     Init  -- once, in ascending <see cref="InitOrder"/>; fetch dependencies here
    ///     Pump  -- once per frame, in the same order, after every module is initialized
    ///     Shutdown -- once, in reverse InitOrder; release engine resources here
    ///
    /// Modules are plain C#: nothing here knows about GameObjects, which is why the
    /// whole lifecycle is assertable in a headless harness.
    /// </summary>
    public interface IModule
    {
        /// <summary>Human-readable name. Must be unique across the centre.</summary>
        string Name { get; }

        /// <summary>
        /// Lower runs earlier. Give pure infrastructure negative or small values
        /// (events, pools), and larger values to modules that depend on them. A
        /// module may only call <see cref="ModuleCenter.Get{T}"/> on modules with a
        /// strictly smaller InitOrder -- that is the whole dependency rule.
        /// </summary>
        int InitOrder { get; }

        /// <summary>Called once. Fetch dependencies via <c>host.Get{T}()</c> here.</summary>
        void Init(ModuleCenter host);

        /// <summary>Advances one frame's worth of module work.</summary>
        void Pump();

        /// <summary>Called once on shutdown, reverse order. Must tolerate double calls.</summary>
        void Shutdown();
    }

    /// <summary>
    /// 模块管理中心：注册 → 按 InitOrder 稳定排序初始化 → 每帧 Pump → 逆序 Shutdown。
    ///
    /// 设计取舍：
    /// 1. 纯 C#、零引擎依赖 —— 引擎宿主是 UnityRuntime 侧的 GameEntry，本类可在无头
    ///    环境逐条检查项（module/* 用例组）；
    /// 2. Pump 是显式契约而非生命周期黑盒 —— 与框架的轮询哲学（AI 层同理）一致，
    ///    谁驱动、何时驱动，宿主说了算；
    /// 3. 不做服务定位器魔法 —— 依赖即 InitOrder 数字，Get{T} 只对更早初始化的模块
    ///    有意义，规则一句话讲完。
    ///
    /// 出错时的处理：注册/生命周期时序错误是程序 bug，直接抛 InvalidOperationException
    /// （fail-fast）；模块自身的运行期错误由模块自己按“失败也是值”处理。
    /// </summary>
    public sealed class ModuleCenter
    {
        private readonly List<IModule> _modules = new List<IModule>();
        private readonly Dictionary<Type, IModule> _byType = new Dictionary<Type, IModule>();
        private readonly HashSet<Type> _ready = new HashSet<Type>();
        private bool _initialized;
        private bool _shutdown;

        /// <summary>True once <see cref="Initialize"/> has completed.</summary>
        public bool IsInitialized { get { return _initialized; } }

        /// <summary>True once <see cref="Shutdown"/> has run. The centre is then inert.</summary>
        public bool IsShutdown { get { return _shutdown; } }

        /// <summary>Registered module count.</summary>
        public int Count { get { return _modules.Count; } }

        /// <summary>
        /// Adds a module. Setup phase only: after Initialize (or Shutdown) this
        /// throws. Duplicate type or duplicate name also throws -- both are bugs.
        /// </summary>
        public void Register(IModule module)
        {
            if (module == null)
            {
                throw new ArgumentNullException("module");
            }

            if (_shutdown)
            {
                throw new InvalidOperationException(
                    "ModuleCenter.Register: the centre has been shut down; it does not restart.");
            }

            if (_initialized)
            {
                throw new InvalidOperationException(
                    "ModuleCenter.Register: too late -- the centre is initialized. Register before Initialize().");
            }

            Type type = module.GetType();
            if (_byType.ContainsKey(type))
            {
                throw new InvalidOperationException(
                    "ModuleCenter.Register: module type " + type.Name
                    + " is already registered. One instance per type keeps Get{T} unambiguous -- "
                    + "subclass or wrap it when you need a second variant.");
            }

            // 校验全部通过之后再落库。先 Add 再查重的话，重名抛异常时 _byType 里已经躺着
            // 这个模块了，而 _modules 里没有它 —— 调用方若捕获异常继续跑，Get{T} 能拿到它，
            // 却永远不会 Init、Pump、Shutdown：一个拿得到的幽灵。
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_modules[i].Name == module.Name)
                {
                    throw new InvalidOperationException(
                        "ModuleCenter.Register: module name '" + module.Name + "' is already used by "
                        + _modules[i].GetType().Name + ".");
                }
            }

            _byType.Add(type, module);
            _modules.Add(module);
        }

        /// <summary>
        /// Initializes every module in stable InitOrder order (ties keep registration
        /// order) and marks each ready before the next one runs, so a module with a
        /// larger InitOrder sees its dependencies fully initialized via Get{T}.
        ///
        /// A module whose Init throws is reported through AiDiagnosticsHost, skipped,
        /// and left out of the ready set (dependents can see that with IsReady{T}); the
        /// rest still initialize. Failing loud AND leaving the centre shut-downable
        /// beats failing loud and stranding everything that already came up.
        /// </summary>
        public void Initialize()
        {
            if (_shutdown)
            {
                throw new InvalidOperationException("ModuleCenter.Initialize: the centre is shut down.");
            }

            if (_initialized)
            {
                throw new InvalidOperationException("ModuleCenter.Initialize: already initialized.");
            }

            // Stable sort, insertion flavour: module counts are tens at most, and
            // this keeps ties in registration order without LINQ allocations.
            for (int i = 1; i < _modules.Count; i++)
            {
                IModule current = _modules[i];
                int j = i - 1;
                while (j >= 0 && _modules[j].InitOrder > current.InitOrder)
                {
                    _modules[j + 1] = _modules[j];
                    j--;
                }

                _modules[j + 1] = current;
            }

            for (int i = 0; i < _modules.Count; i++)
            {
                IModule module = _modules[i];
                try
                {
                    module.Init(this);
                }
                catch (Exception ex)
                {
                    // 与 Pump / Shutdown 同一条铁律：一个模块起不来，既不拖垮其余，
                    // 也不让它那些已经 Init 好的邻居没人收尸。起不来的模块不进 _ready，
                    // 依赖它的人用 IsReady{T} 就能看见，Shutdown 也会跳过它。
                    //
                    // 这里若不拦：异常穿出 Initialize -> _initialized 永远为 false ->
                    // Shutdown 直接 return，先前 Init 出来的线程、句柄、订阅全部烂在手里。
                    AiDiagnosticsHost.ReportModuleError(module.Name, "Init", ex);
                    continue;
                }

                _ready.Add(module.GetType());
            }

            _initialized = true;
        }

        /// <summary>
        /// Advances every module one frame, in init order. Throws before Initialize:
        /// pumping an uninitialized centre means the host wired its loop wrong.
        ///
        /// One module throwing does NOT stop the others (framework red line: a
        /// single module's runtime fault never takes down the frame) -- the
        /// exception is reported through AiDiagnosticsHost and the loop continues.
        /// </summary>
        public void Pump()
        {
            if (!_initialized || _shutdown)
            {
                throw new InvalidOperationException("ModuleCenter.Pump: only valid between Initialize and Shutdown.");
            }

            for (int i = 0; i < _modules.Count; i++)
            {
                try
                {
                    _modules[i].Pump();
                }
                catch (Exception ex)
                {
                    AiDiagnosticsHost.ReportModuleError(_modules[i].Name, "Pump", ex);
                }
            }
        }

        /// <summary>
        /// Shuts every module down in reverse init order, including one whose Init threw
        /// (it may have taken resources before it failed, and this is its one chance to
        /// give them back). A module that throws on teardown is reported rather than
        /// stopping the rest. Idempotent: a second call is a no-op.
        /// </summary>
        public void Shutdown()
        {
            if (_shutdown || !_initialized)
            {
                return;
            }

            for (int i = _modules.Count - 1; i >= 0; i--)
            {
                try
                {
                    _modules[i].Shutdown();
                }
                catch (Exception ex)
                {
                    // A module blowing up on teardown must not prevent the rest from
                    // tearing down; the exception is reported, not swallowed.
                    AiDiagnosticsHost.ReportModuleError(_modules[i].Name, "Shutdown", ex);
                }
            }

            _shutdown = true;
        }

        /// <summary>
        /// The registered instance of T, or null. Before Initialize the instance
        /// exists but has not run Init -- for anything beyond mere registration,
        /// fetch dependencies inside Init and rely on InitOrder.
        /// </summary>
        public T Get<T>() where T : class, IModule
        {
            IModule module;
            return _byType.TryGetValue(typeof(T), out module) ? module as T : null;
        }

        /// <summary>True once T has been registered AND initialized.</summary>
        public bool IsReady<T>() where T : class, IModule
        {
            return _ready.Contains(typeof(T));
        }
    }

    /// <summary>
    /// Where ModuleCenter reports module faults. Hosting code (GameEntry) sets
    /// this to route into its logging; the default writes to Console.Error so the
    /// headless harness surfaces it too. Kept as a hook rather than a Debug call so
    /// the centre stays engine-free.
    /// </summary>
    public static class AiDiagnosticsHost
    {
        /// <summary>Set by the engine host at startup. Null means Console.Error.</summary>
        public static Action<string> LogError;

        /// <summary>
        /// Reports an already-formatted line through the same channel.
        ///
        /// Callers that build their own wording (procedure faults, despawn failures)
        /// go through this instead of touching <see cref="LogError"/> directly: the
        /// field is null by default, and a null-dereference in the error path replaces
        /// the real fault with a NullReferenceException that says nothing.
        /// </summary>
        public static void ReportLine(string line)
        {
            Action<string> handler = LogError;
            if (handler != null)
            {
                handler(line);
            }
            else
            {
                Console.Error.WriteLine(line);
            }
        }

        /// <summary>Reports a module fault in a lifecycle phase (Pump/Shutdown/…).</summary>
        public static void ReportModuleError(string moduleName, string phase, Exception ex)
        {
            ReportLine("[YourFramework] module '" + moduleName + "' threw during " + phase
                + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
