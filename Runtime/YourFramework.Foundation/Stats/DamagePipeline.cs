using System;
using System.Collections.Generic;

namespace YourFramework.Stats
{
    /// <summary>一次结算的结果分类。非 <see cref="Applied"/> 的都是"失败即值"，不是异常。</summary>
    public enum DamageOutcome
    {
        /// <summary>正常算出伤害。</summary>
        Applied,

        /// <summary>倍率/基础值为 0 或负（免疫、无伤害技能）—— 结果是 0 点，属于正常游戏状态。</summary>
        NoEffect,

        /// <summary>
        /// 攻方属性表里没有配置攻击属性。这是**装配错误**（配表换了属性名而代码没跟上），
        /// 但它同样以值的形式返回：一场战斗里一个单位配错不该把整局打崩，UI 有话说更重要。
        /// </summary>
        AggressorStatMissing,

        /// <summary>伤害已算出，但落伤失败（目标没有生命属性、目标已死）。</summary>
        NotApplied
    }

    /// <summary>一次攻击请求：技能倍率、固定加伤、是否真实伤害。</summary>
    public struct DamageRequest
    {
        /// <summary>Skill multiplier; &lt;= 0 means "this does no damage".</summary>
        public float Multiplier;

        /// <summary>Added after the multiplier (enchantments, flat procs).</summary>
        public float FlatBonus;

        /// <summary>Skips mitigation entirely (true damage / 真实伤害).</summary>
        public bool TrueDamage;

        /// <summary>A plain 1× hit.</summary>
        public static DamageRequest Of(float multiplier)
        {
            DamageRequest request = default(DamageRequest);
            request.Multiplier = multiplier;
            return request;
        }

        /// <summary>A 1× hit that ignores defence.</summary>
        public static DamageRequest True(float multiplier)
        {
            DamageRequest request = default(DamageRequest);
            request.Multiplier = multiplier;
            request.TrueDamage = true;
            return request;
        }

        /// <summary>A 1× hit with a flat bonus on top.</summary>
        public static DamageRequest WithBonus(float multiplier, float flatBonus)
        {
            DamageRequest request = default(DamageRequest);
            request.Multiplier = multiplier;
            request.FlatBonus = flatBonus;
            return request;
        }
    }

    /// <summary>
    /// 一次结算的**全过程**，每个中间量都在这里。
    ///
    /// 这是刻意做成 struct 且不带任何引用的：热路径（每人每帧可能好几次）零分配，
    /// 而"为什么这一下打了 37 而不是 40"这个问题由 <see cref="Explain"/> 回答。
    /// </summary>
    public struct DamageResult
    {
        /// <summary>How the resolution ended.</summary>
        public DamageOutcome Outcome;

        /// <summary>The attack stat that was read.</summary>
        public string Stat;

        /// <summary>Attacker's attack value.</summary>
        public float Attack;

        /// <summary>The multiplier from the request.</summary>
        public float Multiplier;

        /// <summary>The flat bonus from the request.</summary>
        public float FlatBonus;

        /// <summary>(Attack × Multiplier + FlatBonus): the number before crit/mitigation/variance.</summary>
        public float Base;

        /// <summary>Whether the crit roll succeeded.</summary>
        public bool Critical;

        /// <summary>Multiplier contributed by the crit (1 when no crit).</summary>
        public float CritFactor;

        /// <summary>Defender's defence value (0 when undeclared).</summary>
        public float Defense;

        /// <summary>The mitigation factor applied, in (0,1] — 1 means no reduction.</summary>
        public float Mitigation;

        /// <summary>The variance factor applied (1 when variance is off).</summary>
        public float VarianceFactor;

        /// <summary>Everything combined, before rounding/clamping.</summary>
        public float Raw;

        /// <summary>The final whole number to subtract from health.</summary>
        public int Amount;

        /// <summary>
        /// How many values were drawn from the random source. Fully predicted by the config:
        /// one if crit is enabled and the crit chance is &gt; 0, plus one if variance is &gt; 0
        /// (so 0, 1 or 2). Exposed so tests and replay tooling can prove the consumption is
        /// exactly what the config says rather than merely "looks reproducible".
        /// </summary>
        public int Rolls;

