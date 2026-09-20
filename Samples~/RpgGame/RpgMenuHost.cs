using UnityEngine;
using YourFramework.Core;
using YourFramework.Event;
using YourFramework.Scenes;
using YourFramework.UnityRuntime;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// 主菜单场景宿主（场景名 RpgMenu）。整个 demo 场景流程的第一站：
    ///
    ///   主菜单 →（切换）→ 城镇 →（切换）→ 野外 / 副本 →（再切）→ 城镇 → …
    ///
    /// 这里能看到 SceneFlow 的最简用法：new 一个模块进 ModuleCenter，
    /// 点按钮 Request，每帧 Pump + 读 Progress 画进度条，完成/失败全走事件。
    /// 想把这套搬进自己项目，把这个类当模板就够了。
    /// </summary>
    public sealed class RpgMenuHost : MonoBehaviour
    {
        private EventBus _bus;
        private ModuleCenter _modules;
        private SceneFlow _scenes;
        private RpgSimpleUi _ui;

        private void Awake()
        {
            _bus = new EventBus();
            _modules = new ModuleCenter();
            // 1.2f = 最短展示时长：空场景毫秒级加载完，进度条停满格 1.2 秒再切过去
            _scenes = new SceneFlow(_bus, new UnitySceneBackend(2f), Debug.Log);
            _modules.Register(_scenes);
            _modules.Initialize();

            _ui = gameObject.AddComponent<RpgSimpleUi>();
            _ui.Build("勇者试炼场", "框架场景流程演示：主菜单 → 城镇 → 野外/副本，角色档案全程跟着走");
            _ui.AddButton("开始冒险", GoTown);
            _ui.SetLine("四个场景（主菜单/城镇/野外/副本）都在 Build Settings 里，见 README 接入步骤。");
        }

        private void Update()
        {
            _modules.Pump();

            bool busy = _scenes.IsBusy;
            _ui.SetButtonsEnabled(!busy);
            if (busy)
            {
                _ui.ShowProgress(_scenes.Progress, "正在进入城镇 … " + (_scenes.Progress * 100f).ToString("0") + "%");
            }
            else
            {
                _ui.ShowProgress(-1f, null);
            }
        }

        private void GoTown()
        {
            SceneFlow.SceneRequestOutcome o = _scenes.Request(RpgScenes.Town);
            if (!o.Accepted)
            {
                _ui.SetLine("走不了：" + o.Error);
            }
        }
    }
}
