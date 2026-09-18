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
18. [数值系统 Stats](#18-数值系统-stats)
19. [地址目录表 AddressCatalog](#19-地址目录表-addresscatalog)
20. [脚本层 Script 与 XLua](#20-脚本层-script-与-xlua)
21. [技能系统 Skills（配置驱动）](#21-技能系统-skills配置驱动)
22. [AI 模块（可选）](#22-ai-模块可选)
23. [完整引导示例（可直接抄）](#23-完整引导示例可直接抄)
24. [框架如何对待你的错误（出错时的处理总表）](#24-框架如何对待你的错误出错时的处理总表)
25. [目录结构与测试](#25-目录结构与测试)
26. [FAQ](#26-faq)

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
在无头环境跑测试——只要不调 Pump，世界就是静止的。这也是框架 407 项离线检查
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
| 实体 | `EntityRegistry` | NPC/机关/组件统一管理 | 是 |
| SDK 管道 | `SdkHub` | 平台渠道编排+故障降级 | 是 |
| 诊断台 | `FrameworkDiagnostics` | 计数器+错误环+一键快照 | 是 |
| 热更 | `HotUpdateFlow` | 检查→下载→应用→重载的编排器 | 是 |
| 数值 | `StatSheet` + `EffectHost` + `DamagePipeline` | 属性三层求值 + 持续效果 + 伤害结算 | 否（服务类） |
| 目录表 | `AddressCatalog` + `CatalogEntry` | 地址→定位符：标签变体 + 补丁合并 + 可校验 + 确定性写出 | 否（服务类） |
| 脚本层 | `IScriptRuntime` + `ScriptPatchStep` + `ScriptSandboxPolicy` | IL2CPP 热更代码：清单/装载序/沙箱/真回滚（XLua 是可插拔实现） | 是（`ScriptRuntimeModule`） |
| 技能 | `SkillLibrary` + `SkillBook` + `SkillCooldown` | 配置驱动技能：表定义/目标选择/冷却充能/施法流程（数值桥可选） | 否（服务类） |
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
fxPool.Release(fx);                    // 还（自动 SetActive(false)、复位）
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
else { /* 坏档/缺失走这里，error 带原因——失败也是值，不抛异常 */ }

foreach (SaveSlotMeta meta in saves.ListSlots()) { }   // 列槽位
saves.Delete("slot1");
```

要点（都是替你踩好的坑）：

- **原子写**：先写临时文件再替换，写一半断电不毁档；
- **坏档隔离**：JSON 损坏的档自动改名 `.corrupt` 隔离，读取永不抛异常
  （"失败也是值"——返回 null，你的 UI 有话可说）；
- 槽位名只允许 `[A-Za-z0-9_-]`，别的名字是 bug，当场抛。

```csharp
var settings = new SettingStore(Application.persistentDataPath + "/settings.json");
settings.SetInt("music.volume", 80);
settings.SetFloat("sfx.volume", 0.8f);
settings.SetString("last.slot", "slot1");
settings.SaveToDisk();                                  // 显式写进文件
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
    public override string Name { get { return "Boot"; } }   // 人类可读的阶段名，不必等于类名
    public override void OnEnter() { /* 进状态：开始加载配置 */ }
    public override void OnUpdate() { /* 每帧：加载完了就切 */ }
    public override void OnExit() { /* 出状态：清理 */ }
}

var flow = new ProcedureFlow("boot", 30);     // 名字 + InitOrder
flow.Add(new BootState())                     // 注册实例：键是 Name 属性（这里是 "Boot"）
    .Add(new MenuState())
    .Add(new PlayingState())
    .Start<BootState>();                      // 设起始状态 —— 不设，模块 Init 会 fail-fast 抛

Host.ChangeProcedure<MenuState>();            // 切换是"下一泵生效"（防同帧重入）
flow.Pump();                                  // GameEntry 会替你泵
```

`Start<T>()` / `ChangeProcedure<T>()` 按**运行时类型**解析，所以 `Name` 写成 `"Boot"` 也照样找得到；
按名字切就用 `ChangeProcedure("Boot")`。同一类型注册了两个实例时泛型重载有歧义，会 fail-fast 抛
（这时改用名字切换）。

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
- **失败也是值**：文件不存在、路径穿越、Provider 抛异常——全部变成
  `Failed + Error 原因`，你的 UI 有话可说；`Load("")` 这种参数 bug 才当场抛；
- 自定义 Provider 十几行就能写（实现 `Handles` 纯谓词 + `Pump` 推进即可，
  内置的 `FileAssetProvider` 就是现成范例）。

### 10.1 自研资源包：清单 → 版本 → 下载 → 缓存 → 加载

框架自带一套资源包实现（不依赖任何第三方包），五件东西各管一段：

| 件 | 类型 | 职责 |
|---|---|---|
| 清单 | `PackageManifest` | 有什么 bundle、每个 bundle 的 hash/crc/size、地址→bundle、bundle 依赖 |
| 版本文件 | `PackageVersion` + `PackageDiff` | 这一版各 bundle 的指纹；与本地版本比出差量（该下什么、能删什么） |
| 下载队列 | `DownloadQueue` + `IDownloadBackend` | 并发上限、指数退避重试、超时截停、断点续传，全部逐帧驱动 |
| 缓存记录 | `CacheLedger` | 引用计数 + 完整性标记 + LRU 逐出名单（只出名单，删文件归你） |
| 加载 | `PackAssetProvider` + `IBundleBackend` | 地址 → 依赖全链（拓扑序）→ 逐帧装载 → 取出资源 |

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
- **断点续传靠一个事实源**：后端报告"已写进文件多少字节"，队列把它作为下次尝试的
  `resumeFrom`，不自己拼偏移；
- **失败也是值贯穿到底**：断网、超时、服务器忽略 Range、bundle 损坏、资源名不在
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
| 门面 | `LocalePack` | 收尾以上全部，并实现 `ILocalizationSource` |

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

**干什么**：NPC、机关、子弹、后台系统组件的统一管理。实现哪些小接口就有哪些能力，
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
var manifest = AssemblyManifest.LoadJson(manifestJson);  // 上一阶段下载到本地的清单
var flow = new HotUpdateFlow(new MyProgressUi());    // 四个进度回调
flow.Run(new IHotUpdateStep[] {
    new CheckVersionStep(),                          // 你的阶段：版本比对，写下载名单
    new DownloadPatchStep(),                         // 你的阶段：驱动 DownloadQueue 到 Idle
    new AssemblyReloadStep("ReloadAssemblies",       // 框架自带（见 17.1）
        manifest, new[] { "HotUpdate" },
        new FileAssemblyBytesSource(cacheRoot),
        new ByteArrayAssemblyLoader(), new HotfixEntryRunner())
});
GameEntry.Ensure().Register(flow);
```

要点：任何阶段失败（或抛异常）→ `OnFailed(阶段名, 原因)` 终态，后续阶段不启动；
阶段顺序、进度事件、终态纪律全部由编排器保证；步骤就是"Begin 一次 + Pump 每帧"，
跟资源/网络的泵哲学同款。

程序集重载这一格框架自带实现（不依赖任何第三方热更库），见 17.1。

### 17.1 自研热更程序集：清单 → 拓扑序 → 逐帧装载 → 入口点 → AOT 白名单

`HotUpdateFlow` 里"程序集重载"那一格，框架自带一套实现（不依赖任何第三方热更库）。
五件东西各管一段：

| 件 | 类型 | 职责 |
|---|---|---|
| 清单 | `AssemblyManifest` | 这一版有哪些热更程序集、各自的 hash/crc/size、彼此依赖；坏清单一律 fail-fast |
| 装载顺序 | `AssemblyLoadOrder` | root 名 → 依赖全链，按拓扑序填列表（被依赖者先；成环报出环上的名字） |
| 逐帧装载 | `AssemblyReloadStep` | 每帧一个程序集：读 → 长度/CRC32 校验 → 装载；失败带原因、带阶段 |
| 入口点 | `IHotfixEntry` + `HotfixEntryRunner` | 扫实现接口的类型，按 `Order` 升序调用；一个抛异常不截停其它 |
| AOT 白名单 | `AotWhitelist` | 扫泛型实例化 → 生成 link.xml（IL2CPP 需要，纯 JIT 平台不需要） |

```csharp
var manifest = AssemblyManifest.LoadJson(manifestJson);      // 坏清单当场抛（构建期 bug）
var source   = new FileAssemblyBytesSource(cacheRoot);       // 字节来源：默认读本地文件
var entries  = new HotfixEntryRunner();

flow.Run(new IHotUpdateStep[] {
    new CheckVersionStep(),
    new DownloadPatchStep(),
    new AssemblyReloadStep("ReloadAssemblies", manifest,
        new[] { "HotUpdate" },                               // 想装载谁（依赖自动带上）
        source, new ByteArrayAssemblyLoader(), entries),
});
```

要点：

- **回滚点在哪里**：.NET 卸载不了已装载的程序集，所以"回滚"不可能指"把装载过的退回去"。
  真正有意义的回滚点是**入口点执行之前** —— 读 / 校验 / Load 任何一步失败，一个入口都没跑，
  游戏仍旧跑在旧代码上，这就是一次干净的整批回滚（`RolledBack == true`）。入口期失败则
  如实汇报 `EntriesAttempted` 与逐条原因，不假装回滚过；
- **每帧一个程序集**：单帧工作量有界，热更不会把加载界面冻住一帧；顺序来自清单里的依赖，
  不是人工排的名单；
- **失败也是值贯穿到底**：文件缺失、声明长度不符、CRC32 不符、装载器拒绝（IL2CPP 上的
  `PlatformNotSupportedException` 就走这条路）—— 每一个都停在 `Failed + 原因`；
- **路径纪律**：清单是从网上来的，字节来源拒绝绝对路径与任何 `..` 段，一份被篡改的清单
  不能借此读到缓存目录之外的任意文件；
- **平台边界**：JIT 平台（编辑器 / Android-Mono / Windows）直接可用；IL2CPP 上
  `Assembly.Load(byte[])` 不支持，把 HybridCLR 的实现塞进 `IAssemblyLoader` 插槽即可，
  上层一个字节都不用改；
- **原因直达 UI**：步骤实现 `IHotUpdateStepFailure` 后，`HotUpdateFlow` 会把它的
  `FailureReason` 原样转发给监听者，不再吞成 "step 'X' failed"。

**AOT 白名单**：菜单 **YourAI → 热更 → AOT 白名单（link.xml）**。窗口里填要扫的程序集，
点生成即得 `Assets/link.xml`（按程序集与类型名排序，同样输入产出**逐字节相同**的文件，
进版本库不会每次 diff）。诚实边界：反射只看得到字段 / 属性 / 方法签名里的泛型实例化，
**只在方法体内部** `new` 出来的（既不进签名也不进字段）它看不见 —— 那部分在窗口的
"额外种子类型"栏里补上。宁可说清覆盖到哪，也不给一个"应该是全的"的假象。

---

## 18. 数值系统 Stats

**干什么**：把"这个单位攻击多少、中了毒之后多少、砍对方一刀掉多少血"抽成可配、可解释、
可复现的一层。四件内聚的东西，一件比一件靠上：

| 件 | 类 | 一句话 |
|---|---|---|
| 可复现随机 | `IRandomSource` / `DeterministicRandom` | PCG32 + 命名分流：同种子同序列，跨平台不变 |
| 属性表 | `StatSheet` | 基础值 + 三层修饰符 → 数值，每个值可解释 |
| 持续效果 | `StatEffect` / `EffectHost` | 逐帧驱动的 buff/debuff 状态机，到期精确摘除 |
| 伤害管道 | `DamageProfile` / `DamagePipeline` | 攻守两张表 → 一个整数伤害，失败也是值 |

为什么不直接用 `System.Random` 加一个 `float atk` 字段？因为这三件事各有各的坑，而且
坑都在后面才炸：随机序列不可复现（回放与帧同步一起废掉）、"多个 +10% 该相加还是连乘"
说不清（数值没法调）、buff 到期漏摘修饰符（属性越打越高）。

### 18.1 可复现随机

```csharp
var rng = new DeterministicRandom(seed: 20260918, stream: "battle");
uint a = rng.NextUInt();                     // 唯一原语，其余都在它之上
double u = rng.NextUnit();                   // [0,1)
int roll = rng.Range(0, 100);                // [0,100)
float jitter = rng.Range(-0.1f, 0.1f);
bool crit = rng.Chance(0.25f);               // p<=0 必假、p>=1 必真，且都不消耗随机数
T picked = rng.Pick(candidates);             // 空集合是布线 bug → 抛
```

**命名分流**是这一块最值钱的设计：

```csharp
var critStream = rng.Fork("crit");
var lootStream = rng.Fork("loot");           // 互不扰动
```

同一个种子下两条流各自独立，于是"战斗里新增一个随机调用点"不会把整场战斗的暴击序列
顺移掉 —— 这是把"随机不可复现"从工程上根除，而不是靠纪律去躲。

**为什么不用 `System.Random`**：它不保证跨 .NET 版本 / 跨平台的序列稳定（.NET Core 换过
算法），而回放、帧同步、断线重连都要求"同种子 → 同结果"。PCG32 的状态就**一个 `ulong`**：

```csharp
ulong snapshot = rng.State;                  // 存档 / 发网络包
rng.NextUInt();
rng.Restore(snapshot);                       // 从该点原样继续
```

本实现与 `tools/.n4/ref_rand.py`（一份按算法另写的 Python 实现）逐值对齐 —— 跨语言也一致，
所以"可复现"不只是同进程内的巧合。

### 18.2 属性表：三层求值

```csharp
var sheet = new StatSheet();
sheet.Declare("atk", 10f);
sheet.AddModifier(new StatModifier("atk", StatKind.Flat,       5f,   "武器#12"));
sheet.AddModifier(new StatModifier("atk", StatKind.PercentAdd, 0.2f, "天赋A"));
sheet.AddModifier(new StatModifier("atk", StatKind.PercentAdd, 0.3f, "天赋B"));
sheet.AddModifier(new StatModifier("atk", StatKind.PercentMul, 0.1f, "光环A"));
sheet.AddModifier(new StatModifier("atk", StatKind.PercentMul, 0.2f, "光环B"));

sheet.Value("atk");     // (10+5) × (1+0.2+0.3) × 1.1 × 1.2 = 29.7
sheet.Explain("atk");   // 完整推导，见下
```

公式（顺序固定，不随插入顺序漂移）：

```
Value = clamp( (Base + ΣFlat) × max(0, 1 + ΣPercentAdd) × Π max(0, 1 + PercentMul) )
```

三个刻意的取舍：

1. **分三层，不合成两层**：数值策划真正要区分的是"多个 +10% 该相加还是连乘"。常见
   天赋与装备该相加（+10% +10% = +20%），罕见高阶加成才连乘。合成一层，谁也没法调；
2. **乘数下限是 0 而不是负无穷**：把加成压到 0 是合法语义（100% 减伤），压成负数不是
   （那等于"打一下给敌人回血"）；
3. **写严读松**：写入未声明的属性当场抛（属性名拼错在装配那一刻就爆），读取未声明的
   属性返回 0（配配置驱动的属性集合本来就会动态长出来，读侧报错会把正常流程也打断）。
   要区分"0"与"不存在"用 `TryValue`。

`Explain` 是排查"伤害看着不对"的主力：

```csharp
StatBreakdown b = sheet.Explain("atk");
b.Explain();   // "atk = (10 + 5) × 1.5 × 1.32 = 29.7（5 个来源）"
b.Parts;       // 每个来源一行，如 "atk +8 flat ← 武器#12"
```

**卸装备是一句话**，不用自己记着删哪几条：

```csharp
sheet.RemoveBySource("武器#12");    // 返回摘掉几条
```

属性之间**不互相引用**（不做 `atk = str × 2` 这类派生）—— 派生值在配表层算好再写进来。
一旦让属性互指，就得处理依赖图与求值顺序，那是把配表的复杂度搬进运行时。

### 18.3 持续效果：逐帧驱动的状态机

效果定义是**纯数据**，同一份配表可以施加到无数个宿主上：

```csharp
var poison = new StatEffect("poison", duration: 3f, EffectStackPolicy.Stack);
poison.MaxStacks = 3;
poison.TickInterval = 1f;
poison.Modifiers = new List<StatModifier> { new StatModifier("atk", StatKind.Flat, 4f, null) };

var effects = new EffectHost(sheet);
effects.Apply(poison, "刺客");        // 满层时返回 null，原因在 effects.LastApplyError

var events = new List<EffectEvent>();
effects.Pump(deltaSeconds, events);   // 每帧推进；tick / 到期作为值填进你的列表
```

三种堆叠策略对应三种真实的策划需求：

| 策略 | 语义 | 典型用法 |
|---|---|---|
| `Refresh` | 已在则只刷新时长，层数恒为 1 | 减速、增益（否则两个法师各放一次就把玩家固定死） |
| `Stack` | 层数 +1 并刷新，每层各贡献一份修饰符 | 中毒、流血（每次命中都该更疼） |
| `Independent` | 每次施加都新建实例，各自计时各自到期 | 多个来源需要分别到期 |

两条顺序上的讲究，都是为了"时长 3 秒、每 1 秒一跳"真的跳出 3 次：

- **先 tick 后到期**：到期那一帧先把最后一跳结掉再移除。若先减寿命，t=3.0 那一帧会直接
  到期、把第 3 跳吃掉 —— 一条 DoT 白少一跳伤害；
- **只结算"还活着的那段时间"**：一帧跨度可能大于剩余寿命（卡顿、快进、无头测试里一次
  `Pump(4)`）。按整帧累加会把死后的时间也算进去，于是 3 秒的 DoT 跳出第 4 跳。

合起来的收益是**步长无关**：`Pump(1)×3` 与 `Pump(3)×1` 给出完全一样的事件序列，回放与
快进因此不会改变战斗结果。

到期摘除是**按引用逐条摘**，不是"按来源标签全清"—— 后者会误伤别人用同样标签挂上来的
修饰符。

### 18.4 伤害管道

```csharp
var profile = DamageProfile.WithCrit();     // 属性名可配，框架不规定别人叫 atk
profile.MitigationConstant = 100f;          // K 就是"减伤 50% 所需的防御值"
profile.Variance = 0.1f;                    // ±10%
profile.MinimumDamage = 1f;                 // 保底 1 点

var pipeline = new DamagePipeline(profile, rng.Fork("damage"));
DamageResult r = pipeline.Resolve(attackerSheet, defenderSheet, DamageRequest.Of(1f));
r.Amount; r.Explain();
```

结算顺序固定，每一步都留在结果里，可复算：

```
Base      = 攻击 × 倍率 + 固定加伤
→ 暴击    × (1 + 暴击伤害)           仅当暴击率 > 0（且抽中）
→ 减伤    × K / (K + max(0, 防御))   真实伤害或 K<=0 时系数为 1
→ 浮动    × (1 + U(-v, v))           仅当浮动 > 0
→ 夹取    下限 → 上限 → 取整
```

三点设计取舍：

1. **减伤用比例曲线而非减法**：减法会在高等级把伤害瞬间压到 0（数值悬崖），比例曲线永不
   到 0，且 `K` 有可解释的含义；
2. **随机消费次数由配置决定**：暴击率 > 0 才抽暴击、浮动 > 0 才抽浮动，抽了几次如实记在
   `DamageResult.Rolls` 里（0/1/2）—— 同样的配置 + 同样的种子 + 同样的输入，永远给同样的
   结果。`Amount` 是夹取后的整数，`Raw` 是夹取前的实际内容，封顶不会把真实强度藏起来；
3. **攻守缺失不对称**：守方缺防御按 0 处理（无防御的召唤物 / 环境物件是常态），攻方缺
   攻击属性则以 `AggressorStatMissing` 返回（属性名对不上是装配错误，值得被看见）。

落伤与失败时的处理：

```csharp
float remaining; string error;
if (pipeline.TryApplyDamage(target, "hp", r, out remaining, out error)) { /* 命中 */ }
else { /* 已死 / 没声明生命属性 / 结果未结算 —— error 里有原因，UI 有话可说 */ }
```

落伤扣的是**基础值**（修饰符继续作用），生命可以为负：框架不规定"0 即死"，由上层判定。

**换公式的成本**：把 `IRandomSource` 换成服务端下发的序列就是"服务端权威"；把
`DamageProfile` 换一套参数就是"换一个游戏"。上层代码一个字节都不用改。

对应离线用例组：`stat/*`（tools/core-test，407 项检查项的一部分）。

---

## 19. 地址目录表 AddressCatalog

写死在代码里的地址有三个老问题：打哪个包、哪些必载、换了画质档该取哪个 —— 都只能在运行中靠试。目录表把它们变成**一张构建期就能验的数据表**。

```json
{
  "name": "base",
  "entries": [
    { "address": "ui/login", "group": "ui", "kind": "scene", "required": true,
      "variants": [
        { "locator": "Assets/UI/Login.prefab" },
        { "tags": ["hd"], "locator": "Assets/UI/Login_hd.prefab" },
        { "tags": ["hd", "zh"], "locator": "Assets/UI/Login_hd_zh.prefab" }
      ] },
    { "address": "ui/hud", "group": "ui", "locator": "Assets/UI/Hud.prefab" }
  ]
}
```

```csharp
AddressCatalog catalog = AddressCatalog.FromJson(json);

// 解析：给一组“当前环境标签”，取该环境该用的那个定位符
CatalogTagSet env = new CatalogTagSet(new[] { "hd", "zh" });
string locator = catalog.Resolve("ui/login", env).Locator;   // Login_hd_zh.prefab
```

### 19.1 变体：子集匹配 + 最具体者优先

一个地址可以有多条**变体**，每条挂一组标签。命中规则是**子集**：变体的标签必须**全部**处于生效集合中。不是“有交集就算” —— 否则 `hd` 会误命中 `hd+boss` 的专属档。

命中多条时取**标签最多**者（最具体）；标签数并列时按定位符的 Ordinal 序取最小，所以并列也有确定解，与书写顺序无关。没有标签的变体是**默认变体**，是最后的兜底。

```csharp
catalog.Resolve("ui/login", CatTags());                   // 默认档
catalog.Resolve("ui/login", CatTags("hd"));               // hd 档
catalog.Resolve("ui/login", CatTags("hd", "zh"));         // hd+zh 档（最具体）
catalog.Resolve("ui/login", CatTags("hd", "zh", "ps5"));  // 仍是 hd+zh，无关标签不干扰
```

解析不到**不是异常**：地址可能本就不该存在（某语言没有配音）。返回 `Resolved == false`，由调用方与校验器决定要不要当问题 —— 与“属性缺失是值”是同一条必须守的规矩。

### 19.2 补丁合并：覆盖 + 归属可追溯

发行补丁只写差异，合并是**整条替换**（不是变体取并集 —— 否则“删掉一个变体”表达不出来，旧变体会从底表漏回来）：

```csharp
catalog.Merge(AddressCatalog.FromJson(patchJson));
catalog.Require("ui/hud").Source;   // "patch-2026-09"：能查到是哪张表改的
```

被覆盖的地址**保留原槽位**，所以遍历顺序稳定。

### 19.3 校验：外部世界用注入谓词

解析只管单条自洽（地址非空、变体不重复）；“必需项在目标环境能否解析”“定位符指向的东西是否真的存在”是跨系统判断，交给 `CatalogValidator`：

```csharp
CatalogReport report = CatalogValidator.Validate(
    catalog,
    new CatalogTagSet(new[] { "hd", "zh" }),
    locator => bundleFiles.Contains(locator));      // 外部世界由此注入

if (!report.IsClean) { /* report.Dump() 逐条列问题 */ }
```

- **Error**（拦构建）：必需项解析不出、注入谓词说定位符不存在；
- **Warning**（只提醒）：两个地址共用同一份内容（复用合法，但常是复制粘贴笔误）、没有默认变体（平台专属项是合理设计）。

注入谓词是刻意设计：目录表**不认识**任何打包实现（那是第 10 节自研资源包的事），两者只通过“地址 → 定位符”这层数据对接 —— 换打包方案不用动校验器。

### 19.4 确定性写出

目录表会被签进包、被 diff、被热更比对。`CatalogWriter.Write` 按地址排序、变体按标签签名排序，**输出只取决于内容，不取决于输入顺序**：

```csharp
string json = CatalogWriter.Write(catalog);
CatalogWriter.Write(AddressCatalog.FromJson(json)) == json;   // true（幂等）
```

对应离线用例组：`catalog/*`（tools/core-test，407 项检查项的一部分）。

**与第 9 节数据表的分工**：`DataTable` 管游戏数值/配置，目录表管**地址与加载单位**。

---

## 20. 脚本层 Script 与 XLua

**这一层是为 IL2CPP 准备的。** 热更代码有两条路，平台的限制不一样：

| 路径 | 机制 | JIT 平台（编辑器 / Android-Mono / Windows） | IL2CPP |
|---|---|---|---|
| 程序集热更 | `Assembly.Load(byte[])` | 可用 | **不可用**（`PlatformNotSupportedException`） |
| 脚本补丁 | 解释执行代码块 | 可用 | **可用** |

所以「IL2CPP 上也能热更代码」这件事，等价于「框架有一个能跑起来的脚本运行时」。

### 20.1 分工：内核管预留接口，适配层管引擎

- **内核**（`YourFramework.Foundation/Script/`）只定义预留接口 —— 代码块、清单、装载序、沙箱策略、
  失败时的处理。**它不认识任何脚本引擎**，也不依赖任何第三方包，所以它能在那道离线检查提交前检查里
  被逐条验证。
- **适配层**（`Runtime/YourFramework.XLua/`）把 XLua 接到预留接口上，是**可选程序集**：

```
asmdef: YourFramework.XLua
  references:        ["YourFramework.Foundation"]
  defineConstraints: ["YOURFRAMEWORK_XLUA"]   ← 没有这个宏，Unity 根本不编译它
  noEngineReferences: true
```

启用步骤：

1. 把 XLua 导入工程（DLL 保持 Auto Reference 勾选，或把 `XLua` 的 asmdef 加进本 asmdef 的 references）；
2. Player Settings → Scripting Define Symbols 加上 `YOURFRAMEWORK_XLUA`；
3. 上层代码一个字都不用改 —— 换的是运行时实现，不是用法。

**没有 XLua 也照样能开发**：内核这一层能被任何实现驱动（包括纯 C# 的假运行时），
所以「清单写错了」「装载序不对」「回滚没干净」这些问题离线就查得出来。

### 20.2 四个预留接口

| 预留接口 | 一句话 | 关键纪律 |
|---|---|---|
| `IScriptRuntime` | 能装块、能卸块、能调入口、能被逐帧泵 | 内容问题失败也是值；写错代码（null 块、已关停）抛 |
| `IScriptChunkSource` | 代码块的文本来源 | 严格 UTF-8（坏字节报出来，不变成替换符）+ 路径检查 |
| `ScriptManifest` + `ScriptLoadOrder` | 这一版有哪些块、按什么序装 | 坏清单一律 fail-fast；依赖在前 |
| `ScriptSandboxPolicy` | 补丁**能碰什么** | 默认从零开始；点名禁用比能力开关硬 |

### 20.3 最小用法

```csharp
// 1. 清单（构建期产物；重名/悬空依赖/成环/未知 kind 一律启动就炸）
ScriptManifest manifest = ScriptManifest.LoadJson(patchJson);

// 2. 来源：默认读缓存目录，且拒绝绝对路径与「..」（清单是从网上来的）
FileScriptChunkSource source = new FileScriptChunkSource(cacheDir);

// 3. 运行时：XLua 适配层（没有 XLua 时换任何实现，用法不变）
IScriptRuntime runtime = new XLuaScriptRuntime("xlua", source, ScriptSandboxPolicy.CreateStrict());

// 4. 阶段：插进热更流水线，与程序集重载是同一个插槽
var step = new ScriptPatchStep(manifest, new[] { "battle.damage" }, source, runtime);
var flow = new HotUpdateFlow(listener);
flow.Run(new IHotUpdateStep[] { check, download, step });

// 每帧：运行时滴答 + 流水线推进。想省事就把运行时包成模块，随 ModuleCenter 自动泵：
GameEntry.Ensure().Register(new ScriptRuntimeModule(runtime));   // InitOrder 50，早于热更编排
```

### 20.4 失败与回滚（这一层最值得看的部分）

**回滚是真的。** 程序集一旦装载就无法卸载，而 Lua **能卸代码块**。所以脚本补丁的装载期
（读 / 校验 / 装）任何一步失败，已装载的块会被**反序卸掉**，真的回到补丁前的状态：

```
装载序  core.util -> battle.damage -> ui.hud
                             ↑ 这里失败
卸载    battle.damage -> core.util            ← 反序
RolledBack = true
```

入口点一旦跑过就不卸载 —— 那时函数已被闭包引用，卸掉只是自欺欺人，框架会如实汇报
（`RolledBack = false` + 逐条原因）。卸载本身失败也会改变结论，并把
`rollback incomplete: ...` 写进 `FailureReason`。

沙箱在**构造时**就应用，并且**回读确认门真的关上了**（发出「io = nil」和执行完「io 不见了」
是两件事）。沙箱关不上 → 运行时转 `Faulted` 并**拒绝装载任何块**：一个没关门的解释器里
跑下载来的补丁，比不热更坏得多。

对应离线用例组：`script/*`。适配层另有自己的提交前检查（见第 24 节）。

### 20.5 边界说清楚

- **XLua 不在框架包内，也不会有**：框架的必须守的规矩是零第三方包依赖。所以形态是「可选预留接口 + 一个实现」，
  用不用、用哪个版本由你决定；
- **真实 XLua 的运行时行为需要你在 Unity 里验一次**：离线检查能证的是「适配层对着冻结的 XLua
  公开 API 能编译、调用协议正确、失败折算正确」，它不能替 XLua 保证 XLua 自己的行为；
- **沙箱策略要按项目调**：默认最严（`io` / `os` / `debug` / `load` / `require` 全关）。
  补丁里要 `require` 自己的模块就 `Allow(ScriptCapability.Modules)`；真要读写文件才
  `Allow(ScriptCapability.FileSystem)`。

---

## 21. 技能系统 Skills（配置驱动）

**干什么**：把"技能"从代码里挪进配置表。技能定义、消耗、效果、资源地址、脚本钩子
全在 JSON 里 —— 代码里**没有一个技能名、没有一个数字**。加一个技能 = 加一段 JSON；
配合第 20 节的脚本层，连技能表现都能热更。

```csharp
SkillLibrary library = SkillLibrary.FromJson(skillsJson);

// 加载期就该炸的：坏 JSON / 未知枚举 / 负数时长 / 效果算不出数 / 技能树成环
// 交叉校验：技能要用的地址在目录表里解析不出来 → Error（到了线上就是没特效）
SkillReport report = library.Validate(catalog, CatalogTagSet.Empty);
if (!report.IsClean) { throw new InvalidOperationException(report.Dump()); }

// 每个施法者一本技能书：定义共享、状态各一份（一万个怪共用一份定义）
SkillBook book = new SkillBook(
    "hero", library,
    new StatSheetStatSource(heroSheet),                        // 资源账本：读属性 + 扣资源
    new StatSkillEffectSink(damage, actors, "hp", "max_hp"),   // 效果落到数值系统
    scriptRuntime);                                            // 热更钩子（可为 null）
book.Learn("ember_bolt");

// 打一发：候选是纯数据快照，技能不查场景 / 物理 / 寻路
SkillCastOutcome outcome = book.TryBegin("ember_bolt", candidates, "boss", targetsInto, eventsInto);
// outcome.Succeeded == false → outcome.Error 就是原因（冷却中 / 蓝不够 / 打不到）
book.Pump(delta, eventsInto);     // 吟唱推进 + 充能恢复；事件装进调用方的列表，帧内零分配
```

**三条设计判断**（每条都被离线检查固定住）：

| 判断 | 为什么 |
|---|---|
| 配置错 fail-fast，打不出来是**值** | 坏表是构建期问题（启动就炸好过打到一半发现"这技能没伤害"）；"冷却中 / 蓝不够 / 目标没了"是玩家行为，抛异常只会把"提示一下"变成"崩一下" |
| 资源在**起手时**扣 | 吟唱被打断 = 资源白花。语义明确、可检查项，而不是"看情况" |
| 拒绝顺序固定：冷却 → 资源 → 目标 | 反过来会让"技能还在转"显示成"打不到"，掩盖真正的原因 |

**核心里没有数值系统**：内核只认 `ISkillStatSource`（读属性/扣资源）与
`ISkillEffectSink`（把一条效果落到一个目标）两个接口；属性表、伤害管道、效果容器
只出现在 `StatSkillEffectSink` 这一个类里。所以这套技能流程能在没有数值系统的工程里编译、
能在无头环境逐条检查项，也能整块换成另一种落地策略（例如"特效先行、伤害后结算"）。

**目标选择**是纯函数（`SkillTargetSelector.Select`），只看调用方给的候选快照：
四种模式 = 自己 / 指定单体 / 全部敌人 / 血最少的友军。`into` 列表**清空再填** ——
这与检索类接口（`AddressCatalog.FindByGroup` 那类）的追加语义**故意不一致**：
目标选择的语义是"这一次打谁"，结果集必须精确，沿用追加语义的话每个调用点都得记得先清空。

**冷却与充能**（`SkillCooldown`）三条语义，两条是踩过坑才会写对的：

- **余量保留**：一帧跨过两个恢复点时两个都要补上，多出来的时间留给下一个充能 ——
  不许"一帧只处理一个"（那样 30 帧和 60 帧打出的连招数会不一样）；
- **满了不攒欠账**：充能满时剩余时间归零；
- **速率乘在推进量上**：冷却缩减是乘在"这一帧推进了多少"上，不是把冷却时长改短
  （中途改属性时后者会算错已经过去的时间）。

**技能树**是有根森林：前置不存在是笔误，成环则永远点不出来，两者都在加载期抛。
检测用三态标记（未访问 / 在栈 / 已完成）—— 两态分不出"环"和"两个技能共用一个公共前置"，
会把一张好表判死。

**确定性写出**：`Write()` 按 id 排序、缺省值省略、修饰符按键排序 —— 同一份内容永远
同一串字节。技能表要签进包、要被 diff、要被热更比对，输出随书写顺序变化的话 diff 全是噪声。

对应离线用例组：`skill/*`（tools/core-test，36 项，含"内核不引用数值层"的反射实证）。

---

## 22. AI 模块（可选）

AI（LLM 对话、流式输出、工具调用、多智能体圆桌）是框架的**可选加成**，不引 API
Key 完全不影响上面所有功能。最小用法：

```csharp
AiAgent agent = AiRuntimeFactory.CreateDeepSeekAgent("smith", apiKey, "你是铁匠。");
var ui = new UIPanelManager(panelSettings).Register(new ChatPanel(agent));
GameEntry.Ensure()
    .Register(ui)
    .Register(new AiModule(() => agent, EventBus.Global));   // InitOrder 100，最晚初始化
ui.Open("chat");

// 不经 UI 的最小回合——逐帧驱动，与资源/网络同一形状：
IAgentTurn turn = agent.BeginTurn("player", "打个招呼。", worldState: null);
turn.Delta += text => Debug.Log(text);        // 流式增量，每帧 Pump 期间到达
while (!turn.IsDone) { turn.Pump(); }         // 每帧推进；turn.Cancel() 干净放弃
if (turn.Result.Success) { /* turn.Result.Text 即最终回复 */ }
else { /* turn.Result.Error 带原因（含 HTTP 状态）——失败也是值 */ }
```

流式输出、工具调用、审批、记忆、检索增强、多智能体——见
[`YourAI-Guide.md`](YourAI-Guide.md)。

---

## 23. 完整引导示例（可直接抄）

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

## 24. 框架如何对待你的错误（出错时的处理总表）

框架对错误只有两种态度，且每个 API 属于哪一类是明确的：

| 态度 | 什么时候 | 表现 | 例子 |
|---|---|---|---|
| **fail-fast** | 程序 bug（布线/合约违规） | 当场抛异常 | 重复注册模块、借还池失衡、Play 未注册通道、终态会话 Send |
| **失败也是值** | 运行期故障（I/O、网络、渠道挂了） | 返回 Failed/ null / fallback + 原因 | 坏档隔离、资源 Failed+Error、SDK 渠道降级、网络 OnDropped(reason) |

判断口诀：**改代码能防住的是 bug → 抛；防不住的世界故障 → 带原因返回，UI 有话可说。**
数值模块的具体分工：`Declare` 重复声明，或 `SetBase` / `AddModifier` / `SetRange` 写未声明的属性 → 抛；
`EffectHost` 满层、`DamagePipeline` 撞上攻方缺攻击属性、`TryApplyDamage` 撞上已死目标或没声明
生命属性 → 返回 `null` / 带原因的 `false`。
技能模块的具体分工：技能表结构错误（坏 JSON、未知枚举、负数时长、效果算不出数、前置成环）
在装载期**抛**；冷却中、蓝不够、没有合法目标、吟唱被打断、单条效果落地失败、脚本钩子挂掉 ——
全是**值**（`SkillCastOutcome` / `SkillCastEvent` 带原因）。

---

## 25. 目录结构与测试

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
│   │   ├── HotUpdate/            #   HotUpdateFlow + 自研热更程序集（清单/装载/入口点/AOT 白名单）
│   │   ├── Stats/                #   数值系统：确定性随机 / 属性表 / 持续效果 / 伤害管道
│   │   ├── Catalog/              #   地址目录表：变体解析 / 补丁合并 / 校验 / 确定性写出
│   │   ├── Script/               #   脚本层预留接口：代码块 / 清单 / 装载序 / 沙箱策略 / 补丁阶段
│   │   └── Skills/               #   技能系统：技能表 / 目标选择 / 冷却充能 / 施法流程 / 数值桥
│   ├── YourFramework.UnityRuntime/ # 引擎壳：GameEntry / GameObjectPool /
│   │                               #   UIPanelManager / UnityAudioBackend /
│   │                               #   Pack/（AssetBundle 装载 · WebRequest 下载）/
│   │                               #   Localization/（语言文件泵加载）
│   └── YourFramework.XLua/         # 可选适配层：XLua → 脚本层预留接口
│                                   #   （defineConstraints：没有 YOURFRAMEWORK_XLUA 宏就不编译）
├── Editor/                       # AI 设置窗口 + 资源包构建器 + AOT 白名单窗口
├── Documentation~/               # 本文档 + YourAI-Guide.md
└── Samples~/                     # 模块速览 Demo（23 个，导入即跑）/ 聊天示例
```

**质量保障**：四套检查 —— 407 项离线检查（纯 BCL 跑，不进编辑器）、引擎编译提交前检查
（全部源码链 Unity 6.5.8f1 真实程序集编译，API 改名立刻红）、Demo 编译提交前检查（二十三个
演示全量编译）、以及 XLua 适配层提交前检查（`tools/xlua-adapter`：把适配层依赖的 XLua 公开 API
子集冻结成录制式替身，编译适配层并驱动一遍，验证构造/装载/卸块/入口/泵/关停的调用协议
与失败折算）。

提交前检查的边界也写清楚：XLua 那道证的是**适配层的协议**，不是 **XLua 自己的运行时行为** ——
后者要在 Unity 里导进插件跑一次。名字里写清能证什么，比事后解释便宜。

每个模块的用例组按前缀组织：`ui/*`、`data/*`、`asset/*`、`pack/*`、`loc/*`、`locale/*`、
`plural/*`、`audio/*`、`net/*`、`entity/*`、`sdk/*`、`diag/*`、`hot/*`、`asm/*`、
`stat/*`、`catalog/*`、`script/*`、`skill/*`。

---

## 26. FAQ

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

**Q：加一个技能要改代码吗？**
A：不用。技能定义、消耗、效果、资源地址、脚本钩子全在 JSON 表里（`SkillLibrary.FromJson`），
代码里没有一个技能名、没有一个数字。地址解析接第 19 节地址目录表，施法钩子接第 20 节脚本层；
技能表还是确定性写出的（可 diff、可签进包、可热更比对）。换一套表就是换一个游戏。

**Q：框架内置 Lua / XLua 吗？**
A：不内置。内核只提供脚本层预留接口（代码块 / 清单 / 装载序 / 沙箱 / 失败时的处理，零第三方依赖），
XLua 是**可选适配层**（`Runtime/YourFramework.XLua`，加了 `YOURFRAMEWORK_XLUA` 宏且工程里有
XLua 才编译）。IL2CPP 上要热更代码就用这一层；JIT 平台上程序集热更已经够用。

**Q：报告 bug 时该给什么信息？**
A：`FrameworkDiagnostics.Snapshot()` 的输出 + 复现步骤。快照里有全部计数器与
最近 64 条框架错误。

**Q：我做回合制，不需要随机浮动，能关掉吗？**
A：能，而且关得很彻底：`DamageProfile.Variance = 0` 时整条浮动关闭，并且**一次随机数都不抽**；
暴击率 ≤ 0 时暴击判定整条关闭（`DamageResult.Rolls` 如实为 0）。

**Q：为什么没有内置的「伤害公式」？**
A：因为公式恰恰是最该由策划调的东西。框架给的是参数（减伤常数 K、浮动幅度、上下限、属性名），
公式骨架固定且每一步都记录在 `DamageResult` 里 —— `Explain()` 一句话就能说清「这一下为什么打 37」。
