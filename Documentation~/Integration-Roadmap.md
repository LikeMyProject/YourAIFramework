# 从想法到上线：手动做一个东西怎么接入框架

> 回答一个问题："**我自己想做个玩法 / 小游戏，这个框架怎么接进来？**"
> 模块细节看 [Framework-Guide.md](Framework-Guide.md)；成品参照
> `YourAIGameDemos/Samples~/RpgGame/`（一个完整 RPG 的接法，本文多处指它当例子）。

---

## 一、总路线：六步

```
装包 → 搭宿主骨架 → 写纯 C# 核心 → 挑模块注册进去 → 接视觉与输入 → 五条件验收
```

记住一条主轴：**框架的一切都是"模块"**——模块注册进 ModuleCenter，
框架每帧泵（Pump）它们；你的玩法核心也是模块或被模块驱动。
接框架 = 把你需要的模块 Register 进去，然后每帧 Pump。

---

## 二、第 1 步：装包

两种方式任选：

1. Unity 菜单 Window → Package Manager → 左上角 **+** → **Add package from disk**
   → 指向 `YourAIFramework/package.json`；
2. 或者直接把 `YourAIFramework/` 整个目录拷进工程的 `Packages/` 文件夹。

零外部依赖，不需要任何第三方包。

如果你的工程用了 asmdef，把你代码所在程序集的 references 加上三个：
`YourAI.Core`、`YourFramework.Foundation`、`YourFramework.UnityRuntime`
（AI 相关再另加 `YourAI.Agent` 系列）。没用 asmdef 的普通工程什么都不用做。

---

## 三、第 2 步：搭宿主骨架（可整段抄）

空场景里建一个空物体，挂这个脚本：

```csharp
using UnityEngine;
using YourFramework.Core;
using YourFramework.Event;

public class MyGameHost : MonoBehaviour
{
    private EventBus _bus = new EventBus();             // 事件总线：模块间通信全走它
    private ModuleCenter _modules = new ModuleCenter(); // 模块容器

    private void Awake()
    {
        _modules.Register(new MyGameModule());          // 按需注册，可链式多个
        _modules.Initialize();                          // 按 InitOrder 从小到大初始化
    }

    private void Update()
    {
        _modules.Pump();     // 框架的心跳：冷却、读条、流程机、场景进度全在这里走
    }

    private void OnDestroy()
    {
        _modules.Shutdown(); // 逆序收尾
    }
}

// 一个最小模块：实现 IModule 五个成员
public class MyGameModule : IModule
{
    public string Name => "MyGame";
    public int InitOrder => 0;                   // 小的先初始化
    public void Init(ModuleCenter host) { }
    public void Pump() { }                       // 每帧调用
    public void Shutdown() { }
}
```

也有偷懒版：不想自己管宿主物体，`GameEntry.Ensure().Register(new MyGameModule())`
一句搞定（框架帮你建宿主、帮你泵）。demo 刻意没用它（教学要展示手工全过程），
你的项目随便选。

---

## 四、第 3 步：写纯 C# 核心（先写规则，不碰引擎）

这是本框架最重要的一条分工：**玩法规则写在不引用 UnityEngine 的纯 C# 里**，
引擎（视图、输入、声音）只是它的"外壳"。

好处马上兑现：核心可以不开 Unity 就逐条检查（离线测试），改坏了一眼看出来。

规矩（demo 全程在守）：

- 纯 C# 文件里不 `using UnityEngine`；
- 随机数用 `DeterministicRandom`（同种子同序列，出了 bug 能复现）；
- 自己写错（参数非法、状态错乱）→ 抛异常；
- 外部失败（存档写失败、查表没这个键）→ 返回**带原因的错误值**，别只回 bool。

参照物：`RpgCore.cs`（物品/怪物/角色规则）、`RpgArena.cs`（刷怪追击的实时模拟）。
离线检查工程的搭法照抄 `tools/.rpg/`——一个控制台 csproj 引进你的核心源码，
`Check(条件, "说明")` 一条条列，跑完全过才算数。

---

## 五、第 4 步：挑模块（按"我要什么"查表）

