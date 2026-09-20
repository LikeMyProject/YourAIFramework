using System.IO;
using UnityEngine;
using YourFramework.Core;
using YourFramework.Event;
using YourFramework.Flow;
using YourFramework.Save;
using YourFramework.Stats;

namespace YourAIFramework.SnakeGame
{
    /// <summary>
    /// 引导与驱动：挂到空 GameObject 上按 Play 即玩。
    ///
    /// 这个演示刻意**不用 GameEntry** —— 自己 new ModuleCenter、自己泵、自己关停，
    /// 说明引擎宿主本质上只是个"泵"：多游戏、多场景各持一套模块，互不干扰。
    /// 想并进大工程的模块栈时，把这里的 Register 搬进 GameEntry 引导即可。
    ///
    /// 依赖图（InitOrder 小的先初始化）：
    ///   SnakeScore 25（事件总线 + 存档）
    ///   → SnakeFlow 30（流程机 Menu → Playing → GameOver）
    ///   → SnakeRules 35（终局事件 → 切 GameOver）
    /// 时间在宿主手里：宿主每帧累计 Time.deltaTime，攒够一拍调 board.Tick()，
    /// 所以棋盘（纯 C#）可以在无头环境逐拍验证。
    /// </summary>
    public sealed class SnakeGameHost : MonoBehaviour
    {
        /// <summary>手动模式拍间隔。AI 模式更慢：大模型一秒只能答一次，身体走太快指令永远追不上。</summary>
        private const float ManualTickInterval = 0.30f;
        private const float AiTickInterval = 0.55f;

        private ModuleCenter _modules;
        private EventBus _bus;
        private SnakeBoard _board;
        private ProcedureFlow _flow;
        private SnakeScoreModule _score;
        private SnakeUi _ui;
        private SnakeAiController _ai;
        private float _accum;

        private void Awake()
        {
            _bus = new EventBus();
            DeterministicRandom random = new DeterministicRandom(20260918UL, "snake/food");
            _board = new SnakeBoard(_bus, random);

            SaveSystem saves = new SaveSystem(Path.Combine(Application.persistentDataPath, "SnakeSaves"));
            SettingStore settings = new SettingStore(Path.Combine(Application.persistentDataPath, "game-settings.json"));
            settings.LoadFromDisk();

            _flow = new ProcedureFlow("Snake", 30);
            _flow.Add(new SnakeMenuProcedure())
                .Add(new SnakePlayingProcedure())
                .Add(new SnakeGameOverProcedure())
                .Start<SnakeMenuProcedure>();

            _score = new SnakeScoreModule(_bus, saves);

            _modules = new ModuleCenter();
            _modules.Register(_score);
            _modules.Register(_flow);
            _modules.Register(new SnakeGameRules(_bus, _flow));
            _modules.Initialize();

            BuildCompanions(settings);
            WireUiEvents();

            Camera mainCamera = Camera.main;
            mainCamera.transform.position = new Vector3(0f, 0f, -10f);
            mainCamera.orthographic = true;
            mainCamera.orthographicSize = (SnakeBoard.Grid / 2f) + 1.5f;
            mainCamera.backgroundColor = new Color(0.94f, 0.93f, 0.88f);

            Debug.Log("[Snake] 就绪：方向键 / WASD 移动，A 键或按钮切 AI 操控，空格重开。最高分走 SaveSystem。");
        }

        private void Update()
        {
            _modules.Pump();            // 流程机在这里推进（切换延迟到泵首是它的契约）

            if (!IsPlaying())
            {
                _accum = 0f;
                return;
            }

            float interval = _ai.AiMode ? AiTickInterval : ManualTickInterval;
            _accum += Time.deltaTime;
            int steps = 0;
            while (_accum >= interval && steps < 4)
            {
                _board.Tick();
                _accum -= interval;
                steps++;
            }

            if (steps == 4)
            {
                _accum = 0f;            // 掉帧补账别滚雪球
            }
        }

        private void OnDestroy()
        {
            if (_modules != null)
            {
                _modules.Shutdown();
            }
        }

        private bool IsPlaying()
        {
            return _flow.State == ProcedureFlowState.Running && _flow.CurrentProcedureName == "Playing";
        }

        private bool CanRestart()
        {
            return _flow.State == ProcedureFlowState.Running && _flow.CurrentProcedureName == "GameOver";
        }

        private bool CanStart()
        {
            return _flow.State == ProcedureFlowState.Running && _flow.CurrentProcedureName == "Menu";
        }

        /// <summary>开一局（开始按钮 / 回车 / 重新开始按钮 / 空格都汇到这里）。</summary>
        private void StartGame()
        {
            _board.Reset();
            _flow.ChangeProcedure<SnakePlayingProcedure>();
            _ui.ShowPlaying();
        }

        private void BuildCompanions(SettingStore settings)
        {
            _ui = gameObject.AddComponent<SnakeUi>();
            _ui.Build();
            _ui.ShowMenu();

            gameObject.AddComponent<SnakeView>().Init(_board);
            gameObject.AddComponent<SnakeInput>().Init(_board, IsPlaying, CanRestart, CanStart, StartGame);
            _ai = gameObject.AddComponent<SnakeAiController>();
            _ai.Init(_board, _bus, settings, IsPlaying, _ui.SetAiStatus);
        }

        private void WireUiEvents()
        {
            _ui.OnStartClicked = StartGame;
            _ui.OnRestartClicked = StartGame;
            _ui.OnToggleAiClicked = _ai.ToggleAi;

            _bus.Subscribe<SnakeStartedEvent>(delegate
            {
                _ui.SetScore(_board.Score, _score.High, false);
            });
            _bus.Subscribe<SnakeFoodEatenEvent>(delegate
            {
                _ui.SetScore(_board.Score, _score.High, _score.NewRecordThisRun);
            });
            _bus.Subscribe<SnakeGameOverEvent>(delegate
            {
                _ui.ShowGameOver(_board.Score, _score.NewRecordThisRun);
            });
        }
    }
}
