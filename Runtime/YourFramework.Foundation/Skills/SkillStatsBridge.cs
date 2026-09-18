using System;
using System.Collections.Generic;
using YourFramework.Stats;

namespace YourFramework.Skills
{
    /// <summary>
    /// 把属性表当技能系统的资源账本：读属性走 <c>TryValue</c>，扣资源走 <c>SetBase</c>
    /// （资源在这是普通属性，所以回蓝、回能量不需要额外通道）。
    /// </summary>
    public sealed class StatSheetStatSource : ISkillStatSource
    {
        private readonly StatSheet _sheet;

        public StatSheetStatSource(StatSheet sheet)
        {
            if (sheet == null)
            {
                throw new ArgumentNullException("sheet");
            }

            _sheet = sheet;
        }

        /// <summary>The wrapped sheet (diagnostics only).</summary>
        public StatSheet Sheet { get { return _sheet; } }

        /// <summary>Reads a stat.</summary>
        public bool TryValue(string stat, out float value)
        {
            if (stat == null)
            {
                throw new ArgumentNullException("stat");
            }

            return _sheet.TryValue(stat, out value);
        }

        /// <summary>
        /// Spends a resource. The balance check is repeated here rather than trusted: the
        /// caller's check and this write are two different moments, and a ledger that
        /// silently goes negative is the kind of bug that only shows up as "mana is -3"
        /// in a screenshot three weeks later.
        /// </summary>
        public bool TrySpend(string stat, float amount, out string error)
        {
            if (stat == null)
            {
                throw new ArgumentNullException("stat");
            }

            float have;
            if (!_sheet.TryValue(stat, out have))
            {
                error = "resource '" + stat + "' is not declared on the sheet";
                return false;
            }

            if (have < amount)
            {
                error = "have " + have + " of '" + stat + "', need " + amount;
                return false;
            }

            _sheet.SetBase(stat, have - amount);
            error = null;
            return true;
        }
    }

    /// <summary>
    /// actor id → (属性表, 效果容器)。技能内核只认 id，具体是实体、角色还是玩家，
    /// 由项目自己的实体系统回答 —— 这是技能模块与"谁在打架"这件事之间唯一的接缝。
    /// </summary>
    public interface ISkillActorSource
    {
        /// <summary>Looks up an actor. Unknown ids are false, not throws (a dead-and-removed actor is normal).</summary>
        bool TryGetActor(string actorId, out StatSheet sheet, out EffectHost effects);
    }

    /// <summary>
    /// 把技能效果落到数值系统上：伤害走伤害管道，治疗改生命属性，其它走效果容器。
    ///
    /// 这是**整个技能模块里唯一知道数值系统存在的地方**。技能内核、配置格式、目标选择、
    /// 冷却逻辑都不认识属性表 —— 所以这套技能系统能在没有数值系统的工程里编译、
    /// 能在无头环境里断言（换一个 ISkillEffectSink 就能验流程），
    /// 也能被换成"特效先行、伤害后结算"的另一套落地策略。
    /// </summary>
    public sealed class StatSkillEffectSink : ISkillEffectSink
    {
        private readonly DamagePipeline _damage;
        private readonly ISkillActorSource _actors;
        private readonly string _healthStat;
        private readonly string _maxHealthStat;

        /// <summary>
        /// Builds the sink. <paramref name="damage"/> is optional: a project that only needs
        /// heal/buff skills can leave it null and damage effects will report a clear failure
        /// instead of half-working.
        /// </summary>
        public StatSkillEffectSink(
            DamagePipeline damage,
            ISkillActorSource actors,
            string healthStat = "hp",
            string maxHealthStat = "max_hp")
        {
            if (actors == null)
            {
                throw new ArgumentNullException("actors");
            }

            if (string.IsNullOrEmpty(healthStat))
            {
                throw new ArgumentException("health stat must not be empty.", "healthStat");
            }

            _damage = damage;
            _actors = actors;
            _healthStat = healthStat;
            _maxHealthStat = maxHealthStat;
        }

        /// <summary>Applies one effect to one target. Content failures come back as values.</summary>
        public SkillEffectResult Apply(SkillEffect effect, string casterId, string targetId)
        {
            if (effect == null)
            {
                throw new ArgumentNullException("effect");
            }

            switch (effect.Kind)
            {
                case SkillEffectKind.Damage:
                    return ApplyDamage(effect, casterId, targetId);
                case SkillEffectKind.Heal:
                    return ApplyHeal(effect, casterId, targetId);
                case SkillEffectKind.ApplyEffect:
                    return ApplyTimedEffect(effect, casterId, targetId);
                default:
                    return SkillEffectResult.Fail("unknown effect kind " + effect.Kind);
            }
        }

        private SkillEffectResult ApplyDamage(SkillEffect effect, string casterId, string targetId)
        {
            if (_damage == null)
            {
                return SkillEffectResult.Fail(
                    "damage pipeline is not wired (this sink was built without one)");
            }

            StatSheet attacker;
            EffectHost attackerEffects;
            if (!_actors.TryGetActor(casterId, out attacker, out attackerEffects))
            {
                return SkillEffectResult.Fail("caster '" + casterId + "' is not registered");
            }

            StatSheet defender;
            EffectHost defenderEffects;
            if (!_actors.TryGetActor(targetId, out defender, out defenderEffects))
            {
                return SkillEffectResult.Fail("target '" + targetId + "' is not registered");
            }

            DamageRequest request = effect.FlatBonus != 0f
                ? DamageRequest.WithBonus(effect.Multiplier, effect.FlatBonus)
                : DamageRequest.Of(effect.Multiplier);

            DamageResult result = _damage.Resolve(attacker, defender, request);
            if (!result.Ok)
            {
                return SkillEffectResult.Fail(
                    "damage did not resolve (" + result.Outcome + ")"
                    + (result.Stat == null ? string.Empty : " reading '" + result.Stat + "'"));
            }

            float remaining;
            string error;
            if (!_damage.TryApplyDamage(defender, _healthStat, result, out remaining, out error))
            {
                return SkillEffectResult.Fail(error);
            }

            return SkillEffectResult.Ok(result.Amount);
        }

