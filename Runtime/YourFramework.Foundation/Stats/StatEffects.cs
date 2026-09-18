using System;
using System.Collections.Generic;

namespace YourFramework.Stats
{
    /// <summary>同名效果的堆叠方式。</summary>
    public enum EffectStackPolicy
    {
        /// <summary>
        /// 已在则只刷新时长，层数恒为 1，不重复加修饰符。绝大多数增益/减速都该用这个 ——
        /// 否则两个法师各放一次减速，玩家会被定在原地。
        /// </summary>
        Refresh,

        /// <summary>
        /// 已在则层数 +1（上限 MaxStacks）并刷新时长；每层各贡献一份修饰符。
        /// 中毒/流血这类"每次命中都该更疼"的用法。
        /// </summary>
        Stack,

        /// <summary>
        /// 每次施加都新建独立实例，各自计时、各自贡献修饰符（同名实例数上限 MaxStacks）。
        /// 需要"多个来源各自到期"时用，代价是 UI 与结算都要按实例数看。
        /// </summary>
        Independent
    }

    /// <summary>
    /// 一个效果（buff/debuff）的**定义**：活多久、多久 tick 一次、改哪些属性、怎么堆叠。
    /// 定义是只读数据，可以被同一份配表反复施加到无数个宿主上。
    /// </summary>
    public sealed class StatEffect
    {
        /// <summary>Effect id; the same id means "the same effect" for stacking purposes.</summary>
        public string Id;

        /// <summary>Seconds it lasts. &lt;= 0 means permanent (until removed by hand).</summary>
        public float Duration;

        /// <summary>Seconds between ticks. &lt;= 0 means "never ticks" (a pure stat change).</summary>
        public float TickInterval;

        /// <summary>Upper bound on layers/instances. &lt;= 0 is treated as 1.</summary>
        public int MaxStacks;

        /// <summary>How repeated applications combine.</summary>
        public EffectStackPolicy Policy;

        /// <summary>Modifiers contributed per layer. May be empty for a tick-only effect.</summary>
        public List<StatModifier> Modifiers;

        /// <summary>Whether the effect never expires on its own.</summary>
        public bool IsPermanent { get { return Duration <= 0f; } }

        /// <summary>Whether the effect emits tick events.</summary>
        public bool Ticks { get { return TickInterval > 0f; } }

        /// <summary>The effective stack cap (never below 1, so a stray 0 means "no stacking").</summary>
        public int EffectiveMaxStacks { get { return MaxStacks <= 0 ? 1 : MaxStacks; } }

        /// <summary>Builds an effect definition.</summary>
        public StatEffect(string id, float duration, EffectStackPolicy policy)
        {
            Id = id;
            Duration = duration;
            Policy = policy;
        }
    }

    /// <summary>效果容器报出来的事情。装进调用方给的列表，热路径不分配。</summary>
    public enum EffectEventKind
    {
        /// <summary>A tick interval elapsed.</summary>
        Tick,

        /// <summary>The effect's duration ran out and its modifiers were removed.</summary>
        Expired
    }

    /// <summary>一次 tick 或一次到期，作为值交给调用方（播特效、结算伤害、刷新 UI 都靠它）。</summary>
    public struct EffectEvent
    {
        /// <summary>What happened.</summary>
        public EffectEventKind Kind;

        /// <summary>The effect definition's id.</summary>
        public string EffectId;

        /// <summary>The source label that applied it.</summary>
        public string Source;

        /// <summary>Layers at the moment of the event.</summary>
        public int Stacks;

        /// <summary>1-based tick counter (0 for events that are not ticks).</summary>
        public int TickIndex;
    }

    /// <summary>一个**已施加**的效果实例：还剩多久、叠了几层、下次 tick 还差多少。</summary>
    public sealed class EffectInstance
    {
        /// <summary>The shared definition.</summary>
        public StatEffect Effect;

        /// <summary>Who applied it (the label used by <see cref="EffectHost.RemoveBySource"/>).</summary>
        public string Source;

        /// <summary>Current layers.</summary>
        public int Stacks;

        /// <summary>Seconds left; meaningless when the definition is permanent.</summary>
        public float Remaining;

        /// <summary>Seconds accumulated towards the next tick.</summary>
        public float TickAccumulator;

        /// <summary>How many ticks have fired so far.</summary>
        public int TickCount;

        internal readonly List<StatModifier> Applied = new List<StatModifier>(4);