        /// <summary>True when the pipeline actually produced damage.</summary>
        public bool Ok { get { return Outcome == DamageOutcome.Applied; } }

        /// <summary>A one-line derivation for a combat log or a debug overlay.</summary>
        public string Explain()
        {
            if (Outcome == DamageOutcome.AggressorStatMissing)
            {
                return "无法结算：攻方属性表没有 '" + Stat + "'";
            }

            if (Outcome == DamageOutcome.NoEffect)
            {
                return "无伤害（倍率 " + Multiplier.ToString("0.###") + "，基础 "
                    + Base.ToString("0.###") + "）";
            }

            return (Stat + " " + Attack.ToString("0.###") + " × " + Multiplier.ToString("0.###")
                + (FlatBonus != 0f ? " + " + FlatBonus.ToString("0.###") : string.Empty)
                + " = " + Base.ToString("0.###"))
                + (Critical ? " × 暴击 " + CritFactor.ToString("0.###") : string.Empty)
                + " × 减伤 " + Mitigation.ToString("0.###")
                + (VarianceFactor != 1f ? " × 浮动 " + VarianceFactor.ToString("0.###") : string.Empty)
                + " = " + Raw.ToString("0.###")
                + " → " + Amount;
        }
    }

    /// <summary>
    /// 伤害公式的参数。做成可换的对象而不是常量，因为"换个游戏就得改公式"是常态，
    /// 而属性名必须可配 —— 框架不该规定别人叫 atk 还是 攻击力。
    /// </summary>
    public sealed class DamageProfile
    {
        /// <summary>攻击属性名。必须由攻方属性表声明，否则结算以"缺属性"返回。</summary>
        public string AttackStat = "atk";

        /// <summary>防御属性名。守方没声明时按 0 处理（无防御的召唤物/环境物件是常态）。</summary>
        public string DefenseStat = "def";

        /// <summary>暴击率属性名（0~1 的小数）。留空则整套暴击判定关闭。</summary>
        public string CritChanceStat;

        /// <summary>暴击伤害加成属性名（0.5 = +50%）。留空则暴击只有暴击率、没有额外倍率。</summary>
        public string CritDamageStat;

        /// <summary>
        /// 减伤曲线的常数 K：减伤系数 = K / (K + 防御)。
        ///
        /// 为什么不用"攻击 - 防御"：减法在高等级会突然把伤害压成 0（数值悬崖），
        /// 而比例曲线永不到 0，且 K 有明确含义 —— K 就是"减伤 50% 所需的防御值"。
        /// &lt;= 0 表示关闭减伤。
        /// </summary>
        public float MitigationConstant = 100f;

        /// <summary>伤害浮动幅度（0.1 = ±10%）。&lt;= 0 表示不浮动，也就不会消耗随机数。</summary>
        public float Variance;

        /// <summary>伤害下限。&gt; 0 时保证每一下都至少这么多（常见的"保底 1 点"）。</summary>
        public float MinimumDamage = 1f;

        /// <summary>伤害上限。&lt;= 0 表示不封顶。上限在下限之后生效。</summary>
        public float MaximumDamage;

        /// <summary>Whether crit resolution is configured at all.</summary>
        public bool Crits
        {
            get { return !string.IsNullOrEmpty(CritChanceStat); }
        }

        /// <summary>A profile with crit enabled on the conventional stat names.</summary>
        public static DamageProfile WithCrit()
        {
            DamageProfile profile = new DamageProfile();
            profile.CritChanceStat = "critChance";
            profile.CritDamageStat = "critDamage";
            return profile;
        }
    }

