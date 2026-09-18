using System;
using System.Collections.Generic;
using System.Globalization;
using YourFramework.Scripting;

namespace YourFramework.Skills
{
    /// <summary>
    /// 读属性与扣资源。技能流程要用到的运行参数（冷却恢复速率、施法速度、资源余量）
    /// 都从这里来 —— **内核不认识属性表**，所以这套技能流程可以在没有数值系统的环境里
    /// 跑（测试、工具、模拟）；要接上数值系统，用 SkillStatsBridge 里的实现即可。
    /// </summary>
    public interface ISkillStatSource
    {
        /// <summary>Reads a stat. A missing stat is false, and callers fall back to defaults.</summary>
        bool TryValue(string stat, out float value);

        /// <summary>
        /// Spends an amount of a resource. Called only after the caller has verified the
        /// balance, but it is still allowed to fail (another system may hold the real ledger) --
        /// failure comes back as a reason string, never as an exception.
        /// </summary>
        bool TrySpend(string stat, float amount, out string error);
    }

    /// <summary>一条效果落地的结果。失败是值：带原因回来，由调用方决定怎么显示。</summary>
    public struct SkillEffectResult
    {
        /// <summary>Whether the effect landed.</summary>
        public bool Succeeded;

        /// <summary>How much landed (damage dealt, health restored...). Zero when it failed.</summary>
        public float Amount;

        /// <summary>Why it did not land (null on success).</summary>
        public string Error;

        public static SkillEffectResult Ok(float amount)
        {
            return new SkillEffectResult { Succeeded = true, Amount = amount, Error = null };
        }

        public static SkillEffectResult Fail(string error)
        {
            return new SkillEffectResult { Succeeded = false, Amount = 0f, Error = error };
        }

        public override string ToString()
        {
            return Succeeded
                ? "ok " + Amount.ToString("0.###", CultureInfo.InvariantCulture)
                : "failed: " + Error;
        }
    }

    /// <summary>
    /// 把一条效果落到一个目标上。**这是技能内核唯一的效果出口**：技能模块因此不需要认识
    /// 伤害管道、效果容器或引擎里的任何东西；而"一条伤害效果具体怎么算"这种会随项目变的
    /// 策略，被收在一个实现里。
    /// </summary>
    public interface ISkillEffectSink
    {
        /// <summary>Applies one effect to one target. Never throws for content reasons.</summary>
        SkillEffectResult Apply(SkillEffect effect, string casterId, string targetId);
    }

    /// <summary>技能槽的三种状态。</summary>
    public enum SkillSlotState
    {
        /// <summary>可以施放。</summary>
        Ready,

        /// <summary>吟唱中。</summary>
        Casting,

        /// <summary>冷却 / 充能恢复中。</summary>
        Recharging
    }

    /// <summary>施法过程中报出来的事情。</summary>
    public enum SkillCastEventKind
    {
        /// <summary>起手（瞬发技能也会报，UI 才好统一处理）。</summary>
        CastStarted,

        /// <summary>效果结算完毕（吟唱技能是吟唱结束时）。</summary>
        CastCompleted,

        /// <summary>打不出来（带原因）。</summary>
        CastRejected,

        /// <summary>吟唱被打断。</summary>
        Interrupted,

        /// <summary>对某个目标落地成功。</summary>
        EffectApplied,

        /// <summary>对某个目标落地失败（带原因）。</summary>
        EffectFailed,

        /// <summary>充能回来了。</summary>
        ChargeRestored,

        /// <summary>热更钩子失败（**不截停施法**）。</summary>
        HookFailed
    }

    /// <summary>施法事件。装进调用方给的列表 —— 帧循环里不分配。</summary>
    public struct SkillCastEvent
    {
        public SkillCastEventKind Kind;
        public string SkillId;
        public string TargetId;
        public float Amount;
        public string Error;
        public int ChargesAvailable;
        public int Restored;

