using System.Collections.Generic;
using YourFramework.Data;
using YourFramework.Stats;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// RPG 玩法核心：角色属性、经验升级、背包、装备、伤害与掉落。
    /// 这一层**不引用 UnityEngine**，可以在无头环境逐项检查 —— 和贪吃蛇的 SnakeBoard 同一条规矩。
    ///
    /// "失败也是值"在这里的形态：背包满、买不起、没这件装备、没这种药水，
    /// 一律返回 false 或带原因的值，不抛异常 —— 玩家点错按钮不该变成崩溃。
    /// 自己人把数据改坏了（注册表里查无此物、数量为负）才抛。
    /// </summary>
    public enum RpgItemKind
    {
        Potion,
        Weapon,
        Armor,
    }

    /// <summary>物品定义（静态表里的一条）：一件东西"是什么"。</summary>
    public struct RpgItemDef
    {
        public string Id;
        public string Name;
        public RpgItemKind Kind;
        /// <summary>药水=回血量；武器=攻击加成；护甲=防御加成。</summary>
        public int Power;
        public int Price;
    }

    /// <summary>背包的一个格子：某件东西攒了几件（药水可堆叠，装备一件占一格）。</summary>
    public struct RpgSlot
    {
        public string DefId;
        public int Count;
    }

    /// <summary>怪物定义（静态表里的一条）。</summary>
    public struct RpgMonsterDef
    {
        public string Id;
        public string Name;
        public int MaxHp;
        public int Attack;
        public int Defense;
        public int ExpReward;
        public int GoldMin;
        public int GoldMax;
        /// <summary>掉落物品（null = 不掉东西）。</summary>
        public string DropId;
        /// <summary>掉落概率 0~1。</summary>
        public float DropChance;
        /// <summary>移动速度倍率（毒蛛快、石魔慢；1 = 标准脚程）。</summary>
        public float SpeedScale;
    }

    /// <summary>
    /// 一只活着的怪：定义 + 本场状态。位置与追击在竞技场层（<see cref="RpgArena"/>）管。
    /// </summary>
    public sealed class RpgMonster
    {
        public int InstanceId;
        public RpgMonsterDef Def;
        public int Hp;
    }

    /// <summary>物品静态表。demo 刻意用代码表而不是配置文件 —— 换成 DataTable 模块就是现成的进阶练习。</summary>
    /// <summary>场景名常量：Build Settings 里四个场景要一字不差（见 README 的接入步骤）。</summary>
    public static class RpgScenes
    {
        public const string Menu = "RpgMenu";
        public const string Town = "RpgTown";
        public const string Wilds = "RpgWilds";
        public const string Dungeon = "RpgDungeon";
    }

    /// <summary>
    /// 跨场景落点账本：场景一换，宿主连同一切状态被销毁，落点必须活得比场景久，
    /// 所以是静态（单机 demo 里没有第二个写手，并发与多开不进这道题）。
    /// 出发前写这里，城镇宿主 Awake 时读；读完立刻复位，防隔次误落。
    /// </summary>
    public static class RpgSpawnBook
    {
        /// <summary>下次进城镇的落点：plaza=广场（从主菜单进）；well=泉水复活点（野外/副本回来）。</summary>
        public static string NextTownSpawn = "plaza";

        /// <summary>标记"下次回城落复活点"—— 野外传送石、副本通关/阵亡回城都走这里。</summary>
        public static void BindTownWell()
        {
            NextTownSpawn = "well";
        }
    }

    /// <summary>
    /// 区域预设：一个战斗场景一套刷怪账。场景加载进来时按场景名领，
    /// 竞技场照着预设刷怪 —— 加新地图 = 加一条预设 + 一个场景，玩法代码不动。
    /// </summary>
    public struct RpgZonePreset
    {
        public string SceneName;
        public string Title;
        /// <summary>副本规则：Boss 同场只留一只，其余名额刷小怪。</summary>
        public bool BossOnly;
        /// <summary>区域 Boss 的怪 id：副本=守关 Boss（在场只留一只）；野外=营地 Boss（死后 30 秒复活）。null = 没有区域 Boss。</summary>
        public string BossId;
    }

    public static class RpgZones
    {
        public static readonly RpgZonePreset Wilds = new RpgZonePreset { SceneName = RpgScenes.Wilds, Title = "野外", BossOnly = false, BossId = "treant-lord" };
        public static readonly RpgZonePreset Dungeon = new RpgZonePreset { SceneName = RpgScenes.Dungeon, Title = "副本·岩石魔王", BossOnly = true, BossId = "golem-king" };

        public static bool TryFind(string sceneName, out RpgZonePreset preset)
        {
            if (sceneName == Wilds.SceneName) { preset = Wilds; return true; }
            if (sceneName == Dungeon.SceneName) { preset = Dungeon; return true; }
            preset = default(RpgZonePreset);
            return false;
        }
    }

    /// <summary>
    /// 物品静态表：**数据行走框架 DataTable**（JSON 配表 → 键行存取），
    /// 这一层只做"表行 → 强类型定义"的映射和缓存。加物品 = 表里添一行，代码不动。
    /// </summary>
    public static class RpgItems
    {
        public const string TableJson = @"
        [ { ""id"": ""potion-small"", ""name"": ""小红药"", ""kind"": ""potion"", ""power"": 30, ""price"": 15 },
          { ""id"": ""sword-iron"", ""name"": ""铁剑"", ""kind"": ""weapon"", ""power"": 5, ""price"": 50 },
          { ""id"": ""sword-steel"", ""name"": ""钢剑"", ""kind"": ""weapon"", ""power"": 10, ""price"": 120 },
          { ""id"": ""armor-leather"", ""name"": ""皮甲"", ""kind"": ""armor"", ""power"": 3, ""price"": 60 },
          { ""id"": ""armor-iron"", ""name"": ""铁甲"", ""kind"": ""armor"", ""power"": 7, ""price"": 140 } ]";

        private static readonly DataTable _table = DataTable.FromJson(TableJson, "id");
        private static readonly Dictionary<string, RpgItemDef> _cache = new Dictionary<string, RpgItemDef>(8);

        /// <summary>全表（掉落/商店都从这里查）。</summary>
        public static List<RpgItemDef> All
        {
            get
            {
                List<string> keys = _table.Keys;
                List<RpgItemDef> defs = new List<RpgItemDef>(keys.Count);
                for (int i = 0; i < keys.Count; i++)
                {
                    defs.Add(Find(keys[i]));
                }

                return defs;
            }
        }

        /// <summary>按 Id 查定义。查无此物是数据错误，抛出来别静默。</summary>
        public static RpgItemDef Find(string id)
        {
            RpgItemDef def;
            if (_cache.TryGetValue(id, out def))
            {
                return def;
            }

            if (!_table.Has(id))
            {
                throw new KeyNotFoundException("RpgItems 里没有 '" + id + "'");
            }

            string kind = _table.GetString(id, "kind", "potion");
            def = new RpgItemDef
            {
                Id = id,
                Name = _table.GetString(id, "name", id),
                Kind = kind == "weapon" ? RpgItemKind.Weapon : kind == "armor" ? RpgItemKind.Armor : RpgItemKind.Potion,
                Power = _table.GetInt(id, "power", 0),
                Price = _table.GetInt(id, "price", 0),
            };
            _cache[id] = def;
            return def;
        }
    }

    /// <summary>怪物静态表。</summary>
    public static class RpgMonsters
    {
        public static readonly RpgMonsterDef Slime = new RpgMonsterDef
        {
            Id = "slime", Name = "史莱姆", MaxHp = 20, Attack = 4, Defense = 0,
            ExpReward = 12, GoldMin = 3, GoldMax = 8, DropId = "potion-small", DropChance = 0.30f,
            SpeedScale = 1f,
        };

        public static readonly RpgMonsterDef Wolf = new RpgMonsterDef
        {
            Id = "wolf", Name = "野狼", MaxHp = 38, Attack = 8, Defense = 1,
            ExpReward = 22, GoldMin = 6, GoldMax = 15, DropId = "sword-iron", DropChance = 0.15f,
            SpeedScale = 1.15f,
        };

        public static readonly RpgMonsterDef Spider = new RpgMonsterDef
        {
            Id = "spider", Name = "毒蛛", MaxHp = 26, Attack = 6, Defense = 0,
            ExpReward = 18, GoldMin = 5, GoldMax = 12, DropId = "potion-small", DropChance = 0.25f,
            SpeedScale = 1.45f,             // 腿多跑得快，贴脸很烦
        };

        public static readonly RpgMonsterDef Boar = new RpgMonsterDef
        {
            Id = "boar", Name = "野猪", MaxHp = 55, Attack = 10, Defense = 2,
            ExpReward = 34, GoldMin = 10, GoldMax = 22, DropId = "potion-small", DropChance = 0.20f,
            SpeedScale = 0.9f,
        };

        public static readonly RpgMonsterDef Golem = new RpgMonsterDef
        {
            Id = "golem", Name = "石魔", MaxHp = 70, Attack = 13, Defense = 4,
            ExpReward = 40, GoldMin = 12, GoldMax = 28, DropId = "armor-iron", DropChance = 0.10f,
            SpeedScale = 0.8f,
        };

        public static readonly RpgMonsterDef[] All = new RpgMonsterDef[] { Slime, Wolf, Spider, Boar, Golem };

        /// <summary>副本守关 Boss：血厚攻高、必掉好货。同场只留一只。</summary>
        public static readonly RpgMonsterDef GolemKing = new RpgMonsterDef
        {
            Id = "golem-king", Name = "岩石魔王", MaxHp = 320, Attack = 16, Defense = 6,
            ExpReward = 120, GoldMin = 80, GoldMax = 150, DropId = "sword-steel", DropChance = 1f,
            SpeedScale = 0.7f,
        };

        /// <summary>野外 Boss：镇守营地，死了隔一阵复活（传统 RPG 的世界 Boss 节奏）。</summary>
        public static readonly RpgMonsterDef TreantLord = new RpgMonsterDef
        {
            Id = "treant-lord", Name = "树妖之王", MaxHp = 240, Attack = 15, Defense = 5,
            ExpReward = 110, GoldMin = 60, GoldMax = 110, DropId = "armor-iron", DropChance = 1f,
            SpeedScale = 0.7f,
        };

        public static RpgMonsterDef Find(string id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id == id)
                {
                    return All[i];
                }
            }

            // Boss 不进 All（区域规则单独管），但按 id 也得查得到
            if (id == GolemKing.Id)
            {
                return GolemKing;
            }

            if (id == TreantLord.Id)
            {
                return TreantLord;
            }

            throw new System.Collections.Generic.KeyNotFoundException("RpgMonsters 里没有 '" + id + "'");
        }

        private static readonly Dictionary<string, StatSheet> _sheets = new Dictionary<string, StatSheet>(8);

        /// <summary>
        /// 同类怪共享一张属性表（怪不改自己的属性，只在外头扣血），刷怪零分配。
        /// 伤害公式（框架 DamagePipeline）从这张表读攻防。
        /// </summary>
        public static StatSheet SheetOf(RpgMonsterDef def)
        {
            StatSheet sheet;
            if (!_sheets.TryGetValue(def.Id, out sheet))
            {
                sheet = new StatSheet();
                sheet.Declare("attack", def.Attack);
                sheet.Declare("defense", def.Defense);
                sheet.Declare("maxhp", def.MaxHp);
                _sheets[def.Id] = sheet;
            }

            return sheet;
        }
    }

    /// <summary>
    /// 角色：等级、经验、金币、血量、背包、穿戴。属性全是推导值 ——
    /// 攻击 = 基础（随等级长）+ 武器加成，防御 = 护甲加成，血上限 = 基础 + 等级成长。
    /// （进阶方向：换 Foundation 的数值系统模块，装备加成走 StatModifier。）
    /// </summary>
    public sealed class RpgPlayer
    {
        /// <summary>背包格数上限。满了再捡东西会被拒绝（返回 false），这是规则不是错误。</summary>
        public const int SlotMax = 12;
        /// <summary>药水一格最多堆几个。</summary>
        public const int PotionStackMax = 9;

        public int Level;
        public int Exp;
        public int Gold;
        public int Hp;
        /// <summary>法力（技能消耗的资源）。技能系统把资源名当属性名读，这里就是 'mp' 的真身。</summary>
        public float Mp;
        public string WeaponId;
        public string ArmorId;
        public readonly List<RpgSlot> Slots = new List<RpgSlot>(SlotMax);

        /// <summary>
        /// 角色属性表：**属性数值走框架 StatSheet** —— 等级给基数，装备给修饰符
        /// （StatModifier 带 source，换装时按来源批量清了重加），攻击/防御/血上限
        /// 全部从表里读。改属性公式不再碰这份代码，加 buff 就是往表里塞修饰符。
        /// </summary>
        public readonly StatSheet Stats = new StatSheet();
        private bool _statsDeclared;

        public RpgPlayer()
        {
            Level = 1;
            RefreshLevelStats();
            Hp = MaxHp;
            Mp = MaxMp;
        }

        /// <summary>等级基数写进属性表（首次 Declare，之后 SetBase）。</summary>
        private void RefreshLevelStats()
        {
            if (!_statsDeclared)
            {
                Stats.Declare("maxhp", 100 + (Level - 1) * 20);
                Stats.Declare("attack", 10 + (Level - 1) * 2);
                Stats.Declare("defense", 0f);
                Stats.Declare("maxmp", 30 + (Level - 1) * 5);
                _statsDeclared = true;
                return;
            }

            Stats.SetBase("maxhp", 100 + (Level - 1) * 20);
            Stats.SetBase("attack", 10 + (Level - 1) * 2);
            Stats.SetBase("maxmp", 30 + (Level - 1) * 5);
        }

        /// <summary>装备修饰符：按 "equip" 来源清了重加（换装/读档后调）。</summary>
        public void RebuildEquipModifiers()
        {
            Stats.RemoveBySource("equip");
            if (WeaponId != null)
            {
                Stats.AddModifier(new StatModifier("attack", StatKind.Flat, RpgItems.Find(WeaponId).Power, "equip"));
            }

            if (ArmorId != null)
            {
                Stats.AddModifier(new StatModifier("defense", StatKind.Flat, RpgItems.Find(ArmorId).Power, "equip"));
            }
        }

        /// <summary>读档后调一次：等级基数 + 装备修饰符都从存档字段重建。</summary>
        public void RefreshStatsAfterLoad()
        {
            RefreshLevelStats();
            RebuildEquipModifiers();
        }

        // ------------------------------------------------------------ 推导属性（数值全部来自属性表）

        public int MaxHp
        {
            get { return (int)Stats.Value("maxhp"); }
        }

        /// <summary>法力上限。技能的消耗从这里出。</summary>
        public int MaxMp
        {
            get { return (int)Stats.Value("maxmp"); }
        }

        public int BaseAttack
        {
            get { return (int)Stats.BaseValue("attack"); }
        }

        public int Attack
        {
            get { return (int)Stats.Value("attack"); }
        }

        public int Defense
        {
            get { return (int)Stats.Value("defense"); }
        }

        public int ExpToNext
        {
            get { return 20 + (Level - 1) * 30; }
        }

        public bool Dead
        {
            get { return Hp <= 0; }
        }

        // ------------------------------------------------------------ 成长

        /// <summary>加经验。返回跨了几级（升级回满血，这是这口游戏的爽点）。</summary>
        public int AddExp(int amount)
        {
            if (amount <= 0)
            {
                throw new System.ArgumentException("经验必须为正数，收到 " + amount);
            }

            Exp += amount;
            int levels = 0;
            while (Exp >= ExpToNext)
            {
                Exp -= ExpToNext;
                Level++;
                levels++;
            }

            if (levels > 0)
            {
                RefreshLevelStats();    // 等级基数进属性表，血蓝上限跟着涨
                Hp = MaxHp;             // 升级回满血
                Mp = MaxMp;             // 顺手回满蓝
            }

            return levels;
        }

        public void AddGold(int amount)
        {
            if (amount < 0)
            {
                throw new System.ArgumentException("金币增量不能为负，收到 " + amount);
            }

            Gold += amount;
        }

        public void RestoreFull()
        {
            Hp = MaxHp;
            Mp = MaxMp;
        }

        /// <summary>治疗：返回实际回复量（溢出不算），给技能效果落地用。</summary>
        public int Heal(int amount)
        {
            if (amount <= 0)
            {
                return 0;
            }

            int before = Hp;
            Hp = System.Math.Min(MaxHp, Hp + amount);
            return Hp - before;
        }

        /// <summary>自然回蓝（竞技场每帧喂）。返回是否真的涨了。</summary>
        public bool RestoreMp(float amount)
        {
            if (Mp >= MaxMp || amount <= 0f)
            {
                return false;
            }

            Mp = System.Math.Min(MaxMp, Mp + amount);
            return true;
        }

        /// <summary>扣蓝（技能系统的资源出口）。扣不动返回 false 带原因。</summary>
        public bool TrySpendMp(float amount, out string error)
        {
            if (amount <= 0f)
            {
                error = null;
                return true;
            }

            if (Mp < amount)
            {
                error = "法力不足（需要 " + amount.ToString("0.#") + "，现有 " + Mp.ToString("0.#") + "）";
                return false;
            }

            Mp -= amount;
            error = null;
            return true;
        }

        /// <summary>受伤，返回是否因此倒下。外部世界造成的失败走返回值，不抛。</summary>
        public bool TakeDamage(int amount)
        {
            if (amount <= 0)
            {
                return false;
            }

            Hp -= amount;
            if (Hp < 0)
            {
                Hp = 0;
            }

            return Hp == 0;
        }

        // ------------------------------------------------------------ 背包

        /// <summary>
        /// 捡东西。药水先找没满的堆叠格；装备（和堆满的新药水）占新格。
        /// 返回 false = 背包满了，东西留在地上，游戏继续。
        /// </summary>
        public bool TryAddItem(string defId, int count)
        {
            RpgItemDef def = RpgItems.Find(defId);
            int stackMax = def.Kind == RpgItemKind.Potion ? PotionStackMax : 1;

            int remaining = count;
            if (stackMax > 1)
            {
                for (int i = 0; i < Slots.Count && remaining > 0; i++)
                {
                    if (Slots[i].DefId == defId && Slots[i].Count < stackMax)
                    {
                        int take = System.Math.Min(stackMax - Slots[i].Count, remaining);
                        Slots[i] = new RpgSlot { DefId = defId, Count = Slots[i].Count + take };
                        remaining -= take;
                    }
                }
            }

            while (remaining > 0)
            {
                if (Slots.Count >= SlotMax)
                {
                    // 半路塞进来的已经生效（堆叠部分），只剩开新格塞不下的原样退回。
                    return false;
                }

                int take = System.Math.Min(stackMax, remaining);
                Slots.Add(new RpgSlot { DefId = defId, Count = take });
                remaining -= take;
            }

            return true;
        }

        /// <summary>扣东西（用药/掉落结算用）。数量不够返回 false 且不动背包。</summary>
        public bool TryRemoveItem(string defId, int count)
        {
            if (CountItem(defId) < count)
            {
                return false;
            }

            int remaining = count;
            for (int i = Slots.Count - 1; i >= 0 && remaining > 0; i--)
            {
                if (Slots[i].DefId != defId)
                {
                    continue;
                }

                int take = System.Math.Min(Slots[i].Count, remaining);
                if (take >= Slots[i].Count)
                {
                    Slots.RemoveAt(i);
                }
                else
                {
                    Slots[i] = new RpgSlot { DefId = defId, Count = Slots[i].Count - take };
                }

                remaining -= take;
            }

            return true;
        }

        public int CountItem(string defId)
        {
            int total = 0;
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].DefId == defId)
                {
                    total += Slots[i].Count;
                }
            }

            return total;
        }

        // ------------------------------------------------------------ 装备与用药

        /// <summary>
        /// 穿一件背包里的装备。换下来的旧装备回到背包（背包必须腾得出那一格，
        /// 所以先扣新的再塞旧的，顺序对了就不会既丢新又丢旧）。
        /// </summary>
        public bool TryEquip(string defId, out string replacedId)
        {
            replacedId = null;
            RpgItemDef def;
            try
            {
                def = RpgItems.Find(defId);
            }
            catch (KeyNotFoundException)
            {
                return false;           // 查无此物：界面传错了，安静拒绝
            }

            if (def.Kind == RpgItemKind.Potion)
            {
                return false;
            }

            if (CountItem(defId) < 1)
            {
                return false;           // 背包里没这件
            }

            TryRemoveItem(defId, 1);
            string slotId = def.Kind == RpgItemKind.Weapon ? WeaponId : ArmorId;
            if (slotId != null)
            {
                replacedId = slotId;
                TryAddItem(slotId, 1);  // 换装前刚腾了一格，这里必然成功
            }

            if (def.Kind == RpgItemKind.Weapon)
            {
                WeaponId = defId;
            }
            else
            {
                ArmorId = defId;
            }

            RebuildEquipModifiers();    // 属性表里的装备加成跟着换
            return true;
        }

        /// <summary>喝药。返回实际回复量；没药返回 -1（界面提示"没有药水"）。</summary>
        public int UsePotion(string defId)
        {
            RpgItemDef def = RpgItems.Find(defId);
            if (def.Kind != RpgItemKind.Potion || CountItem(defId) < 1)
            {
                return -1;
            }

            TryRemoveItem(defId, 1);
            int before = Hp;
            Hp = System.Math.Min(MaxHp, Hp + def.Power);
            return Hp - before;
        }
    }

    /// <summary>
    /// 战斗数值：**伤害公式本体在框架 <see cref="DamagePipeline"/> 里**，
    /// 这里只放这套游戏自己的曲线配置 —— K=30（30 点防御减伤一半的比例曲线，
    /// 不用"攻-防"的减法，高等级不会把伤害压成 0）、±10% 浮动、保底 1 点。
    /// </summary>
    public static class RpgCombat
    {
        public static readonly DamageProfile DamageConfig = new DamageProfile
        {
            AttackStat = "attack",
            DefenseStat = "defense",
            MitigationConstant = 30f,
            Variance = 0.1f,
            MinimumDamage = 1f,
        };

        /// <summary>击杀奖励：金币在怪的定义区间里摇。</summary>
        public static int RollGold(IRandomSource random, RpgMonsterDef def)
        {
            return random.Range(def.GoldMin, def.GoldMax + 1);
        }
    }

    // ------------------------------------------------------------------ 事件（struct 零装箱）

    public struct RpgRunStartedEvent
    {
    }

    public struct RpgMonsterSpawnedEvent
    {
        public int InstanceId;
        public string Name;
        /// <summary>怪物定义 Id（视图按它选造型工厂）。</summary>
        public string DefId;
    }

    public struct RpgMonsterHurtEvent
    {
        public int InstanceId;
        public int Damage;
        public bool Died;
        public int HpLeft;
    }

    public struct RpgMonsterDiedEvent
    {
        public int InstanceId;
        public string Name;
        /// <summary>怪物定义 Id（宿主靠它认 Boss，比如 golem-king）。</summary>
        public string DefId;
        public int Exp;
        public int Gold;
        /// <summary>掉落物名（null = 什么都没掉）。</summary>
        public string DropName;
    }

    public struct RpgPlayerHurtEvent
    {
        public int Damage;
        public int Hp;
        public int MaxHp;
    }

    public struct RpgPlayerLeveledUpEvent
    {
        public int Level;
    }

    public struct RpgPlayerDiedEvent
    {
    }

    public struct RpgItemGainedEvent
    {
        public string DefId;
        public string Name;
        public int Count;
        /// <summary>true = 背包满了没捡走（日志提示"背包已满"）。</summary>
        public bool Rejected;
    }

    public struct RpgItemEquippedEvent
    {
        public string DefId;
        public string Name;
        public string ReplacedName;
    }

    public struct RpgPotionUsedEvent
    {
        public string Name;
        public int Healed;
        /// <summary>true = 没药可喝。</summary>
        public bool Failed;
    }

    /// <summary>属性面板整体刷新的总闸：升级、穿装、捡东西之后发一次，UI 重画。</summary>
    public struct RpgStatsChangedEvent
    {
    }
}
