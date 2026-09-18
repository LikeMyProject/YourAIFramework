using System;
using System.Collections.Generic;
using System.Text;
using YourAI.Core.Json;
using YourFramework.Catalog;

namespace YourFramework.Skills
{
    /// <summary>技能表的一条问题。</summary>
    public sealed class SkillIssue
    {
        public readonly string Severity;
        public readonly string SkillId;
        public readonly string Message;

        public SkillIssue(string severity, string skillId, string message)
        {
            Severity = severity;
            SkillId = skillId;
            Message = message;
        }

        public override string ToString()
        {
            return Severity + "  " + SkillId + "  " + Message;
        }
    }

    /// <summary>
    /// 技能表的校验结果。与 <c>CatalogReport</c> 同构：Error 拦构建，Warning 只提醒。
    /// 分级的判断标准是一致的 —— **「到了线上会静默错」才是 Error**。
    /// </summary>
    public sealed class SkillReport
    {
        private readonly List<SkillIssue> _issues = new List<SkillIssue>();

        public int IssueCount { get { return _issues.Count; } }

        public int ErrorCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _issues.Count; i++)
                {
                    if (_issues[i].Severity == "error")
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public int WarningCount { get { return IssueCount - ErrorCount; } }

        public bool IsClean { get { return ErrorCount == 0; } }

        public List<SkillIssue> Issues { get { return new List<SkillIssue>(_issues); } }

        public void Error(string skillId, string message)
        {
            _issues.Add(new SkillIssue("error", skillId, message));
        }

        public void Warn(string skillId, string message)
        {
            _issues.Add(new SkillIssue("warning", skillId, message));
        }

        public string Summary()
        {
            return "skill table: " + ErrorCount + " error(s), " + WarningCount + " warning(s)";
        }

        public string Dump()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Summary());
            for (int i = 0; i < _issues.Count; i++)
            {
                sb.Append('\n').Append(_issues[i].ToString());
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// 技能表：一堆 <see cref="SkillDef"/> 加上它们之间的技能树关系，以及"技能要用的资源
    /// 地址能不能解析出来"这条跨模块校验。
    ///
    /// 为什么技能表要认识地址目录表：技能配置里写的是**逻辑名**（vfx / icon / sfx），
    /// 真正打到哪个包、画质档取哪个变体，是地址目录表的事。两者在这里对接 —— 单向依赖
    /// （Skills → Catalog），目录表不认识技能，无环。
    ///
    /// 出错时的处理（与 DataTable / AddressCatalog 一致）：
    ///   - **结构错误 fail-fast**：坏 JSON、重复 id、前置技能不存在、技能树成环；
    ///   - **交叉校验出报告**：地址解析不出来是 Error（到了线上就是空特效），
    ///     技能没挂任何资源是 Warning（可能是有意为之，比如纯数值被动）。
    /// </summary>
    public sealed class SkillLibrary
    {
        private readonly Dictionary<string, SkillDef> _byId =
            new Dictionary<string, SkillDef>(StringComparer.Ordinal);

        /// <summary>书写顺序（写出时按 id 重排，这里保留是为了诊断可读）。</summary>
        private readonly List<string> _order = new List<string>();

        private SkillLibrary()
        {
        }

        /// <summary>An empty library.</summary>
        public static SkillLibrary Empty()
        {
            return new SkillLibrary();
        }

        /// <summary>Skill count.</summary>
        public int Count { get { return _byId.Count; } }

        /// <summary>Ids in declaration order (a copy).</summary>
        public List<string> Ids { get { return new List<string>(_order); } }

        /// <summary>Ids sorted ordinal -- the order the writer emits and the natural order to diff.</summary>
        public List<string> SortedIds()
        {
            List<string> ids = new List<string>(_order);
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        // ------------------------------------------------------------------ 解析

        /// <summary>
        /// Parses a skill table document. Structural problems throw; parser exceptions are
        /// normalized to <see cref="ArgumentException"/> so callers see one error shape.
        /// </summary>
        public static SkillLibrary FromJson(string json)
        {
            JsonValue parsed;
            try
            {
                parsed = JsonParser.Parse(json);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("SkillLibrary: invalid JSON: " + ex.Message, ex);
            }

            return FromValue(parsed);
        }

        /// <summary>Same contract as <see cref="FromJson"/>, from an already-parsed root.</summary>
        public static SkillLibrary FromValue(JsonValue root)
        {
            if (root == null || root.Kind != JsonKind.Object)
            {
                throw new ArgumentException("SkillLibrary: root must be a JSON object.");
            }

            JsonValue rows = root["skills"];
            if (rows == null || rows.Kind != JsonKind.Array)
            {
                throw new ArgumentException(
                    "SkillLibrary: root must carry a 'skills' array.");
            }

            SkillLibrary library = new SkillLibrary();
            for (int i = 0; i < rows.Items.Count; i++)
            {
                SkillDef def = SkillDef.FromValue(rows.Items[i], i);
                if (library._byId.ContainsKey(def.Id))
                {
                    throw new ArgumentException(
                        "SkillLibrary: duplicate skill id '" + def.Id + "' (entries are 0-based).");
                }

                library._byId.Add(def.Id, def);
                library._order.Add(def.Id);
            }

            library.CheckTree();
            return library;
        }

        /// <summary>
        /// 技能树必须是**有根的森林**：前置不存在是笔误，成环则永远点不出来。
        /// 两者都在加载期炸掉 —— 这类错误在运行期的表现是「这个技能灰着」，
        /// 几乎不可能被玩家或测试报出来。
        /// </summary>
        private void CheckTree()
        {
            for (int i = 0; i < _order.Count; i++)
            {
                SkillDef def = _byId[_order[i]];
                if (def.Requires != null && !_byId.ContainsKey(def.Requires))
                {
                    throw new ArgumentException(
                        "SkillLibrary: '" + def.Id + "' requires '" + def.Requires
                        + "', which is not in the table.");
                }
            }

            // 三态标记（0 = 未访问，1 = 在栈上，2 = 已完成）：只有"在栈上"才是环。
            // 用三态而不是两态，是因为两态分不出"环"和"共享的公共前置"，会把好表判死。
            Dictionary<string, int> marks = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _order.Count; i++)
            {
                Walk(_order[i], marks);
            }
        }

        private void Walk(string id, Dictionary<string, int> marks)
        {
            int mark;
            if (marks.TryGetValue(id, out mark))
            {
                if (mark == 2)
                {
                    return;
                }

                throw new ArgumentException(
                    "SkillLibrary: the 'requires' chain of '" + id + "' forms a cycle.");
            }

            marks[id] = 1;
            SkillDef def = _byId[id];
            if (def.Requires != null)
            {
                Walk(def.Requires, marks);
            }

            marks[id] = 2;
        }

        // ------------------------------------------------------------------ 查询

        /// <summary>Looks up a skill. A missing id is a value, not a throw.</summary>
        public bool TryGet(string id, out SkillDef def)
        {
            if (id == null)
            {
                throw new ArgumentNullException("id");
            }

            return _byId.TryGetValue(id, out def);
        }

        /// <summary>Looks up a skill; a missing id yields null.</summary>
        public SkillDef Get(string id)
        {
            SkillDef def;
            return TryGet(id, out def) ? def : null;
        }

        /// <summary>An unknown id here is a wiring bug (the caller never asked the user), so it throws.</summary>
        public SkillDef Require(string id)
        {
            SkillDef def = Get(id);
            if (def == null)
            {
                throw new KeyNotFoundException("SkillLibrary: no skill named '" + id + "'.");
            }

            return def;
        }

        /// <summary>All skills in declaration order, for iteration.</summary>
        public List<SkillDef> All()
        {
            List<SkillDef> all = new List<SkillDef>(_order.Count);
            for (int i = 0; i < _order.Count; i++)
            {
                all.Add(_byId[_order[i]]);
            }

            return all;
        }

        /// <summary>
        /// 把一条技能的**逻辑资源名**解析成地址目录表里的定位符。
        ///
        /// 解析不出来**不算异常**：美术资源后补是常态，所以未命中的逻辑名收集到
        /// <paramref name="unresolved"/> 里，由构建期校验（<see cref="Validate"/>）决定要不要拦。
        /// 未知技能 id 则抛 —— 那是调用方接线错了。
        /// </summary>
        public int ResolveAssets(
            AddressCatalog catalog,
            CatalogTagSet active,
            string skillId,
            Dictionary<string, string> into,
            List<string> unresolved)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }

            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            SkillDef def = Require(skillId);

            int resolved = 0;
            List<string> names = new List<string>(def.Assets.Keys);
            names.Sort(StringComparer.Ordinal);

            for (int i = 0; i < names.Count; i++)
            {
                string logicalName = names[i];
                CatalogResolution resolution = catalog.Resolve(def.Assets[logicalName], active);
                if (resolution.Resolved)
                {
                    into[logicalName] = resolution.Locator;
                    resolved++;
                }
                else if (unresolved != null)
                {
                    unresolved.Add(logicalName);
                }
            }

            return resolved;
        }

