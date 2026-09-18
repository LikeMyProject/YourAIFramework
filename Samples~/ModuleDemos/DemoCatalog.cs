using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YourFramework.Catalog;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// 地址目录表速览（一张表管到底）：解析 → 变体解析 → 补丁合并 → 校验 → 确定性写出。
    ///
    /// 这一套解决的是"地址写死在代码里"的三个老问题：
    ///   - 打哪个包、哪些是必载 —— 变成表里可以查、可以校验的声明；
    ///   - 同一份资源在不同画质档/平台要取不同的东西 —— 用标签变体 + 子集匹配表达；
    ///   - 发补丁改了哪几条 —— 合并时保留来源归属，可追溯。
    ///
    /// 与 06 号 DataTable 的分工：DataTable 管**游戏数值/配置**，目录表管**地址与加载单位**；
    /// 目录表刻意不认识任何打包实现（那是 17 号 AssetPack 的事），两者只通过"地址 → 定位符"对接。
    /// </summary>
    internal static class DemoCatalog
    {
        private static void Log(string line)
        {
            Debug.Log("[Demo21] " + line);
        }

        // 一张底表：登录场景（多档变体）、HUD（简写）、Boss 战 BGM（两档）、战斗调参（共用定位符）。
        private const string BaseJson = @"{
          ""name"": ""base"",
          ""entries"": [
            { ""address"": ""ui/login"", ""group"": ""ui"", ""kind"": ""scene"", ""required"": true,
              ""variants"": [
                { ""locator"": ""Assets/UI/Login.prefab"" },
                { ""tags"": [""hd""], ""locator"": ""Assets/UI/Login_hd.prefab"" },
                { ""tags"": [""hd"", ""zh""], ""locator"": ""Assets/UI/Login_hd_zh.prefab"" }
              ] },
            { ""address"": ""ui/hud"", ""group"": ""ui"", ""locator"": ""Assets/UI/Hud.prefab"" },
            { ""address"": ""battle/boss/bgm"", ""group"": ""battle"",
              ""variants"": [
                { ""locator"": ""Audio/Boss.ogg"" },
                { ""tags"": [""hd""], ""locator"": ""Audio/Boss_hd.ogg"" }
              ] },
            { ""address"": ""battle/tuning"", ""group"": ""battle"", ""kind"": ""config"",
              ""locator"": ""Assets/UI/Hud.prefab"" }
          ]
        }";

        // 一张补丁表：只改 HUD，加一条新地址。
        private const string PatchJson = @"{
          ""name"": ""patch-2026-09"",
          ""entries"": [
            { ""address"": ""ui/hud"", ""group"": ""ui"", ""locator"": ""Assets/UI/Hud_v2.prefab"" },
            { ""address"": ""ui/rank"", ""group"": ""ui"", ""locator"": ""Assets/UI/Rank.prefab"" }
          ]
        }";

        private static string Describe(CatalogTagSet tags)
        {
            return tags.Count == 0 ? "（无标签）" : "{" + string.Join(",", tags.Tags.ToArray()) + "}";
        }

        public static IEnumerator AddressCatalogDemo()
        {
            Log("地址目录表：把散落在代码里的地址收成一张能离线校验的表。");

            AddressCatalog catalog = AddressCatalog.FromJson(BaseJson);
            Log("  解析出 " + catalog.Count + " 条地址，分组 = "
                + string.Join(", ", catalog.Groups().ToArray()));

            List<CatalogEntry> required = new List<CatalogEntry>();
            catalog.FindRequired(required);
            Log("  标记为必载的有 " + required.Count + " 条：" + required[0].Address
                + " —— 必载项在目标环境解析不出来就是构建错误。");

            yield return null;

            // ---- 变体解析：同一个地址，换个环境就换个定位符 ----
            CatalogTagSet[] environments =
            {
                CatalogTagSet.Empty,
                new CatalogTagSet(new string[] { "hd" }),
                new CatalogTagSet(new string[] { "hd", "zh" }),
                new CatalogTagSet(new string[] { "hd", "boss" })
            };

            Log("变体解析（同一个地址在不同环境下取到不同的东西）：");
            for (int i = 0; i < environments.Length; i++)
            {
                CatalogResolution login = catalog.Resolve("ui/login", environments[i]);
                Log("  ui/login " + Describe(environments[i]) + " → " + login.Locator
                    + (login.IsDefault ? "（兜底默认档）" : "（命中 " + login.MatchedTags + " 个标签）"));
            }

            // 子集语义：变体要求标签全部生效，不是"有交集就算"
            CatalogResolution partial = catalog.Resolve("battle/boss/bgm", new CatalogTagSet(new string[] { "zh" }));
            Log("  battle/boss/bgm {zh} → " + partial.Locator
                + " —— 只带 zh 命中不了任何专属档，回退默认；标签是「全都要有」而不是「有一个就行」。");

            // 解析不到是值不是异常
            CatalogResolution missing = catalog.Resolve("ui/nope", CatalogTagSet.Empty);
            Log("  ui/nope → 解析出来了吗？" + missing.Resolved
                + " —— 查不到不抛异常（可选地址本就允许缺失），由校验器决定要不要报。");

            yield return null;

            // ---- 补丁合并 ----
            Log("补丁合并（发行补丁只写差异）：");
            List<string> before = catalog.Addresses;
            int countBefore = catalog.Count;

            catalog.Merge(AddressCatalog.FromJson(PatchJson));

            Log("  条目 " + countBefore + " → " + catalog.Count
                + "，头两条仍是 " + before[0] + " / " + before[1] + "（被覆盖的保留原槽位，遍历顺序稳定）");
            Log("  ui/hud 现在是 " + catalog.Require("ui/hud").Variants[0].Locator
                + "，归属 = " + catalog.Require("ui/hud").Source);
            Log("  没被动过的 ui/login 归属仍是 " + catalog.Require("ui/login").Source
                + " —— 出问题能查到是哪张表改的。");

            yield return null;

            // ---- 校验：外部世界用注入谓词表达 ----
            Log("校验（定位符存在性由调用方注入，目录表不认识打包实现）：");

            // 假装只有这几个定位符真的存在，其余都是悬空的
            Func<string, bool> exists = delegate (string locator)
            {
                return locator == "Assets/UI/Login.prefab"
                    || locator == "Assets/UI/Hud_v2.prefab"
                    || locator == "Assets/UI/Rank.prefab"
                    || locator == "Audio/Boss.ogg";
            };

            CatalogTagSet target = new CatalogTagSet(new string[] { "hd", "zh" });
            CatalogReport report = CatalogValidator.Validate(catalog, target, exists);

            Log("  目标环境 " + Describe(target) + " → " + report.Summary()
                + "（错误拦构建，警告只提醒）");
            Log("  干净吗？" + report.IsClean + " —— 必需项解析不出、定位符不存在算错误；"
                + "两个地址共用同一份内容只算警告（复用是合法的，但常是复制粘贴笔误）。");

            List<CatalogIssue> errors = report.IssuesOf(CatalogIssueSeverity.Error);
            for (int i = 0; i < errors.Count && i < 3; i++)
            {
                Log("    ! " + errors[i]);
            }

            yield return null;

            // ---- 确定性写出 ----
            Log("确定性写出（构建期产物要能 diff，所以输出只取决于内容）：");
            string json = CatalogWriter.Write(catalog);
            string again = CatalogWriter.Write(AddressCatalog.FromJson(json));

            Log("  写出 " + json.Length + " 字节；再解析再写出 → 逐字节相同？" + (json == again)
                + "（幂等，热更比对不会因为书写顺序不同就误判内容变了）");
            Log("  输出片段：" + json.Substring(0, Math.Min(150, json.Length)) + " ...");

            Log("演示完毕：地址可查、变体可匹配、补丁可追溯、表可校验也可确定性输出 —— 地址从此不再靠记忆。");
        }
    }
}
