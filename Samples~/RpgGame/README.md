# 勇者试炼场 RPG Demo —— 不用 AI 层的模块拼装实战

贪吃蛇之后的"第三站"：一个**完全不用 AI 模块**的 RPG，证明框架的游戏底盘
单独拿出来就能拼出完整玩法：第三人称 3D 大世界（城镇 / 野外 / 副本各有地形），
WoW 式操作（A/D 转向、右键拖转视角），打怪、升级、背包、装备、野外与副本
Boss 一应俱全。全部程序化生成（基础几何体拼的角色和怪物、UGUI 面板），
不依赖任何美术、字体、预制体资产。

## 工程前置条件

- **Active Input Handling = Both**（或 Input Manager (Old)）：
  Edit → Project Settings → Player → Other Settings → Configuration。
  与贪吃蛇同一条要求；改成 Both 后 Unity 会自动重启一次编辑器。

## 怎么玩（多场景版：主菜单 → 城镇 → 野外 / 副本）

### 第一步：把四个场景搭进 Build Settings（接入教学的一部分）

demo 刻意不带 .unity 场景资产（零资产原则），四个场景都是"空场景 + 一个宿主组件"：

| 场景名（一字不差） | 建 3D 物体 | 挂的组件 |
|---|---|---|
| `RpgMenu` | 保留 Main Camera | `RpgMenuHost` |
| `RpgTown` | 保留 Main Camera | `RpgTownHost` |
| `RpgWilds` | 保留 Main Camera | `RpgFieldHost` |
| `RpgDungeon` | 保留 Main Camera | `RpgFieldHost` |

然后把四个场景按 `RpgMenu, RpgTown, RpgWilds, RpgDungeon` 拖进
**Build Settings → Scenes In Build**（File → Build Settings）。
跑不起来时第一件事就是查这里 —— SceneFlow 拒绝加载时会如实告诉你原因。

### 第二步：玩一遍

1. 从 `RpgMenu` 按 Play：点「开始冒险」，进度条走完自动落到城镇；
2. 城镇是安全区，出门不点按钮，魔兽那一套 —— **走到边上按 F**：
   广场**泉水**（状态回满并自动存档）、北边**副本传送门**（进副本）、
   南边**城门**（去野外）；
3. 走路：**A/D** 转向、**W/S** 前后、**Q/E** 平移，**右键拖动**转视角，**滚轮**缩放；
   **空格 / J** 攻击，**I** 背包，**1/2/3** 放技能，**F** 交互；
4. 野外深处有树精长老（Boss），副本里是岩石魔王（血厚攻高、必掉钢剑）；
   Boss 同场只留一只，倒下 30 秒后重生；出生点旁的**回城传送石**按 F 直接回城；
5. 副本里没有第二个出口，只有死亡或通关 —— 两个都会弹出结算界面，
   选「回城休整」回城，落在**泉水旁的复活点**（等级装备背包全在）；
6. 魔王倒下触发通关结算，回城休整再战。

## 玩法结构（和贪吃蛇同一套拼装思路）

| 层 | 文件 | 内容 |
|---|---|---|
| 纯 C# 核心 | `RpgCore.cs` | 物品表（DataTable 加载）、怪物表、角色属性（StatSheet）、背包堆叠、装备换穿、伤害配置 |
| 纯 C# 核心 | `RpgArena.cs` | 大世界模拟：怪物追击、攻击结算（框架伤害管线）、按等级加权刷怪、Boss 驻守与重生、奖励落账 |
| 模块层 | `RpgModules.cs` | 档案模块（事件一来就存档）+ 规则模块（死亡→切状态）+ 三个流程状态 |
| Unity 宿主 | `RpgView.cs` / `RpgTownView.cs` / `RpgUi.cs` / `RpgControl.cs` | 拼装模型与地形、池化借还、第三人称相机、程序化 UGUI、输入 |

## 用到的框架模块（全部不碰 AI 层）

