# Your AI Framework Game Demos —— 可玩游戏演示扩展包

框架主包（`com.your.ai.framework`）里的 ModuleDemos 是"23 个模块挨个演给你看"；
本扩展包是**第二站**：把模块拼成完整、可玩的小游戏，边玩边学。
全部程序化生成（Cube/Sphere/UGUI），不依赖任何美术资产。

## 安装

**方式一：本地 file: 引用（改包源码即时生效，适合开发期）**

`Packages/manifest.json` 里加：

```json
"com.your.ai.game-demos": "file:C:/path/to/YourAIGameDemos"
```

**方式二：git URL（发布后可用）**

```json
"com.your.ai.game-demos": "https://github.com/LikeMyProject/YourAIFramework.git#game-demos"
```

（`game-demos` 分支/tag 指向只含本扩展包内容的孤儿提交，与主框架的发布方式同构。）

**前置**：先装好主框架 `com.your.ai.framework`（本包的演示引用它的程序集）。

## 导入游戏

`Window → Package Manager → 选本包 → Samples 标签 → Import`，
游戏会拷进 `Assets/Samples/...`，之后你随意改。注意：同一游戏只导入一份，
**手动拷贝的副本 + 包内导入的副本并存会撞类名**（CS0101）。

## 工程前置条件

- Unity 6000.5+，主框架包已安装；
- **Active Input Handling = Both**（或 Input Manager (Old)）——
  演示用经典 `UnityEngine.Input` API（Edit → Project Settings → Player → Other Settings → Configuration）。

## 游戏列表

| 游戏 | 亮点 | 用到的模块 |
|---|---|---|
| 贪吃蛇 SnakeGame | AI 操控现场围观大模型开车；无 Key/超时优雅兜底 | ModuleCenter / ProcedureFlow / EventBus / ObjectPool / SaveSystem / SettingStore / DeterministicRandom / AiAgent |
| 勇者试炼场 RpgGame | 不用 AI 层：打怪升级、背包、装备武器、角色属性，档案跨局保留 | ModuleCenter / ProcedureFlow / EventBus / ObjectPool / SaveSystem / DeterministicRandom |
| 三消 Match3Game（制作中） | AI 生成关卡布局 JSON，失败回落内置关卡 | + JsonParser / AiAgent |
| XR 打靶 XrRangeGame（制作中） | HMD 跟踪 / 手柄射线 / 震动，无头显桌面降级 | + UnityEngine.XR |