    /// <summary>
    /// 伤害管道：把"攻方一张属性表 + 守方一张属性表 + 一次请求"变成"一个整数伤害"。
    ///
    /// 结算顺序（固定，且每一步都留在结果里，可复算）：
    /// <code>
    /// Base      = Attack × Multiplier + FlatBonus
    /// Crit      = Base × (1 + 暴击伤害)        仅当暴击率 &gt; 0 且抽中
    /// Mitigated = Crit × K / (K + max(0, 防御))  真实伤害或 K &lt;= 0 时系数为 1
    /// Variance  = Mitigated × (1 + U(-v, v))   仅当浮动 &gt; 0
    /// Amount    = round(clamp(Variance, 下限, 上限))
    /// </code>
    ///
    /// 三个刻意的取舍：
    /// 1. **减伤用比例曲线而非减法**：减法会在高等级把伤害瞬间压到 0，比例曲线永不到 0，
    ///    且 K 有可解释的含义（"减伤 50% 所需的防御值"）；
    /// 2. **随机消费是条件性的**：暴击率 &gt; 0 才抽暴击、浮动 &gt; 0 才抽浮动，抽了多少次
    ///    如实记在 <see cref="DamageResult.Rolls"/> 里。这意味着改配置会改变随机序列 ——
    ///    但**同样的配置 + 同样的种子 + 同样的输入，永远给同样的结果**，回放与帧同步要的
    ///    正是后者；把"不消耗"做成条件是为了让"浮动 0"的战斗完全不碰随机源；
    /// 3. **攻守两侧的缺失不对称**：守方缺防御按 0 处理（无防御单位是常态），攻方缺攻击属性
    ///    则以"缺属性"返回（属性名对不上是装配错误，值得被看见）。
    ///
    /// 随机源来自 <see cref="IRandomSource"/>，战斗里换 <see cref="DeterministicRandom"/>
    /// 的独立分流（如 <c>fork("crit")</c> / <c>fork("dmg")</c>）就能让暴击序列与掉落序列互不干扰。
    /// 单线程使用（泵、主线程）。
    /// </summary>
    public sealed class DamagePipeline
    {
        private readonly DamageProfile _profile;
        private IRandomSource _random;

        /// <summary>Builds a pipeline. A null profile or random source is a wiring bug.</summary>
        public DamagePipeline(DamageProfile profile, IRandomSource random)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            if (random == null)
            {
                throw new ArgumentNullException("random");
            }

            if (string.IsNullOrEmpty(profile.AttackStat))
            {
                throw new ArgumentException(
                    "DamagePipeline: AttackStat must not be empty (the pipeline would have "
                    + "nothing to read).");
            }

            _profile = profile;
            _random = random;
        }

        /// <summary>The formula parameters.</summary>
        public DamageProfile Profile { get { return _profile; } }

