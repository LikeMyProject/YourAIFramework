using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using YourFramework.Catalog;
using YourFramework.Scripting;
using YourFramework.Skills;
using YourFramework.Stats;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// 配置驱动技能速览（技能全在表里，代码一个技能名都不认识）：
    /// 技能表 + 地址解析 → 目标选择 → 冷却与吟唱 → 通过数值桥打掉血量。
    ///
    /// 这一套要解决的问题是"加一个技能要改代码、改包、重新出客户端"。这里的做法：
    ///   - 技能定义、消耗、效果、资源地址、脚本钩子**全部来自 JSON**，代码里没有
    ///     `if (skillId == "fireball")`，也没有魔数；
    ///   - 技能内核**不认识数值系统**：伤害怎么算、治疗怎么钳、buff 进哪个容器，
    ///     都收在一个 <see cref="ISkillEffectSink"/> 实现里（这里用 StatSkillEffectSink）；
    ///   - 资源地址是**逻辑名**，真正取哪个包、哪个画质档，交给 21 号地址目录表；
    ///   - 施法钩子是脚本入口名，接 22 号脚本层 —— 改技能表现不必重出客户端。
    ///
    /// 与 20 号数值系统的分工：那边讲"数值怎么算"，这里讲"**谁在什么时候按什么规则
    /// 把数值打出去**"。技能表的 <c>stat</c> 字段是给伤害管道读的属性名（默认 atk），
    /// 落地方案换成"特效先行、伤害后结算"时，改的是 sink 而不是表。
    /// </summary>
    internal static class DemoSkill
    {
        private static void Log(string line)
        {
            Debug.Log("[Demo23] " + line);
        }

        // ------------------------------------------------------------ 内联配置（全部内容都在这里）

        /// <summary>
        /// 四个技能，覆盖瞬发 / 吟唱 / 充能 / 群体 / 治疗 / 自我增益六种形态。
        /// 注意这里出现的每个数字与每个名字都只存在于配置里 —— 换一套表就是换一个游戏。
        /// </summary>
        private const string SkillTableJson = @"{
          ""skills"": [
            { ""id"": ""focus"", ""name"": ""凝神"", ""targeting"": ""self"",
              ""effects"": [ { ""kind"": ""apply_effect"", ""effect_id"": ""focus_surge"", ""duration"": 8,
                ""stack"": ""stack"", ""max_stacks"": 3,
                ""modifiers"": [ { ""stat"": ""atk"", ""kind"": ""percent_add"", ""value"": 0.15 } ] } ],
              ""assets"": { ""icon"": ""skill/focus/icon"" } },
            { ""id"": ""ember_bolt"", ""name"": ""余烬弹"", ""cast_time"": 0.8, ""cooldown"": 5,
              ""costs"": [ { ""resource"": ""mp"", ""amount"": 25 } ],
              ""effects"": [ { ""kind"": ""damage"", ""stat"": ""atk"", ""multiplier"": 1.5, ""flat_bonus"": 20 } ],
              ""assets"": { ""vfx"": ""skill/ember/vfx"", ""icon"": ""skill/ember/icon"" },
              ""hook"": ""on_ember_bolt"", ""requires"": ""focus"" },
            { ""id"": ""mend"", ""name"": ""愈合"", ""targeting"": ""lowest_health_ally"",
              ""charges"": 3, ""charge_recovery"": 4,
              ""costs"": [ { ""resource"": ""mp"", ""amount"": 20 } ],
              ""effects"": [ { ""kind"": ""heal"", ""stat"": ""magic_power"", ""multiplier"": 2, ""flat_bonus"": 40 } ],
              ""assets"": { ""vfx"": ""skill/mend/vfx"" }, ""hook"": ""on_mend"" },
            { ""id"": ""quake"", ""name"": ""震地"", ""targeting"": ""all_enemies"", ""cooldown"": 3, ""range"": 6,
              ""effects"": [ { ""kind"": ""damage"", ""stat"": ""atk"", ""multiplier"": 0.8 } ],
              ""assets"": { ""vfx"": ""skill/quake/vfx"" } }
          ]
        }";

        /// <summary>
        /// 地址目录表：技能只写逻辑名（vfx / icon），取到哪个定位符由这张表回答。
        /// 余烬弹的 vfx 特意留了一档 hd 变体 —— 同一份技能表，两档画质取到不同的东西。
        /// quake/vfx **故意不建**，用来看"解析不出来"是怎么被校验器拦住的。
        /// </summary>
        private const string CatalogJson = @"{
          ""name"": ""base"",
          ""entries"": [
            { ""address"": ""skill/ember/vfx"", ""group"": ""skill"",
              ""variants"": [
                { ""locator"": ""VFX/Ember.prefab"" },
                { ""tags"": [""hd""], ""locator"": ""VFX/Ember_hd.prefab"" }
              ] },
            { ""address"": ""skill/ember/icon"", ""group"": ""skill"", ""locator"": ""UI/Ember.png"" },
            { ""address"": ""skill/focus/icon"", ""group"": ""skill"", ""locator"": ""UI/Focus.png"" },
            { ""address"": ""skill/mend/vfx"", ""group"": ""skill"", ""locator"": ""VFX/Mend.prefab"" }
          ]
        }";

        // ------------------------------------------------------------ 替身：脚本钩子 + 单位目录

        /// <summary>
        /// 玩具脚本运行时：只登记了 on_mend。所以余烬弹的 on_ember_bolt 会调用失败 ——
        /// 演示要的就是这个失败：**钩子挂掉不该让技能失灵**，它只是报一条 HookFailed 事件。
        /// </summary>
        private sealed class HookRuntime : IScriptRuntime
        {
            private readonly List<string> _entries = new List<string>();
            private readonly List<string> _log = new List<string>();

            public HookRuntime(params string[] entries)
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    _entries.Add(entries[i]);
                }
            }

            /// <summary>调用流水（技能落地时调了哪个入口）。</summary>
            public IList<string> Log { get { return _log; } }

            public string Name { get { return "demo-hooks"; } }

            public ScriptRuntimeState State { get { return ScriptRuntimeState.Ready; } }

            public ScriptOutcome LoadChunk(ScriptChunk chunk)
            {
                return chunk == null
                    ? ScriptOutcome.Fail("chunk is null")
                    : ScriptOutcome.Ok(chunk.Name);
            }

            public ScriptOutcome UnloadChunk(string name)
            {
                return ScriptOutcome.Ok(name);
            }

            public ScriptOutcome CallEntry(string entryName)
            {
                if (string.IsNullOrEmpty(entryName))
                {
                    throw new ArgumentException("HookRuntime.CallEntry: entryName 不能为空。", "entryName");
                }

                _log.Add(entryName);
                return _entries.Contains(entryName)
                    ? ScriptOutcome.Ok(entryName)
                    : ScriptOutcome.Fail("入口 '" + entryName + "' 在脚本环境里不存在");
            }

            public void Pump(double deltaSeconds)
            {
            }

            public void Shutdown()
            {
            }
        }

        /// <summary>
        /// actor id → (属性表, 效果容器)。技能内核只认 id，具体是玩家、怪还是一块木桩，
        /// 由项目自己的实体系统回答 —— 这是技能与"谁在打架"之间**唯一**的接缝。
        /// </summary>
        private sealed class ActorDirectory : ISkillActorSource
        {
            private readonly Dictionary<string, StatSheet> _sheets =
                new Dictionary<string, StatSheet>(StringComparer.Ordinal);

            private readonly Dictionary<string, EffectHost> _hosts =
                new Dictionary<string, EffectHost>(StringComparer.Ordinal);

            public void Add(string id, StatSheet sheet, EffectHost host)
            {
                _sheets[id] = sheet;
                _hosts[id] = host;
            }

            public bool TryGetActor(string actorId, out StatSheet sheet, out EffectHost effects)
            {
                sheet = null;
                effects = null;

                if (!_sheets.TryGetValue(actorId, out sheet))
                {
                    return false;
                }

                _hosts.TryGetValue(actorId, out effects);
                return true;
            }
        }

        // ------------------------------------------------------------ 小工具

        private static string Join(IList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return "（空）";
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("、");
                }

                builder.Append(values[i]);
            }

            return builder.ToString();
        }

        private static string Money(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Describe(CatalogTagSet tags)
        {
            return tags.Count == 0 ? "（标准档）" : "{" + string.Join(",", tags.Tags.ToArray()) + "}";
        }

        /// <summary>把某一幕产生的事件拼成一行 —— SkillCastEvent.ToString 本身就可读。</summary>
        private static string Trace(List<SkillCastEvent> events, int from)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = from; i < events.Count; i++)
            {
                if (i > from)
                {
                    builder.Append(" | ");
                }

                builder.Append(events[i].ToString());
            }

            return builder.Length == 0 ? "（没有事件）" : builder.ToString();
        }

        /// <summary>按当前真实血量造候选快照 —— 调用方每帧从自己的实体系统填，技能内核不查场景。</summary>
        private static SkillTargetCandidate Candidate(string id, int team, StatSheet sheet, float distance)
        {
            float health;
            float maximum;
            sheet.TryValue("hp", out health);
            sheet.TryValue("max_hp", out maximum);

            float ratio = maximum > 0f ? health / maximum : 1f;
            return new SkillTargetCandidate(id, team, health > 0f, distance, ratio);
        }

        private static List<SkillTargetCandidate> Field(
            StatSheet hero, StatSheet ally, StatSheet boss, StatSheet add)
        {
            List<SkillTargetCandidate> field = new List<SkillTargetCandidate>();
            field.Add(Candidate("hero", 0, hero, 0f));
            field.Add(Candidate("ally", 0, ally, 3f));
            field.Add(Candidate("boss", 1, boss, 4f));
            field.Add(Candidate("add", 1, add, 5f));
            return field;
        }

        // ------------------------------------------------------------ 演示主体

        public static IEnumerator Skills()
        {
            Log("配置驱动技能：代码里没有一个技能名、没有一个数字 —— 加技能 = 加一段 JSON。");

            // ============================================================ ① 技能表与地址解析
            SkillLibrary library = SkillLibrary.FromJson(SkillTableJson);
            Log("  解析出 " + library.Count + " 个技能：" + Join(library.SortedIds()));

            List<SkillDef> all = library.All();
            for (int i = 0; i < all.Count; i++)
            {
                SkillDef def = all[i];
                Log("    " + def.Name + " —— " + def.Describe()
                    + "，效果 " + def.Effects.Count + " 条"
                    + (def.Costs.Count > 0 ? "，消耗 " + def.Costs[0].Resource + " " + Money(def.Costs[0].Amount) : "，无消耗")
                    + (def.Requires == null ? string.Empty : "，前置 " + def.Requires));
            }

            yield return null;

            // 地址解析：技能写的是逻辑名，落到哪个定位符由目录表决定
            AddressCatalog catalog = AddressCatalog.FromJson(CatalogJson);
            CatalogTagSet standard = CatalogTagSet.Empty;
            CatalogTagSet high = new CatalogTagSet(new string[] { "hd" });

            Dictionary<string, string> resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> unresolved = new List<string>();
            int hit = library.ResolveAssets(catalog, high, "ember_bolt", resolved, unresolved);

            Log("  余烬弹声明了 vfx / icon 两个逻辑名，在 " + Describe(high) + " 下解析出 " + hit + " 个：");
            List<string> logicalNames = new List<string>(resolved.Keys);
            logicalNames.Sort(StringComparer.Ordinal);
            for (int i = 0; i < logicalNames.Count; i++)
            {
                Log("    " + logicalNames[i] + " → " + resolved[logicalNames[i]]);
            }

            Log("  同一份技能表换 " + Describe(standard) + " → " + catalog.Resolve("skill/ember/vfx", standard).Locator
                + "（画质档是目录表的事，技能表不认识 hd 这个词）");

            yield return null;

            // 校验：解析不出来的地址是 Error，拦构建
            SkillReport report = library.Validate(catalog, standard);
            Log("  技能表 × 地址目录表交叉校验：" + report.Summary() + "，干净吗？" + report.IsClean);

            List<SkillIssue> issues = report.Issues;
            for (int i = 0; i < issues.Count; i++)
            {
                Log("    " + issues[i]);
            }

            Log("  —— 错误是「到了线上会静默错」的那种（震地没有特效，玩家只会觉得打击感差）；"
                + "警告只提醒（凝神没消耗也没冷却，可以被无限按 —— 可能是有意的）。");

            yield return null;

            // 确定性写出：构建期产物要能 diff，两次构建不能因为书写顺序不同就产出不同字节
            string written = library.Write();
            string again = SkillLibrary.FromJson(written).Write();
            Log("  确定性写出 " + written.Length + " 字节；再解析再写出 → 逐字节相同？" + (written == again));
            Log("  输出片段：" + written.Substring(0, Math.Min(160, written.Length)) + " ...");

            yield return null;

            // ============================================================ ② 目标选择
            Log("② 目标选择：纯函数，只看候选快照 —— 不查场景、不查物理、不查寻路。");

            List<SkillTargetCandidate> scouts = new List<SkillTargetCandidate>();
            scouts.Add(new SkillTargetCandidate("hero", 0, true, 0f, 0.9f));
            scouts.Add(new SkillTargetCandidate("ally", 0, true, 3f, 0.35f));
            scouts.Add(new SkillTargetCandidate("boss", 1, true, 4f, 0.6f));
            scouts.Add(new SkillTargetCandidate("add", 1, true, 5f, 0.2f));
            scouts.Add(new SkillTargetCandidate("corpse", 1, false, 2f, 0f));

            List<SkillTargetCandidate> into = new List<SkillTargetCandidate>();

            SkillTargetSelector.Select(SkillTargetQuery.For("hero", 0, SkillTargeting.Self), scouts, into);
            Log("    self → " + Join(Ids(into)));

            SkillTargetSelector.Select(
                SkillTargetQuery.For("hero", 0, SkillTargeting.SingleTarget).WithTarget("boss"), scouts, into);
            Log("    single_target(boss) → " + Join(Ids(into)));

            SkillTargetSelector.Select(SkillTargetQuery.For("hero", 0, SkillTargeting.AllEnemies), scouts, into);
            Log("    all_enemies → " + Join(Ids(into)) + "（尸体不在里面：死人不参与群体筛选）");

            SkillTargetSelector.Select(SkillTargetQuery.For("hero", 0, SkillTargeting.LowestHealthAlly), scouts, into);
            Log("    lowest_health_ally → " + Join(Ids(into)) + "（血最少的友军；并列时取候选顺序靠前的，"
                + "所以同一份快照永远选出同一个人）");

            // into 会被清空再填：这里先塞两个假数据，验证"结果集是精确的"
            into.Add(new SkillTargetCandidate("stale", 9, true, 0f, 1f));
            into.Add(new SkillTargetCandidate("stale2", 9, true, 0f, 1f));
            int selected = SkillTargetSelector.Select(
                SkillTargetQuery.For("hero", 0, SkillTargeting.AllEnemies), scouts, into);
            Log("    复用同一个列表（先塞了两条脏数据）→ 选出 " + selected + " 个：" + Join(Ids(into))
                + " —— 目标选择先清空再填，和「检索类接口的追加语义」故意不一致。");

            yield return null;

            // ============================================================ ③ 冷却、充能、施法流程
            Log("③ 冷却与充能：一帧跨过几个恢复点就补几个，且多出来的时间留给下一充能（帧率无关）。");

            SkillCooldown charges = new SkillCooldown(3, 4f);
            charges.TryConsume();
            charges.TryConsume();
            charges.TryConsume();
            Log("    三连发之后：" + charges.Describe());

            int bigFrame = charges.Pump(9f, 1f);
            Log("    一帧推进 9 秒（模拟一次卡顿）→ 回来 " + bigFrame + " 个：" + charges.Describe());
            int nextFrame = charges.Pump(3f, 1f);
            Log("    再推进 3 秒 → 又回来 " + nextFrame + " 个：" + charges.Describe()
                + " —— 上一帧多吃掉的那 1 秒没有被丢掉（丢掉就是「30 帧比 60 帧少打一套连招」）。");

            charges.Pump(100f, 1f);
            int idle = charges.Pump(100f, 1f);
            Log("    满仓之后再推进 100 秒 → 回来 " + idle + " 个：满了就不再攒欠账。");

            SkillCooldown accelerated = new SkillCooldown(1, 4f);
            accelerated.TryConsume();
            accelerated.Pump(2f, 3f);
            Log("    冷却恢复速率是乘在推进量上的：推进 2 秒 × 速率 3 = " + accelerated.Describe()
                + "（不是「把 4 秒冷却改成 1.33 秒」—— 中途改属性时后者会算错已经过去的时间）");

            yield return null;

            // ---- 一个真实的最小世界：四张属性表 + 伤害管道 + 效果容器
            StatSheet hero = new StatSheet();
            hero.Declare("atk", 120f);
            hero.Declare("mp", 100f, StatRange.Between(0f, 100f));
            hero.Declare("hp", 200f, StatRange.Between(0f, 200f));
            hero.Declare("max_hp", 200f);
            hero.Declare("magic_power", 30f);

            StatSheet ally = new StatSheet();
            ally.Declare("atk", 80f);
            ally.Declare("hp", 90f, StatRange.Between(0f, 200f));
            ally.Declare("max_hp", 200f);

            StatSheet boss = new StatSheet();
            boss.Declare("atk", 150f);
            boss.Declare("def", 60f);
            boss.Declare("hp", 400f, StatRange.Between(0f, 400f));
            boss.Declare("max_hp", 400f);

            StatSheet add = new StatSheet();
            add.Declare("atk", 50f);
            add.Declare("def", 0f);
            add.Declare("hp", 200f, StatRange.Between(0f, 200f));
            add.Declare("max_hp", 200f);

            EffectHost heroEffects = new EffectHost(hero);
            EffectHost allyEffects = new EffectHost(ally);
            EffectHost bossEffects = new EffectHost(boss);
            EffectHost addEffects = new EffectHost(add);

            ActorDirectory actors = new ActorDirectory();
            actors.Add("hero", hero, heroEffects);
            actors.Add("ally", ally, allyEffects);
            actors.Add("boss", boss, bossEffects);
            actors.Add("add", add, addEffects);

            DamageProfile profile = new DamageProfile();
            profile.MitigationConstant = 100f;   // 60 防 → 减伤 100/160 = 37.5%（比例曲线，不会压成 0）
            profile.Variance = 0f;               // 演示要可复现
            profile.MinimumDamage = 1f;
            DamagePipeline damage = new DamagePipeline(profile, new DeterministicRandom(23UL, "demo-skill"));

            // 桥：把技能效果落到数值系统上。技能内核只看见 ISkillEffectSink 这个接口。
            ISkillEffectSink sink = new StatSkillEffectSink(damage, actors, "hp", "max_hp");
            ISkillStatSource stats = new StatSheetStatSource(hero);

            HookRuntime hooks = new HookRuntime("on_mend");
            SkillBook book = new SkillBook("hero", library, stats, sink, hooks);
            book.Learn("focus");
            book.Learn("ember_bolt");
            book.Learn("mend");
            book.Learn("quake");

            Log("    英雄学会 " + book.Count + " 个技能：" + Join(book.Known())
                + "（表里用 requires 声明了「余烬弹的前置是凝神」；谁有资格学由游戏侧决定，内核只认表）");

            List<SkillCastEvent> events = new List<SkillCastEvent>();
            List<SkillTargetCandidate> targets = new List<SkillTargetCandidate>();
            List<SkillTargetCandidate> field = Field(hero, ally, boss, add);

            // 拒绝：“打不出来”是值，不是异常
            int mark = events.Count;
            SkillCastOutcome outcome = book.TryBegin("ember_bolt", field, null, targets, events);
            Log("    不给目标就按余烬弹 → " + outcome);
            Log("      " + Trace(events, mark));

            // 起手：资源在吟唱开始时就扣
            mark = events.Count;
            outcome = book.TryBegin("ember_bolt", field, "boss", targets, events);
            Log("    指定 boss → " + outcome + "，蓝 100 → " + Money(hero.Value("mp"))
                + "（**资源在起手时扣**：吟唱还没走完，蓝已经花掉了）");
            Log("      " + Trace(events, mark));

            book.Pump(0.5f, events);
            SkillSlot bolt = book.Slot("ember_bolt");
            Log("    推进 0.5 秒 → 状态 " + bolt.State + "，还剩 " + Money(bolt.CastRemaining) + " 秒才落地");

            // 打断：不退款
            mark = events.Count;
            bool cut = book.Interrupt("ember_bolt", "被眩晕打断", events);
            Log("    眩晕打断 → " + cut + "，蓝仍是 " + Money(hero.Value("mp"))
                + "（**不退款**：扣费发生在起手，这个语义是明确的，不是看情况）");

            // 拒绝优先级：冷却最先
            mark = events.Count;
            outcome = book.TryBegin("ember_bolt", field, null, targets, events);
            Log("    立刻再按（连目标都没给）→ " + outcome);
            Log("      报的是冷却而不是「没目标」—— 拒绝的检查顺序是固定的：冷却 → 资源 → 目标。"
                + "把目标排在冷却前面，会让「技能还在转」显示成「打不到」。");

            // 拒绝优先级：资源
            book.Reset();
            hero.SetBase("mp", 10f);
            mark = events.Count;
            outcome = book.TryBegin("ember_bolt", field, "boss", targets, events);
            Log("    冷却归满但蓝只剩 10（技能要 25）→ " + outcome);
            Log("      这次报资源 —— 同一个技能，原因不同，UI 才能给出不同的提示。");

            yield return null;

            // ============================================================ ④ 通过数值桥真正落地
            Log("④ 落地：技能内核不认识属性表，它只把效果交给 sink —— 这里用的是接数值系统的那一个。");

            hero.SetBase("mp", 100f);
            book.Reset();

            // (1) 自我增益：瞬发，走效果容器
            mark = events.Count;
            int atkBefore = (int)hero.Value("atk");
            outcome = book.TryBegin("focus", field, null, targets, events);
            Log("    凝神（自我增益）→ " + outcome + "，攻击 " + atkBefore + " → " + (int)hero.Value("atk")
                + "，效果容器里 " + heroEffects.Count + " 个实例");
            Log("      " + Trace(events, mark));
            Log("      buff 是 ActEffect 定义 + 效果容器实例：同一份配置施加到一万个怪身上，也只有一份定义。");

            // (2) 吟唱技能走满 → 落地成伤害（同时看钩子的失败姿态）
            mark = events.Count;
            int bossBefore = (int)boss.Value("hp");
            outcome = book.TryBegin("ember_bolt", field, "boss", targets, events);
            book.Pump(0.5f, events);
            book.Pump(0.4f, events);
            Log("    余烬弹吟唱 0.8 秒（推进 0.5 + 0.4）→ boss 生命 " + bossBefore + " → " + (int)boss.Value("hp")
                + "（攻击 120→" + (int)hero.Value("atk") + " 的增益也吃进去了，因为伤害管道读的是属性表）");
            Log("      " + Trace(events, mark));
            Log("      钩子的入口没登记 → 报 HookFailed，**伤害照落**：脚本是热更代码，"
                + "一个错别字不该让技能整体失灵。");

            // (3) 群体：一次打全部敌人
            mark = events.Count;
            int addBefore = (int)add.Value("hp");
            outcome = book.TryBegin("quake", field, null, targets, events);
            Log("    震地（群体，无消耗）→ " + outcome + "，boss " + (int)boss.Value("hp")
                + "、add " + addBefore + " → " + (int)add.Value("hp"));
            Log("      " + Trace(events, mark));

            // (4) 治疗：选血最少的友军，过量治疗被上限钳住
            mark = events.Count;
            int allyBefore = (int)ally.Value("hp");
            outcome = book.TryBegin("mend", field, null, targets, events);
            Log("    愈合（血最少的友军）→ " + outcome + "，友军生命 " + allyBefore + " → " + (int)ally.Value("hp")
                + "（magic_power 30 × 2 + 40 = 100，上限 200 钳住）");
            Log("      " + Trace(events, mark));
            Log("      钩子 on_mend 这次是登记过的 → 脚本层被真的调用了，流水：" + Join(new List<string>(hooks.Log)));

            yield return null;

            // (5) 收尾：目标倒下之后，技能不判生死，只信调用方给的快照
            book.Reset();
            boss.SetBase("hp", 0f);
            field = Field(hero, ally, boss, add);
            mark = events.Count;
            outcome = book.TryBegin("ember_bolt", field, "boss", targets, events);
            Log("    boss 倒下（快照里 Alive = false）后再打 → " + outcome);
            Log("      技能不判生死、不查场景，它只信调用方每帧填的那份快照 —— "
                + "所以「谁算活着」是项目自己的规则，不是框架的规则。");

            Log("演示完毕：表驱动定义、目录表解析地址、纯函数选目标、泵驱动冷却与吟唱、"
                + "数值落地收在一个 sink 里 —— 加一个技能不需要改一行代码。");
        }

        private static List<string> Ids(List<SkillTargetCandidate> targets)
        {
            List<string> ids = new List<string>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                ids.Add(targets[i].Id);
            }

            return ids;
        }
    }
}
