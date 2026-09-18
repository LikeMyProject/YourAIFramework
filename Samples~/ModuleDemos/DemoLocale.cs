using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YourFramework.Localization;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// 自研本地化包速览：语言标识与回退链 → 多集合表与共享表 → CLDR 复数 →
    /// 内联格式化 → 本地化资源地址 → 插进 LocalizationStore 的源插槽。
    ///
    /// 与 08 号演示的分工：08 讲的是 LocalizationStore 的"语言链 + 兜底 + 源插槽"，
    /// 本个讲的是内容组织层（LocalePack）。两者叠加就是完整的本地化链路。
    /// </summary>
    internal static class DemoLocale
    {
        public static IEnumerator LocalePack()
        {
            // ---- 1. 语言标识：解析、归一化、规范内截断 ----
            LocaleId device = LocaleId.Parse("zh-hans-cn");
            List<string> chain = device.AutoChain();
            Debug.Log("[Demo18] 语言标识 LocaleId：'zh-hans-cn' 归一为 " + device.Tag
                + "（语言 " + device.Language + " / 文字 " + device.Script + " / 地区 " + device.Region + "）");
            Debug.Log("[Demo18] 回退链 = " + string.Join(" → ", chain.ToArray())
                + "（只做截断，不做 zh-TW⇒zh-Hant 那种补全 —— 要就显式写进 FallbackLanguages）");
            yield return null;

            // ---- 2. 多集合表 + 共享表：共享的是同一个对象，不是副本 ----
            LocalePack pack = new LocalePack();
            pack.LoadTable("zh-CN", "UI", "{\"ui.title\":\"山河问剑录（简体）\",\"ui.play\":\"开始游戏\"}");
            pack.LoadTable("zh", "UI", "{\"ui.title\":\"山河问剑录\",\"ui.quit\":\"退出\"}");
            pack.LoadTable("en", "UI",
                "{\"ui.title\":\"Mountain and Sword\",\"ui.settings\":\"Settings\","
                + "\"ui.items\":\"{count:plural:one{1 item}|other{{count} items}}\"}");

            StringTable shared = new StringTable("zh", "shared");
            shared.LoadJson("{\"common.ok\":\"确定\"}");
            pack.Collection("UI").AddSharedTable(shared);

            pack.SetFallbacks("en");
            pack.CurrentLanguage = "zh-CN";
            Debug.Log("[Demo18] 集合 × 语言链 [zh-CN → zh → en]：ui.title 命中 zh-CN 自己的表 → "
                + pack.Get("ui.title"));
            Debug.Log("[Demo18]   ui.quit 在 zh-CN 表里没有，落链上更基础的 zh → " + pack.Get("ui.quit")
                + "；ui.settings 只有 en 有 → " + pack.Get("ui.settings"));
            Debug.Log("[Demo18] 共享表 common.ok → " + pack.Get("common.ok")
                + "（同一个 StringTable 实例可被多个集合引用，改一处处处生效）");
            yield return null;

            // ---- 3. CLDR 复数：同一个模板，不同语言走不同形态 ----
            LocaleArguments one = new LocaleArguments().Add("count", 1);
            LocaleArguments three = new LocaleArguments().Add("count", 3);
            int u1;
            int u3;
            Debug.Log("[Demo18] 复数（文案来自 en，按英语规则）：count=1 → "
                + pack.Format("ui.items", one, out u1) + "；count=3 → "
                + pack.Format("ui.items", three, out u3)
                + "。注意：复数按**文案实际命中的语言**判，不是当前语言。");
            Debug.Log("[Demo18] 规则表查一把：ru 的 1/2/5/21 → "
                + PluralRules.Select("ru", 1) + "/" + PluralRules.Select("ru", 2) + "/"
                + PluralRules.Select("ru", 5) + "/" + PluralRules.Select("ru", 21)
                + "；ar 的 0/1/2/5/15 → " + PluralRules.Select("ar", 0) + "/"
                + PluralRules.Select("ar", 1) + "/" + PluralRules.Select("ar", 2) + "/"
                + PluralRules.Select("ar", 5) + "/" + PluralRules.Select("ar", 15));
            yield return null;

            // ---- 4. 内联格式化：具名/位置/复数同模板共存，取值失败只计数不抛 ----
            LocaleFormatter formatter = pack.FormatterFor("zh");
            int unresolved;
            string line = formatter.Format(
                "{name} 的 {0} 号订单：{count:plural:other{{count} 件}}，备注 {note}",
                new LocaleArguments().Add("name", "少侠").Add(7).Add("count", 2), out unresolved);
            Debug.Log("[Demo18] 格式化 → " + line + "（未解析占位符 " + unresolved
                + " 个：note 没传，原样留着 —— UI 有内容可显，诊断有数可看）");
            yield return null;

            // ---- 5. 本地化资源地址：按语言算出地址，再交给资源系统 ----
            pack.Assets.LoadJson(
                "{\"ui.logo\":{\"zh-CN\":\"Assets/UI/{locale}/logo.png\"},"
                + "\"ui.banner\":{\"zh\":\"Assets/UI/{language}/banner.png\"},"
                + "\"ui.icon\":\"Assets/UI/icon.png\"}");
            string logo;
            string banner;
            string icon;
            pack.TryGetAddress("ui.logo", out logo);
            pack.TryGetAddress("ui.banner", out banner);
            pack.TryGetAddress("ui.icon", out icon);
            Debug.Log("[Demo18] 资源地址：ui.logo → " + logo + "（{locale} 展开成命中的完整标签）");
            Debug.Log("[Demo18]   ui.banner → " + banner + "（{language} 只取基础语言）；"
                + "ui.icon → " + icon + "（与语言无关的兜底地址，换语言不变）");
            Debug.Log("[Demo18] 拿到地址后交给 AssetManager.Load(address) 即可，本地化与资源加载完全解耦。");
            yield return null;

            // ---- 6. 插进 LocalizationStore 的源插槽：两层叠加成完整链路 ----
            LocalizationStore store = new LocalizationStore();
            store.LoadJson("zh-CN", "{\"ui.stone\":\"灵石\"}");
            store.CurrentLanguage = "zh-CN";
            store.AddSource(pack);
            Debug.Log("[Demo18] 插进源插槽：store.Get(\"ui.stone\")=" + store.Get("ui.stone")
                + "（来自 store 自己的表）；store.Get(\"common.ok\")=" + store.Get("common.ok")
                + "（store 表未命中，落到本地化包这个源）");
            Debug.Log("[Demo18] 本地化链路 = LocalizationStore（语言链/兜底/源插槽）"
                + " + LocalePack（多集合表/共享表/CLDR 复数/内联格式化/资源地址）。");
        }
    }
}