        public override string ToString()
        {
            string text = Kind + " " + SkillId;
            if (!string.IsNullOrEmpty(TargetId))
            {
                text += " -> " + TargetId;
            }

            if (Amount != 0f)
            {
                text += " (" + Amount.ToString("0.###", CultureInfo.InvariantCulture) + ")";
            }

            return Error == null ? text : text + " : " + Error;
        }
    }

    /// <summary>
    /// 一次施法尝试的结果。**运行期的"打不出来"一律是这个（带原因），不是异常** ——
    /// 按键、冷却中、蓝不够都是正常玩家行为，抛异常只会把"提示一下"变成"崩一下"。
    /// 与之相对，接线写错（null 列表、空 id）仍然是抛。
    /// </summary>
    public struct SkillCastOutcome
    {
        /// <summary>Whether the cast was accepted (instant skills have already landed by then).</summary>
        public bool Succeeded;

        /// <summary>Why it was rejected (null on success).</summary>
        public string Error;

        /// <summary>How many units were selected.</summary>
        public int TargetCount;

        /// <summary>Charges left after this cast.</summary>
        public int ChargesAvailable;

        /// <summary>Cast time left (&gt; 0 means the effect has not landed yet).</summary>
        public float CastTimeRemaining;

        /// <summary>Whether the skill is now casting (waiting for the cast bar to fill).</summary>
        public bool Casting;

        public static SkillCastOutcome Ok(int targetCount, int chargesAvailable, float castTimeRemaining)
        {
            return new SkillCastOutcome
            {
                Succeeded = true,
                Error = null,
                TargetCount = targetCount,
                ChargesAvailable = chargesAvailable,
                CastTimeRemaining = castTimeRemaining,
                Casting = castTimeRemaining > 0f
            };
        }

        public static SkillCastOutcome Fail(string error)
        {
            return new SkillCastOutcome
            {
                Succeeded = false,
                Error = error,
                TargetCount = 0,
                ChargesAvailable = 0,
                CastTimeRemaining = 0f,
                Casting = false
            };
        }

        public override string ToString()
        {
            return Succeeded
                ? "ok (" + TargetCount + " target(s)" + (Casting ? ", casting " + CastTimeRemaining.ToString("0.##", CultureInfo.InvariantCulture) + "s" : string.Empty) + ")"
                : "rejected: " + Error;
        }
    }

    /// <summary>
    /// 一个技能槽的运行时状态：冷却 + 吟唱。定义（<see cref="SkillDef"/>）是共享只读的，
    /// 状态是每个施法者一份 —— 这正是"配置驱动"该有的样子：改数值不动代码，
    /// 一万个怪共用一份定义。
    /// </summary>
    public sealed class SkillSlot
    {
        /// <summary>共享的只读定义。</summary>
        public readonly SkillDef Def;

        /// <summary>冷却与充能。</summary>
        public readonly SkillCooldown Cooldown;

        private readonly List<string> _pendingTargets = new List<string>();
        private bool _casting;
        private float _castRemaining;

        internal SkillSlot(SkillDef def)
        {
            Def = def;
            Cooldown = new SkillCooldown(def.Charges, def.RechargeTime);
        }

        /// <summary>Whether the cast bar is filling.</summary>
        public bool IsCasting { get { return _casting; } }

        /// <summary>Cast time left in seconds.</summary>
        public float CastRemaining { get { return _castRemaining; } }

        /// <summary>Current state, for UI.</summary>
        public SkillSlotState State
        {
            get
            {
                if (_casting)
                {
                    return SkillSlotState.Casting;
                }

                return Cooldown.IsReady ? SkillSlotState.Ready : SkillSlotState.Recharging;
            }
        }

        /// <summary>Targets locked in when the cast began (their ids, in selection order).</summary>
        public List<string> PendingTargets { get { return _pendingTargets; } }

