using System.IO;
using UnityEngine;
using YourFramework.Core;
using YourFramework.Event;
using YourFramework.Save;
using YourFramework.Scenes;
using YourFramework.UnityRuntime;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// 城镇场景宿主（场景名 RpgTown）：安全区，没有战斗。
    ///
    /// 教学点有两个：
    /// - 档案跨场景：读档用的是和战斗场景同一个 SaveSystem 目录，
    ///   Profile 模块 Load 时把角色捞回来；泉水回满后 SaveNow 落盘，
    ///   下一个场景（野外/副本）再读，成长的账一路连续；
    /// - 多入口调度：去野外（城门）、下副本（传送门）、回主菜单都是一次
    ///   SceneFlow.Request，忙碌时拒绝原因直接显示在界面上 —— 失败也是值。
    ///   出门不点按钮，魔兽那一套：走到城门/传送门边上按 F —— 交互点由视图报，
    ///   触发归宿主，"走路归视图，玩法归宿主"的分界线不变。
    /// </summary>
    public sealed class RpgTownHost : MonoBehaviour
    {
        private EventBus _bus;
        private ModuleCenter _modules;
        private SceneFlow _scenes;
        private RpgProfileModule _profile;
        private RpgPlayer _player;
        private RpgSimpleUi _ui;
        private RpgControl _input;
        private RpgTownView _view;

        private void Awake()
        {
            _bus = new EventBus();
            _player = new RpgPlayer();
            // 1.2f = 最短展示时长：空场景毫秒级加载完，进度条停满格 1.2 秒再切过去
            _scenes = new SceneFlow(_bus, new UnitySceneBackend(2f), Debug.Log);

            SaveSystem saves = new SaveSystem(Path.Combine(Application.persistentDataPath, "RpgSaves"));
            _profile = new RpgProfileModule(_bus, saves, _player, Debug.Log);

            _modules = new ModuleCenter();
            _modules.Register(_profile);
            _modules.Register(_scenes);
            _modules.Initialize();

            _ui = gameObject.AddComponent<RpgSimpleUi>();
            // 角落模式：玩的过程里界面只是附属品，标题/状态/按钮/提示全收角落，
            // 不挡 3D 视野（居中的菜单式界面只属于主菜单场景）
            _ui.Build("城镇", "安全区。走到泉水 / 副本传送门 / 城门边按 F 交互；WASD/QE 走路，右键拖动转视角，滚轮拉远近", true);
            _ui.AddButton("返回主菜单", delegate { Go(RpgScenes.Menu); });
            RefreshLine();

            // 城镇也是能逛的 3D 世界：输入和野外同一套，走路归视图、交互归宿主
            _input = gameObject.AddComponent<RpgControl>();
            _input.Init(delegate { return true; }, null, null, null, null);
            _view = gameObject.AddComponent<RpgTownView>();
            _view.Init(_input);

            // 落点：野外/副本回来落复活点（泉水旁），从主菜单进落广场。读完立刻复位。
            if (RpgSpawnBook.NextTownSpawn == "well")
            {
                _view.SpawnAt(2.6f, 0.8f);
            }

            RpgSpawnBook.NextTownSpawn = "plaza";
        }

        private void Update()
        {
            _modules.Pump();

            bool busy = _scenes.IsBusy;
            _ui.SetButtonsEnabled(!busy);
            if (busy)
            {
                _ui.ShowProgress(_scenes.Progress, "正在前往 " + _scenes.ActiveScene + " … " + (_scenes.Progress * 100f).ToString("0") + "%");
                _ui.SetPrompt(null);
                return;
            }

            _ui.ShowProgress(-1f, null);

            // 交互：走近了出提示，按 F 触发（泉水回满 / 进副本 / 出城门）
            _ui.SetPrompt(_view.CurrentInteractionLabel);
            if (_input.ConsumeInteract())
            {
                string id = _view.CurrentInteractionId;
                if (id == "well")
                {
                    Rest();
                }
                else if (id == "dungeon")
                {
                    Go(RpgScenes.Dungeon);
                }
                else if (id == "wilds")
                {
                    Go(RpgScenes.Wilds);
                }
            }
        }

        private void Rest()
        {
            _player.RestoreFull();
            string error = _profile.SaveNow();
            RefreshLine();
            _ui.SetLine(error == null
                ? "泉水洗去疲惫，状态回满（已存档）。"
                : "恢复成功，但存档失败：" + error);
        }

        private void Go(string sceneName)
        {
            SceneFlow.SceneRequestOutcome o = _scenes.Request(sceneName);
            if (!o.Accepted)
            {
                _ui.SetLine("走不了：" + o.Error);
            }
        }

        private void RefreshLine()
        {
            _ui.SetLine("Lv." + _player.Level + "　攻击 " + _player.Attack + "　防御 " + _player.Defense
                + "　血 " + _player.Hp + "/" + _player.MaxHp
                + "　法力 " + _player.Mp.ToString("0") + "/" + _player.MaxMp
                + "　金币 " + _player.Gold);
        }
    }
}
