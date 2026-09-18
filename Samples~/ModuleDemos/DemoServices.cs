using System.Collections;
using UnityEngine;
using YourAI.Agent;
using YourAI.Core.Ai;
using YourAI.Core.Contracts;
using YourAI.UnityRuntime;
using YourFramework.HotUpdate;
using YourFramework.Net;
using YourFramework.Platform;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// Demo 12-16：服务层——网络 / SDK 管道 / 诊断台 / 热更编排 / AI（可选）。
    /// 全部"失败也是值"：每个失败都带原因，不会拖垮演示流程。
    /// </summary>
    public static class DemoServices
    {
        private static bool _sdkRegistered;
        // ==================================================================
        // Demo 12：网络——状态机/心跳/退避重连/断线排队（连不上也全是戏）
        // ==================================================================
        public static IEnumerator Net()
        {
            var net = DemoSetup.Net;

            Debug.Log("[Demo12] 连接 " + DemoSetup.NetConfig.Url + "（演示域名必然失败——正好展示失败路径）：");
            net.Open();

            // 演示断线排队：未连接时 Send 进有界队列，连上按序补发。
            net.Send(System.Text.Encoding.UTF8.GetBytes("early-1"), 0, 7);
            net.Send(System.Text.Encoding.UTF8.GetBytes("early-2"), 0, 7);
            Debug.Log("[Demo12] 未连接时 Send 了两条 → 队列长度 " + net.QueuedCount);

            // ModuleCenter 的泵给的是 0 增量（生命周期推进）；重连计时需要真实时间，
            // 所以演示自己用 Time.deltaTime 喂——这正是"逐帧驱动"：谁驱动、喂多少，宿主说了算。
            float watch = 0f;
            while (net.State != NetState.Closed && watch < 12f)
            {
                net.Pump(Time.deltaTime);
                watch += Time.deltaTime;
                yield return null;
            }

            Debug.Log("[Demo12] 观察结束：State=" + net.State
                + "，重连尝试 " + net.ReconnectAttempts + " 次，LastReason=" + net.LastReason);
            Debug.Log("[Demo12] 会话终结后 Send 是 fail-fast（程序 bug），开新会话才是正路。");
        }

        // ==================================================================
        // Demo 13：SDK 管道——渠道故障自动降级，业务永不判 SDK 死活
        // ==================================================================
        public static IEnumerator Sdk()
        {
            var sdk = DemoSetup.Sdk;
            // 渠道在 DemoSetup.RegisterAll 里注册（必须在模块 Init 之前）。

            Debug.Log("[Demo13] 渠道已注册，GameEntry 初始化时按 InitOrder 编排（本演示在 Play 起始已完成）。");

            // 业务侧：不可用即 null，UI 自然隐藏按钮。
            var login = sdk.Get<ISdkChannel>("login");
            var pay = sdk.Get<ISdkChannel>("pay");
            Debug.Log("[Demo13] login.IsReady=" + sdk.IsReady("login")
                + "（拿到渠道对象: " + (login != null) + "）");
            Debug.Log("[Demo13] pay.IsReady=" + sdk.IsReady("pay")
                + "，Get 返回 null → " + (pay == null)
                + "，失败原因: " + sdk.FailureReason("pay"));

            yield break;
        }

        /// <summary>SDK 渠道必须在 SdkHub.Init 之前注册——由 DemoSetup.RegisterAll 调用。</summary>
        public static void RegisterSdkChannels(SdkHub sdk)
        {
            if (DemoServices._sdkRegistered)
            {
                return;
            }

            DemoServices._sdkRegistered = true;
            sdk.Register(new DemoLoginChannel());
            sdk.Register(new DemoPayChannel());     // Initialize 会抛异常 → 渠道自动降级
        }

        private sealed class DemoLoginChannel : ISdkChannel
        {
            public string Name { get { return "login"; } }
            public int InitOrder { get { return 10; } }
            public void Initialize() { Debug.Log("[Demo13]   login 渠道初始化成功"); }
            public bool IsAvailable { get { return true; } }
        }

        private sealed class DemoPayChannel : ISdkChannel
        {
            public string Name { get { return "pay"; } }
            public int InitOrder { get { return 20; } }
            public void Initialize()
            {
                throw new System.InvalidOperationException("支付 SDK 未初始化：缺少商户号配置");
            }
            public bool IsAvailable { get { return false; } }
        }

        // ==================================================================
        // Demo 14：诊断台——计数器/仪表/错误环/一键快照
        // ==================================================================
        public static IEnumerator Diagnostics()
        {
            var diag = DemoSetup.Diag;

            diag.Inc("items.sold");                 // 计数器 +1
            diag.Inc("items.sold");
            diag.Inc("items.sold", 3);              // 步进 +3
            diag.Set("fps", 59.9);                  // 仪表
            diag.RecordError("演示：这是一条被诊断台捕获的错误");

            Debug.Log("[Demo14] items.sold=" + diag.GetValue("items.sold")
                + "，fps=" + diag.GetValue("fps")
                + "，累计错误=" + diag.TotalErrors);
            Debug.Log("[Demo14] 一键快照（调试面板/远程上报只消费它）：\n" + diag.Snapshot());

            yield break;
        }

        // ==================================================================
        // Demo 15：热更编排——阶段顺序/进度事件/失败截停/终态纪律
        // ==================================================================
        public static IEnumerator HotUpdate()
        {
            var flow = DemoSetup.Hot;
            var listener = DemoSetup.HotListener;

            // ---- 第一轮：三个假阶段全部成功 ----
            Debug.Log("[Demo15] 第一轮：检查版本 → 下载补丁 → 应用（每阶段泵约 1 秒）：");
            flow.Run(new IHotUpdateStep[]
            {
                new DemoHotStep("检查版本", 40),
                new DemoHotStep("下载补丁", 60),
                new DemoHotStep("应用", 30),
            });
            while (flow.IsRunning) { flow.Pump(); yield return null; }

            yield return new WaitForSeconds(1f);

            // ---- 第二轮：Reset 后重跑，第二阶段失败 → 截停 ----
            Debug.Log("[Demo15] 第二轮：第二阶段故意失败 → 后续阶段不启动（终态纪律）：");
            flow.Reset();
            flow.Run(new IHotUpdateStep[]
            {
                new DemoHotStep("检查版本", 30),
                new DemoHotStep("下载补丁", 45, failAtPump: 20, failReason: "补丁服务器 503"),
                new DemoHotStep("应用", 30),
            });
            while (flow.IsRunning) { flow.Pump(); yield return null; }
            Debug.Log("[Demo15] 失败原因已通过 OnFailed(阶段名, 原因) 交给 UI 有话可说。");
        }

        /// <summary>假热更阶段：Begin 一次 + Pump 每帧推进，跟资源/网络同款泵哲学。</summary>
        private sealed class DemoHotStep : IHotUpdateStep
        {
            private readonly string _name;
            private readonly int _frames;
            private readonly int _failAtPump;
            private readonly string _failReason;
            private int _pumped;

            public DemoHotStep(string name, int frames, int failAtPump = -1, string failReason = null)
            {
                _name = name;
                _frames = frames;
                _failAtPump = failAtPump;
                _failReason = failReason;
            }

            public string Name { get { return _name; } }

            public void Begin(HotUpdateContext context)
            {
                _pumped = 0;
                Debug.Log("[Demo15]   " + _name + ".Begin");
            }

            public HotUpdateStepState Pump()
            {
                _pumped++;
                if (_failAtPump > 0 && _pumped >= _failAtPump)
                {
                    return HotUpdateStepState.Failed;   // 失败原因经 flow 交给 listener
                }

                if (_pumped == _frames)
                {
                    return HotUpdateStepState.Done;
                }

                if (_pumped % 15 == 0)
                {
                    int percent = _pumped * 100 / _frames;
                    Debug.Log("[Demo15]   " + _name + " 进度 " + percent + "%");
                }

                return HotUpdateStepState.Running;
            }
        }

        // ==================================================================
        // Demo 16：AI（可选）——无 Key 照常演示"失败也是值"；配了 Key 就是真对话
        // ==================================================================
        public static IEnumerator Ai()
        {
            string apiKey = DemoSetup.Settings.GetString("ai.apiKey", "");
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.Log("[Demo16] 未配置 AI Key —— 框架承诺：AI 缺席，一切照常。");
                Debug.Log("[Demo16] 想看真对话：在 demo-settings.json 里加 \"ai.apiKey\": \"sk-...\" 再重放本演示。");
            }
            else
            {
                Debug.Log("[Demo16] 检测到 Key，发起一次流式对话：");
            }

            AiAgent agent;
            try
            {
                agent = AiRuntimeFactory.CreateDeepSeekAgent("demo-agent", apiKey, "你是一个演示助手，回答保持一句话。");
            }
            catch (System.Exception ex)
            {
                // 连"造不出服务"都是值：演示继续，不拖垮巡演。
                Debug.Log("[Demo16] AI 服务创建失败（失败也是值）: " + ex.Message);
                yield break;
            }

            // 发起一回合：逐帧驱动 —— BeginTurn 非阻塞，每帧 Pump 推进流式输出。
            IAgentTurn turn = agent.BeginTurn("player", "用一句话介绍你自己。", null);
            turn.Delta += text => Debug.Log("[Demo16] 流式增量: " + text);

            float watch = 0f;
            while (!turn.IsDone && watch < 20f)
            {
                turn.Pump();                    // 真实项目由 GameEntry/AiModule 每帧调
                watch += Time.deltaTime;
                yield return null;
            }

            if (!turn.IsDone)
            {
                turn.Cancel();                  // 主人说不等了：不抛异常、无回调拖尾
                Debug.Log("[Demo16] 20 秒未完成，已取消（Cancel 是干净放弃，无拖尾回调）。");
                yield break;
            }

            AgentTurnResult result = turn.Result;
            if (result.Success)
            {
                Debug.Log("[Demo16] 对话完成 ✔ " + result.Text
                    + "（耗时 " + result.ElapsedMs.ToString("0") + "ms，上下文 " + result.ContextTokens + " tokens）");
            }
            else
            {
                Debug.Log("[Demo16] 对话失败（失败也是值，原因真实可读）: " + result.Error
                    + "（HTTP " + result.HttpStatus + "）");
            }
        }
    }
}