        /// <summary>Effect id (convenience for UI and diagnostics).</summary>
        public string Id { get { return Effect == null ? null : Effect.Id; } }

        /// <summary>Remaining fraction in [0,1], or 1 for a permanent effect (progress bars).</summary>
        public float RemainingRatio
        {
            get
            {
                if (Effect == null || Effect.IsPermanent)
                {
                    return 1f;
                }

                float ratio = Remaining / Effect.Duration;
                return ratio < 0f ? 0f : (ratio > 1f ? 1f : ratio);
            }
        }
    }

    /// <summary>
    /// 效果容器：把"持续效果"这件事做成**泵驱动的状态机**，而不是散落在各处的计时器。
    ///
    /// 它只做三件事，但每件都做干净：
    /// 1. 施加时按堆叠策略把修饰符落到 <see cref="StatSheet"/> 上，并记住自己加了哪些
    ///    —— 到期时逐条精确移除，不靠"按来源全清"去猜；
    /// 2. 每帧 <see cref="Pump"/> 推进寿命与 tick 计时，把 tick / 到期作为**值**填进调用方
    ///    的列表（不回调、不分配、不抛异常）；
    /// 3. 任何时候都能按效果 id 或来源标签批量移除 —— 换场景、死亡、离开光环范围都是这一句。
    ///
    /// 失败的姿态：栈满、效果 id 为空这类"施加不上去"是**值**（返回 false + 原因），
    /// 因为它是正常的游戏状态；而"定义里带 null 修饰符"或"指向未声明的属性"是布线 bug，
    /// 在动任何状态之前就抛（抛完宿主与属性表都和抛之前一模一样）。
    ///
    /// 时间注入：<see cref="Pump"/> 收显式 delta，暂停/快进/回放/无头测试全都成立。
    /// 单线程使用（泵、主线程）。
    /// </summary>
    public sealed class EffectHost
    {
        private readonly StatSheet _sheet;
        private readonly List<EffectInstance> _active = new List<EffectInstance>(8);

        /// <summary>Why the last <see cref="Apply"/> returned null (a value, not an exception).</summary>
        public string LastApplyError { get; private set; }

        /// <summary>Builds a host that writes its modifiers into the given sheet.</summary>
        public EffectHost(StatSheet sheet)
        {
            if (sheet == null)
            {
                throw new ArgumentNullException("sheet");
            }

            _sheet = sheet;
        }

        /// <summary>Live instance count (for <see cref="EffectStackPolicy.Independent"/> this is layer count).</summary>
        public int Count { get { return _active.Count; } }

        /// <summary>The live instances, in application order.</summary>
        public List<EffectInstance> Active { get { return _active; } }

        /// <summary>The sheet this host writes to.</summary>
        public StatSheet Sheet { get { return _sheet; } }

        /// <summary>
        /// Applies an effect. Returns null (and sets <see cref="LastApplyError"/>) when it
        /// cannot be applied — a stack cap reached is a normal game state, not an exception.
        /// </summary>
        public EffectInstance Apply(StatEffect effect, string source)
        {
            EffectInstance instance;
            string error;
            return TryApply(effect, source, out instance, out error) ? instance : null;
        }

        /// <summary>Applies an effect, reporting the reason when it does not stick.</summary>
        public bool TryApply(StatEffect effect, string source, out EffectInstance instance, out string error)
        {
            instance = null;
            error = null;

            if (effect == null)
            {
                error = "effect must not be null";
                LastApplyError = error;
                return false;
            }

            if (string.IsNullOrEmpty(effect.Id))
            {
                // 没有 id 就没法堆叠判定、没法按 id 移除 —— 这是布线 bug，不是游戏状态。
                throw new ArgumentException(
                    "EffectHost.Apply: effect id must not be empty (id is what stacking and "
                    + "removal are keyed on).");
            }

            // 在动任何状态之前先验定义：否则"实例已登记、修饰符才抛"会留下半施加的残留，
            // fail-fast 就变成了"抛了但还脏着"。
            RequireModifiers(effect);

            if (effect.Policy == EffectStackPolicy.Independent)
            {
                return ApplyIndependent(effect, source, out instance, out error);
            }

            EffectInstance existing = Find(effect.Id);
            if (existing == null)
            {
                instance = Create(effect, source, 1, effect.Duration);
                _active.Add(instance);
                AddModifiers(instance);
                return true;
            }

            if (effect.Policy == EffectStackPolicy.Refresh)
            {
                existing.Effect = effect;
                existing.Source = source;
                if (!effect.IsPermanent)
                {
                    existing.Remaining = effect.Duration;
                }

                instance = existing;
                return true;
            }

            // Stack: 层数 +1，刷新时长，再加一份修饰符（每层各贡献一份）。
            if (existing.Stacks >= effect.EffectiveMaxStacks)
            {
                error = "effect '" + effect.Id + "' already at max stacks ("
                    + effect.EffectiveMaxStacks + ")";
                LastApplyError = error;
                return false;
            }

            existing.Effect = effect;
            existing.Source = source;
            existing.Stacks++;
            if (!effect.IsPermanent)
            {
                existing.Remaining = effect.Duration;
            }

            AddModifiers(existing);
            instance = existing;
            return true;
        }

