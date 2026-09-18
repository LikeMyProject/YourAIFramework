using System;
using System.Collections.Generic;
using System.Globalization;
using YourAI.Core.Json;

namespace YourFramework.Skills
{
    /// <summary>
    /// 技能对谁生效。故意只保留这四种 —— 它们都能用纯逻辑（只看候选列表，不查场景、
    /// 不查物理、不查寻路）算出来。一旦把目标选择做成插件系统，配置表就没法在构建期
    /// 校验了：表里写了个拼错的选择器，要等到玩家按下技能才发现。
    /// 更复杂的选择（锥形范围、连锁跳跃）由调用方先把候选列表筛好再传进来。
    /// </summary>
    public enum SkillTargeting
    {
        /// <summary>只对自己。</summary>
        Self,

        /// <summary>由施法者指定的单个目标（必须存在于候选列表里）。</summary>
        SingleTarget,

        /// <summary>所有敌对目标。</summary>
        AllEnemies,

        /// <summary>血量比例最低的存活友军（并列时取候选列表里靠前的那个，保证确定性）。</summary>
        LowestHealthAlly
    }

    /// <summary>技能怎么出效果。</summary>
    public enum SkillDelivery
    {
        /// <summary>当帧结算。</summary>
        Instant,

        /// <summary>先吟唱一段时间，时间到了再结算。</summary>
        Cast
    }

    /// <summary>一条效果的落地方式。落地的具体实现由 <c>ISkillEffectSink</c> 提供。</summary>
    public enum SkillEffectKind
    {
        /// <summary>按属性算伤害。</summary>
        Damage,

        /// <summary>按属性算治疗。</summary>
        Heal,

        /// <summary>施加一个持续效果（改属性、可跳伤害/治疗）。</summary>
        ApplyEffect
    }

    /// <summary>
    /// 效果叠加方式。**故意不复用数值层的 EffectStackPolicy**：技能表是配置格式，
    /// 把数值层枚举写进配置意味着"换一种数值实现就得改所有配表"。两者在
    /// SkillStatsBridge 里做一次显式映射，映射点只有一个，改起来有据可依。
    /// </summary>
    public enum SkillStackPolicy
    {
        /// <summary>重复施加刷新时长，不叠层。</summary>
        Refresh,

        /// <summary>重复施加叠层，受上限约束。</summary>
        Stack,

        /// <summary>各自独立计时。</summary>
        Independent
    }

    /// <summary>
    /// 改属性的三种方式。**和数值层的 StatKind 是同一个三分法，但仍然是两个枚举**：
    /// 配置格式不该被实现细节钉住。真正约束它们一致的是桥里的映射表 —— 那里少映射一个
    /// 分支，编译器和断言都会叫（映射用 switch 穷举，没有 default 兜底）。
    /// </summary>
    public enum SkillStatKind
    {
        /// <summary>加到基础值上。</summary>
        Flat,

        /// <summary>先相加再一次性乘。</summary>
        PercentAdd,

        /// <summary>各自连乘。</summary>
        PercentMul
    }

    /// <summary>一条属性改动：改哪个属性、怎么改、改多少。</summary>
    public sealed class SkillModifier
    {
        public readonly string Stat;
        public readonly SkillStatKind Kind;
        public readonly float Value;

        public SkillModifier(string stat, SkillStatKind kind, float value)
        {
            Stat = stat;
            Kind = kind;
            Value = value;
        }