        internal void BeginCast(List<string> targetIds, float castTime)
        {
            _casting = true;
            _castRemaining = castTime;
            _pendingTargets.Clear();
            for (int i = 0; i < targetIds.Count; i++)
            {
                _pendingTargets.Add(targetIds[i]);
            }
        }

        internal void AdvanceCast(float delta)
        {
            _castRemaining -= delta;
        }

        internal void EndCast()
        {
            _casting = false;
            _castRemaining = 0f;
        }

        internal void ClearPendingTargets()
        {
            _pendingTargets.Clear();
        }

        /// <summary>Cancels a running cast (stun, movement, death). No-op when not casting.</summary>
        public bool Interrupt()
        {
            if (!_casting)
            {
                return false;
            }

            EndCast();
            _pendingTargets.Clear();
            return true;
        }

        /// <summary>Back to a brand-new state: full charges, no cast.</summary>
        public void Reset()
        {
            EndCast();
            _pendingTargets.Clear();
            Cooldown.ResetToFull();
        }
    }

    /// <summary>
    /// 一个施法者的技能书：他会哪些技能、每个技能现在能不能放、放了多久出效果。
    ///
    /// 三个设计判断：
    ///   - **资源在起手时扣**（吟唱技能也一样）。所以吟唱被打断是"资源白花"，
    ///     这个语义是明确的、可断言的，而不是"看情况"；
    ///   - **效果落地失败不截停**：一条效果打空（目标已死）不影响同一技能的另一条效果，
    ///     更不影响充能已经消耗的事实 —— 半途停下的战斗逻辑比失败更难查；
    ///   - **热更钩子失败不截停**：脚本是热更代码，一个错别字不该让技能整体失灵，
    ///     失败如实报成 HookFailed 事件，让运维看得见。
    ///
    /// 线程：可变状态，单线程（帧循环）内使用。<see cref="Pump"/> 帧内零分配
    /// （事件装进调用方给的列表）。
    /// </summary>
    public sealed class SkillBook
    {
        private readonly string _ownerId;
        private readonly SkillLibrary _library;
        private readonly ISkillStatSource _stats;
        private readonly ISkillEffectSink _effects;
        private readonly IScriptRuntime _scripts;

        private readonly Dictionary<string, SkillSlot> _slots =
            new Dictionary<string, SkillSlot>(StringComparer.Ordinal);

        /// <summary>学习顺序 —— 也就是 Pump 的推进顺序（确定性来自这里）。</summary>
        private readonly List<SkillSlot> _order = new List<SkillSlot>();

        private readonly List<string> _targetIds = new List<string>();
        private readonly List<SkillTargetCandidate> _targets = new List<SkillTargetCandidate>();

        public SkillBook(
            string ownerId,
            SkillLibrary library,
            ISkillStatSource stats,
            ISkillEffectSink effects,
            IScriptRuntime scripts)
        {
            if (string.IsNullOrEmpty(ownerId))
            {
                throw new ArgumentException("SkillBook: owner id must not be empty.", "ownerId");
            }

            if (library == null)
            {
                throw new ArgumentNullException("library");
            }

            _ownerId = ownerId;
            _library = library;
            _stats = stats;
            _effects = effects;
            _scripts = scripts;
        }

        /// <summary>Who owns this book (used as the caster id in effects and hooks).</summary>
        public string OwnerId { get { return _ownerId; } }

        /// <summary>How many skills are learned.</summary>
        public int Count { get { return _order.Count; } }

        /// <summary>Learned skill ids in learning order (a copy).</summary>
        public List<string> Known()
        {
            List<string> ids = new List<string>(_order.Count);
            for (int i = 0; i < _order.Count; i++)
            {
                ids.Add(_order[i].Def.Id);
            }

            return ids;
        }

        /// <summary>Whether the skill is learned.</summary>
        public bool Knows(string skillId)
        {
            if (skillId == null)
            {
                throw new ArgumentNullException("skillId");
            }

            return _slots.ContainsKey(skillId);
        }

