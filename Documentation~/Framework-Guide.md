# Your AI Framework —— 通用框架完整指南

> 写给**任何 Unity 开发者**：不管你用不用 AI 功能，这份指南都能让你把框架用起来。
> 目标读者是"会写 C#、会用 Unity，但第一次接触本框架"的人。每一节都先讲"这是什么、
> 什么时候用"，再给可以直接抄进工程的代码。

- 框架包名：`com.your.ai.framework`（UPM 包，Unity 6000.5+）
- 两份文档的分工：**本文**讲通用框架（内核 + 全部业务模块）与上手路径；
  [`YourAI-Guide.md`](YourAI-Guide.md) 讲 AI（LLM）模块的深度用法与设计取舍、附录 A
  有每个模块的设计决策记录。
- 一句话定位：**模块化、低耦合、可测试的 Unity 游戏框架**——AI 只是其中一个可选模块。

---

## 目录

1. [五分钟跑起来](#1-五分钟跑起来)
2. [核心概念：ModuleCenter 与"泵"哲学](#2-核心概念modulecenter-与泵哲学)
3. [模块速查表](#3-模块速查表)
4. [事件总线 EventBus](#4-事件总线-eventbus)
5. [对象池 ObjectPool / GameObjectPool](#5-对象池-objectpool--gameobjectpool)
6. [存档 SaveSystem 与设置 SettingStore](#6-存档-savesystem-与设置-settingstore)
7. [流程状态机 ProcedureFlow](#7-流程状态机-procedureflow)
8. [UI：面板栈 PanelStack 与 UIPanelManager](#8-ui面板栈-panelstack-与-uipanelmanager)
9. [数据表 DataTable](#9-数据表-datatable)
10. [资源管理 AssetManager](#10-资源管理-assetmanager)
11. [本地化 LocalizationStore](#11-本地化-localizationstore)
12. [声音 AudioManager](#12-声音-audiomanager)
13. [网络 NetClient](#13-网络-netclient)
14. [实体注册表 EntityRegistry](#14-实体注册表-entityregistry)
15. [SDK 管道 SdkHub](#15-sdk-管道-sdkhub)
16. [诊断台 FrameworkDiagnostics](#16-诊断台-frameworkdiagnostics)
17. [热更编排 HotUpdateFlow](#17-热更编排-hotupdateflow)
18. [AI 模块（可选）](#18-ai-模块可选)
19. [完整引导示例（可直接抄）](#19-完整引导示例可直接抄)
20. [框架如何对待你的错误（错误姿态总表）](#20-框架如何对待你的错误错误姿态总表)
21. [目录结构与测试](#21-目录结构与测试)
22. [FAQ](#22-faq)

---

## 1. 五分钟跑起来

### 1.1 安装

把本仓库的 `YourAIFramework/` 目录放进工程的 `Packages/` 下（或用 Package Manager
的 "Add package from disk" 指向 `package.json`）。无需任何外部依赖即可编译运行；
资源包、本地化、热更程序集都是**自家实现**，不需要任何第三方包；引擎侧实现
（AssetBundle 装载 / UnityWebRequest 下载）在 `YourFramework.UnityRuntime` 里。

### 1.2 最小工程：一个引导脚本 + 一个模块

在场景里随便一个物体上挂这个脚本，运行，Console 会看到模块初始化日志：

```csharp
using UnityEngine;
using YourFramework.Core;
using YourFramework.UnityRuntime;

public class GameBootstrap : MonoBehaviour
{
    void Awake()
    {
        GameEntry.Ensure()                       // 找到或创建 [GameEntry] 宿主
            .Register(new MyFirstModule());      // 注册你的模块（可链式多个）
    }
}

public class MyFirstModule : IModule
{
    public string Name => "MyFirst";
    public int InitOrder => 0;                   // 小的先初始化
    public void Init(ModuleCenter host) { Debug.Log("我的第一个模块上线了"); }
    public void Pump() { }                       // 每帧调用
    public void Shutdown() { }                   // 退出时调用（逆序）
}
```

就这么多。**GameEntry 只是一个引擎壳**：它把 Unity 的 `Start/Update/OnDestroy`
翻译成模块的 `Init → Pump → Shutdown`，自己不知道任何具体模块。你注册什么，框架
就有什么——不注册就没有，零负担。

---

## 2. 核心概念：ModuleCenter 与"泵"哲学

理解这两点，框架所有模块的设计就都顺了：

**① 一切皆模块。** 功能以 `IModule` 为单位注册进 `ModuleCenter`：

- `InitOrder` 小的先初始化——**依赖就是数字**：你只能依赖 InitOrder 比你小的模块
  （`host.Get<T>()` 取依赖，T 是模块类型，每类型一个实例，重复注册直接抛异常）；
- 关停时逆序 Shutdown，单模块 Shutdown 抛异常不拖垮别人。

**② 泵（Pump）驱动一切。** 框架公开 API 里**没有 async/await、没有回调风暴**。
所有"需要跨帧的事情"——资源加载、网络收发、热更下载、声音回收——都是同一个形状：

```csharp
// 发起（非阻塞）
var request = assets.Load("configs/items.json");
// 每帧推进（GameEntry 的 Update 已经替你泵了；带时间的泵自己调）
void Update() {
    if (request.IsDone) {
        // request.IsReady → request.Payload；request.IsFailed → request.Error
    }
}
```

为什么这样设计：**谁驱动、何时驱动，宿主说了算**。游戏可以暂停、快进、回放、
在无头环境跑测试——只要不调 Pump，世界就是静止的。这也是框架 248 项离线断言
能够存在的原因：所有逻辑不碰引擎就能测。

---

## 3. 模块速查表

| 模块 | 类 | 一句话 | 是 IModule？ |
|---|---|---|---|
| 模块中心 | `ModuleCenter` | 注册/排序/泵/关停的容器 | （是容器本身） |
| 事件总线 | `EventBus` | 发布/订阅，同步+延迟双模式 | 否（`EventBus.Global` 直接用） |
| 对象池 | `ObjectPool<T>` / `GameObjectPool` | C# 对象 / GameObject 复用 | 否 |
| 存档 | `SaveSystem` | 槽位 JSON 存档，原子写+坏档隔离 | 否（服务类） |
| 设置 | `SettingStore` | 键值设置，可选混淆 | 否（服务类） |
| 流程机 | `ProcedureFlow` | 启动流程/游戏状态的状态机 | 是 |
| UI 面板 | `PanelStack` + `UIPanelManager` | 面板生命周期+层级+独占屏蔽 | 是（引擎壳） |
| 数据表 | `DataTable` / `DataTableSet` | JSON 配表，主键索引，类型化读取 | 否（服务类） |
| 资源 | `AssetManager` + 自研资源包 | 统一加载入口 + 清单/版本/下载/缓存 | 是 |
| 本地化 | `LocalizationStore` | 多语言+回退链+格式化 | 否（服务类） |
| 声音 | `AudioManager` | 分组混音+声部抢断 | 是 |
| 网络 | `NetClient` | WebSocket 状态机+心跳+重连 | 是 |
| 实体 | `EntityRegistry` | NPC/机关/组件统一住址 | 是 |
| SDK 管道 | `SdkHub` | 平台渠道编排+故障降级 | 是 |
| 诊断台 | `FrameworkDiagnostics` | 计数器+错误环+一键快照 | 是 |
| 热更 | `HotUpdateFlow` | 检查→下载→应用→重载的编排器 | 是 |
| AI（可选） | `AiModule` + 七层 | LLM 对话/工具/团队 | 是 |

**"是 IModule？"为"否"的服务类**：直接 `new`，想进模块体系就包一层自己的
IModule（十几行的事）。框架不强迫一切皆模块。

---

## 4. 事件总线 EventBus

**干什么**：游戏系统之间发通知，不让系统互相引用。UI 监听背包变化、任务系统监听
击杀事件——全走总线。

```csharp
// 事件就是普通类/struct
public struct PlayerDied { public string Reason; }

// 发布（同步、零装箱——struct 直接走）
EventBus.Global.Publish(new PlayerDied { Reason = "坠落" });

// 订阅
EventBus.Global.Subscribe<PlayerDied>(e => Debug.Log("玩家死了：" + e.Reason));
```

要点：

- **双模式**：`Publish` 同步立即派发；`Post` 进队列、`bus.Pump()` 时按序派发
  （双缓冲，一次 Pump = 一帧的量）——想避免"事件在调用栈深处改状态"就用 Post；
- **派发中订阅/退订是安全的**：当帧不收到，下一帧生效；
- `EventBus.Global` 是现成实例；大型工程可以 `new EventBus()` 按域隔离。

---

## 5. 对象池 ObjectPool / GameObjectPool

**干什么**：子弹、飘字、粒子这类高频创建销毁的东西，复用而不是 GC。

```csharp
// C# 对象池
var pool = new ObjectPool<StringBuilder>(() => new StringBuilder(), null, sb => sb.Clear());
var sb = pool.Get();
try { sb.Append("hello"); /* 用它 */ }
finally { pool.Release(sb); }         // 借还必须配平：重复归还是 bug，当场抛
```

```csharp
// GameObject 池（引擎侧）：挂在一个根节点下
var fxPool = new GameObjectPool(fxPrefab, fxRoot, maxIdle: 256);
GameObject fx = fxPool.Get();          // 取（克隆预制体并激活）
fxPool.Release(fx);                    // 还（自动 SetActive(false)、归位）
```

要点：LIFO 复用（刚还的最先借出，缓存友好）；借/还失衡是程序 bug，立即 fail-fast。

---

## 6. 存档 SaveSystem 与设置 SettingStore

**干什么**：单机存档（多槽位）和玩家设置（音量、画质键值）。

```csharp
var saves = new SaveSystem(Application.persistentDataPath + "/Saves");

JsonValue payload = JsonParser.Parse("{\"level\":3,\"gold\":120}");  // 任意 JSON 值
string path = saves.Save("slot1", payload, version: 1);                // 原子写

if (saves.TryLoad("slot1", out JsonValue loaded, out int version, out string error))
{
    /* loaded 里是你的数据（JsonValue，loaded.ToJson() 可再序列化） */
}
else { /* 坏档/缺失走这里，error 带原因——失败是值，不抛异常 */ }

foreach (SaveSlotMeta meta in saves.ListSlots()) { }   // 列槽位
saves.Delete("slot1");
```

要点（都是替你踩好的坑）：

- **原子写**：先写临时文件再替换，写一半断电不毁档；
- **坏档隔离**：JSON 损坏的档自动改名 `.corrupt` 隔离，读取永不抛异常
  （"失败是值"——返回 null，你的 UI 有话可说）；
- 槽位名只允许 `[A-Za-z0-9_-]`，别的名字是 bug，当场抛。

```csharp
var settings = new SettingStore(Application.persistentDataPath + "/settings.json");
settings.SetInt("music.volume", 80);
settings.SetFloat("sfx.volume", 0.8f);
settings.SetString("last.slot", "slot1");
settings.SaveToDisk();                                  // 显式落盘
int vol = settings.GetInt("music.volume", 50);          // 缺省有兜底
```

`SettingStore` 还支持 `IValueCodec`（内置 `XorValueCodec`）做轻量混淆——防玩家
手改 JSON 够用，防破解请用系统级方案。

---

## 7. 流程状态机 ProcedureFlow

**干什么**：启动流程（Logo→主菜单→加载→游戏）、游戏大状态（战斗/暂停/结算）
这类"同一时刻只在一个状态"的东西。

```csharp
public class BootState : Procedure {          // 继承 Procedure，覆写三段
    protected internal override void OnEnter() { /* 进状态：开始加载配置 */ }
    protected internal override void OnUpdate() { /* 每帧：加载完了就切 */ }
    protected internal override void OnExit() { /* 出状态：清理 */ }
}

var flow = new ProcedureFlow();
flow.Register<BootState>().Register<MenuState>().Register<PlayingState>();
flow.ChangeProcedure<BootState>();            // 切换是"下一泵生效"（防同帧重入）
flow.Pump();                                  // GameEntry 会替你泵
```

要点：状态切换延迟到下一帧执行——同一帧内连环切状态不会打架；状态回调抛异常被
隔离上报，状态机不死。典型的启动流程：

```
Boot(检查热更) → Patch(热更中) → Menu(主菜单) → Game(游戏内)
```

---

## 8. UI：面板栈 PanelStack 与 UIPanelManager

分两层：**纯 C# 内核**（PanelStack：生命周期/层级/屏蔽，离线可测）+ **引擎壳**
（UIPanelManager：挂到 Unity 6 的 PanelRenderer 上）。

```csharp
// ① 定义一个面板：继承 UIPanel，CreateRoot 里搭 UI（纯代码或 UXML clone 均可）
public class SettingsPanel : UIPanel
{
    public override string Name => "settings";
    public override PanelLayer Layer => PanelLayer.Popup;   // 弹窗层，自动屏蔽下层

    public override VisualElement CreateRoot()
    {
        var root = new VisualElement();
        root.style.backgroundColor = new Color(0, 0, 0, 0.8f);
        var label = new Label("设置");
        var closeBtn = new Button(() => ui.Close("settings")) { text = "关闭" };
        root.Add(label); root.Add(closeBtn);
        return root;
    }

    [Inject] public UIPanelManager ui;   // 见下：Init 时由宿主喂进来也行
}

// ② 注册并打开
var ui = new UIPanelManager(panelSettingsAsset)
    .Register(new SettingsPanel())
    .Register(new HudPanel());
GameEntry.Ensure().Register(ui);
ui.Open("settings");
```

要点：

- **生命周期两段式**：`Opening → Open → Closing → Closed`，
  `CompleteOpen/CompleteClose` 接口为动画和异步资源预留（现在开合同帧完成，
  将来接入资源包异步加载 UI 预制体时接口不变）；
- **层级自动屏蔽**：`PanelLayer.Popup` 及以上的 `Exclusive` 面板打开时，下层收不到
  输入（`Stack.IsInputBlocked("hud")` 可查询）；Closing 状态立即解除屏蔽；
- **PanelRenderer 重载自愈**：Unity 重建视觉树后，管理器自动重建分层容器并重挂
  打开中的面板；
- **每帧泵**：面板实现 `IPanelTickable`，Open 期间自动获得每帧 `PanelPump()`；
- `ui.CloseAll()` 顶层先关。

---

## 9. 数据表 DataTable

**干什么**：策划配表（Excel → 导出 JSON）的运行时读取。格式就是 JSON 行数组：

```json
[ {"id":"sword_001","name":"铁剑","price":120,"desc":"{name}售价{price}文"} ]
```

```csharp
var tables = new DataTableSet();
tables.LoadJson("items", itemsJson);            // 文件/网络来的 JSON 字符串

var items = tables.Get("items");
string name  = items.GetString("sword_001", "name");
int    price = items.GetInt("sword_001", "price", 999);       // 缺字段走兜底
bool   ok    = items.GetBool("sword_001", "sellable");
var    deep  = items.GetInt("sword_001", "stats.hp");         // 点路径读嵌套
string tip   = items.Format("sword_001", "desc");             // "铁剑售价120文"
```

要点：

- **坏数据当场炸**（fail-fast）：坏 JSON、缺主键、主键重复、非对象行——加载时就
  报清楚第几行，绝不留到运行中静默查空；
- **缺行/缺字段走兜底**（"缺"是业务不是错误）；
- **同表名再 Load = 整表替换**——这就是数值热更的落点（热更管线落地后直接对接）；
- 加载后只读，可多线程并发查。

---

## 10. 资源管理 AssetManager 与自研资源包

**干什么**：全工程统一的资源加载入口。你只面对 `AssetManager`，后端随便换——
自研资源包、本地直读都只是 Provider。

```csharp
var assets = new AssetManager(new IAssetProvider[] {
    new MemoryAssetProvider(),                               // 代码内内容/测试替身
    new FileAssetProvider(Application.streamingAssetsPath),  // 兜底直读
    new PackAssetProvider(manifest, bundleBackend, ledger),  // 自研资源包（见 10.1）
});
GameEntry.Ensure().Register(assets);                          // InitOrder 20

// 用：轮询，不等待
AssetRequest req = assets.Load("configs/items.json");
// 每帧（或下一帧）：
if (req.IsDone) {
    if (req.IsReady)  Debug.Log(req.Payload.Text);            // 文本扩展名自动解 UTF-8
    else              Debug.LogWarning(req.Error);            // 失败带原因，不抛异常
}
```

要点：

- **同 key 全程唯一**：重复 Load 拿到同一个请求（自动去重），Ready 后进缓存；
  `assets.Drop("key")` 显式逐出（会通知 Provider 归还引用，见 10.1）；
- **并发上限** `MaxConcurrent`（默认 4）内按 Provider 优先级派发；
- **失败是值**：文件不存在、路径穿越、Provider 抛异常——全部变成
  `Failed + Error 原因`，你的 UI 有话可说；`Load("")` 这种参数 bug 才当场抛；
- 自定义 Provider 十几行就能写（实现 `Handles` 纯谓词 + `Pump` 推进即可，
  内置的 `FileAssetProvider` 就是现成范例）。

### 10.1 自研资源包：清单 → 版本 → 下载 → 缓存 → 加载

框架自带一套资源包实现（不依赖任何第三方包），五件东西各管一段：

| 件 | 类型 | 职责 |
|---|---|---|
| 清单 | `PackageManifest` | 有什么 bundle、每个 bundle 的 hash/crc/size、地址→bundle、bundle 依赖 |
| 版本文件 | `PackageVersion` + `PackageDiff` | 这一版各 bundle 的指纹；与本地版本比出差量（该下什么、能删什么） |
| 下载队列 | `DownloadQueue` + `IDownloadBackend` | 并发上限、指数退避重试、超时截停、断点续传，全部泵驱动 |
| 缓存账本 | `CacheLedger` | 引用计数 + 完整性标记 + LRU 逐出名单（只出名单，删文件归你） |
| 加载 | `PackAssetProvider` + `IBundleBackend` | 地址 → 依赖闭包（拓扑序）→ 逐帧装载 → 取出资源 |

**打包**：把资源按"一级子目录 = 一个 bundle"放进 `Assets/YourAI/AssetPackSource/`，
菜单 **YourAI → 资源包 → 构建资源包（当前平台）**，产物落在 `Build/AssetPack/`：
bundle 文件 + `<包名>.manifest.json` + `<包名>.version.json`。依赖关系不用手写——
构建器直接取 Unity 产出的 AssetBundleManifest，人的依赖表和打包结果不会打架。

**运行时**（清单与版本都来自上面的产物）：

```csharp
var manifest = PackageManifest.LoadJson(manifestText);   // 坏清单当场抛（构建期 bug）
var remote   = PackageVersion.LoadJson(remoteVersionText);
var local    = File.Exists(localVersionPath)
    ? PackageVersion.LoadJson(File.ReadAllText(localVersionPath)) : null;

PackageDiffResult diff = PackageDiff.Compute(local, remote);   // 只下差量
Debug.Log(diff.Explain());                                     // "新增 2 个、更新 1 个…需下载 N 字节"

var ledger  = new CacheLedger();                               // 缓存簿记（引用计数 + LRU）
var backend = new AssetBundleBackend(cacheRoot);
var queue   = new DownloadQueue(new UnityWebDownloadBackend()) { MaxConcurrent = 3, MaxAttempts = 3 };
foreach (string bundle in diff.Added)   queue.Enqueue(bundle, baseUrl + bundle, cacheRoot + bundle, size);
foreach (string bundle in diff.Changed) queue.Enqueue(bundle, baseUrl + bundle, cacheRoot + bundle, size);
// 每帧：queue.Pump(Time.deltaTime); 直到 queue.IsIdle
//        queue.HasFailures ? 显示 queue.FirstFailureReason : 继续

GameEntry.Ensure().Register(new AssetManager(new IAssetProvider[] {
    new PackAssetProvider(manifest, backend, ledger),
}));
```

要点：

- **闭包按拓扑序装载**：拿一个地址，先把它依赖的 bundle 全部备好再取资源；每帧只
  推进当前一个 bundle（有界工作量），已经就绪的连续推进不空等；
- **断点续传靠一个事实源**：后端报告"已落盘多少字节"，队列把它作为下次尝试的
  `resumeFrom`，不自己拼偏移；
- **失败是值贯穿到底**：断网、超时、服务器忽略 Range、bundle 损坏、资源名不在
  bundle 内——每一个都带原因地停在 `Failed + Error`；
- **接口留着**：`IBundleBackend` / `IDownloadBackend` 是引擎边界，`AssetBundleBackend`
  与 `UnityWebDownloadBackend` 只是默认实现，换后端（加密包、自定义传输）不动上半场。

---

## 11. 本地化 LocalizationStore

**干什么**：多语言。JSON 平铺键值表，按语言加载：

```json
{ "ui.title": "山河问剑录", "ui.play": "开始游戏", "item.sword": "剑名 {0}，价值 {1} 文" }
```

```csharp
var loc = new LocalizationStore();
loc.LoadJson("zh", zhJson);
loc.LoadJson("en", enJson);
loc.FallbackLanguages.Add("en");       // zh 没有的键回落到 en
loc.DefaultLanguage = "en";            // 最后的兜底语言
loc.CurrentLanguage = "zh";            // 切语言（触发 OnLanguageChanged 一次）

loc.Get("ui.title");                   // "山河问剑录"
loc.Get("item.sword", "铁剑", 3.5);    // "剑名 铁剑，价值 3.5 文"（不变文化，数字不随设备漂移）
loc.Get("ui.nowhere");                 // 全链未命中 → 返回键名本身，UI 永远有话可显
loc.Has("ui.play");                    // 显式检查是否存在
```

要点：回退链显式逐跳（"zh-CN" 绝不隐式回落 "zh"，要回落就写进链里）；同语言再
Load = 整表替换（热更语义）；需要多表集合/复数变体/外部取词时，`AddSource(...)`
挂一个 `ILocalizationSource` 实现并表使用。

### 11.1 自研本地化包：语言标识 → 多表 → 复数 → 格式化 → 资源地址

`LocalizationStore` 管的是"当前是哪个语言、全链没命中怎么办"；`LocalePack` 管的是
"内容怎么组织"。两者不重复能力：本地化包实现 `ILocalizationSource`，`store.AddSource(pack)`
一插即合，上层只跟 store 打交道。

| 件 | 类型 | 职责 |
|---|---|---|
| 语言标识 | `LocaleId` | BCP-47 子集（language[-Script][-REGION]）解析、大小写归一、规范内截断出回退链 |
| 表 | `StringTable` / `StringTableCollection` | 一个语言一张自己的表（再 Load 即整表替换）；集合跨语言，另有共享表层 |
| 复数 | `PluralRules` | CLDR 类别（zero/one/two/few/many/other）按语族归类的精选规则表 |
| 格式化 | `LocaleFormatter` + `LocaleArguments` | `{name}` / `{0}` / `{0:N0}` / `{count:plural:one{…}\|other{…}}` |
| 资源地址 | `AssetTable` | key → 按语言算出的资源地址，支持 `{locale}` / `{language}` 占位 |
| 门面 | `LocalePack` | 收口以上全部，并实现 `ILocalizationSource` |

**装载与查询**：

```csharp
var pack = new LocalePack();
pack.LoadTable("zh-CN", "UI", zhCnJson);        // 装载时归一：zh_cn 与 zh-CN 落同一张表
pack.LoadTable("zh",    "UI", zhJson);
pack.LoadTable("en",    "UI", enJson);
pack.SetFallbacks("en");                         // 每个回退按同样规则截断后拼接、去重
pack.CurrentLanguage = "zh-CN";                  // 链 = zh-CN → zh → en

pack.Get("ui.title");                            // 命中 zh-CN 自己的表
pack.Get("ui.settings");                         // 一路落到底，命中 en
pack.Collection("UI").AddSharedTable(commonTable);   // 共享表共享的是同一个实例，不是副本

// 复数按"文案实际命中的语言"判：回退到英文文案就按英语规则选形态
int unresolved;
pack.Format("ui.items", new LocaleArguments().Add("count", 1), out unresolved);

// 本地化资源：算出地址 → 交给资源系统，两边完全解耦
pack.Assets.LoadJson("{\"ui.logo\":{\"zh-CN\":\"Assets/UI/{locale}/logo.png\"}}");
string address;
pack.TryGetAddress("ui.logo", out address);      // "Assets/UI/zh-CN/logo.png"
```

**引擎侧取文件**：`UnityLocaleLoader` 按 `<根目录>/<locale>/<名字>.json` 读取。根目录是
普通路径（编辑器 / PC / iOS）就同步文件读，是 URL（Android 的 `jar:file://`、WebGL）就
走 `UnityWebRequest` —— 用"根里有没有 `://`"判形态，比按平台硬编码分支更难写错。

```csharp
var loader = new UnityLocaleLoader(Application.streamingAssetsPath + "/Locale");
loader.Request("zh-CN", "UI");
// 每帧：loader.Pump();
// while (loader.TryTakeSettled(out var r)) {
//     if (r.IsReady) pack.LoadTable(r.Locale, r.Collection, r.Json);
//     else Debug.LogWarning(r.Error);      // 缺某语言的表是发行期常态，不是异常
// }
```

要点：

- **缺 key 是业务，坏数据是 bug**：查不到返回 key 本身；坏 JSON、非字符串值、空 key 在
  装载期就抛；复数形态缺 `other`、类别名写错，由 `LocaleFormatter.Validate` 在工具链上挡住；
- **复数是内联形态**，模板自带全部形态，不会因为少建一个 `.one` 键而在运行时静默退化；
- **取值失败是计数不是异常**：`Format(..., out int unresolved)` 报出未解析的占位符数目，
  UI 有内容可显、诊断有数可看；显式传 `null` 渲染成空串且不计；
- **链上每一跳都是显式的**：截断只从右往左丢，"zh-CN 不隐式回落 zh"这条铁律不受影响；
  要 zh-TW ⇒ zh-Hant 那种补全，在 `SetFallbacks` 里写出来；
- **语言是格式化器的一部分**：`LocalePack` 按标签缓存 `LocaleFormatter`，复数规则跟着语言走。

---

## 12. 声音 AudioManager

**干什么**：声音分组（master/music/sfx/ui）、音量管理、声部上限——发声本体交给
引擎后端。

```csharp
// 引导：池大小 = 物理声部上限；ClipKey 解析器接你的资源系统
var audio = new AudioManager(new UnityAudioBackend(root, 16,
    key => /* 从 AssetManager 拿 AudioClip */ null));
audio.RegisterChannel("music", maxVoices: 2);
audio.RegisterChannel("sfx", maxVoices: 8);
GameEntry.Ensure().Register(audio);

audio.Play("music/town", "music", loop: true);                  // 背景乐
audio.Play("sfx/slash", "sfx", priority: 1, volumeScale: 0.8f); // 音效
audio.SetVolume("master", 0.8f);                                // 全局音量滑条
audio.SetMute("sfx", true);                                     // 静音（存活声部实时归零）
```

要点：

- 最终音量 = 每次播放 scale × 通道音量 × master 音量；`SetVolume/SetMute`
  **立即推送进所有存活声部**，不是只对下一个声音生效；
- 通道满时：新声音优先级高过最低者就**抢断**（平级取最老），高不过就**拒绝**
  （`Play` 返回 null）——拒绝是业务不是错误；
- 返回的 `AudioVoice` 可 `audio.Stop(voice.Id)` 单独停。

---

## 13. 网络 NetClient

**干什么**：长连接游戏业务（WebSocket）。连接状态机、心跳、断线重连、断线补发
全部内置且**显式可配**。

```csharp
var config = NetClientConfig.CreateDefault("wss://game.example.com/ws");
config.HeartbeatIntervalSeconds = 15f;    // 每 15s 发一次 ping
config.HeartbeatTimeoutSeconds  = 30f;    // 30s 没有入站字节判死
config.MaxReconnectAttempts     = 5;      // 重试 5 次（0 = 无限）
config.ReconnectBaseDelaySeconds = 1f;    // 退避 1,2,4,8s 封顶
config.QueueCapacity            = 64;     // 断线时排队补发的容量

var net = new NetClient(new ClientWebSocketLink(), config, new MyNetListener());
GameEntry.Ensure().Register(net);
net.Open();

// MyNetListener 实现五个回调：
// OnOpen() / OnData(bytes, offset, count) / OnDropped(reason) /
// OnReconnectAttempt(attempt, delay) / OnClosed(reason)
// 所有失败都带原因（reason 非空），UI 有话可说。
net.Send(payload, 0, payload.Length);     // 未连接时自动排队，连上按序补发
```

要点：

- `Pump(delta)` 时间注入——回放/快进/离线测试天然支持；
- 队列溢出丢**最旧**（新状态比旧状态重要），丢弃计数可见；
- 彻底关闭（`Closed`）后 `Send` 直接抛——会话已死，开新会话才是正路；
- WebGL 等特殊平台自己写一个 `INetLink` 实现（几十行），上层不动。

---

## 14. 实体注册表 EntityRegistry

**干什么**：NPC、机关、子弹、后台系统组件的统一住址。实现哪些小接口就有哪些能力，
**不强迫继承任何框架基类**。

```csharp
public class NpcGuard : IEntity, ISpawnable, IEntityTickable, IDespawnable
{
    public string Name => "guard_001";
    public void OnSpawn() { /* 进场 */ }
    public void EntityPump(float dt) { /* 每帧 AI/移动 */ }
    public void OnDespawn() { /* 出场清理 */ }
}

var entities = new EntityRegistry();
GameEntry.Ensure().Register(entities);
entities.Spawn(new NpcGuard());
entities.Despawn("guard_001");
```

要点：Spawn 抛异常 = 实体没进门（无半孵化状态）；Tick 抛异常 = 单实体隔离上报，
帧继续；重名 fail-fast；引擎宿主用 `Pump(Time.deltaTime)` 喂时间增量。

---

## 15. SDK 管道 SdkHub

**干什么**：微信登录、TapTap 支付、广告、推送这类平台渠道的统一编排。**渠道故障
自动降级**，游戏业务永远不用写 if-else 判 SDK 死活。

```csharp
public class WeChatLogin : ISdkChannel
{
    public string Name => "weChat";
    public int InitOrder => 10;               // login 先于 pay
    public void Initialize() { /* 调平台 SDK；抛异常=渠道不可用 */ }
    public bool IsAvailable => true;
}

var sdk = new SdkHub();
sdk.Register(new WeChatLogin());
sdk.Register(new TapPay());
GameEntry.Ensure().Register(sdk);             // Init 时按 InitOrder 编排

// 业务侧：不可用即 null，UI 自然隐藏按钮
var login = sdk.Get<ISdkChannel>("weChat");
if (login != null) { /* 走微信登录 */ }
string why = sdk.FailureReason("tapPay");     // 渠道挂了？原因在这
```

---

## 16. 诊断台 FrameworkDiagnostics

**干什么**：运行期全框架的健康状况：计数器、仪表、最近错误环、一键文本快照。
调试面板和远程上报都只消费 `Snapshot()`。

```csharp
var diag = new FrameworkDiagnostics();
GameEntry.Ensure().Register(diag);            // InitOrder -100，最早注册
// Init 时自动挂钩：所有模块的运行期错误自动落进诊断台

diag.Inc("items.sold");                       // 计数器
diag.Set("fps", 59.9);                        // 仪表
Debug.Log(diag.Snapshot());                   // 排好序的文本快照（含最近 64 条错误）
```

---

## 17. 热更编排 HotUpdateFlow

**干什么**：版本检查 → 补丁下载 → 应用 → 程序集重载的**编排器**。阶段是插槽
（`IHotUpdateStep`）：编排只管顺序、进度事件、失败截停、终态语义，每帧推进一步。

```csharp
var flow = new HotUpdateFlow(new MyProgressUi());    // 四个进度回调
flow.Context.Set("hotUpdateAssemblies", new[] { "HotUpdate.dll" });
flow.Context.Set("aotMetadataAssemblies", new[] { "mscorlib.dll" });
flow.Run(new IHotUpdateStep[] {
    new CheckVersionStep(),                          // 你的阶段：版本比对，写下载名单
    new DownloadPatchStep(),                         // 你的阶段：驱动 DownloadQueue 到 Idle
    new ReloadAssembliesStep()                       // 你的阶段：程序集按依赖序装载
});
GameEntry.Ensure().Register(flow);
```

要点：任何阶段失败（或抛异常）→ `OnFailed(阶段名, 原因)` 终态，后续阶段不启动；
阶段顺序、进度事件、终态纪律全部由编排器保证；步骤就是"Begin 一次 + Pump 每帧"，
跟资源/网络的泵哲学同款。

---

## 18. AI 模块（可选）

AI（LLM 对话、流式输出、工具调用、多智能体圆桌）是框架的**可选加成**，不引 API
Key 完全不影响上面所有功能。最小用法：

```csharp
AiAgent agent = AiRuntimeFactory.CreateDeepSeekAgent("smith", apiKey, "你是铁匠。");
var ui = new UIPanelManager(panelSettings).Register(new ChatPanel(agent));
GameEntry.Ensure()
    .Register(ui)
    .Register(new AiModule(() => agent, EventBus.Global));   // InitOrder 100，最晚初始化
ui.Open("chat");

// 不经 UI 的最小回合——泵驱动，与资源/网络同一形状：
IAgentTurn turn = agent.BeginTurn("player", "打个招呼。", worldState: null);
turn.Delta += text => Debug.Log(text);        // 流式增量，每帧 Pump 期间到达
while (!turn.IsDone) { turn.Pump(); }         // 每帧推进；turn.Cancel() 干净放弃
if (turn.Result.Success) { /* turn.Result.Text 即最终回复 */ }
else { /* turn.Result.Error 带原因（含 HTTP 状态）——失败是值 */ }
```

流式输出、工具调用、审批、记忆、检索增强、多智能体——见
[`YourAI-Guide.md`](YourAI-Guide.md)。

---

## 19. 完整引导示例（可直接抄）

```csharp
using UnityEngine;
using YourFramework.Audio;
using YourFramework.Core;
using YourFramework.Diagnostics;
using YourFramework.Entity;
using YourFramework.UnityRuntime;

public class GameBootstrap : MonoBehaviour
{
    [SerializeField] private PanelSettings _panelSettings;   // Create > UI Toolkit > Panel Settings
    [SerializeField] private Transform _audioRoot;

    void Awake()
    {
        GameEntry.Ensure()
            .Register(new FrameworkDiagnostics())            // -100 最早：错误都进诊断台
            .Register(new EntityRegistry())                  //   25
            .Register(new ProcedureFlow())                   //   30（示例省略状态注册）
            .Register(new SdkHub())                          //   50（渠道用前注册）
            .Register(new AudioManager(
                new UnityAudioBackend(_audioRoot.gameObject, 16, LoadClip))) // 20
            .Register(new UIPanelManager(_panelSettings));   //   50
    }

    private AudioClip LoadClip(string key)
    {
        // 生产环境接 AssetManager 的轮询包装；示例直接返回 null（Play 会安全放弃）
        return null;
    }
}
```

初始化顺序一目了然：数字就是依赖关系。往 `GameEntry.Ensure()` 的链上加你需要的
模块即可——**框架没有"必须全开"的负担**。

---

## 20. 框架如何对待你的错误（错误姿态总表）

框架对错误只有两种态度，且每个 API 属于哪一类是明确的：

| 态度 | 什么时候 | 表现 | 例子 |
|---|---|---|---|
| **fail-fast** | 程序 bug（布线/合约违规） | 当场抛异常 | 重复注册模块、借还池失衡、Play 未注册通道、终态会话 Send |
| **失败是值** | 运行期故障（I/O、网络、渠道挂了） | 返回 Failed/ null / fallback + 原因 | 坏档隔离、资源 Failed+Error、SDK 渠道降级、网络 OnDropped(reason) |

判断口诀：**改代码能防住的是 bug → 抛；防不住的世界故障 → 带原因返回，UI 有话可说。**

---

## 21. 目录结构与测试

```
YourAIFramework/
├── Runtime/
│   ├── YourAI.*                  # AI 七层（可选模块，详见 YourAI-Guide.md）
│   ├── YourFramework.Foundation/ # 纯 C# 内核：零引擎依赖，全部可离线单测
│   │   ├── Core/                 #   ModuleCenter / IModule
│   │   ├── Event/  Pool/  Save/  #   EventBus / ObjectPool / SaveSystem
│   │   ├── Flow/   UI/           #   ProcedureFlow / PanelStack
│   │   ├── Data/   Assets/       #   DataTable / AssetManager + Pack/（自研资源包）
│   │   ├── Localization/ Audio/  #   LocalizationStore + LocalePack / AudioManager
│   │   ├── Net/    Entity/       #   NetClient / EntityRegistry
│   │   ├── Platform/ Diagnostics/#   SdkHub / FrameworkDiagnostics
│   │   └── HotUpdate/            #   HotUpdateFlow
│   └── YourFramework.UnityRuntime/ # 引擎壳：GameEntry / GameObjectPool /
│                                   #   UIPanelManager / UnityAudioBackend /
│                                   #   Pack/（AssetBundle 装载 · WebRequest 下载）/
│                                   #   Localization/（语言文件泵加载）
├── Editor/                       # AI 设置窗口 + 资源包构建器
├── Documentation~/               # 本文档 + YourAI-Guide.md
└── Samples~/                     # 模块速览 Demo（18 个，导入即跑）/ 聊天示例
```

**质量保障**：三道门禁 —— 248 项离线断言（纯 BCL 跑，不进编辑器）、引擎编译门禁
（全部源码链 Unity 6.5.8f1 真实程序集编译，API 改名立刻红）、Demo 编译门禁（十八个
演示全量编译）。每个模块的用例组按前缀组织：`ui/*`、`data/*`、`asset/*`、`pack/*`、
`loc/*`、`locale/*`、`plural/*`、`audio/*`、`net/*`、`entity/*`、`sdk/*`、`diag/*`、`hot/*`。

---

## 22. FAQ

**Q：我不需要 AI，会有负担吗？**
A：零负担。AI 是独立程序集的可选模块，不注册就不参与，也没有任何外部依赖。

**Q：为什么没有"框架管理一切"的超级类？**
A：刻意的。框架给的是容器（ModuleCenter）+ 一组互相不引用的模块，模块互访走
`Get<T>()` 显式声明依赖。你可以在任何现有工程里只挑两个模块用。

**Q：怎么接我自己的服务端/网关？**
A：AI 层的 OpenAI 兼容网关见 YourAI-Guide 6.1；游戏长连接见本文第 13 节
（`INetLink` 可换成任何传输）。

**Q：异步加载 UI 预制体/场景怎么办？**
A：面板两段式接口（`CompleteOpen/CompleteClose`）就是为此预留的，接入
AssetManager 异步后同帧语义自动升级为跨帧。

**Q：能只做单机不用热更吗？**
A：能。热更是编排器 + 步骤，不 Run 就不存在；数据表整表替换路径天然支持后续热更。

**Q：报告 bug 时该给什么信息？**
A：`FrameworkDiagnostics.Snapshot()` 的输出 + 复现步骤。快照里有全部计数器与
最近 64 条框架错误。