        public override string ToString()
        {
            return Stat + " " + Kind + " " + Value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>一项资源消耗。资源名就是属性名（例如 "mp"），所以扣减最终落到属性表上。</summary>
    public struct SkillCost
    {
        public string Resource;
        public float Amount;

        public SkillCost(string resource, float amount)
        {
            Resource = resource;
            Amount = amount;
        }

        public override string ToString()
        {
            return Resource + " x" + Amount.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 一条技能效果。字段是**并集**而不是继承树：配置格式一旦分层（damage 节点 / heal 节点 /
    /// buff 节点），解析器就得按 kind 分派三套校验，而写出/回读还要保证三种形状都能还原。
    /// 用并集 + 按 kind 校验该有的字段，模型只有一个，序列化也就一种。
    /// </summary>
    public sealed class SkillEffect
    {
        /// <summary>效果类型。</summary>
        public readonly SkillEffectKind Kind;

        /// <summary>伤害/治疗读哪个属性（damage 通常是攻方属性名，heal 通常是生命属性名）。</summary>
        public string Stat;

        /// <summary>倍率。</summary>
        public float Multiplier;

        /// <summary>固定加值。</summary>
        public float FlatBonus;

        /// <summary>ApplyEffect 用的效果 id。</summary>
        public string EffectId;

        /// <summary>ApplyEffect 的持续时长（&lt;= 0 表示永久）。</summary>
        public float Duration;

        /// <summary>ApplyEffect 的跳间隔（&lt;= 0 表示不跳，只改属性）。</summary>
        public float TickInterval;

        /// <summary>ApplyEffect 的叠层上限。</summary>
        public int MaxStacks;

        /// <summary>ApplyEffect 的叠加方式。</summary>
        public SkillStackPolicy StackPolicy;

        /// <summary>ApplyEffect 要改的属性（可以为空：纯计时标记效果）。</summary>
        public List<SkillModifier> Modifiers;

        public SkillEffect(SkillEffectKind kind)
        {
            Kind = kind;
            Stat = string.Empty;
            EffectId = string.Empty;
            Multiplier = 0f;
            FlatBonus = 0f;
            Duration = 0f;
            TickInterval = 0f;
            MaxStacks = 1;
            StackPolicy = SkillStackPolicy.Refresh;
            Modifiers = new List<SkillModifier>();
        }

        /// <summary>Human-readable one-liner for logs and diagnostics.</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case SkillEffectKind.Damage:
                    return "damage " + Stat + " x" + Multiplier.ToString("0.###", CultureInfo.InvariantCulture)
                        + (FlatBonus != 0f ? " +" + FlatBonus.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty);
                case SkillEffectKind.Heal:
                    return "heal " + Stat + " x" + Multiplier.ToString("0.###", CultureInfo.InvariantCulture)
                        + (FlatBonus != 0f ? " +" + FlatBonus.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty);
                default:
                    return "apply '" + EffectId + "' for " + Duration.ToString("0.###", CultureInfo.InvariantCulture) + "s";
            }
        }
    }

    /// <summary>
    /// 一条技能的**只读定义**，全部来自配置。
    ///
    /// 这个类型的存在理由就是「配置驱动」四个字：代码里不该出现任何具体技能的名字、
    /// 数字和资源地址。加一个技能 = 加一段 JSON，不改代码、不改包、不重新出客户端
    /// （配合热更，连版本都能省）。
    ///
    /// 错误姿态：技能表是构建期产物，**结构错误一律 fail-fast**（未知枚举、负数时长、
    /// 自相矛盾的写法、空效果列表）—— 启动就炸好过打到一半发现"这个技能没伤害"。
    /// 运行期的"打不出来"（冷却中、资源不够、目标没了）是**值**，走 SkillCastOutcome。
    ///
    /// 线程：构造后只读，可并发读。
    /// </summary>
    public sealed class SkillDef
    {
        /// <summary>技能 id，全局唯一，同时是施法时的索引键。</summary>
        public readonly string Id;

        /// <summary>显示名（缺省等于 Id）。</summary>
        public readonly string Name;

        /// <summary>瞬发还是吟唱。</summary>
        public readonly SkillDelivery Delivery;

        /// <summary>吟唱时长（秒）。瞬发时为 0。</summary>
        public readonly float CastTime;

        /// <summary>冷却时长（秒）。0 表示无冷却。</summary>
        public readonly float Cooldown;

        /// <summary>充能数（默认 1）。</summary>
        public readonly int Charges;

        /// <summary>单个充能的恢复时长；0 表示用 Cooldown 当恢复时长。</summary>
        public readonly float ChargeRecovery;

        /// <summary>目标选择方式。</summary>
        public readonly SkillTargeting Targeting;

        /// <summary>射程（数据，供调用方筛候选；本模块不查空间）。</summary>
        public readonly float Range;

        /// <summary>资源消耗清单。</summary>
        public readonly List<SkillCost> Costs;

        /// <summary>效果清单（至少一条）。</summary>
        public readonly List<SkillEffect> Effects;

        /// <summary>逻辑名 → 地址（例如 vfx / icon / sfx）。地址的解析交给地址目录表。</summary>
        public readonly Dictionary<string, string> Assets;

        /// <summary>施法时调用的脚本入口名（热更钩子，可空）。</summary>
        public readonly string ScriptHook;

        /// <summary>前置技能 id（技能树用，可空）。</summary>
        public readonly string Requires;

        private SkillDef(
            string id,
            string name,
            SkillDelivery delivery,
            float castTime,
            float cooldown,
            int charges,
            float chargeRecovery,
            SkillTargeting targeting,
            float range,
            List<SkillCost> costs,
            List<SkillEffect> effects,
            Dictionary<string, string> assets,
            string scriptHook,
            string requires)
        {
            Id = id;
            Name = name;
            Delivery = delivery;
            CastTime = castTime;
            Cooldown = cooldown;
            Charges = charges;
            ChargeRecovery = chargeRecovery;
            Targeting = targeting;
            Range = range;
            Costs = costs;
            Effects = effects;
            Assets = assets;
            ScriptHook = scriptHook;
            Requires = requires;
        }

        /// <summary>Whether this skill needs a cast time before it lands.</summary>
        public bool IsCast { get { return Delivery == SkillDelivery.Cast; } }

        /// <summary>Whether this skill has more than one charge (so cooldown is per-charge).</summary>
        public bool IsCharged { get { return Charges > 1; } }

        /// <summary>Time to restore one charge (falls back to the cooldown when unspecified).</summary>
        public float RechargeTime { get { return ChargeRecovery > 0f ? ChargeRecovery : Cooldown; } }

        /// <summary>Looks up a logical asset address (null when the skill declares none).</summary>
        public string AssetAddress(string logicalName)
        {
            if (logicalName == null)
            {
                throw new ArgumentNullException("logicalName");
            }

            string address;
            return Assets.TryGetValue(logicalName, out address) ? address : null;
        }

        /// <summary>Human-readable one-liner for logs and diagnostics.</summary>
        public string Describe()
        {
            return Id + " (" + Delivery + (IsCast ? " " + CastTime.ToString("0.##", CultureInfo.InvariantCulture) + "s" : string.Empty)
                + ", cd " + Cooldown.ToString("0.##", CultureInfo.InvariantCulture)
                + (IsCharged ? ", " + Charges + " charges" : string.Empty)
                + ", " + Targeting + ")";
        }

        // ------------------------------------------------------------------ 解析

        /// <summary>
        /// Parses one skill definition. Every structural problem throws
        /// (build-time artifact, fail-fast); the message carries the row index so a bad
        /// table is locatable without a debugger.
        /// </summary>
        public static SkillDef FromValue(JsonValue row, int index)
        {
            if (row == null || row.Kind != JsonKind.Object)
            {
                throw new ArgumentException("SkillDef: entry " + index + " is not a JSON object.");
            }

            string where = "entry " + index;
            string id = RequireToken(row, "id", where);

            string name = OptionalString(row, "name");
            if (string.IsNullOrEmpty(name))
            {
                name = id;
            }

            float castTime = OptionalFloat(row, "cast_time", 0f);
            if (castTime < 0f)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' has a negative cast_time (" + castTime + ").");
            }

            // delivery 可以省略，由 cast_time 推断；但显式写错就不放过 ——
            // 「instant + cast_time > 0」是一张自相矛盾的表，两种写法只能有一种意思。
            SkillDelivery delivery;
            string deliveryText = OptionalString(row, "delivery");
            if (string.IsNullOrEmpty(deliveryText))
            {
                delivery = castTime > 0f ? SkillDelivery.Cast : SkillDelivery.Instant;
            }
            else if (!TryParseDelivery(deliveryText, out delivery))
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' has an unknown delivery '" + deliveryText
                    + "' (expected instant or cast).");
            }

