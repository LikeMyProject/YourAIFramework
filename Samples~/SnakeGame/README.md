# 贪吃蛇 Snake Game —— 模块拼装实战演示

ModuleDemos 巡演看完后的"第二站"：一个真正能玩的贪吃蛇，演示怎么把框架的模块
拼成一个完整游戏。全部程序化生成（Cube 身体、Sphere 食物、UGUI 面板），
不依赖任何美术、字体、预制体资产。

## 工程前置条件

- **Active Input Handling = Both**（或 Input Manager (Old)）：
  Edit → Project Settings → Player → Other Settings → Configuration。
  演示用经典 Input API；改成 Both 后 Unity 会自动重启一次编辑器。

## 怎么玩

1. 新建空场景（保留 Main Camera），建空 GameObject，挂 `SnakeGameHost`；
2. 按 Play：菜单态点「开始游戏」（或按回车）；
3. 方向键 / WASD 移动，空格或「重新开始」按钮重开；
4. **A 键或「AI 操控」按钮**：把方向盘交给大模型。

## AI 操控（重点演示）

点「AI 操控」后，把局面（头的位置、朝向、食物位置、四个方向安不安全）发给
deepseek-chat，**一次要三步**方向，存进小队列，蛇每走一步消耗一个 —— 网络来回
约 1 秒，换回 1.65 秒的方向储备，蛇基本不用干等网络。队列见底、回复还没到的
那一拍用本地启发式垫场，**面板上实时显示 AI 在思考、计划了几步、有没有落到
兜底**，AI 的在场与缺席看得见。

三条设计纪律，全部来自框架的"失败也是值"：

- **没配 Key**：AI 缺席，本地启发式照样玩，不报错不崩；
- **回复超时（2.5 秒）/ 答非所问**：清空旧计划，落到启发式，下一拍继续问；
- **上下文不无限涨**：每 10 次请求重建一次干净的 agent。

配 Key（本地配置，**不要提交进仓库**）：`persistentDataPath` 下建 `game-settings.json`：

```json
{ "ai.apiKey": "sk-..." }
```

提示词也不写死：代码里只有默认值，想调就加一个键，改完不用重编译：

```json
{ "ai.apiKey": "sk-...", "snake.ai.systemPrompt": "你的提示词……" }
```

默认端点是 DeepSeek 官方 OpenAI 兼容接口（`api.deepseek.com/chat/completions`，
模型 `deepseek-chat`），`AiRuntimeFactory.CreateDeepSeekAgent` 开箱即用。

## 用到的框架模块

| 模块 | 在这个游戏里的角色 |
|---|---|
| `ModuleCenter` | 宿主持有模块栈：Score 25 → Flow 30 → Rules 35 |
| `ProcedureFlow` | Menu → Playing → GameOver，切换延迟到泵首 |
| `EventBus` | 移动/吃食物/终局全走 Publish（struct 事件零装箱） |
| `ObjectPool` | 蛇身 Cube 借还配平，长度伸缩不产生泄漏 |
| `SaveSystem` | 最高分原子写 + 损坏隔离，失败也是值 |
| `SettingStore` | `ai.apiKey`、`snake.ai.systemPrompt` 都从本地配置读，不硬编码 |
| `DeterministicRandom` | PCG32 撒食物，同种子同序列，方便复现 bug |
| `AiAgent`（AI 层） | 一次要三步方向，流式 Pump，超时干净取消 |

## 文件导读

| 文件 | 内容 |
|---|---|
| `SnakeBoard.cs` | 纯 C# 玩法逻辑（事件 struct + 棋盘），每拍零分配，无头可测 |
| `SnakeModules.cs` | 分数模块（存档教学）+ 规则模块（事件→状态切换）+ 三个流程状态 |
| `SnakeView.cs` | 池化 Cube/Sphere 视图，格坐标→世界坐标 |
| `SnakeUi.cs` | UGUI 面板程序化搭建（Canvas/EventSystem/按钮/字体） |
| `SnakeControl.cs` | 键盘输入 + AI 控制器（提问/解析/超时/启发式） |
| `SnakeGameHost.cs` | 引导、时间驱动、依赖图、UI 接线 |

## 这个演示刻意不用的东西

- **不用 GameEntry**：自己 new ModuleCenter 自己泵 —— 宿主只是个泵，
  多游戏各持一套模块互不干扰；想并进大工程就把 Register 搬进 GameEntry 引导。
- **不用协程/async**：轮询驱动（框架铁律），每帧 `_modules.Pump()` + 攒时间片。