        /// <summary>Learns a skill. Re-learning returns false; an unknown id throws (wiring bug).</summary>
        public bool Learn(string skillId)
        {
            SkillDef def = _library.Require(skillId);
            if (_slots.ContainsKey(skillId))
            {
                return false;
            }

            SkillSlot slot = new SkillSlot(def);
            _slots.Add(skillId, slot);
            _order.Add(slot);
            return true;
        }

        /// <summary>Forgets a skill (respec). Returns false when it was not learned.</summary>
        public bool Forget(string skillId)
        {
            SkillSlot slot;
            if (skillId == null)
            {
                throw new ArgumentNullException("skillId");
            }

            if (!_slots.TryGetValue(skillId, out slot))
            {
                return false;
            }

            _slots.Remove(skillId);
            _order.Remove(slot);
            return true;
        }

        /// <summary>Looks up a slot (null when not learned).</summary>
        public SkillSlot Slot(string skillId)
        {
            SkillSlot slot;
            return _slots.TryGetValue(skillId, out slot) ? slot : null;
        }

        /// <summary>Charges available for a skill (0 when not learned).</summary>
        public int ChargesOf(string skillId)
        {
            SkillSlot slot = Slot(skillId);
            return slot == null ? 0 : slot.Cooldown.Available;
        }

        /// <summary>Resets every slot to a fresh state (respawn, encounter restart).</summary>
        public void Reset()
        {
            for (int i = 0; i < _order.Count; i++)
            {
                _order[i].Reset();
            }
        }

        // ------------------------------------------------------------------ 起手

        /// <summary>
        /// Tries to start a cast.
        ///
        /// 检查顺序 = 报错优先级，这是**故意固定**的：冷却 → 资源 → 目标。冷却最先，因为
        /// "这个技能这帧不能用"是最不需要解释的一条；目标最后，因为"打不到"往往取决于
        /// 当前帧的战场快照，把它排在资源不足前面会掩盖真正的原因。
        /// </summary>
        public SkillCastOutcome TryBegin(
            string skillId,
            IList<SkillTargetCandidate> candidates,
            string requestedTargetId,
            List<SkillTargetCandidate> targetsInto,
            List<SkillCastEvent> eventsInto)
        {
            if (skillId == null)
            {
                throw new ArgumentNullException("skillId");
            }

            if (candidates == null)
            {
                throw new ArgumentNullException("candidates");
            }

            if (targetsInto == null)
            {
                throw new ArgumentNullException("targetsInto");
            }

            if (eventsInto == null)
            {
                throw new ArgumentNullException("eventsInto");
            }

            SkillSlot slot;
            if (!_slots.TryGetValue(skillId, out slot))
            {
                return Reject(eventsInto, skillId, "this caster does not know '" + skillId + "'");
            }

            if (slot.IsCasting)
            {
                return Reject(eventsInto, skillId, "already casting '" + skillId + "'");
            }

            if (!slot.Cooldown.IsReady)
            {
                return Reject(
                    eventsInto,
                    skillId,
                    "still recharging (" + slot.Cooldown.Describe() + ")");
            }

            string error;
            if (!CheckCosts(slot.Def, out error))
            {
                return Reject(eventsInto, skillId, error);
            }

            SkillTargetQuery query = SkillTargetQuery.For(_ownerId, TeamOf(candidates), slot.Def.Targeting)
                .WithTarget(requestedTargetId);
            int targetCount = SkillTargetSelector.Select(query, candidates, _targets);
            if (targetCount == 0)
            {
                return Reject(
                    eventsInto,
                    skillId,
                    "no valid target for " + slot.Def.Targeting
                    + (requestedTargetId == null ? string.Empty : " (requested '" + requestedTargetId + "')"));
            }

            if (!SpendCosts(slot.Def, out error))
            {
                return Reject(eventsInto, skillId, error);
            }

            if (!slot.Cooldown.TryConsume())
            {
                // 走到这里说明前置检查与消耗互相矛盾 —— 这是本模块自己的 bug，不是玩家行为。
                throw new InvalidOperationException(
                    "SkillBook: cooldown said ready but refused to consume for '" + skillId + "'.");
            }

            targetsInto.Clear();
            _targetIds.Clear();
            for (int i = 0; i < _targets.Count; i++)
            {
                targetsInto.Add(_targets[i]);
                _targetIds.Add(_targets[i].Id);
            }

            eventsInto.Add(new SkillCastEvent
            {
                Kind = SkillCastEventKind.CastStarted,
                SkillId = skillId,
                ChargesAvailable = slot.Cooldown.Available
            });

            if (slot.Def.IsCast)
            {
                slot.BeginCast(_targetIds, slot.Def.CastTime);
                return SkillCastOutcome.Ok(targetCount, slot.Cooldown.Available, slot.Def.CastTime);
            }

            Land(slot, _targetIds, eventsInto);
            return SkillCastOutcome.Ok(targetCount, slot.Cooldown.Available, 0f);
        }