        // ------------------------------------------------------------------ 校验

        /// <summary>
        /// 技能表 × 地址目录表的交叉校验。传 null 目录则只做表内检查
        /// （"地址解析不出"这一类自然跳过 —— 没目录就无从判断）。
        /// </summary>
        public SkillReport Validate(AddressCatalog catalog, CatalogTagSet active)
        {
            SkillReport report = new SkillReport();
            Dictionary<string, string> resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> unresolved = new List<string>();

            for (int i = 0; i < _order.Count; i++)
            {
                SkillDef def = _byId[_order[i]];

                if (def.Assets.Count == 0)
                {
                    report.Warn(def.Id, "declares no asset addresses (no icon, no vfx, no sfx).");
                }

                if (def.Costs.Count == 0 && def.Cooldown <= 0f)
                {
                    report.Warn(def.Id, "has neither a cost nor a cooldown; it can be spammed.");
                }

                if (catalog == null || def.Assets.Count == 0)
                {
                    continue;
                }

                resolved.Clear();
                unresolved.Clear();
                ResolveAssets(catalog, active, def.Id, resolved, unresolved);
                for (int u = 0; u < unresolved.Count; u++)
                {
                    report.Error(
                        def.Id,
                        "asset '" + unresolved[u] + "' points at address '"
                        + def.Assets[unresolved[u]] + "', which the catalog does not resolve.");
                }
            }

            return report;
        }

