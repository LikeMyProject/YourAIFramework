using System.Collections.Generic;
using YourFramework.Core;
using YourFramework.Event;
using YourFramework.Skills;
using YourFramework.Stats;

namespace YourAIFramework.RpgGame
{
    /// <summary>
    /// 技能模块：把框架的技能系统（Foundation/Skills）接进这个 RPG。
    ///
    /// **配置驱动**的教学点在这里落地：三个技能（火球术 / 旋风斩 / 治疗术）的全部数值
    /// 都写在下面的 JSON 表里 —— 代码里没有出现任何一个技能名、伤害数字和消耗量。
    /// 加第四个技能 = 在 JSON 里添一段，代码一行不动；数值要调，改表重开就生效。
    ///
    /// 接线的三个口（技能内核不认识属性表和伤害管道，全靠注入）：
    /// - <see cref="ISkillStatSource"/>：读蓝、扣蓝 —— "mp" 的真身是 <see cref="RpgPlayer.Mp"/>；
    /// - <see cref="ISkillEffectSink"/>：效果落地 —— 伤害落到竞技场的怪身上，治疗落到勇者身上；
    /// - <see cref="SkillBook.Pump"/>：吟唱与冷却的逐帧推进（挂在 ModuleCenter 的 Pump 上）。
    ///
    /// "失败也是值"的教学点：按键、冷却中、蓝不够、射程没怪 —— 全是
    /// <see cref="SkillCastOutcome"/> 带原因的失败值，不抛异常；只有接线错误才抛。
    /// </summary>
    public sealed class RpgSkillModule : IModule, ISkillStatSource, ISkillEffectSink
    {
        /// <summary>
        /// 技能表（JSON）。字段与技能系统对齐：id / name / targeting / range / cooldown /
        /// costs / effects（kind: damage / heal，stat: 用施法者哪个属性当基数）。
        /// </summary>
        public const string TableJson = @"
        { ""skills"": [
          {
            ""id"": ""fireball"", ""name"": ""火球术"",
            ""targeting"": ""self"", ""range"": 6, ""cooldown"": 2,
            ""delivery"": ""cast"", ""cast_time"": 0.15,
            ""costs"": [ { ""resource"": ""mp"", ""amount"": 8 } ],
            ""effects"": [ { ""kind"": ""damage"", ""stat"": ""attack"", ""multiplier"": 1.6, ""flat_bonus"": 5 } ]
          },
          {
            ""id"": ""whirlwind"", ""name"": ""旋风斩"",
            ""targeting"": ""all_enemies"", ""range"": 2.6, ""cooldown"": 5,
            ""delivery"": ""cast"", ""cast_time"": 0.4,
            ""costs"": [ { ""resource"": ""mp"", ""amount"": 12 } ],
            ""effects"": [ { ""kind"": ""damage"", ""stat"": ""attack"", ""multiplier"": 1.0, ""flat_bonus"": 3 } ]
          },
          {
            ""id"": ""mend"", ""name"": ""治疗术"",
            ""targeting"": ""self"", ""delivery"": ""cast"", ""cast_time"": 1.2, ""cooldown"": 8,
            ""costs"": [ { ""resource"": ""mp"", ""amount"": 15 } ],
            ""effects"": [ { ""kind"": ""heal"", ""stat"": ""maxhp"", ""multiplier"": 0.35, ""flat_bonus"": 0 } ]
          }
        ]
        }";

        private readonly EventBus _bus;
        private readonly RpgPlayer _player;
        private readonly RpgArena _arena;
        private readonly System.Action<string> _log;

        private SkillLibrary _library;
        private SkillBook _book;

        // 施法是低频操作，这几个列表按次复用，不在帧路径上。
        private readonly DeterministicRandom _random = new DeterministicRandom(20260921UL, "rpg/skill");
        private readonly List<SkillTargetCandidate> _candidates = new List<SkillTargetCandidate>(16);
        private readonly List<SkillTargetCandidate> _targets = new List<SkillTargetCandidate>(16);
        private readonly List<SkillCastEvent> _events = new List<SkillCastEvent>(16);

        public RpgSkillModule(EventBus bus, RpgPlayer player, RpgArena arena, System.Action<string> log = null)
        {
            _bus = bus;
            _player = player;
            _arena = arena;
            _log = log;
        }

        public string Name { get { return "RpgSkills"; } }
        public int InitOrder { get { return 28; } }

