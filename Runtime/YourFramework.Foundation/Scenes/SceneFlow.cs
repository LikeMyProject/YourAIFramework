using System;
using System.Collections.Generic;
using YourFramework.Core;
using YourFramework.Event;

namespace YourFramework.Scenes
{
    /// <summary>
    /// 场景流程：把"切场景"从一句 LoadSceneAsync 变成一条看得见、测得了的管线。
    ///
    /// 分层与资产系统（Assets）同款：
    /// - 这里的核心只管状态机：请求 → 忙碌 → 进度 → 完成/失败，全部轮询驱动，
    ///   纯 C# 不依赖 Unity，无头环境可逐帧验证；
    /// - 引擎那一下（SceneManager.LoadSceneAsync）藏在 <see cref="ISceneBackend"/>
    ///   后面，UnityRuntime 里给了一个薄壳实现，换成 Addressables/自研分包
    ///   只需要再写一个后端。
    ///
    /// 三条纪律：
    /// - 失败也是值：加载失败发 <see cref="SceneLoadFailedEvent"/> 带原因，
    ///   状态复位可以再请求，绝不抛异常打断游戏；
    /// - 同一时刻只允许一个进行中的场景操作（IsBusy）：single 加载期间叠加
    ///   别的场景没有意义，与其隐性排队不如如实拒绝，让 UI 决定怎么排队；
    /// - 事件是 struct 走 EventBus，零装箱。
    /// </summary>
    public sealed class SceneFlow : IModule
    {
        /// <summary>后端把引擎的加载操作包成句柄。核心每帧调 <see cref="SceneHandle.Refresh"/>，
        /// 句柄自己报告进度与完成。放不放场景、进度怎么算，全是后端的事。</summary>
        public interface ISceneBackend
        {
            /// <summary>整场切换（旧的卸、新的进）。操作发不出去（比如场景不在 Build Settings）
            /// 返回 null，核心按失败处理，不抛异常。</summary>
            SceneHandle LoadSingle(string sceneName);

            /// <summary>叠加加载（分块/子场景）。</summary>
            SceneHandle LoadAdditive(string sceneName);

            /// <summary>卸载一个叠加场景。主场景不归它卸。</summary>
            SceneHandle Unload(string sceneName);

            /// <summary>该场景现在是否已加载（叠加判重用）。</summary>
            bool IsLoaded(string sceneName);
        }

        /// <summary>进行中的一笔场景操作。后端实现 Refresh：把引擎进度翻译进来。</summary>
        public abstract class SceneHandle
        {
            public string SceneName;
            public bool IsDone;
            public bool IsSucceeded;
            public float Progress;
            public string Error;

            /// <summary>后端报告进度（0..1，别自己报 1，完成走 <see cref="MarkSucceeded"/>）。</summary>
            protected void SetProgress(float value)
            {
                if (value < 0f) value = 0f;
                if (value > 1f) value = 1f;
                Progress = value;
            }

            /// <summary>落终态。粘性：再调是无害的空操作。</summary>
            protected void MarkSucceeded() { if (!IsDone) { IsDone = true; IsSucceeded = true; } }

            protected void MarkFailed(string reason)
            {
                if (IsDone) return;
                IsDone = true;
                IsSucceeded = false;
                Error = reason == null ? "unknown" : reason;
            }

            /// <summary>每帧由 SceneFlow 泵。后端在这里读引擎、报进度、落终态。</summary>
            public abstract void Refresh();
        }

        /// <summary>请求的受理结果。失败也是值：Accepted=false 时 Error 说人话。</summary>
        public struct SceneRequestOutcome
        {
            public bool Accepted;
            public string Error;

            public static SceneRequestOutcome Ok()
            {
                SceneRequestOutcome o;
                o.Accepted = true;
                o.Error = null;
                return o;
            }

            public static SceneRequestOutcome Reject(string error)
            {
                SceneRequestOutcome o;
                o.Accepted = false;
                o.Error = error;
                return o;
            }
        }

        public struct SceneLoadStartedEvent { public string SceneName; public bool Additive; }
        public struct SceneLoadCompletedEvent { public string SceneName; public bool Additive; }
        public struct SceneLoadFailedEvent { public string SceneName; public string Error; }
        public struct SceneUnloadCompletedEvent { public string SceneName; }

        private readonly EventBus _bus;
        private readonly ISceneBackend _backend;
        private readonly Action<string> _log;

        private SceneHandle _active;
        private string _current;
        private readonly List<string> _additives = new List<string>(8);

        public SceneFlow(EventBus bus, ISceneBackend backend, Action<string> log = null)
        {
            if (bus == null) throw new ArgumentNullException("bus");
            if (backend == null) throw new ArgumentNullException("backend");
            _bus = bus;
            _backend = backend;
            _log = log;
        }

        public string Name { get { return "Scenes"; } }
        public int InitOrder { get { return 26; } }

        public void Init(ModuleCenter host) { }
        public void Shutdown() { _active = null; }

        /// <summary>最近一次整场切换完成的场景名。还没切过就是 null。</summary>
        public string CurrentScene { get { return _current; } }

