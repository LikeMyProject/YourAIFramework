using System.Collections.Generic;
using YourAI.Core.Json;
using YourFramework.Core;
using YourFramework.Event;
using YourFramework.Flow;
using YourFramework.Save;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// 角色档案：等级、金币、背包、穿戴的持久层。
    ///
    /// 与贪吃蛇"最高分"的不同：RPG 的成长要跨局保留 —— 死亡只结束这一局，
    /// 等级装备背包都在。所以存档时机不是"终局"，而是**每次变化之后**：
    /// 击杀、升级、捡东西、穿装备、喝药，事件一来就落一次盘。量小（字节级），
    /// 换来"随时拔电源也不丢进度"。
    ///
    /// 这一层不引用 UnityEngine：日志走注入的回调（宿主传 Debug.Log 进来），
    /// 模块本体可以在无头环境连存档一起真存真读。
    ///
    /// 存档走 <see cref="SaveSystem"/>：Save 返回 null 才是成功，失败带原因打进日志，
    /// 游戏照玩（失败也是值）；读不到存档（第一次玩 / 损坏被隔离）就从 1 级开新档。
    /// </summary>
    public sealed class RpgProfileModule : IModule
    {
        private const string Slot = "rpg-profile";

        private readonly EventBus _bus;
        private readonly SaveSystem _saves;
        private readonly RpgPlayer _player;
        private readonly System.Action<string> _log;

        public RpgProfileModule(EventBus bus, SaveSystem saves, RpgPlayer player,
            System.Action<string> log = null)
        {
            _bus = bus;
            _saves = saves;
            _player = player;
            _log = log;
        }

        public string Name { get { return "RpgProfile"; } }
        public int InitOrder { get { return 25; } }

        /// <summary>是否读到了历史档案（菜单界面用它决定要不要显示"继续冒险"）。</summary>
        public bool LoadedFromDisk { get; private set; }

        public void Init(ModuleCenter host)
        {
            LoadedFromDisk = TryLoad();
            Log(LoadedFromDisk
                ? "[RPG] 读到档案：Lv" + _player.Level + " 金币" + _player.Gold
                : "[RPG] 没有历史档案（第一次玩或已损坏隔离），从 1 级开始");

            _bus.Subscribe<RpgMonsterDiedEvent>(OnChanged);
            _bus.Subscribe<RpgPlayerLeveledUpEvent>(OnChanged);
            _bus.Subscribe<RpgItemGainedEvent>(OnChanged);
            _bus.Subscribe<RpgItemEquippedEvent>(OnChanged);
            _bus.Subscribe<RpgPotionUsedEvent>(OnChanged);
        }

        public void Pump()
        {
        }

        public void Shutdown()
        {
            SaveNow();      // 退出前兜底存一次
            _bus.Unsubscribe<RpgMonsterDiedEvent>(OnChanged);
            _bus.Unsubscribe<RpgPlayerLeveledUpEvent>(OnChanged);
            _bus.Unsubscribe<RpgItemGainedEvent>(OnChanged);
            _bus.Unsubscribe<RpgItemEquippedEvent>(OnChanged);
            _bus.Unsubscribe<RpgPotionUsedEvent>(OnChanged);
        }

        /// <summary>把角色写进存档。返回 null = 成功，否则带原因（给无头检查断言用）。</summary>
        public string SaveNow()
        {
            List<JsonValue> slotRows = new List<JsonValue>(_player.Slots.Count);
            for (int i = 0; i < _player.Slots.Count; i++)
            {
                slotRows.Add(new JsonValue
                {
                    Kind = JsonKind.Object,
                    Members = new Dictionary<string, JsonValue>
                    {
                        { "id", new JsonValue { Kind = JsonKind.String, StringValue = _player.Slots[i].DefId } },
                        { "count", new JsonValue { Kind = JsonKind.Number, NumberValue = _player.Slots[i].Count } },
                    },
                });
            }

            JsonValue payload = new JsonValue
            {
                Kind = JsonKind.Object,
                Members = new Dictionary<string, JsonValue>
                {
                    { "level", new JsonValue { Kind = JsonKind.Number, NumberValue = _player.Level } },
                    { "exp", new JsonValue { Kind = JsonKind.Number, NumberValue = _player.Exp } },
                    { "gold", new JsonValue { Kind = JsonKind.Number, NumberValue = _player.Gold } },
                    { "hp", new JsonValue { Kind = JsonKind.Number, NumberValue = _player.Hp } },
                    { "mp", new JsonValue { Kind = JsonKind.Number, NumberValue = _player.Mp } },
                    { "weapon", new JsonValue { Kind = JsonKind.String, StringValue = _player.WeaponId ?? "" } },
                    { "armor", new JsonValue { Kind = JsonKind.String, StringValue = _player.ArmorId ?? "" } },
                    { "slots", new JsonValue { Kind = JsonKind.Array, Items = slotRows } },
                },
            };

            return _saves.Save(Slot, payload);
        }

        private bool TryLoad()
        {
            JsonValue payload;
            int version;
            string error;
            if (!_saves.TryLoad(Slot, out payload, out version, out error))
            {
                return false;
            }

            _player.Level = System.Math.Max(1, payload.Path("level").AsInt(1));
            _player.Exp = payload.Path("exp").AsInt(0);
            _player.Gold = payload.Path("gold").AsInt(0);
            _player.Hp = payload.Path("hp").AsInt(_player.MaxHp);
            _player.WeaponId = NullToEmpty(payload.Path("weapon").AsString(null));
            if (_player.WeaponId.Length == 0)
            {
                _player.WeaponId = null;
            }

            _player.ArmorId = NullToEmpty(payload.Path("armor").AsString(null));
            if (_player.ArmorId.Length == 0)
            {
                _player.ArmorId = null;
            }

            _player.Slots.Clear();
            JsonValue slots = payload.Path("slots");
            if (slots.Kind == JsonKind.Array)
            {
                for (int i = 0; i < slots.Items.Count; i++)
                {
                    string id = slots.Items[i].Path("id").AsString(null);
                    int count = slots.Items[i].Path("count").AsInt(0);
                    if (id != null && count > 0)
                    {
                        _player.Slots.Add(new RpgSlot { DefId = id, Count = count });
                    }
                }
            }

            // 属性表从存档字段重建（等级基数 + 装备修饰符），血量夹取才有正确的上限
            _player.RefreshStatsAfterLoad();
            // 血量夹进当前等级的合法区间（老档升过曲线也不至于出现血量爆表）。
            if (_player.Hp > _player.MaxHp)
            {
                _player.Hp = _player.MaxHp;
            }

            if (_player.Hp <= 0)
            {
                _player.Hp = _player.MaxHp;
            }

            _player.Mp = System.Math.Max(0f, System.Math.Min(_player.MaxMp, (float)payload.Path("mp").AsDouble(_player.MaxMp)));
            return true;
        }

        private static string NullToEmpty(string text)
        {
            return text ?? "";
        }

        private void OnChanged(RpgMonsterDiedEvent evt)
        {
            SaveQuietly();
        }

        private void OnChanged(RpgPlayerLeveledUpEvent evt)
        {
            SaveQuietly();
        }

        private void OnChanged(RpgItemGainedEvent evt)
        {
            SaveQuietly();
        }

        private void OnChanged(RpgItemEquippedEvent evt)
        {
            SaveQuietly();
        }

        private void OnChanged(RpgPotionUsedEvent evt)
        {
            SaveQuietly();
        }

        private void SaveQuietly()
        {
            string error = SaveNow();
            if (error != null)
            {
                Log("[RPG] 存档没写上（失败也是值）: " + error);
            }
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                _log(message);
            }
        }
    }

    /// <summary>
    /// 规则接线：玩家倒下 → 流程机切到 Dead。竞技场不知道流程机存在，
    /// 流程机不知道竞技场存在，翻译住在这个模块里 —— 和贪吃蛇的 Rules 同一分工。
    /// </summary>
    public sealed class RpgRulesModule : IModule
    {
        private readonly EventBus _bus;
        private readonly ProcedureFlow _flow;

        public RpgRulesModule(EventBus bus, ProcedureFlow flow)
        {
            _bus = bus;
            _flow = flow;
        }

        public string Name { get { return "RpgRules"; } }
        public int InitOrder { get { return 35; } }

        public void Init(ModuleCenter host)
        {
            _bus.Subscribe<RpgPlayerDiedEvent>(OnPlayerDied);
        }

        public void Pump()
        {
        }

        public void Shutdown()
        {
            _bus.Unsubscribe<RpgPlayerDiedEvent>(OnPlayerDied);
        }

        private void OnPlayerDied(RpgPlayerDiedEvent evt)
        {
            _flow.ChangeProcedure("Dead");
        }
    }

    /// <summary>菜单态：等"开始冒险"。</summary>
    public sealed class RpgMenuProcedure : Procedure
    {
        public override string Name { get { return "Menu"; } }
    }

    /// <summary>冒险中：竞技场按真实时间逐帧推进。</summary>
    public sealed class RpgPlayingProcedure : Procedure
    {
        public override string Name { get { return "Playing"; } }
    }

    /// <summary>倒下：角色成长还在，等"再战"。</summary>
    public sealed class RpgDeadProcedure : Procedure
    {
        public override string Name { get { return "Dead"; } }
    }
}