        public void Init(ModuleCenter host)
        {
            _library = SkillLibrary.FromJson(TableJson);
            _book = new SkillBook("hero", _library, this, this, null);
            List<string> ids = _library.Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                _book.Learn(ids[i]);
            }

            Log("[RPG] 技能上场：" + string.Join("、", ids.ToArray()) + "（按键 1/2/3，配置驱动，改表不改码）");
        }

        public void Pump()
        {
            // 吟唱与冷却的推进走 Advance(delta)：和棋盘/竞技场一样，时间在宿主手里。
        }

        public void Shutdown()
        {
        }

        /// <summary>逐帧推进吟唱与冷却。宿主每帧喂 Time.deltaTime。</summary>
        public void Advance(float delta)
        {
            if (_player.Dead || delta <= 0f)
            {
                return;
            }

            _book.Pump(delta, _events);
            for (int i = 0; i < _events.Count; i++)
            {
                if (_events[i].Kind == SkillCastEventKind.CastCompleted)
                {
                    _bus.Publish(new RpgSkillCastEvent
                    {
                        SkillId = _events[i].SkillId,
                        SkillName = _library.Get(_events[i].SkillId).Name,
                        Detail = "吟唱完成",
                        Completed = true,   // 事后通知只进战报；不标会被视图当起手，再放一遍特效
                    });
                }
            }

            _events.Clear();
        }

        /// <summary>会几个技能（UI 摆技能栏用）。</summary>
        public int Count
        {
            get { return _book == null ? 0 : _book.Count; }
        }

        /// <summary>第 index 个技能的 id（学习顺序 = 摆栏顺序 = 按键顺序）。</summary>
        public string IdAt(int index)
        {
            List<string> ids = _book.Known();
            return ids[index];
        }

        /// <summary>技能定义（UI 显示名称与消耗用）。</summary>
        public SkillDef Def(string skillId)
        {
            return _library.Get(skillId);
        }

        /// <summary>槽状态（UI 冷却遮罩用）。没学过返回 null。</summary>
        public SkillSlot Slot(string skillId)
        {
            return _book.Slot(skillId);
        }

        /// <summary>
        /// 施放一个技能。目标规则：单体自动锁最近的怪（射程内），全体就是射程内所有怪，
        /// 自身类不需要目标。返回失败带原因，调用方（宿主）转成界面提示。
        /// </summary>
        public SkillCastOutcome TryCast(string skillId)
        {
            if (_player.Dead)
            {
                return SkillCastOutcome.Fail("人都倒了");
            }

            SkillDef def = _library.Get(skillId);
            _candidates.Clear();
            _arena.CollectSkillCandidates(_candidates, def.Range);

            string requested = null;
            int lockedTarget = -1;
            if (def.Targeting == SkillTargeting.SingleTarget)
            {
                int monsterId;
                if (!_arena.TryNearestMonsterId(out monsterId))
                {
                    return SkillCastOutcome.Fail("附近没有目标");
                }

                requested = "m" + monsterId;
                lockedTarget = monsterId;
            }

            SkillCastOutcome outcome = _book.TryBegin(skillId, _candidates, requested, _targets, _events);
            PublishSkillEvents(skillId, lockedTarget);
            _events.Clear();
            return outcome;
        }

        private void PublishSkillEvents(string skillId, int lockedTarget)
        {
            SkillDef def = _library.Get(skillId);
            for (int i = 0; i < _events.Count; i++)
            {
                SkillCastEvent raw = _events[i];
                if (raw.Kind == SkillCastEventKind.CastRejected)
                {
                    _bus.Publish(new RpgSkillCastEvent
                    {
                        SkillId = skillId,
                        SkillName = def.Name,
                        Detail = "放不出来：" + raw.Error,
                        Failed = true,
                    });
                    continue;
                }
                if (raw.Kind == SkillCastEventKind.CastStarted)
                {
                    // 起手就发：TargetInstanceId/CastTime 供视图挂特效（火球弹道 = 读条时长）。
                    _bus.Publish(new RpgSkillCastEvent
                    {
                        SkillId = skillId,
                        SkillName = def.Name,
                        Detail = def.CastTime > 0f
                            ? "开始吟唱 " + def.CastTime.ToString("0.0") + " 秒"
                            : "出手",
                        TargetInstanceId = lockedTarget,
                        CastTime = def.CastTime,
                    });
                    continue;
                }
                if (raw.Kind == SkillCastEventKind.CastCompleted)
                {
                    // 效果此刻已生效（瞬发=起手即完成；读条=吟唱结束）。
                    _bus.Publish(new RpgSkillCastEvent
                    {
                        SkillId = skillId,
                        SkillName = def.Name,
                        Detail = "生效",
                        TargetInstanceId = lockedTarget,
                        Completed = true,
                    });
                }
            }
        }