        // ------------------------------------------------------------------ 写出

        /// <summary>
        /// 技能表的**确定性** JSON 写出：按 id Ordinal 排序、字段顺序固定、
        /// 缺省值省略（读到缺省值会重新推断出同一个定义）。
        ///
        /// 为什么要确定性：技能表会被签进包、被 diff、被热更比对。两次构建若因为
        /// 「书写顺序不同」就产出不同字节，diff 全是噪声，热更还会误判成内容变了。
        /// </summary>
        public string Write()
        {
            List<string> ids = SortedIds();

            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"skills\":[");

            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                AppendSkill(sb, _byId[ids[i]]);
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendSkill(StringBuilder sb, SkillDef def)
        {
            sb.Append("{\"id\":\"").Append(JsonParser.Escape(def.Id)).Append('"');

            if (!string.Equals(def.Name, def.Id, StringComparison.Ordinal))
            {
                sb.Append(",\"name\":\"").Append(JsonParser.Escape(def.Name)).Append('"');
            }

            // delivery 只在「不能由 cast_time 推断出来」时才写：cast 必有 cast_time，
            // 所以只要 cast_time 写出去了，读回来就能推断出同一个 delivery。
            if (def.IsCast)
            {
                sb.Append(",\"cast_time\":").Append(Number(def.CastTime));
            }

            if (def.Cooldown > 0f)
            {
                sb.Append(",\"cooldown\":").Append(Number(def.Cooldown));
            }

            if (def.Charges != 1)
            {
                sb.Append(",\"charges\":").Append(def.Charges);
            }

            if (def.ChargeRecovery > 0f)
            {
                sb.Append(",\"charge_recovery\":").Append(Number(def.ChargeRecovery));
            }

            if (def.Targeting != SkillTargeting.SingleTarget)
            {
                sb.Append(",\"targeting\":\"").Append(SkillDef.TargetingToken(def.Targeting)).Append('"');
            }

            if (def.Range > 0f)
            {
                sb.Append(",\"range\":").Append(Number(def.Range));
            }

            if (def.Costs.Count > 0)
            {
                sb.Append(",\"costs\":[");
                for (int i = 0; i < def.Costs.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append("{\"resource\":\"").Append(JsonParser.Escape(def.Costs[i].Resource))
                        .Append("\",\"amount\":").Append(Number(def.Costs[i].Amount)).Append('}');
                }

                sb.Append(']');
            }

            sb.Append(",\"effects\":[");
            for (int i = 0; i < def.Effects.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                AppendEffect(sb, def.Effects[i]);
            }

            sb.Append(']');

            if (def.Assets.Count > 0)
            {
                sb.Append(",\"assets\":{");
                List<string> names = new List<string>(def.Assets.Keys);
                names.Sort(StringComparer.Ordinal);
                for (int i = 0; i < names.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append('"').Append(JsonParser.Escape(names[i])).Append("\":\"")
                        .Append(JsonParser.Escape(def.Assets[names[i]])).Append('"');
                }

                sb.Append('}');
            }

            if (def.ScriptHook != null)
            {
                sb.Append(",\"hook\":\"").Append(JsonParser.Escape(def.ScriptHook)).Append('"');
            }

            if (def.Requires != null)
            {
                sb.Append(",\"requires\":\"").Append(JsonParser.Escape(def.Requires)).Append('"');
            }

            sb.Append('}');
        }

