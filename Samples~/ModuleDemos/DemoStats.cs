using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using YourFramework.Stats;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// 数值系统速览（四块内聚的东西，按战斗里的真实顺序走一遍）：
    /// 可复现随机 → 属性表三层求值 → 逐帧驱动效果 → 伤害管道。
    ///
    /// 这一整套的价值不在"能算伤害"，而在**每一层都各有一个可替换的预留接口**：
    /// 随机源可换（服务端下发序列 / 回放录制序列）、属性名可配（框架不规定别人叫 atk）、
    /// 减伤公式参数可换、效果是纯数据定义（同一份配表施加到无数个单位）。
    ///
    /// 与别的演示不重复：11 号讲实体注册表，这里讲挂在实体身上的**数值**。
    /// </summary>
    internal static class DemoStats
    {
        private static void Log(string line)
        {
            Debug.Log("[Demo20] " + line);
        }

        private static string Numbers(int[] values)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(values[i]);
            }

            return builder.ToString();
        }

        private static string Numbers(uint[] values)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(values[i]);
            }

            return builder.ToString();
        }

        public static IEnumerator Stats()
        {
            // ---------------------------------------------------------- 1. 可复现随机
            Log("① 确定性随机 —— 同样种子给同样序列，且「加一行新的随机调用」不会顺移掉原有结果。");

            DeterministicRandom runA = new DeterministicRandom(20260918UL, "battle");
            DeterministicRandom runB = new DeterministicRandom(20260918UL, "battle");
            uint[] a = new uint[] { runA.NextUInt(), runA.NextUInt(), runA.NextUInt() };
            uint[] b = new uint[] { runB.NextUInt(), runB.NextUInt(), runB.NextUInt() };
            Log("    同种子两条流：" + Numbers(a) + " / " + Numbers(b)
                + " —— 一模一样（战斗回放、帧同步、断线重连都靠这个）。");

            // 命名分流：暴击序列与掉落序列互不干扰，各自消费多少都不影响对方
            DeterministicRandom critStream = new DeterministicRandom(20260918UL, "battle").Fork("crit");
            DeterministicRandom lootStream = new DeterministicRandom(20260918UL, "battle").Fork("loot");
            for (int i = 0; i < 100; i++)
            {
                lootStream.NextUInt();                       // 掉落狂抽 100 次
            }

            uint firstCrit = critStream.NextUInt();
            uint freshCrit = new DeterministicRandom(20260918UL, "battle").Fork("crit").NextUInt();
            Log("    分流：掉落流先抽 100 次之后，crit 流的第一个值 = " + firstCrit
                + "，与全新实例的 crit 流首值 = " + freshCrit + " 相同 —— 分流互不扰动。");

            ulong snapshot = critStream.State;
            uint predicted = critStream.NextUInt();
            critStream.Restore(snapshot);
            Log("    存档：状态就是一个 ulong（" + snapshot + "），搬走再搬回来，下一个值仍是 "
                + critStream.NextUInt() + "（预演过的是 " + predicted + "）—— 存档/网络同步都只需搬这个数。");

            yield return null;

            // ---------------------------------------------------------- 2. 属性表三层求值
            Log("② 属性表 —— Flat 相加、(Base+Flat) 乘 PercentAdd 之和、再乘 PercentMul 之积。");

            StatSheet sheet = new StatSheet();
            sheet.Declare("atk", 10f);
            sheet.AddModifier(new StatModifier("atk", StatKind.Flat, 5f, "武器#12"));
            sheet.AddModifier(new StatModifier("atk", StatKind.PercentAdd, 0.2f, "天赋A"));
            sheet.AddModifier(new StatModifier("atk", StatKind.PercentAdd, 0.3f, "天赋B"));
            sheet.AddModifier(new StatModifier("atk", StatKind.PercentMul, 0.1f, "光环A"));
            sheet.AddModifier(new StatModifier("atk", StatKind.PercentMul, 0.2f, "光环B"));

            Log("    " + sheet.Explain("atk").Explain());
            Log("    注意两个 +20%/+30% 是**相加**（×1.5）而不是连乘（×1.56）：常见天赋该相加，"
                + "罕见高阶加成才连乘，合成一层就没法调了。");

            // 卸装备 = 按来源一句话摘掉，而不是自己记着删哪几条
            int removed = sheet.RemoveBySource("武器#12");
            Log("    卸下武器 → RemoveBySource(\"武器#12\") 摘掉 " + removed
                + " 条修饰符，攻击回到 " + sheet.Value("atk") + "。");

            // 未声明的属性：写入侧立刻爆，读取侧返回 0
            float readBack;
            bool declared = sheet.TryValue("magic", out readBack);
            Log("    读未声明的 magic → TryValue 返回 " + declared + "、值 " + readBack
                + "（读松：配配置驱动的属性集合本来就会动态长出来；而写侧拼错属性名会立刻抛异常）。");

            yield return null;

            // ---------------------------------------------------------- 3. 逐帧驱动效果
            Log("③ 持续效果 —— 逐帧驱动的状态机：施加/叠层/跳伤/到期都是 Pump(delta) 推出来的。");

            StatSheet combatant = new StatSheet();
            combatant.Declare("atk", 100f);
            combatant.Declare("hp", 300f, StatRange.Between(0f, 300f));
            EffectHost effects = new EffectHost(combatant);

            // 中毒：每层 +4 攻击（这里用攻击代表"越叠越疼"），最多 3 层
            StatEffect poison = new StatEffect("poison", 3f, EffectStackPolicy.Stack);
            poison.MaxStacks = 3;
            poison.TickInterval = 1f;
            poison.Modifiers = new List<StatModifier>();
            poison.Modifiers.Add(new StatModifier("atk", StatKind.Flat, 4f, null));

            // 减速：只刷新时长，不叠层（否则两个法师各放一次就把玩家固定死）
            StatEffect slow = new StatEffect("slow", 2f, EffectStackPolicy.Refresh);
            slow.Modifiers = new List<StatModifier>();
            slow.Modifiers.Add(new StatModifier("atk", StatKind.PercentAdd, -0.3f, null));

            effects.Apply(poison, "刺客");
            effects.Apply(poison, "刺客");
            effects.Apply(poison, "刺客");
            EffectInstance overflow = effects.Apply(poison, "刺客");
            Log("    中毒叠 3 层 → 攻击 = " + combatant.Value("atk")
                + "；第 4 次施加返回 " + (overflow == null ? "null" : "实例")
                + "，原因：" + effects.LastApplyError + "（满层是正常游戏状态，所以是值不是异常）。");

            effects.Apply(slow, "法师A");
            effects.Apply(slow, "法师B");
            Log("    两个法师的减速 → 仍是 " + effects.Count + " 个实例、"
                + combatant.ModifierCount("atk") + " 条修饰符（Refresh 不重复叠加）。");

            List<EffectEvent> events = new List<EffectEvent>();
            effects.Pump(1f, events);
            Log("    推进 1 秒：产生 " + events.Count + " 个事件（tick 由调用方拿去做飘字/结算）。");

            List<EffectEvent> finalRound = new List<EffectEvent>();
            effects.Pump(2f, finalRound);
            int ticks = 0;
            int expiries = 0;
            for (int i = 0; i < finalRound.Count; i++)
            {
                if (finalRound[i].Kind == EffectEventKind.Tick)
                {
                    ticks++;
                }
                else
                {
                    expiries++;
                }
            }

            Log("    再推进 2 秒：tick " + ticks + " 次 + 到期 " + expiries
                + " 次 —— 3 秒的 DoT 正好跳 3 次（先 tick 后到期，边界那一跳不会被吃掉）。");
            Log("    到期后攻击 = " + combatant.Value("atk")
                + "，修饰符 " + combatant.ModifierCount("atk") + " 条 —— 逐条精确摘除，不会误伤别人。");

            yield return null;

            // ---------------------------------------------------------- 4. 伤害管道
            Log("④ 伤害管道 —— 比例减伤而非减法，随机消费次数完全由配置决定。");

            DamageProfile profile = DamageProfile.WithCrit();
            profile.MitigationConstant = 100f;   // 防御 100 恰好减伤 50%
            profile.Variance = 0.1f;
            profile.MinimumDamage = 1f;

            DamagePipeline pipeline = new DamagePipeline(profile, new DeterministicRandom(7UL, "damage"));

            StatSheet attacker = new StatSheet();
            attacker.Declare("atk", 200f);
            attacker.Declare("critChance", 1f);       // 演示里让它必爆，好看清公式
            attacker.Declare("critDamage", 0.5f);

            StatSheet naked = new StatSheet();
            naked.Declare("def", 0f);
            StatSheet armored = new StatSheet();
            armored.Declare("def", 100f);
            StatSheet tanked = new StatSheet();
            tanked.Declare("def", 300f);

            DamageResult noArmor = pipeline.Resolve(attacker, naked, DamageRequest.Of(1f));
            DamageResult half = pipeline.Resolve(attacker, armored, DamageRequest.Of(1f));
            DamageResult quarter = pipeline.Resolve(attacker, tanked, DamageRequest.Of(1f));
            Log("    同一发攻击打不同护甲：" + noArmor.Amount + " / " + half.Amount + " / " + quarter.Amount
                + "（0/100/300 防）—— 比例曲线永不到 0，高等级不会出现「减法把伤害压成 0」的数值悬崖。");
            Log("    明细：" + half.Explain());

            DamageResult pierce = pipeline.Resolve(attacker, tanked, DamageRequest.True(1f));
            Log("    真实伤害跳过减伤 → " + pierce.Amount + "（系数 " + pierce.Mitigation
                + "），但防御读数仍留在结果里（" + pierce.Defense + "），因为那是事实。");

            // 攻方属性名配错：以值返回，不把战斗打崩
            StatSheet misconfigured = new StatSheet();
            misconfigured.Declare("power", 200f);
            DamageResult broken = pipeline.Resolve(misconfigured, naked, DamageRequest.Of(1f));
            Log("    攻方没有配置 " + broken.Stat + " → Outcome = " + broken.Outcome
                + "、伤害 " + broken.Amount + "、随机消费 " + broken.Rolls
                + " 次 —— 装配错误以值的形式露出来，UI 有话可说。");

            float remaining;
            string error;
            StatSheet target = new StatSheet();
            target.Declare("hp", 400f, StatRange.Between(0f, 400f));
            bool applied = pipeline.TryApplyDamage(target, "hp", half, out remaining, out error);
            Log("    落伤：" + applied + "，剩余生命 " + remaining + "（扣的是基础值，修饰符继续作用）。");

            int hits = 1;
            while (hits < 1000 && pipeline.TryApplyDamage(target, "hp", half, out remaining, out error))
            {
                hits++;
            }

            Log("    连砍 " + hits + " 刀后目标倒下（剩余 " + remaining + "），再补刀被拒 → " + error
                + " —— 往尸体上补刀是游戏状态，所以是值不是异常。");

            Log("全部演示完毕：随机可复现、属性可解释、效果可精确摘除、伤害失败可归因。");
        }
    }
}