| 我要…… | 用这个模块 | 指南章节 | demo 里的样子 |
|---|---|---|---|
| 存进度（拔电源不丢） | `SaveSystem` | §6 | `RpgProfileModule` |
| 切场景 + 加载进度条 | `SceneFlow` + `UnitySceneBackend` | §（场景） | `RpgTownHost` / `RpgFieldHost` |
| 界面开合 / 弹窗层级 / 互斥 | `PanelStack` | §8 | `RpgUi`（GoPanel 适配器） |
| 技能（配置表 + 冷却 + 读条 + 耗蓝） | `SkillLibrary` + `SkillBook` | §21 | `RpgSkillModule` |
| 攻防伤害 / 属性成长 / 暴击 | `StatSheet` + `DamagePipeline` | §18 | `RpgCombat.DamageConfig` |
| 怪物 / 子弹反复生灭 | `GameObjectPool` | §5 | `RpgView`（怪壳池 + 火球池） |
| 物品表 / 关卡表配置化 | `DataTable` | §9 | `RpgItems`（JSON 一键加载） |
| 模块间 / 系统 → 视图 通信 | `EventBus`（struct 零装箱） | §4 | 全 demo |
| 游戏状态机（菜单→游戏中→结算） | `ProcedureFlow` | §7 | `RpgModules` |
| 音乐音效分组混音 | `AudioManager` | §12 | — |
| 多语言 | `LocalizationStore` | §11 | — |
| 长连接（心跳+重连） | `NetClient` | §13 | — |
| 引导/一次性流程 | `GuideFlow` | §（引导） | — |

原则只有一条：**能走框架的走框架**；框架确实没有的（寻路、动画、AI 之外的特殊
系统）才自己写。你的东西接得越深，框架的问题暴露得越早——发现问题回来改框架，
这是这套 demo 一直在做的事（比如 PanelStack 的 `OpenNow/CloseNow` 就是接入时
发现两段式太啰嗦补的）。

---

## 六、第 5 步：接视觉与输入

宿主每帧的固定三拍（`RpgFieldHost.Update` 就是模板）：

```
1. _modules.Pump()                 // 框架的事先走
2. 核心.Tick(dt, 输入, ...)        // 纯 C# 核心推进（换帧率不影响规则）
3. UI / 视图同步                    // 把核心的账画出来
```

- **输入**自己写个组件（参照 `RpgControl.cs`：读键 → 存成数据字段，宿主来取），
  别在核心里直接读 Input——核心要能离线跑；
- **视图跟事件走**：怪物 `Spawned` → 从池子借、`Died` → 还池子（`RpgView` 的
  OnSpawned/OnDied），每帧只同步位置，不做别的；
- **UI 开合交给 PanelStack**：搭好的面板注册进去，开 = `OpenNow`，关 = `CloseNow`，
  层级互斥不用自己写；
- **切场景交给 SceneFlow**：`Request("场景名")` + 每帧 Pump，进度条读 `Progress`，
  忙碌/失败都返回带原因的值。

---

## 七、第 6 步：五条件验收（做完的定义）

一个东西做完 = **源码 + 检查项 + 检查全过 + git + 文档**，缺一不算完：

1. 源码：核心 + 模块 + 宿主各就各位；
2. 检查项：核心规则在离线检查里逐条列出（照抄 `tools/.rpg` 的格式）；
3. 检查全过：不许有跳过的；
4. git：提交；
5. 文档：README 或 guide 里写清楚"这是什么、怎么接"。

---

## 八、常见的坑（我们都踩过的）

| 坑 | 症状 | 规矩 |
|---|---|---|
| 初始化顺序 | LateUpdate 里空引用连坏 | **先建被依赖方**：输入先于视图、相机先取好再搭景 |
| 流程切换时机 | 切了状态但当帧还在跑旧逻辑 | 切换延迟到泵首（ProcedureFlow 的契约，别自己抢跑） |
| 每帧产生垃圾 | 长跑掉帧 | 每帧路径零 GC，事件用 struct，别装箱 |
| 异步到处飞 | 时序没法测 | 轮询驱动，宿主每帧 Pump，不写裸 async/await |
| UGUI 进度条永远满 | fillAmount 不生效 | Image 没挂 sprite 时 Filled 整个失效，先给纯白 sprite |
| 只返回 bool 的失败 | 出了问题不知道为什么 | 失败也是值：返回带原因的结果对象 |

---

## 九、然后呢

- 模块细节、参数、代码示例：`Framework-Guide.md` 对应章节；
- 想看"全都接起来"长什么样：跑 `YourAIGameDemos/Samples~/RpgGame`（README 有
  逐步搭建指南），或贪吃蛇 `Samples~/SnakeGame`（更小的一站）；
- 框架哪里不顺手：改框架、提检查项、写进文档——用户才是框架的第一块试金石。