        private static void AppendEffect(StringBuilder sb, SkillEffect effect)
        {
            sb.Append("{\"kind\":\"").Append(SkillDef.EffectKindToken(effect.Kind)).Append('"');

            if (effect.Kind == SkillEffectKind.ApplyEffect)
            {
                sb.Append(",\"effect_id\":\"").Append(JsonParser.Escape(effect.EffectId)).Append('"');

                if (effect.Duration > 0f)
                {
                    sb.Append(",\"duration\":").Append(Number(effect.Duration));
                }

                if (effect.TickInterval > 0f)
                {
                    sb.Append(",\"tick_interval\":").Append(Number(effect.TickInterval));
                }

                if (effect.MaxStacks != 1)
                {
                    sb.Append(",\"max_stacks\":").Append(effect.MaxStacks);
                }

                if (effect.StackPolicy != SkillStackPolicy.Refresh)
                {
                    sb.Append(",\"stack\":\"").Append(SkillDef.StackPolicyToken(effect.StackPolicy)).Append('"');
                }

                // modifiers 的顺序**真的无关**（三层求值只按 kind 分组：Flat 相加、
                // PercentAdd 相加、PercentMul 连乘），所以这里排序写出，让"同一份内容
                // 无论怎么排都产出同一份字节"这条承诺覆盖到 modifiers。
                if (effect.Modifiers.Count > 0)
                {
                    List<SkillModifier> modifiers = new List<SkillModifier>(effect.Modifiers);
                    modifiers.Sort(CompareModifiers);

                    sb.Append(",\"modifiers\":[");
                    for (int i = 0; i < modifiers.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }

                        sb.Append("{\"stat\":\"").Append(JsonParser.Escape(modifiers[i].Stat))
                            .Append("\",\"kind\":\"").Append(SkillDef.StatKindToken(modifiers[i].Kind))
                            .Append("\",\"value\":").Append(Number(modifiers[i].Value)).Append('}');
                    }

                    sb.Append(']');
                }

                sb.Append('}');
                return;
            }

            sb.Append(",\"stat\":\"").Append(JsonParser.Escape(effect.Stat)).Append('"');

            if (effect.Multiplier != 0f)
            {
                sb.Append(",\"multiplier\":").Append(Number(effect.Multiplier));
            }

            if (effect.FlatBonus != 0f)
            {
                sb.Append(",\"flat_bonus\":").Append(Number(effect.FlatBonus));
            }

            sb.Append('}');
        }

        /// <summary>Stat first, then kind, then value -- so modifier order never reaches the bytes.</summary>
        private static int CompareModifiers(SkillModifier a, SkillModifier b)
        {
            int byStat = string.CompareOrdinal(a.Stat, b.Stat);
            if (byStat != 0)
            {
                return byStat;
            }

            int byKind = ((int)a.Kind).CompareTo((int)b.Kind);
            return byKind != 0 ? byKind : a.Value.CompareTo(b.Value);
        }

        /// <summary>
        /// Invariant-culture number rendering: the same float must serialize to the same
        /// bytes on every machine, or the determinism promise is a lie on a German locale.
        /// </summary>
        private static string Number(float value)
        {
            return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