| 模块 | 在这个 RPG 里的角色 |
|---|---|
| `ModuleCenter` | 宿主持有模块栈：Profile 25 → Skills 28 → Flow 30 → Rules 35 |
| `SkillBook` / `SkillLibrary` | 技能表 JSON 加载、冷却/读条/耗蓝/目标选择全在框架里，游戏侧只实现"查蓝花钱"和"结算效果"两个接口（失败也是值，比如没蓝没目标如实返回原因） |
| `ProcedureFlow` | Menu → Playing → Dead，切换延迟到泵首 |
| `EventBus` | 刷怪/受击/击杀/升级/捡物/穿装全走 struct 事件零装箱 |
| `GameObjectPool` | 怪物 / 火球按预制体借还配平，生死靠事件驱动，长跑不漏 |
| `StatSheet` + `DamagePipeline` | 玩家和每只怪一张属性表；装备加成走 StatModifier（按来源挂/摘），伤害按"攻 × 减伤曲线 K/(K+防) × 浮动"结算，公式参数只在 `RpgCombat.DamageConfig` 一处 |
| `DataTable` | 物品表 JSON 一键加载，查表带缓存 |
| `PanelStack` | HUD / 背包 / 菜单 / 阵亡四个面板注册进面板栈，开合走 `OpenNow` / `CloseNow`，层级与互斥屏蔽不自己写 |
| `SaveSystem` | 角色档案原子写 + 损坏隔离；**每次变化后落盘**，随时拔电源不丢进度 |
| `SceneFlow` | 场景流程：Request 切场景 → 每帧 Pump → 进度条 → 完成/失败事件。引擎那一下藏在 `ISceneBackend` 后面（UnityRuntime 里的 `UnitySceneBackend` 只有几十行），换 Addressables 不用动核心 |
| `DeterministicRandom` | 伤害方差、金币区间、掉落判定、刷怪加权，同种子同序列可复现 |

### 存档教学点（与贪吃蛇"最高分"的差异）

贪吃蛇是终局存一次；RPG 是**每次变化后存**（击杀、升级、捡物、穿装、喝药），
因为角色成长跨局保留。Save 返回 null 才算成功，失败带原因进日志、游戏照玩
（失败也是值）；读不到档案（第一次玩/损坏隔离）从 1 级开新档。

## 文件导读

| 文件 | 内容 |
|---|---|
| `RpgCore.cs` | 数据与规则：`RpgPlayer`（StatSheet 属性）、背包堆叠、`TryEquip` 换装回旧件、`RpgCombat`（伤害配置）、`RpgZones`（野外/副本预设，含 Boss） |
| `RpgArena.cs` | 实时模拟：`Tick` 一口喂进去，刷怪/追击/Boss 驻守重生，奖励只有一个写入者 |
| `RpgModules.cs` | `RpgProfileModule`（存档）+ `RpgRulesModule`（死亡接线）+ 三个流程状态 |
| `RpgSkills.cs` | 技能模块：JSON 技能表 → `SkillLibrary`，`SkillBook` 泵冷却与读条；实现 `ISkillStatSource`（蓝）与 `ISkillEffectSink`（走框架伤害管线落账） |
| `RpgView.cs` / `RpgTownView.cs` | 拼装模型（勇者 / 史莱姆 / 狼 / 蜘蛛 / 野猪 / 石魔 / 两个魔王）、野外与副本地形、火球投射物、第三人称跟随相机 |
| `RpgUi.cs` | HUD 血条蓝条经验条、12 格背包、菜单/阵亡覆盖层；面板开合全部注册进框架 `PanelStack` |
| `RpgControl.cs` | WoW 式操作：A/D 转向、Q/E 平移、右键视角、滚轮缩放、F 交互，朝向与移动解耦 |
| `RpgFieldHost.cs` | 战斗场景宿主：场景名领刷怪账（RpgZones）、模块栈、事件接线、野外回城传送石、回城 |
| `RpgMenuHost.cs` / `RpgTownHost.cs` | 主菜单与城镇宿主：SceneFlow 最简用法模板 |
| `RpgSimpleUi.cs` | 菜单/城镇共用的程序化 UI（标题 + 按钮列 + 进度条） |

## 无头检查

