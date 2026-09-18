using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using YourFramework.Entity;
using YourFramework.UI;
using YourFramework.UnityRuntime;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// Demo 09-11：表现层——UI 面板栈 / 声音 / 实体。
    /// UI 与声音全部用运行时生成的视觉树与波形，零资产依赖。
    /// </summary>
    public static class DemoExperience
    {
        private static bool _uiRegistered;
        // ==================================================================
        // Demo 09：UI 面板栈——层级、独占屏蔽、生命周期
        // ==================================================================
        public static IEnumerator UiPanels()
        {
            var ui = DemoSetup.Ui;

            if (!DemoExperience._uiRegistered)
            {
                ui.Register(new DemoHudPanel())
                  .Register(new DemoExitPanel());
                DemoExperience._uiRegistered = true;
            }

            Debug.Log("[Demo09] 打开 HUD 层面板：");
            ui.Open("hud");
            Debug.Log("[Demo09] hud.IsOpen=" + ui.Stack.IsOpen("hud")
                + "，输入被屏蔽=" + ui.Stack.IsInputBlocked("hud"));

            yield return new WaitForSeconds(2f);

            Debug.Log("[Demo09] 打开 Popup 层（Exclusive）——下层输入被自动屏蔽：");
            ui.Open("exit");
            Debug.Log("[Demo09] popup.IsOpen=" + ui.Stack.IsOpen("exit")
                + "，hud 输入被屏蔽=" + ui.Stack.IsInputBlocked("hud"));

            yield return new WaitForSeconds(2f);

            Debug.Log("[Demo09] 关闭 Popup——屏蔽立即解除（Closing 即解锁）：");
            ui.Close("exit");
            Debug.Log("[Demo09] hud 输入被屏蔽=" + ui.Stack.IsInputBlocked("hud"));

            yield return new WaitForSeconds(1f);
            Debug.Log("[Demo09] 关闭 HUD，面板栈清空（生命周期回调见上方日志）。");
            ui.Close("hud");
        }

        /// <summary>HUD 面板：普通层，代码直接搭 UI Toolkit 视觉树。</summary>
        private sealed class DemoHudPanel : UIPanel
        {
            public override string Name { get { return "hud"; } }
            public override PanelLayer Layer { get { return PanelLayer.Normal; } }

            public override VisualElement CreateRoot()
            {
                var root = new VisualElement();
                root.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
                root.style.position = Position.Absolute;
                root.style.left = 20f; root.style.top = 20f;
                root.style.paddingTop = 10f; root.style.paddingBottom = 10f;
                root.style.paddingLeft = 14f; root.style.paddingRight = 14f;

                var title = new Label("HUD 面板（Normal 层）");
                title.style.fontSize = 18f;
                title.style.color = Color.white;
                root.Add(title);

                var hint = new Label("打开 Popup 后本面板输入会被屏蔽");
                hint.style.fontSize = 12f;
                hint.style.color = new Color(1f, 1f, 1f, 0.75f);
                root.Add(hint);
                return root;
            }

            public override void OnOpening() { Debug.Log("[Demo09]   hud.OnOpening"); }
            public override void OnOpened() { Debug.Log("[Demo09]   hud.OnOpened"); }
            public override void OnClosed() { Debug.Log("[Demo09]   hud.OnClosed"); }
        }

        /// <summary>弹窗面板：Popup 层，自带"关闭"按钮（关自己走 ui.Close）。</summary>
        private sealed class DemoExitPanel : UIPanel
        {
            public override string Name { get { return "exit"; } }
            public override PanelLayer Layer { get { return PanelLayer.Popup; } }

            public override VisualElement CreateRoot()
            {
                var root = new VisualElement();
                root.style.backgroundColor = new Color(0f, 0f, 0f, 0.8f);
                root.style.position = Position.Absolute;
                root.style.left = 0f; root.style.right = 0f;
                root.style.top = 0f; root.style.bottom = 0f;
                root.style.alignItems = Align.Center;
                root.style.justifyContent = Justify.Center;

                var box = new VisualElement();
                box.style.backgroundColor = new Color(0.15f, 0.15f, 0.18f, 1f);
                box.style.paddingTop = 24f; box.style.paddingBottom = 24f;
                box.style.paddingLeft = 32f; box.style.paddingRight = 32f;
                box.style.borderTopWidth = 1f; box.style.borderBottomWidth = 1f;
                box.style.borderLeftWidth = 1f; box.style.borderRightWidth = 1f;
                box.style.borderTopColor = new Color(1f, 1f, 1f, 0.2f);
                box.style.borderBottomColor = box.style.borderTopColor;
                box.style.borderLeftColor = box.style.borderTopColor;
                box.style.borderRightColor = box.style.borderTopColor;

                var text = new Label("这是一张 Popup 弹窗（Exclusive）\n打开期间下层收不到输入");
                text.style.color = Color.white;
                text.style.whiteSpace = WhiteSpace.Normal;
                box.Add(text);

                var close = new Button(() => DemoSetup.Ui.Close("exit")) { text = "关闭（也会看到演示自动关）" };
                close.style.marginTop = 14f;
                box.Add(close);

                root.Add(box);
                return root;
            }

            public override void OnOpening() { Debug.Log("[Demo09]   exit.OnOpening"); }
            public override void OnOpened() { Debug.Log("[Demo09]   exit.OnOpened"); }
            public override void OnClosed() { Debug.Log("[Demo09]   exit.OnClosed"); }
        }

        // ==================================================================
        // Demo 10：声音——分组混音、优先级抢断、实时静音
        // ==================================================================
        public static IEnumerator Audio()
        {
            var audio = DemoSetup.Audio;
            audio.RegisterChannel("music", maxVoices: 1);
            audio.RegisterChannel("sfx", maxVoices: 2);

            Debug.Log("[Demo10] 播放 BGM（music 通道，循环）：");
            var bgm = audio.Play("music/low/town", "music", loop: true);
            Debug.Log("[Demo10] bgm 音量 = scale × 通道 × master = " + bgm.Volume.ToString("0.00"));

            yield return new WaitForSeconds(1f);

            Debug.Log("[Demo10] sfx 上限 2，连放 3 个音效 → 第 3 个抢断优先级最低者：");
            var s1 = audio.Play("sfx/low/hit", "sfx", priority: 0);
            var s2 = audio.Play("sfx/low/cast", "sfx", priority: 0);
            var s3 = audio.Play("sfx/hit-critical", "sfx", priority: 5);
            Debug.Log("[Demo10] s1=" + (s1 == null ? "已被抢断" : "存活")
                + "，s2=" + (s2 == null ? "已被抢断" : "存活")
                + "，s3(高优先)=" + (s3 == null ? "被拒" : "存活")
                + "（抢断规则：高过最低者就偷，平级取最老；高不过就拒，返回 null）");

            yield return new WaitForSeconds(1f);

            Debug.Log("[Demo10] SetMute(sfx, true) —— 存活声部实时归零：");
            audio.SetMute("sfx", true);
            yield return new WaitForSeconds(1f);
            audio.SetMute("sfx", false);
            Debug.Log("[Demo10] 解除静音，声音回来。");

            audio.Stop(bgm.Id);
            Debug.Log("[Demo10] BGM 已单独停止（voiceId 精确停）。");
        }

        // ==================================================================
        // Demo 11：实体——实现哪些小接口就有哪些能力，不强迫继承
        // ==================================================================
        public static IEnumerator Entities()
        {
            var entities = DemoSetup.Entities;

            Debug.Log("[Demo11] 生成两个 NPC（卫兵会 Tick，石像只监听生成/回收）：");
            entities.Spawn(new DemoGuard("guard_001"));
            entities.Spawn(new DemoStatue("statue_001"));

            yield return new WaitForSeconds(1.5f);   // 期间 EntityRegistry 每帧 Pump，卫兵自己 Tick

            Debug.Log("[Demo11] 回收卫兵（IDespawnable.OnDespawn 触发）：");
            entities.Despawn("guard_001");
            entities.Despawn("statue_001");
            Debug.Log("[Demo11] 场上剩余实体: " + entities.Count);
        }

        private sealed class DemoGuard : IEntity, ISpawnable, IEntityTickable, IDespawnable
        {
            private readonly string _id;
            private int _ticks;
            public DemoGuard(string id) { _id = id; }
            public string Name { get { return _id; } }
            public void OnSpawn() { Debug.Log("[Demo11]   卫兵进场: " + _id); }
            public void EntityPump(float dt)
            {
                _ticks++;
                if (_ticks % 60 == 0) { Debug.Log("[Demo11]   卫兵巡逻中… 已 Tick " + _ticks + " 帧"); }
            }
            public void OnDespawn() { Debug.Log("[Demo11]   卫兵退场（共 Tick " + _ticks + " 帧）: " + _id); }
        }

        private sealed class DemoStatue : IEntity, ISpawnable, IDespawnable
        {
            private readonly string _id;
            public DemoStatue(string id) { _id = id; }
            public string Name { get { return _id; } }
            public void OnSpawn() { Debug.Log("[Demo11]   石像就位: " + _id + "（不实现 IEntityTickable 就不会被泵）"); }
            public void OnDespawn() { Debug.Log("[Demo11]   石像移除: " + _id); }
        }
    }
}