        /// <summary>当前叠加加载的场景列表（快照副本）。</summary>
        public List<string> Additives { get { return new List<string>(_additives); } }

        /// <summary>同一时刻只允许一笔场景操作在飞。</summary>
        public bool IsBusy { get { return _active != null; } }

        /// <summary>进行中操作的进度（0..1）；空闲时 0。</summary>
        public float Progress { get { return _active == null ? 0f : _active.Progress; } }

        /// <summary>进行中操作的场景名；空闲时 null。进度条 UI 读这两个就够。</summary>
        public string ActiveScene { get { return _active == null ? null : _active.SceneName; } }

        /// <summary>整场切换到目标场景。旧的会被引擎卸掉。</summary>
        public SceneRequestOutcome Request(string sceneName)
        {
            string problem = Preflight(sceneName, "切换");
            if (problem != null) return SceneRequestOutcome.Reject(problem);
            if (_current == sceneName) return SceneRequestOutcome.Reject("已在场景 " + sceneName + " 里");

            SceneHandle handle = _backend.LoadSingle(sceneName);
            return Begin(handle, sceneName, false, false);
        }

        /// <summary>叠加加载一个场景（分块/子场景）。</summary>
        public SceneRequestOutcome RequestAdditive(string sceneName)
        {
            string problem = Preflight(sceneName, "叠加加载");
            if (problem != null) return SceneRequestOutcome.Reject(problem);
            if (sceneName == _current) return SceneRequestOutcome.Reject(sceneName + " 是主场景，叠加它没有意义");
            if (_backend.IsLoaded(sceneName)) return SceneRequestOutcome.Reject(sceneName + " 已加载，不要重复叠加");

            SceneHandle handle = _backend.LoadAdditive(sceneName);
            return Begin(handle, sceneName, true, false);
        }

        /// <summary>卸载一个叠加场景。主场景不归这里管。</summary>
        public SceneRequestOutcome UnloadAdditive(string sceneName)
        {
            string problem = Preflight(sceneName, "卸载");
            if (problem != null) return SceneRequestOutcome.Reject(problem);
            if (sceneName == _current) return SceneRequestOutcome.Reject(sceneName + " 是主场景，要离开它请用 Request 切走");
            if (!_additives.Contains(sceneName)) return SceneRequestOutcome.Reject(sceneName + " 不是本流程叠加加载的，不替你卸");

            SceneHandle handle = _backend.Unload(sceneName);
            return Begin(handle, sceneName, true, true);
        }

        /// <summary>每帧泵：刷新进行中的句柄，落事件。空闲时是纯空转。</summary>
        public void Pump()
        {
            if (_active == null) return;
            _active.Refresh();
            if (!_active.IsDone) return;

            SceneHandle done = _active;
            _active = null;
            if (done.IsSucceeded)
            {
                if (_pendingUnload)
                {
                    _additives.Remove(done.SceneName);
                    _bus.Publish(new SceneUnloadCompletedEvent { SceneName = done.SceneName });
                    Say("场景已卸载：" + done.SceneName);
                }
                else if (_pendingAdditive)
                {
                    if (!_additives.Contains(done.SceneName)) _additives.Add(done.SceneName);
                    _bus.Publish(new SceneLoadCompletedEvent { SceneName = done.SceneName, Additive = true });
                    Say("场景已叠加：" + done.SceneName);
                }
                else
                {
                    _current = done.SceneName;
                    _bus.Publish(new SceneLoadCompletedEvent { SceneName = done.SceneName, Additive = false });
                    Say("场景已切换：" + done.SceneName);
                }
            }
            else
            {
                _bus.Publish(new SceneLoadFailedEvent { SceneName = done.SceneName, Error = done.Error });
                Say("场景操作失败：" + done.SceneName + "，原因：" + done.Error);
            }
        }

        // ------------------------------------------------------------------ 内部

        private bool _pendingAdditive;
        private bool _pendingUnload;

        private string Preflight(string sceneName, string what)
        {
            if (string.IsNullOrEmpty(sceneName)) return "场景名为空";
            if (IsBusy) return "有场景操作正在进行（" + _active.SceneName + "），等它完成再" + what;
            return null;
        }

        private SceneRequestOutcome Begin(SceneHandle handle, string sceneName, bool additive, bool unload)
        {
            if (handle == null)
            {
                // 后端发不出这张牌（典型：场景不在 Build Settings）。失败也是值。
                _bus.Publish(new SceneLoadFailedEvent { SceneName = sceneName, Error = "后端无法开始该操作（检查场景是否在 Build Settings）" });
                Say("场景操作没能开始：" + sceneName);
                return SceneRequestOutcome.Reject("后端无法开始该操作");
            }

            _active = handle;
            _pendingAdditive = additive;
            _pendingUnload = unload;
            _bus.Publish(new SceneLoadStartedEvent { SceneName = sceneName, Additive = additive });
            return SceneRequestOutcome.Ok();
        }

        private void Say(string text)
        {
            if (_log != null) _log(text);
        }
    }
}