        /// <summary>
        /// 火球命中结算：火球是直飞弹，伤害不由吟唱完成自动落地，而是弹体撞到怪时
        /// 由视图回调这里。伤害公式照读表里的配置（与旋风斩同一条管道），打空了
        /// 没人调 —— 没命中就没伤害，弹体 10 秒后自毁。
        /// </summary>
        public SkillEffectResult ApplyFireballHit(string targetId)
        {
            SkillDef def = _library.Get("fireball");
            for (int i = 0; i < def.Effects.Count; i++)
            {
                if (def.Effects[i].Kind == SkillEffectKind.Damage)
                {
                    return ApplyDamage(def.Effects[i], targetId);
                }
            }

            return SkillEffectResult.Fail("火球表里没有伤害配置");
        }

        // ------------------------------------------------------------------ 资源口

        bool ISkillStatSource.TryValue(string stat, out float value)
        {
            if (stat == "mp")
            {
                value = _player.Mp;
                return true;
            }

            value = 0f;     // cast_speed / cooldown_rate 不设：走内核默认 1（教学点：缺省是合法的）
            return false;
        }

        bool ISkillStatSource.TrySpend(string stat, float amount, out string error)
        {
            if (stat != "mp")
            {
                error = "这个施法者没有资源 '" + stat + "'";
                return false;
            }

            return _player.TrySpendMp(amount, out error);
        }

        // ------------------------------------------------------------------ 效果口

        SkillEffectResult ISkillEffectSink.Apply(SkillEffect effect, string casterId, string targetId)
        {
            switch (effect.Kind)
            {
                case SkillEffectKind.Damage:
                    return ApplyDamage(effect, targetId);
                case SkillEffectKind.Heal:
                    return ApplyHeal(effect);
                default:
                    return SkillEffectResult.Fail("这个演示没有实现 '" + effect.Kind + "' 的落地");
            }
        }

        private SkillEffectResult ApplyDamage(SkillEffect effect, string targetId)
        {
            if (targetId == null || !targetId.StartsWith("m"))
            {
                return SkillEffectResult.Fail("目标已消失");
            }

            int instanceId;
            if (!int.TryParse(targetId.Substring(1), out instanceId))
            {
                return SkillEffectResult.Fail("目标 id 不合法 '" + targetId + "'");
            }

            // 倍率与固定加成是技能表的配置，公式（减伤/浮动/保底）在框架 DamagePipeline 里，
            // 随机用竞技场自己的分流 —— 数值配置与公式实现分家，改表不改码。
            DamageRequest request = DamageRequest.WithBonus(effect.Multiplier, effect.FlatBonus);
            int dealt;
            bool landed = _arena.DamageMonsterById(instanceId, request, out dealt);
            return landed
                ? SkillEffectResult.Ok(dealt)
                : SkillEffectResult.Fail("目标已消失");
        }

        private SkillEffectResult ApplyHeal(SkillEffect effect)
        {
            int baseValue = effect.Stat == "maxhp" ? _player.MaxHp : _player.Attack;
            int amount = (int)System.Math.Round(baseValue * effect.Multiplier + effect.FlatBonus);
            int healed = _player.Heal(amount);
            _bus.Publish(new RpgSkillCastEvent
            {
                SkillId = "mend",
                SkillName = "治疗术",
                Detail = "回血 " + healed,
                Completed = true,           // 起手时绿光已排上日程，这里只进战报
            });
            return SkillEffectResult.Ok(healed);
        }

        private void Log(string message)
        {
            if (_log != null)
            {
                _log(message);
            }
        }

    }

    /// <summary>技能事件：起手/命中/放不出来的总账，UI 战报与特效都订阅它。</summary>
    public struct RpgSkillCastEvent
    {
        public string SkillId;
        public string SkillName;
        public string Detail;
        /// <summary>true = 这次是"放不出来"（带原因），战报里灰一下。</summary>
        public bool Failed;
        /// <summary>单体技能锁定的怪（-1 = 无：群攻/自疗）。视图拿它定位火球落点。</summary>
        public int TargetInstanceId;
        /// <summary>读条时长（秒，0 = 瞬发）。视图在起手事件挂特效。</summary>
        public float CastTime;
        /// <summary>true = 效果已生效的总账（特效在起手放过，别再放一遍）。</summary>
        public bool Completed;
    }
}