            if (delivery == SkillDelivery.Instant && castTime > 0f)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' says delivery is instant but asks for cast_time "
                    + castTime + "s -- contradicting declarations, pick one.");
            }

            if (delivery == SkillDelivery.Cast && castTime <= 0f)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' says delivery is cast but has no cast_time.");
            }

            float cooldown = OptionalFloat(row, "cooldown", 0f);
            if (cooldown < 0f)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' has a negative cooldown (" + cooldown + ").");
            }

            int charges = OptionalInt(row, "charges", 1);
            if (charges < 1)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' declares " + charges + " charges (must be at least 1).");
            }

            float chargeRecovery = OptionalFloat(row, "charge_recovery", 0f);
            if (chargeRecovery < 0f)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' has a negative charge_recovery (" + chargeRecovery + ").");
            }

            if (charges > 1 && chargeRecovery <= 0f && cooldown <= 0f)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' has " + charges
                    + " charges but no way to restore them (charge_recovery and cooldown are both 0)"
                    + " -- the skill would be spent forever after " + charges + " casts.");
            }

            SkillTargeting targeting;
            string targetingText = OptionalString(row, "targeting");
            if (string.IsNullOrEmpty(targetingText))
            {
                targeting = SkillTargeting.SingleTarget;
            }
            else if (!TryParseTargeting(targetingText, out targeting))
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' has an unknown targeting '" + targetingText + "'.");
            }

            float range = OptionalFloat(row, "range", 0f);
            if (range < 0f)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' has a negative range (" + range + ").");
            }

            string hook = OptionalString(row, "hook");
            if (hook != null && hook.Length == 0)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' has an empty hook name (omit the field instead).");
            }

            if (hook != null && HasWhitespace(hook))
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' has a hook name with whitespace ('" + hook + "').");
            }

            string requires = OptionalString(row, "requires");
            if (requires != null && requires.Length == 0)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' has an empty 'requires' (omit the field instead).");
            }

            if (string.Equals(requires, id, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' requires itself.");
            }

            return new SkillDef(
                id,
                name,
                delivery,
                castTime,
                cooldown,
                charges,
                chargeRecovery,
                targeting,
                range,
                ParseCosts(row, id),
                ParseEffects(row, id),
                ParseAssets(row, id),
                hook,
                requires);
        }

        private static List<SkillCost> ParseCosts(JsonValue row, string id)
        {
            List<SkillCost> costs = new List<SkillCost>();

            JsonValue rows = row["costs"];
            if (rows == null || rows.Kind == JsonKind.Null)
            {
                return costs;
            }

            if (rows.Kind != JsonKind.Array)
            {
                throw new ArgumentException("SkillDef: '" + id + "' has a non-array 'costs'.");
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Items.Count; i++)
            {
                JsonValue costRow = rows.Items[i];
                if (costRow == null || costRow.Kind != JsonKind.Object)
                {
                    throw new ArgumentException(
                        "SkillDef: cost " + i + " of '" + id + "' is not a JSON object.");
                }

                string resource = RequireToken(costRow, "resource", "cost " + i + " of '" + id + "'");
                float amount = OptionalFloat(costRow, "amount", 0f);
                if (amount <= 0f)
                {
                    throw new ArgumentException(
                        "SkillDef: cost " + i + " of '" + id + "' has a non-positive amount ("
                        + amount + ").");
                }

                if (!seen.Add(resource))
                {
                    throw new ArgumentException(
                        "SkillDef: '" + id + "' charges '" + resource
                        + "' twice; merge the two cost rows into one.");
                }

                costs.Add(new SkillCost(resource, amount));
            }

            return costs;
        }

        private static List<SkillEffect> ParseEffects(JsonValue row, string id)
        {
            JsonValue rows = row["effects"];
            if (rows == null || rows.Kind != JsonKind.Array || rows.Items.Count == 0)
            {
                throw new ArgumentException(
                    "SkillDef: '" + id + "' declares no effects (a skill that does nothing).");
            }

            List<SkillEffect> effects = new List<SkillEffect>(rows.Items.Count);
            for (int i = 0; i < rows.Items.Count; i++)
            {
                JsonValue effectRow = rows.Items[i];
                if (effectRow == null || effectRow.Kind != JsonKind.Object)
                {
                    throw new ArgumentException(
                        "SkillDef: effect " + i + " of '" + id + "' is not a JSON object.");
                }

                effects.Add(ParseEffect(effectRow, id, i));
            }

            return effects;
        }

        private static SkillEffect ParseEffect(JsonValue row, string id, int index)
        {
            string where = "effect " + index + " of '" + id + "'";
            string kindText = RequireToken(row, "kind", where);

            SkillEffectKind kind;
            if (!TryParseEffectKind(kindText, out kind))
            {
                throw new ArgumentException(
                    "SkillDef: " + where + " has an unknown kind '" + kindText
                    + "' (expected damage, heal or apply_effect).");
            }

            SkillEffect effect = new SkillEffect(kind);

            if (kind == SkillEffectKind.ApplyEffect)
            {
                effect.EffectId = RequireToken(row, "effect_id", where);

                effect.Duration = OptionalFloat(row, "duration", 0f);
                if (effect.Duration < 0f)
                {
                    throw new ArgumentException(
                        "SkillDef: " + where + " has a negative duration (" + effect.Duration + ").");
                }

                effect.TickInterval = OptionalFloat(row, "tick_interval", 0f);
                if (effect.TickInterval < 0f)
                {
                    throw new ArgumentException(
                        "SkillDef: " + where + " has a negative tick_interval ("
                        + effect.TickInterval + ").");
                }

                effect.MaxStacks = OptionalInt(row, "max_stacks", 1);
                if (effect.MaxStacks < 0)
                {
                    throw new ArgumentException(
                        "SkillDef: " + where + " has a negative max_stacks (" + effect.MaxStacks + ").");
                }

                SkillStackPolicy policy;
                string policyText = OptionalString(row, "stack");
                if (string.IsNullOrEmpty(policyText))
                {
                    policy = SkillStackPolicy.Refresh;
                }
                else if (!TryParseStackPolicy(policyText, out policy))
                {
                    throw new ArgumentException(
                        "SkillDef: " + where + " has an unknown stack policy '" + policyText
                        + "' (expected refresh, stack or independent).");
                }

                effect.StackPolicy = policy;
                ParseModifiers(row, effect, where);
                return effect;
            }

            effect.Stat = RequireToken(row, "stat", where);
            effect.Multiplier = OptionalFloat(row, "multiplier", 0f);
            effect.FlatBonus = OptionalFloat(row, "flat_bonus", 0f);

            // 倍率和加值都是 0 = 这条效果算出来永远是 0 —— 配置写漏了一个字段的样子，
            // 不该等到线上打出一串 0 才发现。
            if (effect.Multiplier == 0f && effect.FlatBonus == 0f)
            {
                throw new ArgumentException(
                    "SkillDef: " + where + " has neither multiplier nor flat_bonus; it would always "
                    + "compute zero.");
            }

            return effect;
        }

        private static void ParseModifiers(JsonValue row, SkillEffect effect, string where)
        {
            JsonValue rows = row["modifiers"];
            if (rows == null || rows.Kind == JsonKind.Null)
            {
                return;
            }

            if (rows.Kind != JsonKind.Array)
            {
                throw new ArgumentException("SkillDef: " + where + " has a non-array 'modifiers'.");
            }

            for (int i = 0; i < rows.Items.Count; i++)
            {
                JsonValue modifierRow = rows.Items[i];
                if (modifierRow == null || modifierRow.Kind != JsonKind.Object)
                {
                    throw new ArgumentException(
                        "SkillDef: modifier " + i + " of " + where + " is not a JSON object.");
                }

                string stat = RequireToken(modifierRow, "stat", "modifier " + i + " of " + where);

                SkillStatKind kind;
                string kindText = OptionalString(modifierRow, "kind");
                if (string.IsNullOrEmpty(kindText))
                {
                    kind = SkillStatKind.Flat;
                }
                else if (!TryParseStatKind(kindText, out kind))
                {
                    throw new ArgumentException(
                        "SkillDef: modifier " + i + " of " + where + " has an unknown kind '"
                        + kindText + "' (expected flat, percent_add or percent_mul).");
                }

                // 改零等于没写这条 —— 多半是漏填了 value，早点炸。
                float value = OptionalFloat(modifierRow, "value", 0f);
                if (value == 0f)
                {
                    throw new ArgumentException(
                        "SkillDef: modifier " + i + " of " + where + " changes '" + stat
                        + "' by zero.");
                }

                effect.Modifiers.Add(new SkillModifier(stat, kind, value));
            }
        }

        private static Dictionary<string, string> ParseAssets(JsonValue row, string id)
        {
            Dictionary<string, string> assets = new Dictionary<string, string>(StringComparer.Ordinal);

            JsonValue rows = row["assets"];
            if (rows == null || rows.Kind == JsonKind.Null)
            {
                return assets;
            }

            if (rows.Kind != JsonKind.Object)
            {
                throw new ArgumentException("SkillDef: '" + id + "' has a non-object 'assets'.");
            }

            foreach (KeyValuePair<string, JsonValue> pair in rows.Members)
            {
                if (string.IsNullOrEmpty(pair.Key) || HasWhitespace(pair.Key))
                {
                    throw new ArgumentException(
                        "SkillDef: '" + id + "' has an asset logical name that is empty or has "
                        + "whitespace ('" + pair.Key + "').");
                }

                JsonValue value = pair.Value;
                string address = value == null || value.Kind == JsonKind.Null ? null : value.AsString(null);
                if (string.IsNullOrEmpty(address))
                {
                    throw new ArgumentException(
                        "SkillDef: '" + id + "' maps asset '" + pair.Key + "' to an empty address.");
                }

                assets.Add(pair.Key, address);
            }

            return assets;
        }

        // ------------------------------------------------------------- 枚举与取值

        /// <summary>Parses the delivery token. Unknown tokens are rejected by the caller.</summary>
        public static bool TryParseDelivery(string text, out SkillDelivery delivery)
        {
            switch (text)
            {
                case "instant":
                    delivery = SkillDelivery.Instant;
                    return true;
                case "cast":
                    delivery = SkillDelivery.Cast;
                    return true;
                default:
                    delivery = SkillDelivery.Instant;
                    return false;
            }
        }

        /// <summary>Parses the targeting token.</summary>
        public static bool TryParseTargeting(string text, out SkillTargeting targeting)
        {
            switch (text)
            {
                case "self":
                    targeting = SkillTargeting.Self;
                    return true;
                case "single":
                    targeting = SkillTargeting.SingleTarget;
                    return true;
                case "all_enemies":
                    targeting = SkillTargeting.AllEnemies;
                    return true;
                case "lowest_health_ally":
                    targeting = SkillTargeting.LowestHealthAlly;
                    return true;
                default:
                    targeting = SkillTargeting.SingleTarget;
                    return false;
            }
        }

        /// <summary>Parses the effect kind token.</summary>
        public static bool TryParseEffectKind(string text, out SkillEffectKind kind)
        {
            switch (text)
            {
                case "damage":
                    kind = SkillEffectKind.Damage;
                    return true;
                case "heal":
                    kind = SkillEffectKind.Heal;
                    return true;
                case "apply_effect":
                    kind = SkillEffectKind.ApplyEffect;
                    return true;
                default:
                    kind = SkillEffectKind.Damage;
                    return false;
            }
        }

        /// <summary>Parses the stack policy token.</summary>
        public static bool TryParseStackPolicy(string text, out SkillStackPolicy policy)
        {
            switch (text)
            {
                case "refresh":
                    policy = SkillStackPolicy.Refresh;
                    return true;
                case "stack":
                    policy = SkillStackPolicy.Stack;
                    return true;
                case "independent":
                    policy = SkillStackPolicy.Independent;
                    return true;
                default:
                    policy = SkillStackPolicy.Refresh;
                    return false;
            }
        }

        /// <summary>Token form of the delivery (the form the writer emits).</summary>
        public static string DeliveryToken(SkillDelivery delivery)
        {
            return delivery == SkillDelivery.Cast ? "cast" : "instant";
        }

        /// <summary>Token form of the targeting (the form the writer emits).</summary>
        public static string TargetingToken(SkillTargeting targeting)
        {
            switch (targeting)
            {
                case SkillTargeting.Self:
                    return "self";
                case SkillTargeting.AllEnemies:
                    return "all_enemies";
                case SkillTargeting.LowestHealthAlly:
                    return "lowest_health_ally";
                default:
                    return "single";
            }
        }

        /// <summary>Token form of the effect kind (the form the writer emits).</summary>
        public static string EffectKindToken(SkillEffectKind kind)
        {
            switch (kind)
            {
                case SkillEffectKind.Heal:
                    return "heal";
                case SkillEffectKind.ApplyEffect:
                    return "apply_effect";
                default:
                    return "damage";
            }
        }

        /// <summary>Token form of the stack policy (the form the writer emits).</summary>
        public static string StackPolicyToken(SkillStackPolicy policy)
        {
            switch (policy)
            {
                case SkillStackPolicy.Stack:
                    return "stack";
                case SkillStackPolicy.Independent:
                    return "independent";
                default:
                    return "refresh";
            }
        }

        /// <summary>Parses the stat-modifier kind token.</summary>
        public static bool TryParseStatKind(string text, out SkillStatKind kind)
        {
            switch (text)
            {
                case "flat":
                    kind = SkillStatKind.Flat;
                    return true;
                case "percent_add":
                    kind = SkillStatKind.PercentAdd;
                    return true;
                case "percent_mul":
                    kind = SkillStatKind.PercentMul;
                    return true;
                default:
                    kind = SkillStatKind.Flat;
                    return false;
            }
        }

        /// <summary>Token form of the stat-modifier kind (the form the writer emits).</summary>
        public static string StatKindToken(SkillStatKind kind)
        {
            switch (kind)
            {
                case SkillStatKind.PercentAdd:
                    return "percent_add";
                case SkillStatKind.PercentMul:
                    return "percent_mul";
                default:
                    return "flat";
            }
        }

        // ------------------------------------------------------------ 解析小工具

        /// <summary>Whether the text contains any whitespace (used by token-shaped fields).</summary>
        public static bool HasWhitespace(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static string RequireToken(JsonValue row, string field, string where)
        {
            string text = OptionalString(row, field);
            if (string.IsNullOrEmpty(text))
            {
                throw new ArgumentException(
                    "SkillDef: " + where + " is missing a non-empty '" + field + "'.");
            }

            if (HasWhitespace(text))
            {
                throw new ArgumentException(
                    "SkillDef: " + where + " has whitespace in '" + field + "' ('" + text + "').");
            }

            return text;
        }

        private static string OptionalString(JsonValue row, string field)
        {
            JsonValue value = row[field];
            return value == null || value.Kind == JsonKind.Null ? null : value.AsString(null);
        }

        private static float OptionalFloat(JsonValue row, string field, float fallback)
        {
            JsonValue value = row[field];
            if (value == null || value.Kind == JsonKind.Null)
            {
                return fallback;
            }

            if (value.Kind != JsonKind.Number)
            {
                throw new ArgumentException(
                    "SkillDef: '" + field + "' must be a number (got " + value.Kind + ").");
            }

            return (float)value.NumberValue;
        }

        private static int OptionalInt(JsonValue row, string field, int fallback)
        {
            JsonValue value = row[field];
            if (value == null || value.Kind == JsonKind.Null)
            {
                return fallback;
            }

            if (value.Kind != JsonKind.Number)
            {
                throw new ArgumentException(
                    "SkillDef: '" + field + "' must be a number (got " + value.Kind + ").");
            }

            double raw = value.NumberValue;
            if (raw != Math.Floor(raw))
            {
                throw new ArgumentException(
                    "SkillDef: '" + field + "' must be a whole number (got " + raw + ").");
            }

            return (int)raw;
        }
    }
}