        private int TeamOf(IList<SkillTargetCandidate> candidates)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i].Id, _ownerId, StringComparison.Ordinal))
                {
                    return candidates[i].Team;
                }
            }

            // 施法者不在候选列表里时按 0 处理：这只影响 AllEnemies / LowestHealthAlly
            // 的过滤结果（会选出全部或选不出），属于调用方的数据缺口，不该抛。
            return 0;
        }

        private bool CheckCosts(SkillDef def, out string error)
        {
            error = null;
            if (def.Costs.Count == 0)
            {
                return true;
            }

            if (_stats == null)
            {
                error = "'" + def.Id + "' costs resources but no stat source is wired";
                return false;
            }

            for (int i = 0; i < def.Costs.Count; i++)
            {
                SkillCost cost = def.Costs[i];
                float have;
                if (!_stats.TryValue(cost.Resource, out have))
                {
                    error = "caster does not declare resource '" + cost.Resource + "'";
                    return false;
                }

                if (have < cost.Amount)
                {
                    error = "not enough " + cost.Resource + " (need "
                        + cost.Amount.ToString("0.###", CultureInfo.InvariantCulture) + ", have "
                        + have.ToString("0.###", CultureInfo.InvariantCulture) + ")";
                    return false;
                }
            }

            return true;
        }

        private bool SpendCosts(SkillDef def, out string error)
        {
            error = null;
            for (int i = 0; i < def.Costs.Count; i++)
            {
                string spendError;
                if (!_stats.TrySpend(def.Costs[i].Resource, def.Costs[i].Amount, out spendError))
                {
                    error = "could not spend " + def.Costs[i] + ": " + spendError;
                    return false;
                }
            }

            return true;
        }

        private static SkillCastOutcome Reject(List<SkillCastEvent> eventsInto, string skillId, string error)
        {
            eventsInto.Add(new SkillCastEvent
            {
                Kind = SkillCastEventKind.CastRejected,
                SkillId = skillId,
                Error = error
            });

            return SkillCastOutcome.Fail(error);
        }

        // ------------------------------------------------------------------ 帧推进

        /// <summary>
        /// Advances every slot: cast bars first, then recharge clocks. Events go into
        /// <paramref name="into"/> -- nothing is allocated per frame.
        /// </summary>
        public void Pump(float delta, List<SkillCastEvent> into)
        {
            if (delta < 0f)
            {
                throw new ArgumentException("SkillBook: delta must not be negative.", "delta");
            }

            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            float castSpeed = ReadStat("cast_speed", 1f);
            float cooldownRate = ReadStat("cooldown_rate", 1f);

            for (int i = 0; i < _order.Count; i++)
            {
                SkillSlot slot = _order[i];

                if (slot.IsCasting)
                {
                    slot.AdvanceCast(delta * castSpeed);
                    if (slot.CastRemaining <= 0f)
                    {
                        slot.EndCast();
                        Land(slot, slot.PendingTargets, into);
                    }
                }

                int restored = slot.Cooldown.Pump(delta, cooldownRate);
                if (restored > 0)
                {
                    into.Add(new SkillCastEvent
                    {
                        Kind = SkillCastEventKind.ChargeRestored,
                        SkillId = slot.Def.Id,
                        Restored = restored,
                        ChargesAvailable = slot.Cooldown.Available
                    });
                }
            }
        }

        /// <summary>
        /// Reads a tuning stat with a default. A missing stat, or one that is zero/negative,
        /// falls back to 1: a zero cast speed would mean "this cast never finishes", which is
        /// never what a designer meant by leaving the stat unset.
        /// </summary>
        private float ReadStat(string stat, float fallback)
        {
            if (_stats == null)
            {
                return fallback;
            }

            float value;
            if (!_stats.TryValue(stat, out value) || value <= 0f)
            {
                return fallback;
            }

            return value;
        }

        /// <summary>
        /// Lands every effect of a cast on every locked target, then fires the script hook.
        /// 落地失败只记事件：同一技能的第二条效果、以及下一个目标，都要继续走完。
        /// </summary>
        private void Land(SkillSlot slot, List<string> targetIds, List<SkillCastEvent> into)
        {
            into.Add(new SkillCastEvent
            {
                Kind = SkillCastEventKind.CastCompleted,
                SkillId = slot.Def.Id,
                ChargesAvailable = slot.Cooldown.Available
            });

            SkillDef def = slot.Def;
            for (int t = 0; t < targetIds.Count; t++)
            {
                string targetId = targetIds[t];

                for (int e = 0; e < def.Effects.Count; e++)
                {
                    SkillEffect effect = def.Effects[e];

                    if (_effects == null)
                    {
                        into.Add(new SkillCastEvent
                        {
                            Kind = SkillCastEventKind.EffectFailed,
                            SkillId = def.Id,
                            TargetId = targetId,
                            Error = "'" + def.Id + "' has effects but no effect sink is wired"
                        });
                        break;
                    }

                    SkillEffectResult result = _effects.Apply(effect, _ownerId, targetId);
                    into.Add(new SkillCastEvent
                    {
                        Kind = result.Succeeded ? SkillCastEventKind.EffectApplied : SkillCastEventKind.EffectFailed,
                        SkillId = def.Id,
                        TargetId = targetId,
                        Amount = result.Amount,
                        Error = result.Error
                    });
                }
            }

            if (def.ScriptHook == null)
            {
                return;
            }

            if (_scripts == null)
            {
                into.Add(new SkillCastEvent
                {
                    Kind = SkillCastEventKind.HookFailed,
                    SkillId = def.Id,
                    Error = "'" + def.Id + "' declares hook '" + def.ScriptHook
                        + "' but no script runtime is wired"
                });
                return;
            }

            ScriptOutcome outcome = _scripts.CallEntry(def.ScriptHook);
            if (!outcome.Succeeded)
            {
                into.Add(new SkillCastEvent
                {
                    Kind = SkillCastEventKind.HookFailed,
                    SkillId = def.Id,
                    Error = "hook '" + def.ScriptHook + "' failed: " + outcome.Error
                });
            }
        }

        /// <summary>
        /// Interrupts a running cast. Returns false when nothing was casting (a stun landing a
        /// frame late is normal). 资源不退：扣费发生在起手时，这是明确的语义而不是疏漏。
        /// </summary>
        public bool Interrupt(string skillId, string reason, List<SkillCastEvent> into)
        {
            if (skillId == null)
            {
                throw new ArgumentNullException("skillId");
            }

            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            SkillSlot slot = Slot(skillId);
            if (slot == null || !slot.Interrupt())
            {
                return false;
            }

            into.Add(new SkillCastEvent
            {
                Kind = SkillCastEventKind.Interrupted,
                SkillId = skillId,
                Error = reason,
                ChargesAvailable = slot.Cooldown.Available
            });

            return true;
        }
    }
}
