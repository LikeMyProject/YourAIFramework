using System;
using System.Collections.Generic;
using System.Text;
using YourFramework.Scripting;
using XLua;

namespace YourFramework.XLua
{
    /// <summary>一次沙箱应用的报告。</summary>
    public sealed class XLuaSandboxReport
    {
        /// <summary>实际发给 Lua 的那段代码（空 = 本策略没有要禁的全局，什么都没发）。</summary>
        public string Command;

        /// <summary>策略要求禁掉的全局名（确定性顺序）。</summary>
        public List<string> Denied = new List<string>();

        /// <summary>
        /// 应用后**回读**仍然看得见的个数。这个字段存在的理由：发出「io = nil」和执行完
        /// 「io 真的不见了」是两件事，只有回读能把它们分开。
        /// </summary>
        public int StillVisible;

        /// <summary>应用失败的原因（执行期异常）。</summary>
        public string Error;

        /// <summary>是否关上：执行没抛异常，且回读确实看不到它们了。</summary>
        public bool Succeeded;

        /// <summary>诊断用一行。</summary>
        public override string ToString()
        {
            return "sandbox[" + (Succeeded ? "closed" : "OPEN") + "] denied=" + Denied.Count
                + " stillVisible=" + StillVisible + (Error == null ? string.Empty : (" error=" + Error));
        }
    }

    /// <summary>
    /// 把内核的沙箱策略翻译成 XLua 能执行的东西，并**回读确认门真的关上了**。
    ///
    /// 为什么翻译放在适配层而不是内核：内核只说「哪些全局名不该存在」（纯数据、可逐字检查项），
    /// 「怎么让一个名字消失」取决于解释器 —— Lua 是往全局表里赋 nil，别的引擎可能是摘键。
    /// 两边各管一件自己确实懂的事。
    ///
    /// 回读不是仪式感：沙箱是**安全控制**，安全控制的正确姿势是「配好了」不算数，
    /// 得「验过了」才算数。所以这里发的那段代码自己会数一遍还剩几个看得见，并把个数带回来。
    /// </summary>
    public static class XLuaSandbox
    {
        /// <summary>这段代码在 Lua 侧的 chunkName（报错时不至于显示成一大坨匿名代码）。</summary>
        public const string ChunkName = "@YourFramework.Sandbox";

        /// <summary>
        /// 只渲染「置 nil」那部分：<c>io = nil; os = nil;</c>。
        /// 顺序就是策略给的顺序（已排序），所以它可以被逐字检查项。
        /// </summary>
        public static string Render(ScriptSandboxPolicy policy)
        {
            if (policy == null)
            {
                throw new ArgumentNullException("policy");
            }

            List<string> denied = policy.DeniedGlobals;
            if (denied.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < denied.Count; i++)
            {
                builder.Append(denied[i]).Append(" = nil; ");
            }

            return builder.ToString();
        }

        /// <summary>
        /// 应用策略：置 nil + 回读。失败也是值（返回报告，不抛）—— 调用方（运行时）拿
        /// <see cref="XLuaSandboxReport.Succeeded"/> 决定要不要拒绝装载。
        /// </summary>
        public static XLuaSandboxReport Apply(LuaEnv env, ScriptSandboxPolicy policy)
        {
            if (env == null)
            {
                throw new ArgumentNullException("env");
            }

            XLuaSandboxReport report = new XLuaSandboxReport();
            ScriptSandboxPolicy actual = policy ?? throw new ArgumentNullException("policy");
            report.Denied.AddRange(actual.DeniedGlobals);

            if (report.Denied.Count == 0)
            {
                // 没有要禁的：不发代码，也就不存在「发了但没生效」的可能。
                report.Succeeded = true;
                return report;
            }

            report.Command = BuildCommand(report.Denied);

            object[] result;
            try
            {
                result = env.DoString(report.Command, ChunkName);
            }
            catch (Exception ex)
            {
                report.Error = ex.GetType().Name + ": " + ex.Message;
                report.Succeeded = false;
                return report;
            }

            report.StillVisible = ReadCount(result);
            if (report.StillVisible < 0)
            {
                report.Error = "the deny pass did not report its result back (cannot prove the sandbox is closed)";
                report.Succeeded = false;
                return report;
            }

            report.Succeeded = report.StillVisible == 0;
            if (!report.Succeeded)
            {
                report.Error = report.StillVisible + " denied global(s) are still visible after the deny pass";
            }

            return report;
        }

        /// <summary>
        /// 生成一段自校验的 Lua：
        ///   1. 把名字装进一张表（名字是字符串字面量，所以随便什么字符都安全）；
        ///   2. 逐个置 nil；
        ///   3. 回读 <c>_G</c>，数还剩几个看得见；
        ///   4. 把这个数 return 出来。
        /// 全程只用 <c>_G</c> / 循环 / 比较，不依赖任何被禁的库 —— 沙箱动作本身不能靠沙箱里的东西。
        /// </summary>
        private static string BuildCommand(List<string> denied)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("local _yfNames = {");
            for (int i = 0; i < denied.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append('"').Append(Escape(denied[i])).Append('"');
            }

            builder.Append("}\n");

            for (int i = 0; i < denied.Count; i++)
            {
                builder.Append(denied[i]).Append(" = nil\n");
            }

            builder.Append("local _yfVisible = 0\n");
            builder.Append("for _yfI = 1, #_yfNames do if _G[_yfNames[_yfI]] ~= nil then _yfVisible = _yfVisible + 1 end end\n");
            builder.Append("return _yfVisible\n");
            return builder.ToString();
        }

        private static int ReadCount(object[] result)
        {
            if (result == null || result.Length == 0 || result[0] == null)
            {
                // DoString 没把数字带回来：宁可当成「没验过」，也不假装它是 0。
                return -1;
            }

            try
            {
                return Convert.ToInt32(result[0]);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