        /// <summary>The instance for an effect id, or null (Independent effects: the oldest one).</summary>
        public EffectInstance Find(string effectId)
        {
            if (effectId == null)
            {
                return null;
            }

            for (int i = 0; i < _active.Count; i++)
            {
                if (string.Equals(_active[i].Id, effectId, StringComparison.Ordinal))
                {
                    return _active[i];
                }
            }

            return null;
        }

        /// <summary>Layers of an effect (0 when absent).</summary>
        public int StacksOf(string effectId)
        {
            EffectInstance instance = Find(effectId);
            return instance == null ? 0 : instance.Stacks;
        }

        /// <summary>Removes every instance of one effect id; returns how many went away.</summary>
        public int Remove(string effectId)
        {
            if (effectId == null)
            {
                return 0;
            }

            int removed = 0;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_active[i].Id, effectId, StringComparison.Ordinal))
                {
                    Detach(_active[i]);
                    _active.RemoveAt(i);
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>Removes every instance applied by a source label (death, leaving an aura).</summary>
        public int RemoveBySource(string source)
        {
            if (source == null)
            {
                return 0;
            }

            int removed = 0;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                EffectInstance instance = _active[i];
                if (string.Equals(instance.Source, source, StringComparison.Ordinal))
                {
                    Detach(instance);
                    _active.RemoveAt(i);
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>Removes everything (respawn / scene change).</summary>
        public int Clear()
        {
            int removed = _active.Count;
            for (int i = 0; i < _active.Count; i++)
            {
                Detach(_active[i]);
            }

            _active.Clear();
            return removed;
        }

        /// <summary>
        /// Advances every effect by one frame. Ticks and expiries are appended to
        /// <paramref name="into"/> in the order they happen (may be null when the caller
        /// does not care).
        ///
        /// 两条顺序上的讲究，都是为了让"时长 3 秒、每 1 秒一跳"真的跳出 3 次：
        /// 1. **先 tick 后到期**：到期那一帧先把最后一跳结掉再移除。若先减寿命，
        ///    t=3.0 那一帧会直接到期、把第 3 跳吃掉 —— 一条 DoT 白少一跳伤害；
        /// 2. **只结算"还活着的那段时间"**：一帧跨度可能大于剩余寿命（卡顿、快进、
        ///    无头测试里一次 Pump 4 秒）。按整帧累加会把死后的时间也算进去，
        ///    于是 3 秒的 DoT 跳出第 4 跳。所以累加量取 min(delta, 剩余寿命)。
        ///
        /// 合起来的收益是**步长无关**：Pump(1)×3 与 Pump(3)×1 给出完全一样的
        /// tick / 到期序列，回放与快进因此不会改变战斗结果。
        /// </summary>
        public void Pump(float deltaSeconds, List<EffectEvent> into)
        {
            if (deltaSeconds < 0f)
            {
                deltaSeconds = 0f;
            }

            for (int i = _active.Count - 1; i >= 0; i--)
            {
                EffectInstance instance = _active[i];
                StatEffect effect = instance.Effect;

                float alive = deltaSeconds;
                if (!effect.IsPermanent && alive > instance.Remaining)
                {
                    alive = instance.Remaining;
                }

                if (effect.Ticks && alive > 0f)
                {
                    instance.TickAccumulator += alive;
                    while (instance.TickAccumulator >= effect.TickInterval)
                    {
                        instance.TickAccumulator -= effect.TickInterval;
                        instance.TickCount++;
                        if (into != null)
                        {
                            into.Add(MakeEvent(instance, EffectEventKind.Tick, instance.TickCount));
                        }
                    }
                }

                if (effect.IsPermanent)
                {
                    continue;
                }

                instance.Remaining -= deltaSeconds;
                if (instance.Remaining > 0f)
                {
                    continue;
                }

                Detach(instance);
                _active.RemoveAt(i);
                if (into != null)
                {
                    into.Add(MakeEvent(instance, EffectEventKind.Expired, 0));
                }
            }
        }

        private bool ApplyIndependent(
            StatEffect effect, string source, out EffectInstance instance, out string error)
        {
            instance = null;
            error = null;

            int layers = 0;
            for (int i = 0; i < _active.Count; i++)
            {
                if (string.Equals(_active[i].Id, effect.Id, StringComparison.Ordinal))
                {
                    layers++;
                }
            }

            if (layers >= effect.EffectiveMaxStacks)
            {
                error = "effect '" + effect.Id + "' already at max layers ("
                    + effect.EffectiveMaxStacks + ")";
                LastApplyError = error;
                return false;
            }

            instance = Create(effect, source, 1, effect.Duration);
            _active.Add(instance);
            AddModifiers(instance);
            return true;
        }

        private static EffectInstance Create(StatEffect effect, string source, int stacks, float remaining)
        {
            EffectInstance instance = new EffectInstance();
            instance.Effect = effect;
            instance.Source = source;
            instance.Stacks = stacks;
            instance.Remaining = remaining;
            return instance;
        }

        /// <summary>
        /// Rejects a definition that cannot be applied cleanly. Called before any state
        /// changes so a bad definition leaves the host (and the sheet) exactly as it was.
        ///
        /// 这里连"目标属性是否已声明"一起校验（虽然 StatSheet.AddModifier 自己也会拦）：
        /// 不提前拦的话，实例会先登记进 _active、再在挂修饰符时抛，留下一个"在册但没生效"
        /// 的幽灵实例 —— fail-fast 就变成了"抛了但还脏着"。
        /// </summary>
        private void RequireModifiers(StatEffect effect)
        {
            List<StatModifier> definitions = effect.Modifiers;
            if (definitions == null)
            {
                return;
            }

            for (int i = 0; i < definitions.Count; i++)
            {
                StatModifier definition = definitions[i];
                if (definition == null)
                {
                    throw new ArgumentException(
                        "EffectHost: effect '" + effect.Id + "' has a null modifier at index "
                        + i + ". A half-applied effect is worse than a loud failure.");
                }

                if (!_sheet.IsDeclared(definition.Stat))
                {
                    throw new ArgumentException(
                        "EffectHost: effect '" + effect.Id + "' targets stat '" + definition.Stat
                        + "', which its sheet does not declare.");
                }
            }
        }

        /// <summary>
        /// 每层各加一份修饰符实例（值不变，靠份数表达层数），并把来源改写成
        /// "fx:&lt;id&gt;:&lt;来源&gt;" —— StatSheet.Explain 里一眼能看出这 8 点攻击是哪个效果给的。
        /// </summary>
        private void AddModifiers(EffectInstance instance)
        {
            List<StatModifier> definitions = instance.Effect.Modifiers;
            if (definitions == null || definitions.Count == 0)
            {
                return;
            }

            string label = "fx:" + instance.Effect.Id
                + (string.IsNullOrEmpty(instance.Source) ? string.Empty : ":" + instance.Source);

            for (int i = 0; i < definitions.Count; i++)
            {
                StatModifier definition = definitions[i];
                if (definition == null)
                {
                    throw new ArgumentException(
                        "EffectHost: effect '" + instance.Effect.Id + "' has a null modifier entry.");
                }

                StatModifier applied = new StatModifier(definition.Stat, definition.Kind, definition.Value, label);
                _sheet.AddModifier(applied);
                instance.Applied.Add(applied);
            }
        }

        /// <summary>逐条精确移除自己加过的修饰符（不靠"按来源全清"去猜别人的）。</summary>
        private void Detach(EffectInstance instance)
        {
            for (int i = 0; i < instance.Applied.Count; i++)
            {
                _sheet.RemoveModifier(instance.Applied[i]);
            }

            instance.Applied.Clear();
        }

        private static EffectEvent MakeEvent(EffectInstance instance, EffectEventKind kind, int tickIndex)
        {
            EffectEvent value = default(EffectEvent);
            value.Kind = kind;
            value.EffectId = instance.Id;
            value.Source = instance.Source;
            value.Stacks = instance.Stacks;
            value.TickIndex = tickIndex;
            return value;
        }
    }
}