        private SkillEffectResult ApplyHeal(SkillEffect effect, string casterId, string targetId)
        {
            StatSheet caster;
            EffectHost casterEffects;
            if (!_actors.TryGetActor(casterId, out caster, out casterEffects))
            {
                return SkillEffectResult.Fail("caster '" + casterId + "' is not registered");
            }

            StatSheet target;
            EffectHost targetEffects;
            if (!_actors.TryGetActor(targetId, out target, out targetEffects))
            {
                return SkillEffectResult.Fail("target '" + targetId + "' is not registered");
            }

            float power;
            if (!caster.TryValue(effect.Stat, out power))
            {
                return SkillEffectResult.Fail(
                    "caster '" + casterId + "' does not declare '" + effect.Stat + "'");
            }

            float amount = power * effect.Multiplier + effect.FlatBonus;
            if (amount <= 0f)
            {
                return SkillEffectResult.Fail(
                    "heal computed " + amount + " from '" + effect.Stat + "'");
            }

            float health;
            if (!target.TryValue(_healthStat, out health))
            {
                return SkillEffectResult.Fail(
                    "target '" + targetId + "' does not declare '" + _healthStat + "'");
            }

            float healed = amount;
            float maximum;
            if (!string.IsNullOrEmpty(_maxHealthStat) && target.TryValue(_maxHealthStat, out maximum))
            {
                // 有过量治疗上限就钳住；没声明上限属性则按"无上限"处理（不失败：
                // 上限是可选的，不是所有项目都做）。
                float room = maximum - health;
                if (room < healed)
                {
                    healed = room;
                }
            }

            if (healed <= 0f)
            {
                // 满血时治疗不是失败，是"没效果"：返回 Ok(0)，让 UI 显示 0 而不是报错。
                return SkillEffectResult.Ok(0f);
            }

            target.SetBase(_healthStat, health + healed);
            return SkillEffectResult.Ok(healed);
        }

        private SkillEffectResult ApplyTimedEffect(SkillEffect effect, string casterId, string targetId)
        {
            StatSheet target;
            EffectHost host;
            if (!_actors.TryGetActor(targetId, out target, out host))
            {
                return SkillEffectResult.Fail("target '" + targetId + "' is not registered");
            }

            if (host == null)
            {
                return SkillEffectResult.Fail("target '" + targetId + "' has no effect host");
            }

            StatEffect statEffect = ToStatEffect(effect);

            EffectInstance instance;
            string error;
            if (!host.TryApply(statEffect, casterId, out instance, out error))
            {
                return SkillEffectResult.Fail(error);
            }

            return SkillEffectResult.Ok(instance == null ? 0f : instance.Stacks);
        }

        /// <summary>
        /// 配置层定义 → 数值层定义。映射穷举每一个分支、**不写 default 兜底**：
        /// 以后任何一边加了新枚举值，这里会编译不过或直接抛，而不是静默变成 Flat。
        /// </summary>
        public static StatEffect ToStatEffect(SkillEffect effect)
        {
            if (effect == null)
            {
                throw new ArgumentNullException("effect");
            }

            StatEffect statEffect = new StatEffect(
                effect.EffectId,
                effect.Duration,
                MapPolicy(effect.StackPolicy));
            statEffect.TickInterval = effect.TickInterval;
            statEffect.MaxStacks = effect.MaxStacks;

            // StatEffect 的构造函数**不建 Modifiers 列表**（数值层的约定是调用方自己塞），
            // 所以这里必须显式建一个 —— 忘了就是 NullReferenceException。
            if (statEffect.Modifiers == null)
            {
                statEffect.Modifiers = new List<StatModifier>();
            }

            for (int i = 0; i < effect.Modifiers.Count; i++)
            {
                SkillModifier modifier = effect.Modifiers[i];
                statEffect.Modifiers.Add(new StatModifier(
                    modifier.Stat,
                    MapKind(modifier.Kind),
                    modifier.Value,
                    effect.EffectId));
            }

            return statEffect;
        }

        private static StatKind MapKind(SkillStatKind kind)
        {
            switch (kind)
            {
                case SkillStatKind.Flat:
                    return StatKind.Flat;
                case SkillStatKind.PercentAdd:
                    return StatKind.PercentAdd;
                case SkillStatKind.PercentMul:
                    return StatKind.PercentMul;
            }

            throw new ArgumentException("SkillStatsBridge: unmapped stat kind " + kind + ".");
        }

        private static EffectStackPolicy MapPolicy(SkillStackPolicy policy)
        {
            switch (policy)
            {
                case SkillStackPolicy.Refresh:
                    return EffectStackPolicy.Refresh;
                case SkillStackPolicy.Stack:
                    return EffectStackPolicy.Stack;
                case SkillStackPolicy.Independent:
                    return EffectStackPolicy.Independent;
            }

            throw new ArgumentException("SkillStatsBridge: unmapped stack policy " + policy + ".");
        }
    }
}
