using UnityEngine;
using UnityEngine.SceneManagement;
using YourFramework.Scenes;

namespace YourFramework.UnityRuntime
{
    /// <summary>
    /// <see cref="SceneFlow.ISceneBackend"/> 的 Unity 实现：几十行薄壳，
    /// 把 SceneManager 的异步操作包成句柄、把 AsyncOperation.progress 翻译成 0..1。
    /// 换 Addressables 或自研分包时，另写一个后端就行，SceneFlow 一行不用动。
    ///
    /// 进度口径：AsyncOperation.progress 到 0.9 就停住等激活，这里按 /0.9 归一化，
    /// isDone 才落终态 —— 进度条会一路跑到头，不会卡在 90%。
    ///
    /// 最短展示时长：空场景加载是毫秒级的，进度条一闪而过等于没有。
    /// 构造传一个秒数（demo 用 1.2），加载完成后进度条停在满格把时间耗完才落完成，
    /// 玩家看得见"这一屏是在切场景"。这是引擎壳的表现职责，核心状态机不感知。
    /// </summary>
    public sealed class UnitySceneBackend : SceneFlow.ISceneBackend
    {
        private readonly float _minVisibleSeconds;

        public UnitySceneBackend(float minVisibleSeconds = 0f)
        {
            _minVisibleSeconds = minVisibleSeconds < 0f ? 0f : minVisibleSeconds;
        }

        public SceneFlow.SceneHandle LoadSingle(string sceneName)
        {
            return Wrap(sceneName, SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single));
        }

        public SceneFlow.SceneHandle LoadAdditive(string sceneName)
        {
            return Wrap(sceneName, SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive));
        }

        public SceneFlow.SceneHandle Unload(string sceneName)
        {
            return Wrap(sceneName, SceneManager.UnloadSceneAsync(sceneName));
        }

        public bool IsLoaded(string sceneName)
        {
            return SceneManager.GetSceneByName(sceneName).isLoaded;
        }

        private SceneFlow.SceneHandle Wrap(string sceneName, AsyncOperation op)
        {
            // LoadSceneAsync 找不到场景（多半是没进 Build Settings）返回 null —— 如实上交，
            // SceneFlow 会发带原因的失败事件，不抛异常。
            if (op == null) return null;
            return new UnitySceneHandle(sceneName, op, _minVisibleSeconds);
        }

        private sealed class UnitySceneHandle : SceneFlow.SceneHandle
        {
            private readonly AsyncOperation _op;
            private readonly float _hold;
            private float _held;

            public UnitySceneHandle(string sceneName, AsyncOperation op, float hold)
            {
                SceneName = sceneName;
                _op = op;
                _hold = hold;
            }

            public override void Refresh()
            {
                if (IsDone || _op == null) return;
                if (_op.isDone)
                {
                    // 加载完了：进度条钉在满格，把最短展示时间耗完再落完成
                    SetProgress(1f);
                    if (_hold <= 0f)
                    {
                        MarkSucceeded();
                        return;
                    }
                    _held += Time.deltaTime;
                    if (_held >= _hold)
                    {
                        MarkSucceeded();
                    }
                    return;
                }
                SetProgress(_op.progress / 0.9f);
            }
        }
    }
}