`tools/.rpg/`（开发期验证，不是提交前检查）：730 个检查项全过 ——
属性曲线、背包堆叠与满拒绝、换装回旧件、用药、框架伤害管线的保底与确定性、
存档往返（真存真读）、死亡切状态、大世界追击/击杀/Boss 驻守与重生。
其中"怪贴着玩家却永远不咬"的冷却写回 bug 就是这套检查抓出来的。

### 技能教学点（框架技能模块的第一块拼图）

技能表是一段 JSON（`RpgSkillModule.TableJson`，根对象带 `skills` 数组），
三条技能全是**配置**而非代码：火球术（单体远程）、旋风斩（周身群伤）、
治疗术（读条 1.2 秒的自疗）。游戏侧只做三件事：

1. 每帧 `SkillBook.Pump(dt)`（冷却走表、读条走条）；
2. 实现 `ISkillStatSource`：查蓝、花钱（花在起手，读条失败也不退——规则先说清）；
3. 实现 `ISkillEffectSink`：把 `damage`/`heal` 效果翻译成打怪掉血、给自己回血。

目标选择交给 `SkillTargetQuery` + 候选列表（自己一队、怪物一队），
单体技能没目标、没蓝、冷却没好，`TryBegin` 都**返回带原因的失败值**而不是抛异常，
战报里"释放失败：没有目标"就是这么来的。

### 场景流程教学点（把这套搬进你自己的项目）

demo 现在是四个场景的完整链路：**主菜单 → 城镇 → 野外 / 副本 → 回城**，
全走框架的 `SceneFlow`，没有一处直接调 `SceneManager`。接入自己项目五步：

1. **引擎壳**：`UnitySceneBackend` 直接用（它只把 `LoadSceneAsync` 包成句柄）；
2. **建模块**：`new SceneFlow(bus, new UnitySceneBackend(), Debug.Log)` 注册进 ModuleCenter；
3. **发请求**：按钮回调里 `_scenes.Request("场景名")`；忙碌/重名/没进 Build Settings
   都会返回带原因的拒绝值，进度条读 `IsBusy` + `Progress`；
4. **听事件**：`SceneLoadCompletedEvent` / `SceneLoadFailedEvent` 走 EventBus（struct 零装箱）；
5. **加地图**：新战斗场景 = `RpgZones` 加一条预设 + 一个空场景挂 `RpgFieldHost`，
   玩法代码一行不动 —— 配置驱动落到场景层。

场景切换的教学核心：**加载进度、失败原因、忙碌保护全是显式的**，
UI 层看得见也测得了 —— 无头检查里用假后端把整条管线逐帧验证过。

### 框架接管记（能走框架的都走了）

demo 最早为了跑通玩法，伤害、属性、物品表、池子都是自己手写的。后来按
"能走框架的必须走框架"逐个搬进框架，搬的过程本身就是接入教学：

| 原来自己写 | 现在交给框架 | 换来的东西 |
|---|---|---|
| 手写伤害公式（攻-防+方差） | `DamagePipeline` + `DamageProfile` | 结算每一步可复算，暴击/保底/浮动全配置化，随机消费如实记录 |
| 手写属性推导 | `StatSheet` + `StatModifier` | 装备加成按来源挂/摘，升级重算不改代码 |
| 物品表写死在代码里 | `DataTable` | 表进 JSON，加装备不加代码 |
| 手写对象池 | `GameObjectPool` | 按预制体借还，怪物和火球共用一套 |
| GameObject.SetActive 开关面板 | `PanelStack` | 开合状态机、层级、互斥屏蔽不自己写 |

接入也反过来推动了框架：`PanelStack` 对无动画面板的两段式（Open + CompleteOpen）
太啰嗦，demo 接入时补了 `OpenNow` / `CloseNow` 同步开关 —— 演示就是框架的
第一块试金石，用得越深问题暴露得越早。

## 这个演示刻意不用的东西

- **AI 层**：全程不引用 YourAI.Agent —— 底盘归底盘，AI 是可选增益；
- **动画系统**：动作全靠程序摆（受击脉冲、出生长大、火球自转），不碰 Animator；
- **寻路**：怪物直线追击 + 边界夹住，A* 之类留给真实项目；
- **协程/async**：轮询驱动（框架铁律），宿主每帧 `_modules.Pump()` + `arena.Tick()`。
