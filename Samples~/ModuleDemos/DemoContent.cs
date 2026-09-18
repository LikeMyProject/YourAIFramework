using System.Collections;
using System.IO;
using UnityEngine;
using YourAI.Core.Json;
using YourFramework.Assets;
using YourFramework.Data;
using YourFramework.Flow;
using YourFramework.Localization;
using YourFramework.Save;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// Demo 04-08：内容管线——存档/设置 / 流程状态机 / 数据表 / 资源 / 本地化。
    /// 全部自包含：JSON 内联在代码里，文件写在 temporaryCachePath / persistentDataPath。
    /// </summary>
    public static class DemoContent
    {
        // ==================================================================
        // Demo 04：存档与设置——原子写、坏档隔离、键值设置
        // ==================================================================
        public static IEnumerator SaveAndSettings()
        {
            // ---- 多槽位存档：任意 JSON 值都能存 ----
            JsonValue payload = JsonParser.Parse("{\"level\":3,\"gold\":120}");
            string path = DemoSetup.Saves.Save("slot1", payload, version: 1);
            Debug.Log("[Demo04] 已写入存档 slot1 → " + path);

            JsonValue loaded;
            int version;
            string error;
            if (DemoSetup.Saves.TryLoad("slot1", out loaded, out version, out error))
            {
                Debug.Log("[Demo04] 读回 slot1（版本 " + version + "）: " + loaded.ToJson());
            }
            else
            {
                Debug.Log("[Demo04] 读取失败也是值: " + error);
            }

            Debug.Log("[Demo04] 现有槽位: " + string.Join(", ",
                DemoSetup.Saves.ListSlots().ConvertAll(s => s.Slot).ToArray()));

            // ---- 键值设置：显式落盘，缺省有兜底 ----
            var settings = DemoSetup.Settings;
            settings.SetInt("music.volume", 80);
            settings.SetString("last.slot", "slot1");
            settings.SaveToDisk();
            Debug.Log("[Demo04] 设置已落盘: music.volume=" + settings.GetInt("music.volume", 50)
                + "，last.slot=" + settings.GetString("last.slot", "无"));

            // 重新读盘验证（真实项目是下次启动时）
            var reloaded = new SettingStore(GetSettingsPath());
            reloaded.LoadFromDisk();
            Debug.Log("[Demo04] 重新读盘: music.volume=" + reloaded.GetInt("music.volume", 50)
                + "（兜底值 50 只在键不存在时生效）");

            yield break;
        }

        private static string GetSettingsPath()
        {
            return Path.Combine(Application.persistentDataPath, "demo-settings.json");
        }

        // ==================================================================
        // Demo 05：流程状态机——启动流程骨架，切换延迟到下一拍
        // ==================================================================
        public static IEnumerator ProcedureFlowDemo()
        {
            var flow = new ProcedureFlow("DemoTour", 30);
            flow.Add(new DemoBootState())
                .Add(new DemoMenuState())
                .Add(new DemoPlayingState())
                .Start<DemoBootState>();

            Debug.Log("[Demo05] 启动流程 Boot → Menu → Playing，每态停留约 1 秒：");
            while (flow.State == ProcedureFlowState.Running)
            {
                flow.Pump();                    // 注册成模块时 GameEntry 每帧替你泵
                yield return null;
            }

            Debug.Log("[Demo05] 流程优雅结束（State=" + flow.State + "）；切换延迟到下一拍，同帧连环切不会打架。");
        }

        private sealed class DemoBootState : Procedure
        {
            private float _elapsed;
            public override string Name { get { return "Boot"; } }
            public override void OnEnter() { Debug.Log("[Demo05]   → Boot.OnEnter（检查版本/加载配置）"); }
            public override void OnUpdate()
            {
                _elapsed += Time.deltaTime;
                if (_elapsed > 1f) { Host.ChangeProcedure<DemoMenuState>(); }
            }
            public override void OnExit() { Debug.Log("[Demo05]   ← Boot.OnExit"); }
        }

        private sealed class DemoMenuState : Procedure
        {
            private float _elapsed;
            public override string Name { get { return "Menu"; } }
            public override void OnEnter() { Debug.Log("[Demo05]   → Menu.OnEnter（主菜单）"); }
            public override void OnUpdate()
            {
                _elapsed += Time.deltaTime;
                if (_elapsed > 1f) { Host.ChangeProcedure("Playing"); }   // 按名字切也可以
            }
            public override void OnExit() { Debug.Log("[Demo05]   ← Menu.OnExit"); }
        }

        private sealed class DemoPlayingState : Procedure
        {
            private float _elapsed;
            public override string Name { get { return "Playing"; } }
            public override void OnEnter() { Debug.Log("[Demo05]   → Playing.OnEnter（进游戏）"); }
            public override void OnUpdate()
            {
                _elapsed += Time.deltaTime;
                if (_elapsed > 1f) { Host.Stop(); }
            }
            public override void OnExit() { Debug.Log("[Demo05]   ← Playing.OnExit"); }
        }

        // ==================================================================
        // Demo 06：数据表——JSON 配表、类型化读取、点路径、格式化、整表替换
        // ==================================================================
        public static IEnumerator DataTableDemo()
        {
            var tables = new DataTableSet();
            tables.LoadJson("items", "["
                + "{\"id\":\"sword_001\",\"name\":\"铁剑\",\"price\":120,\"sellable\":true,"
                + "\"desc\":\"{name} 售价 {price} 文\",\"stats\":{\"hp\":10,\"atk\":7}},"
                + "{\"id\":\"shield_001\",\"name\":\"木盾\",\"price\":60,\"sellable\":false,"
                + "\"desc\":\"{name} 售价 {price} 文\",\"stats\":{\"hp\":15,\"atk\":0}}"
                + "]");

            DataTable items = tables.Get("items");
            Debug.Log("[Demo06] 铁剑: name=" + items.GetString("sword_001", "name")
                + "，price=" + items.GetInt("sword_001", "price")
                + "，sellable=" + items.GetBool("sword_001", "sellable"));
            Debug.Log("[Demo06] 点路径读嵌套: stats.atk=" + items.GetInt("sword_001", "stats.atk"));
            Debug.Log("[Demo06] 模板格式化: " + items.Format("sword_001", "desc"));
            Debug.Log("[Demo06] 缺字段走兜底: " + items.GetInt("sword_001", "price.nope", 999)
                + "；缺行也兜底: " + items.GetString("no_such_row", "name", "未知物品"));

            // 同表名再 Load = 整表替换——数值热更的落点。
            tables.LoadJson("items", "["
                + "{\"id\":\"sword_001\",\"name\":\"铁剑\",\"price\":150,\"sellable\":true,"
                + "\"desc\":\"{name} 售价 {price} 文\",\"stats\":{\"hp\":10,\"atk\":7}}"
                + "]");
            Debug.Log("[Demo06] 整表替换后: price=" + tables.Get("items").GetInt("sword_001", "price")
                + "（120 → 150，这就是配表热更）");

            yield break;
        }

        // ==================================================================
        // Demo 07：资源管理——统一入口轮询加载，失败是值
        // ==================================================================
        public static IEnumerator AssetsDemo()
        {
            // 内存 Provider：代码内内容直接进缓存链（测试替身/内置文本都走这里）
            var memory = new MemoryAssetProvider();
            memory.Add("demo://greeting", AssetPayload.FromText("你好，来自内存 Provider 的资源！"));

            // 文件 Provider：往 temporaryCachePath 写一个文件再加载
            string textPath = Path.Combine(Application.temporaryCachePath, "demo-note.txt");
            File.WriteAllText(textPath, "这是 FileAssetProvider 从磁盘读到的文本。", System.Text.Encoding.UTF8);

            var assets = new AssetManager(new System.Collections.Generic.List<IAssetProvider>
            {
                memory,                                          // 先问内存
                new FileAssetProvider(Application.temporaryCachePath),   // 再问磁盘
            });

            Debug.Log("[Demo07] 发起两个加载（不阻塞，轮询推进）：");
            AssetRequest fromMemory = assets.Load("demo://greeting");
            AssetRequest fromFile = assets.Load("demo-note.txt");

            while (!fromMemory.IsDone || !fromFile.IsDone)
            {
                yield return null;                              // 真实项目里 GameEntry 已替你泵
            }

            Debug.Log("[Demo07] 内存资源就绪: " + fromMemory.Payload.Text);
            Debug.Log("[Demo07] 文件资源就绪: " + fromFile.Payload.Text);

            // 失败是值：不存在的 key 不抛异常，落 Failed + 原因。
            AssetRequest missing = assets.Load("no/such/file.json");
            while (!missing.IsDone) { yield return null; }
            Debug.Log("[Demo07] 不存在的资源: IsFailed=" + missing.IsFailed + "，原因: " + missing.Error);

            // 参数 bug 才当场抛（fail-fast）：空 key。
            try
            {
                assets.Load("");
            }
            catch (System.ArgumentException ex)
            {
                Debug.Log("[Demo07] 空 key 被当场拦截: " + ex.Message);
            }
        }

        // ==================================================================
        // Demo 08：本地化——多语言、回退链、格式化、缺 key 有话可显
        // ==================================================================
        public static IEnumerator LocalizationDemo()
        {
            var loc = new LocalizationStore();
            loc.LoadJson("zh", "{\"ui.title\":\"山河问剑录\",\"ui.play\":\"开始游戏\",\"item.sword\":\"剑名 {0}，价值 {1} 文\"}");
            loc.LoadJson("en", "{\"ui.title\":\"Mountain & Blade\",\"ui.play\":\"Play\",\"ui.extra\":\"English only key\"}");
            loc.FallbackLanguages.Add("en");        // zh 没有的键回落到 en
            loc.CurrentLanguage = "zh";

            Debug.Log("[Demo08] zh: " + loc.Get("ui.title") + " / " + loc.Get("ui.play"));
            Debug.Log("[Demo08] 格式化（不变文化，数字不漂移）: " + loc.Get("item.sword", "铁剑", 3.5));
            Debug.Log("[Demo08] 回退链生效（zh 无此键 → en）: " + loc.Get("ui.extra"));

            loc.OnLanguageChanged += lang => Debug.Log("[Demo08] 语言切换事件 → " + lang);
            loc.CurrentLanguage = "en";
            Debug.Log("[Demo08] en: " + loc.Get("ui.title") + " / " + loc.Get("ui.play"));

            // 全链未命中 → 返回键名本身，UI 永远有话可显。
            Debug.Log("[Demo08] 未命中键返回键名: " + loc.Get("ui.nowhere"));
            Debug.Log("[Demo08] 显式检查: loc.Has(\"ui.play\")=" + loc.Has("ui.play"));

            yield break;
        }
    }
}
