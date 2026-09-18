using UnityEngine;
using YourFramework.Core;

namespace YourFramework.UnityRuntime
{
    /// <summary>
    /// 模块管理中心在场景里的宿主：纯粹的引擎壳，所有逻辑都在纯 C# 的
    /// ModuleCenter 里，本类只负责把 Unity 生命周期翻译过去。
    ///
    ///     Awake   → 站位（单例 + 可选 DontDestroyOnLoad）
    ///     Start   → modules.Initialize()（其他对象的 Awake 已全部跑完，
    ///               所以引导脚本在 Awake 里 Register 是安全的）
    ///     Update  → modules.Pump()
    ///     OnDestroy → modules.Shutdown()
    ///
    /// 引导写法：
    ///
    ///     void Awake()
    ///     {
    ///         GameEntry.Ensure()
    ///             .Register(new EventBusModule())
    ///             .Register(new SaveModule("/存档目录"));
    ///     }
    ///
    /// 耦合纪律：本类不知道任何具体模块的类型——模块在引导代码里注册，
    /// 模块互访走 ModuleCenter.Get{T}，不经过这里。
    /// </summary>
    public sealed class GameEntry : MonoBehaviour
    {
        private static GameEntry _instance;

        private readonly ModuleCenter _modules = new ModuleCenter();

        /// <summary>Scene-placed or Ensure-created instance. Null before Awake.</summary>
        public static GameEntry Instance { get { return _instance; } }

        /// <summary>The centre this host drives. Register before its Start runs.</summary>
        public ModuleCenter Modules { get { return _modules; } }

        [Tooltip("跨场景存活。数字孪生/仿真类单场景工程可以关掉。")]
        public bool persistAcrossScenes = true;

        /// <summary>
        /// Finds the scene instance or creates one named [GameEntry]. Safe to call
        /// from any Awake: created instances run their own Awake immediately.
        /// </summary>
        public static GameEntry Ensure()
        {
            if (_instance != null)
            {
                return _instance;
            }

            GameEntry found = Object.FindAnyObjectByType<GameEntry>();
            if (found != null)
            {
                _instance = found;
                return found;
            }

            return new GameObject("[GameEntry]").AddComponent<GameEntry>();
        }

        /// <summary>Chainable module registration, setup phase only.</summary>
        public GameEntry Register(IModule module)
        {
            _modules.Register(module);
            return this;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            if (persistAcrossScenes)
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        private void Start()
        {
            _modules.Initialize();
        }

        private void Update()
        {
            if (_modules.IsInitialized && !_modules.IsShutdown)
            {
                _modules.Pump();
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _modules.Shutdown();
                _instance = null;
            }
        }
    }
}
