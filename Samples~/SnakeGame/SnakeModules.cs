using System.Collections.Generic;
using UnityEngine;
using YourAI.Core.Json;
using YourFramework.Core;
using YourFramework.Event;
using YourFramework.Flow;
using YourFramework.Save;

namespace YourAIFramework.SnakeGame
{
    /// <summary>
    /// 分数与最高分：事件驱动的薄模块。
    ///
    /// 存档教学的两个点：
    /// 1. "失败也是值"—— <see cref="SaveSystem.Save"/> 返回 null 才是成功，失败带原因；
    ///    磁盘出问题时游戏不崩，错误文本打到日志里让玩家看得见；
    /// 2. 读不到存档（第一次玩 / 损坏被隔离）不算错，最高分从 0 开始照样开玩。
    /// </summary>
    public sealed class SnakeScoreModule : IModule
    {
        private const string Slot = "snake-high";

        private readonly EventBus _bus;
        private readonly SaveSystem _saves;

        public SnakeScoreModule(EventBus bus, SaveSystem saves)
        {
            _bus = bus;
            _saves = saves;
        }

        public string Name { get { return "SnakeScore"; } }
        public int InitOrder { get { return 25; } }

        /// <summary>历史最高分（启动时从存档读入）。</summary>
        public int High { get; private set; }

        /// <summary>本局是否破了纪录（游戏结束面板用它显示"新纪录！"）。</summary>
        public bool NewRecordThisRun { get; private set; }

        public void Init(ModuleCenter host)
        {
            JsonValue payload;
            int version;
            string error;
            if (_saves.TryLoad(Slot, out payload, out version, out error))
            {
                High = payload.Path("high").AsInt(0);
                Debug.Log("[Snake] 读到最高分 " + High);
            }
            else
            {
                Debug.Log("[Snake] 没有历史存档（" + error + "），最高分从 0 开始");
            }

            _bus.Subscribe<SnakeStartedEvent>(OnStarted);
            _bus.Subscribe<SnakeFoodEatenEvent>(OnFoodEaten);
            _bus.Subscribe<SnakeGameOverEvent>(OnGameOver);
        }

        public void Pump()
        {
        }

        public void Shutdown()
        {
            _bus.Unsubscribe<SnakeStartedEvent>(OnStarted);
            _bus.Unsubscribe<SnakeFoodEatenEvent>(OnFoodEaten);
            _bus.Unsubscribe<SnakeGameOverEvent>(OnGameOver);
        }

        private void OnStarted(SnakeStartedEvent evt)
        {
            NewRecordThisRun = false;
        }

        private void OnFoodEaten(SnakeFoodEatenEvent evt)
        {
            if (evt.Score > High)
            {
                High = evt.Score;
                NewRecordThisRun = true;
            }
        }

        private void OnGameOver(SnakeGameOverEvent evt)
        {
            JsonValue payload = new JsonValue
            {
                Kind = JsonKind.Object,
                Members = new Dictionary<string, JsonValue>
                {
                    { "high", new JsonValue { Kind = JsonKind.Number, NumberValue = High } },
                },
            };

            string saveError = _saves.Save(Slot, payload);
            if (saveError == null)
            {
                Debug.Log("[Snake] 最高分 " + High + " 已写入存档");
            }
            else
            {
                Debug.Log("[Snake] 最高分没存上（失败也是值）: " + saveError);
            }
        }
    }

    /// <summary>
    /// 游戏规则接线：棋盘的终局事件翻译成流程机的状态切换。
    /// 棋盘不知道流程机存在，流程机不知道棋盘存在 —— 中间的翻译住在这个模块里。
    /// </summary>
    public sealed class SnakeGameRules : IModule
    {
        private readonly EventBus _bus;
        private readonly ProcedureFlow _flow;

        public SnakeGameRules(EventBus bus, ProcedureFlow flow)
        {
            _bus = bus;
            _flow = flow;
        }

        public string Name { get { return "SnakeRules"; } }
        public int InitOrder { get { return 35; } }

        public void Init(ModuleCenter host)
        {
            _bus.Subscribe<SnakeGameOverEvent>(OnGameOver);
        }

        public void Pump()
        {
        }

        public void Shutdown()
        {
            _bus.Unsubscribe<SnakeGameOverEvent>(OnGameOver);
        }

        private void OnGameOver(SnakeGameOverEvent evt)
        {
            _flow.ChangeProcedure("GameOver");
        }
    }

    /// <summary>菜单态：棋盘不走，等开始按钮。</summary>
    public sealed class SnakeMenuProcedure : Procedure
    {
        public override string Name { get { return "Menu"; } }

        public override void OnEnter()
        {
            Debug.Log("[Snake] 菜单：点「开始游戏」（或按回车）");
        }
    }

    /// <summary>进行中：棋盘由宿主按真实时间逐拍推进。</summary>
    public sealed class SnakePlayingProcedure : Procedure
    {
        public override string Name { get { return "Playing"; } }

        public override void OnEnter()
        {
            Debug.Log("[Snake] 开局！方向键 / WASD 移动，A 键切换 AI 操控");
        }
    }

    /// <summary>终局：等空格或按钮重开。</summary>
    public sealed class SnakeGameOverProcedure : Procedure
    {
        public override string Name { get { return "GameOver"; } }

        public override void OnEnter()
        {
            Debug.Log("[Snake] 游戏结束，按空格或点「重新开始」再来一局");
        }
    }
}
