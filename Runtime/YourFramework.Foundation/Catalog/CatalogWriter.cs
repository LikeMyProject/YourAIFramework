using System;
using System.Collections.Generic;
using System.Text;
using YourAI.Core.Json;

namespace YourFramework.Catalog
{
    /// <summary>
    /// 目录表的**确定性** JSON 写出（沿用 ManifestWriter 的做法：手写 StringBuilder +
    /// 显式字段顺序 + Ordinal 排序，不依赖字典迭代顺序）。
    ///
    /// 为什么要确定性：目录表是构建期产物，会被签进包、被 diff、被热更比对。若两次
    /// 构建因为"条目书写顺序不同"就产出不同字节，那 diff 全是噪声，热更也会误判成
    /// "内容变了"。所以写出前一律按地址 Ordinal 排序、变体按标签签名排序 ——
    /// **输出只取决于内容，不取决于输入顺序**。
    /// </summary>
    public static class CatalogWriter
    {
        /// <summary>
        /// Serializes a catalog. Entries come out addressed-sorted, variants tag-signature
        /// sorted, so the same content always yields the same bytes.
        /// </summary>
        public static string Write(AddressCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }

            List<string> addresses = catalog.Addresses;
            addresses.Sort(StringComparer.Ordinal);

            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"name\":\"").Append(JsonParser.Escape(catalog.Name))
                .Append("\",\"entries\":[");

            for (int i = 0; i < addresses.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                AppendEntry(sb, catalog.Get(addresses[i]), catalog.Name);
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendEntry(StringBuilder sb, CatalogEntry entry, string catalogName)
        {
            sb.Append("{\"address\":\"").Append(JsonParser.Escape(entry.Address))
                .Append("\",\"group\":\"").Append(JsonParser.Escape(entry.Group))
                .Append("\",\"kind\":\"").Append(KindName(entry.Kind))
                .Append('"');

            if (entry.Required)
            {
                sb.Append(",\"required\":true");
            }

            // 只在与表名不同时才写 source：单表 round-trip 时靠根部的 name 就能还原归属，
            // 写出来只是噪声；合并过的表才需要逐条标注。
            if (entry.Source.Length > 0
                && !string.Equals(entry.Source, catalogName, StringComparison.Ordinal))
            {
                sb.Append(",\"source\":\"").Append(JsonParser.Escape(entry.Source)).Append('"');
            }

            List<CatalogVariant> variants = new List<CatalogVariant>(entry.Variants);
            variants.Sort(CompareVariants);

            sb.Append(",\"variants\":[");
            for (int i = 0; i < variants.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                CatalogVariant variant = variants[i];
                sb.Append("{\"tags\":[");
                for (int t = 0; t < variant.Tags.Count; t++)
                {
                    if (t > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append('"').Append(JsonParser.Escape(variant.Tags[t])).Append('"');
                }

                sb.Append("],\"locator\":\"").Append(JsonParser.Escape(variant.Locator))
                    .Append("\"}");
            }

            sb.Append("]}");
        }

        /// <summary>Tag signature first (so the default variant leads), then locator.</summary>
        private static int CompareVariants(CatalogVariant a, CatalogVariant b)
        {
            int bySignature = string.CompareOrdinal(a.TagSignature, b.TagSignature);
            return bySignature != 0 ? bySignature : string.CompareOrdinal(a.Locator, b.Locator);
        }

        private static string KindName(CatalogKind kind)
        {
            switch (kind)
            {
                case CatalogKind.Scene:
                    return "scene";
                case CatalogKind.Config:
                    return "config";
                case CatalogKind.Table:
                    return "table";
                default:
                    return "asset";
            }
        }
    }
}
