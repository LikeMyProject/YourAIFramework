using System;
using System.Collections.Generic;

namespace YourFramework.Catalog
{
    /// <summary>
    /// 问题等级。**只有 Error 会让报告不干净**：Warning 是"看着可疑但可能有意"的提醒
    /// （比如两个地址共用同一份内容），不该拦住构建；Error 是"这张表一定不对"。
    /// </summary>
    public enum CatalogIssueSeverity
    {
        Warning = 0,
        Error = 1
    }

    /// <summary>单条问题：等级 + 归属地址 + 人话说明。</summary>
    public sealed class CatalogIssue
    {
        public readonly CatalogIssueSeverity Severity;
        public readonly string Address;
        public readonly string Message;

        public CatalogIssue(CatalogIssueSeverity severity, string address, string message)
        {
            Severity = severity;
            Address = address;
            Message = message;
        }

        public override string ToString()
        {
            return "[" + Severity + "] " + Address + ": " + Message;
        }
    }

    /// <summary>
    /// 校验报告。问题按目录的**文件顺序**产出，所以同一张表每次校验结果逐条一致
    /// （构建脚本可以直接 diff 报告，而不是靠人眼扫）。
    /// </summary>
    public sealed class CatalogReport
    {
        private readonly List<CatalogIssue> _issues = new List<CatalogIssue>();

        public int IssueCount { get { return _issues.Count; } }

        public int ErrorCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _issues.Count; i++)
                {
                    if (_issues[i].Severity == CatalogIssueSeverity.Error)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public int WarningCount { get { return IssueCount - ErrorCount; } }

        /// <summary>Whether the catalog passed (warnings do not fail a build).</summary>
        public bool IsClean { get { return ErrorCount == 0; } }

        /// <summary>A copy of the issues, in production order.</summary>
        public List<CatalogIssue> Issues { get { return new List<CatalogIssue>(_issues); } }

        /// <summary>Issues of one severity, in production order.</summary>
        public List<CatalogIssue> IssuesOf(CatalogIssueSeverity severity)
        {
            List<CatalogIssue> filtered = new List<CatalogIssue>();
            for (int i = 0; i < _issues.Count; i++)
            {
                if (_issues[i].Severity == severity)
                {
                    filtered.Add(_issues[i]);
                }
            }

            return filtered;
        }

        public void Add(CatalogIssueSeverity severity, string address, string message)
        {
            _issues.Add(new CatalogIssue(severity, address, message));
        }

        /// <summary>One-line verdict, e.g. "clean" / "2 error(s), 1 warning(s)".</summary>
        public string Summary()
        {
            if (IssueCount == 0)
            {
                return "clean";
            }

            if (ErrorCount == 0)
            {
                return WarningCount + " warning(s)";
            }

            return ErrorCount + " error(s), " + WarningCount + " warning(s)";
        }

        /// <summary>All issues, newline-joined (for logs and test details).</summary>
        public string Dump()
        {
            if (IssueCount == 0)
            {
                return "(no issues)";
            }

            return string.Join("\n", _issues.ConvertAll(delegate (CatalogIssue i) { return i.ToString(); }).ToArray());
        }
    }

    /// <summary>
    /// 目录校验器：把"这张表到底对不对"从解析里分出来。
    ///
    /// 分层的理由：解析只管**单条自洽**（地址非空、变体不重复），而"必需项在目标环境
    /// 能否解析""定位符指向的东西是否真的存在""两个地址是不是撞了同一份内容"都是
    /// **跨条目 / 跨系统**的判断，需要目标环境（标签集合）与外部世界（定位符存在性）参与。
    ///
    /// 外部世界用**注入谓词**表达（<c>Func&lt;string,bool&gt; locatorExists</c>），
    /// 于是本模块不必认识 Pack / Bundle / 文件系统 —— 谁调用谁提供谓词。
    /// 这正是"可替换性由自家接口补上"的做法：换打包方案不用动校验器。
    /// </summary>
    public static class CatalogValidator
    {
        /// <summary>
        /// 校验一张目录表。
        /// <paramref name="active"/> 是目标环境的生效标签（null 等价空集）。
        /// <paramref name="locatorExists"/> 可选；给了就额外检查每个变体的定位符是否存在
        /// （构建期传"bundle 里有没有这个 asset"，运行期传 null 或查缓存表）。
        /// </summary>
        public static CatalogReport Validate(
            AddressCatalog catalog,
            CatalogTagSet active,
            Func<string, bool> locatorExists)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }

            CatalogReport report = new CatalogReport();
            List<string> addresses = catalog.Addresses;

            // 同一份内容被两个地址指向：多数是有意的（两个键共用一张图集），
            // 但也常是复制粘贴留下的笔误 —— 报 Warning 让人看一眼，不拦构建。
            Dictionary<string, string> firstClaim =
                new Dictionary<string, string>(StringComparer.Ordinal);

            for (int i = 0; i < addresses.Count; i++)
            {
                CatalogEntry entry = catalog.Get(addresses[i]);
                if (entry == null)
                {
                    continue;
                }

                if (entry.Required)
                {
                    CatalogResolution resolution = catalog.Resolve(entry.Address, active);
                    if (!resolution.Resolved)
                    {
                        report.Add(CatalogIssueSeverity.Error, entry.Address,
                            "必需项在生效标签 " + DescribeTags(active)
                            + " 下解析不出定位符（没有可命中的变体，也没有默认变体兜底）。");
                    }
                }

                if (!entry.HasDefaultVariant)
                {
                    report.Add(CatalogIssueSeverity.Warning, entry.Address,
                        "没有默认变体：在标签未覆盖的环境里会解析不出来（若这是有意的平台专属项，可忽略）。");
                }

                for (int v = 0; v < entry.Variants.Count; v++)
                {
                    CatalogVariant variant = entry.Variants[v];
                    string label = variant.IsDefault
                        ? "默认变体"
                        : "变体[" + variant.TagSignature + "]";

                    if (locatorExists != null && !locatorExists(variant.Locator))
                    {
                        report.Add(CatalogIssueSeverity.Error, entry.Address,
                            label + " 的定位符 '" + variant.Locator + "' 不存在。");
                    }

                    string owner;
                    if (firstClaim.TryGetValue(variant.Locator, out owner))
                    {
                        if (!string.Equals(owner, entry.Address, StringComparison.Ordinal))
                        {
                            report.Add(CatalogIssueSeverity.Warning, entry.Address,
                                label + " 的定位符 '" + variant.Locator + "' 已被 '"
                                + owner + "' 占用（同一份内容被两个地址指向，确认是否有意）。");
                        }
                    }
                    else
                    {
                        firstClaim.Add(variant.Locator, entry.Address);
                    }
                }
            }

            return report;
        }

        private static string DescribeTags(CatalogTagSet active)
        {
            return active == null ? "{}" : active.ToString();
        }
    }
}
