using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using YourAI.UnityRuntime;
using YourFramework.Assets;
using YourFramework.Audio;
using YourFramework.Core;
using YourFramework.Diagnostics;
using YourFramework.Entity;
using YourFramework.Flow;
using YourFramework.HotUpdate;
using YourFramework.Localization;
using YourFramework.Net;
using YourFramework.Platform;
using YourFramework.Save;
using YourFramework.UnityRuntime;
using YourFramework.UnityRuntime.Audio;

namespace YourAIFramework.ModuleDemos
{
    /// <summary>
    /// 演示总装：一次性把所有模块注册进 GameEntry。
    ///
    /// 上手只需三步：
    /// 1. 新建空场景（保留默认 Main Camera）；
    /// 2. 建一个空 GameObject，挂上 <see cref="DemosHub"/>；
    /// 3. 按 Play——Console 会依次上演全部 16 个模块演示。
    ///
    /// 注册顺序就是依赖关系：InitOrder 小的先初始化，数字摆在明面上。
    /// </summary>
    public static class DemoSetup
    {
        private static bool _done;

        public static FrameworkDiagnostics Diag { get; private set; }
        public static AssetManager Assets { get; private set; }
        public static AudioManager Audio { get; private set; }
        public static EntityRegistry Entities { get; private set; }
        public static ProcedureFlow Flow { get; private set; }
        public static NetClient Net { get; private set; }
        public static SdkHub Sdk { get; private set; }
        public static UIPanelManager Ui { get; private set; }
        public static HotUpdateFlow Hot { get; private set; }
        public static LocalizationStore Loc { get; private set; }
        public static SaveSystem Saves { get; private set; }
        public static SettingStore Settings { get; private set; }
        public static DemoNetListener NetListener { get; private set; }
        public static DemoHotListener HotListener { get; private set; }
        public static NetClientConfig NetConfig { get; private set; }

        public static void RegisterAll()
        {
            if (_done)
            {
                return;
            }

            _done = true;

            GameObject root = new GameObject("[DemoRoot]");
            Object.DontDestroyOnLoad(root);

            Diag = new FrameworkDiagnostics();                                   // -100 最早：错误都进诊断台
            Assets = new AssetManager(new List<IAssetProvider>                   //   20
            {
                new MemoryAssetProvider(),
                new FileAssetProvider(Application.temporaryCachePath),
            });
            Audio = new AudioManager(new UnityAudioBackend(root, 8, ResolveClip)); //   20
            Entities = new EntityRegistry();                                     //   25
            Flow = new ProcedureFlow("Demo", 30);                                //   30
            NetListener = new DemoNetListener();
            NetConfig = NetClientConfig.CreateDefault("wss://demo.invalid.example/ws");
            NetConfig.MaxReconnectAttempts = 2;          // 演示：2 次重连后进终态
            NetConfig.ReconnectBaseDelaySeconds = 1f;    // 退避 1, 2, 4… 秒封顶
            Net = new NetClient(new ClientWebSocketLink(), NetConfig, NetListener);
            Sdk = new SdkHub();                                                  //   50
            Ui = new UIPanelManager(CreatePanelSettings());                      //   50
            HotListener = new DemoHotListener();
            Hot = new HotUpdateFlow(HotListener);                                //   60
            Saves = new SaveSystem(Path.Combine(Application.persistentDataPath, "DemoSaves"));
            Settings = new SettingStore(Path.Combine(Application.persistentDataPath, "demo-settings.json"));
            Settings.LoadFromDisk();

            DemoServices.RegisterSdkChannels(Sdk);   // 渠道必须在 SdkHub.Init 之前注册

            GameEntry.Ensure()
                .Register(Diag)
                .Register(Assets)
                .Register(Audio)
                .Register(Entities)
                .Register(Flow)
                .Register(Net)
                .Register(Sdk)
                .Register(Ui)
                .Register(Hot);

            Debug.Log("[DemoSetup] 全部模块已注册，GameEntry.Start 后按 InitOrder 依次初始化。");
        }

        /// <summary>
        /// 运行时生成 PanelSettings——演示不依赖任何工程资产；
        /// 真实项目请用 Create &gt; UI Toolkit &gt; Panel Settings 资产。
        /// </summary>
        private static PanelSettings CreatePanelSettings()
        {
            return ScriptableObject.CreateInstance<PanelSettings>();
        }

        /// <summary>
        /// 程序化生成正弦波音效——演示不依赖任何音频文件；
        /// 真实项目这里换成从 AssetManager / YooAsset 取 AudioClip。
        /// </summary>
        private static readonly Dictionary<string, AudioClip> ClipCache =
            new Dictionary<string, AudioClip>();

        private static AudioClip ResolveClip(string key)
        {
            AudioClip clip;
            if (ClipCache.TryGetValue(key, out clip))
            {
                return clip;
            }

            int sampleRate = 44100;
            int samples = sampleRate / 2;                       // 半秒
            float freq = key.Contains("low") ? 220f : 660f;
            clip = AudioClip.Create("demo-" + key, samples, 1, sampleRate, false);
            float[] data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float fade = 1f - (float)i / samples;           // 尾部淡出
                data[i] = Mathf.Sin(2f * Mathf.PI * freq * i / sampleRate) * 0.4f * fade;
            }

            clip.SetData(data, 0);
            ClipCache[key] = clip;
            return clip;
        }
    }

    /// <summary>网络演示监听器：把 NetClient 的五个回调全部翻译成人话打进 Console。</summary>
    public sealed class DemoNetListener : INetClientListener
    {
        public void OnOpen()
        {
            Debug.Log("[DemoNet] 已连接（排队中的消息开始补发）");
        }

        public void OnData(byte[] buffer, int offset, int count)
        {
            Debug.Log("[DemoNet] 收到 " + count + " 字节");
        }

        public void OnDropped(string reason)
        {
            Debug.Log("[DemoNet] 掉线，原因: " + reason + " —— 进入自动重连");
        }

        public void OnReconnectAttempt(int attemptNumber, float delaySeconds)
        {
            Debug.Log("[DemoNet] 第 " + attemptNumber + " 次重连（退避 " + delaySeconds.ToString("0.0") + "s）");
        }

        public void OnClosed(string reason)
        {
            Debug.Log("[DemoNet] 会话终结，原因: " + (string.IsNullOrEmpty(reason) ? "干净关闭" : reason));
        }
    }

    /// <summary>热更演示监听器：阶段开始/完成/失败三件事。</summary>
    public sealed class DemoHotListener : IHotUpdateListener
    {
        public void OnStageStarted(string stageName, int stageIndex, int stageCount)
        {
            Debug.Log("[DemoHot] 阶段开始 " + stageName + " (" + (stageIndex + 1) + "/" + stageCount + ")");
        }

        public void OnStageDone(string stageName)
        {
            Debug.Log("[DemoHot] 阶段完成 " + stageName);
        }

        public void OnFailed(string stageName, string reason)
        {
            Debug.Log("[DemoHot] 热更失败于 " + stageName + "，原因: " + reason + "（终态，后续阶段不会启动）");
        }

        public void OnSucceeded()
        {
            Debug.Log("[DemoHot] 全部阶段完成 ✔");
        }
    }
}