        /// <summary>
        /// The random source. Swappable so a replay can drop in the recorded sequence and a
        /// server-authoritative game can drop in the server's.
        /// </summary>
        public IRandomSource Random
        {
            get { return _random; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException("value");
                }

                _random = value;
            }
        }

        /// <summary>
        /// Resolves one hit. A null sheet is a wiring bug and throws; everything the game can
        /// legitimately be in (missing attacker stat, zero multiplier, dead target) comes back
        /// as a value in <see cref="DamageResult.Outcome"/>.
        /// </summary>
        public DamageResult Resolve(StatSheet attacker, StatSheet defender, DamageRequest request)
        {
            if (attacker == null)
            {
                throw new ArgumentNullException("attacker");
            }

            if (defender == null)
            {
                throw new ArgumentNullException("defender");
            }

            DamageResult result = default(DamageResult);
            result.Stat = _profile.AttackStat;
            result.Multiplier = request.Multiplier;
            result.FlatBonus = request.FlatBonus;
            result.CritFactor = 1f;
            result.Mitigation = 1f;
            result.VarianceFactor = 1f;

            float attack;
            if (!attacker.TryValue(_profile.AttackStat, out attack))
            {
                result.Outcome = DamageOutcome.AggressorStatMissing;
                return result;
            }

            result.Attack = attack;
            result.Defense = defender.Value(_profile.DefenseStat);
            result.Base = attack * request.Multiplier + request.FlatBonus;

            if (request.Multiplier <= 0f || result.Base <= 0f)
            {
                result.Outcome = DamageOutcome.NoEffect;
                return result;
            }

            ResolveCrit(attacker, ref result);
            ResolveVariance(ref result);
            ResolveMitigation(request.TrueDamage, ref result);

            float raw = result.Base * result.CritFactor * result.Mitigation * result.VarianceFactor;

            // Raw 保留夹取前的实际内容，Amount 才是夹取后的整数：诊断时"本来该打多少"
            // 与被上下限改写过的结果必须能分开看，否则封顶会把真实强度藏起来。
            result.Raw = raw;
            result.Amount = ToWholeNumber(ApplyBounds(raw));
            result.Outcome = DamageOutcome.Applied;
            return result;
        }

        /// <summary>
        /// Subtracts a resolved hit from a health stat on the target's sheet.
        ///
        /// 生命可以是负数 —— 框架不替游戏规定"0 就是死"，上层看 <c>Value("hp") &lt;= 0</c>
        /// 自己决定。目标已死（生命 ≤ 0）或没声明生命属性时返回 false 并给出原因，
        /// 因为"往尸体上补刀"是正常的游戏状态，不是异常。
        /// </summary>
        public bool TryApplyDamage(
            StatSheet target, string healthStat, DamageResult result, out float remaining, out string error)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }

            remaining = 0f;
            if (string.IsNullOrEmpty(healthStat))
            {
                error = "health stat id must not be empty";
                return false;
            }

            if (result.Outcome != DamageOutcome.Applied)
            {
                error = "the hit was not resolved (" + result.Outcome + "), nothing to apply";
                return false;
            }

            float baseHealth;
            if (!target.TryValue(healthStat, out baseHealth))
            {
                error = "target does not declare '" + healthStat + "'";
                return false;
            }

            if (baseHealth <= 0f)
            {
                error = "target is already at or below zero health";
                return false;
            }

            error = null;
            // 扣的是 base 而不是求值后的值：修饰符（护甲、增益）还要继续作用在结果上。
            target.SetBase(healthStat, target.BaseValue(healthStat) - result.Amount);
            remaining = target.Value(healthStat);
            return true;
        }

        private void ResolveCrit(StatSheet attacker, ref DamageResult result)
        {
            if (!_profile.Crits)
            {
                return;
            }

            float chance = attacker.Value(_profile.CritChanceStat);
            if (chance <= 0f)
            {
                return;
            }

            // 显式抽一个 [0,1) 再比，而不是调 Chance()：Chance() 在 p >= 1 时直接返回 true
            // 且**不消耗**随机数，那样 Rolls 就不等于"实际抽了几次"，这条可检查项的不变量会碎。
            result.Rolls++;
            if (_random.NextUnit() >= chance)
            {
                return;
            }

            result.Critical = true;
            float bonus = string.IsNullOrEmpty(_profile.CritDamageStat)
                ? 0f
                : attacker.Value(_profile.CritDamageStat);

            // 负的暴击伤害不许把暴击变成"打得更少"：暴击至少不该有惩罚。
            result.CritFactor = bonus > 0f ? 1f + bonus : 1f;
        }

        private void ResolveVariance(ref DamageResult result)
        {
            if (_profile.Variance <= 0f)
            {
                return;
            }

            result.Rolls++;
            result.VarianceFactor = 1f + _random.Range(-_profile.Variance, _profile.Variance);
        }

        private void ResolveMitigation(bool trueDamage, ref DamageResult result)
        {
            if (trueDamage || _profile.MitigationConstant <= 0f)
            {
                result.Mitigation = 1f;
                return;
            }

            float defense = result.Defense < 0f ? 0f : result.Defense;
            result.Mitigation = _profile.MitigationConstant / (_profile.MitigationConstant + defense);
        }

        private float ApplyBounds(float raw)
        {
            if (_profile.MinimumDamage > 0f && raw < _profile.MinimumDamage)
            {
                raw = _profile.MinimumDamage;
            }

            if (_profile.MaximumDamage > 0f && raw > _profile.MaximumDamage)
            {
                raw = _profile.MaximumDamage;
            }

            return raw;
        }

        /// <summary>
        /// Rounds half up. Explicit rather than Math.Round because the default float overload
        /// is banker's rounding, and "0.5 becomes 0" is not what a damage formula means.
        /// </summary>
        private static int ToWholeNumber(float value)
        {
            return (int)Math.Floor(value + 0.5f);
        }
    }
}
