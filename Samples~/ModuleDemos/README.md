# 模块速览 Demo（ModuleDemos）

一分钟看懂 23 个模块各自能干什么。所有演示**零资产依赖**：UI 是运行时生成的
UI Toolkit 视觉树，音效是运行时合成的正弦波，JSON 内联在代码里，文件写在系统
临时目录——导入即可跑。

## 上手三步

1. Package Manager 里导入本示例（Samples → Module Demos → Import）；
2. 新建空场景（保留默认 Main Camera），建一个空 GameObject，挂上 **DemosHub** 组件；
3. 按 **Play**——Console 会依次上演全部 23 个演示，每个之间停 2 秒。

想单独重放某一个：运行中右键 DemosHub 组件标题 → ContextMenu 菜单 → 选对应演示。

> **注册要满足各模块的 Init 前置条件**：例如流程机必须在 setup 阶段 `Start<T>()` 设起始状态、
> SDK 渠道必须在 `SdkHub.Init` 之前注册。这类检查是**运行期**的 —— 四套离线检查只看编译与
> 纯 C# 检查项，看不见它们，缺了会在 Play 时当场抛异常。改动注册清单时，对照 `DemoSetup`
> 类注释里那张表过一遍；运行中的 `[DemoTour] 模块自检` 那行会显示模块数与流程机状态。

## 演示导览

| # | 模块 | 演示什么 | 重点看什么 |
|---|---|---|---|
| 01 | ModuleCenter | 注册→按 InitOrder 排序初始化→每帧 Pump→逆序 Shutdown | Engine 依赖 FuelTank：**依赖就是数字**；重复注册当场抛 |
| 02 | EventBus | Publish 同步直发 / Post + Pump 延迟派发 | Post 之后、Pump 之前没有人收到事件 |
| 03 | ObjectPool / GameObjectPool | StringBuilder 池 + 方块池借还 | 借还失衡**当场抛**（程序 bug fail-fast） |
| 04 | SaveSystem / SettingStore | 槽位存档 + 键值设置 | 原子写、TryLoad 失败带 error 不抛异常 |
| 05 | ProcedureFlow | Boot → Menu → Playing 启动流程 | 切换延迟到下一拍，状态回调永远完整 |
| 06 | DataTable | 配表读取/点路径/模板格式化/整表替换 | 第二次 Load 同表名 = **配表热更**（120→150） |
| 07 | AssetManager | 内存/文件双 Provider 轮询加载 | 不存在的资源 → `Failed + 原因`，不抛异常 |
| 08 | LocalizationStore | 中英双语、回退链、不变文化格式化 | 全链未命中返回键名本身，UI 永远有话可显 |
| 09 | PanelStack + UIPanelManager | HUD + Popup 层级与独占屏蔽 | Popup 打开瞬间 `IsInputBlocked(hud)` 变 true |
| 10 | AudioManager | 分组混音、优先级抢断、实时静音 | sfx 上限 2 放 3 个 → 高优先级抢断；拒绝返回 null |
| 11 | EntityRegistry | 实现 ISpawnable/IEntityTickable 即获得能力 | 不强迫继承基类；Tick 异常单实体隔离 |
| 12 | NetClient | 连接→掉线→退避重连→终结（演示域名必失败） | 未连接时 Send 进队列；**失败全部带原因** |
| 13 | SdkHub | 正常渠道 + 故障渠道 | 支付渠道挂了 → `Get` 返回 null + `FailureReason` |
| 14 | FrameworkDiagnostics | 计数器/仪表/错误环 | `Snapshot()` 一键导出全部健康信息 |
| 15 | HotUpdateFlow | 三阶段编排 + 故意失败的第二轮 | 失败 → `OnFailed(阶段名, 原因)` 终态，后续不启动 |
| 16 | AI 模块（可选） | 无 Key 展示降级表现；配 Key 即真流式对话 | **AI 缺席，一切照常**——框架的核心承诺 |
| 17 | 自研资源包 AssetPack | 清单 → 版本差量 → 下载队列 → 依赖全链装载 | 闭包按**拓扑序**装载，每帧只推进一个 bundle；Drop 归还引用 |
| 18 | 自研本地化包 LocalePack | 语言链 → 多集合表 → CLDR 复数 → 内联格式化 → 资源地址 → 插进源插槽 | 复数按**文案实际命中的语言**判；取值失败只计数不抛 |
| 19 | 自研热更程序集 AssemblyReload | 清单 → 拓扑装载顺序 → 逐帧装载 → 完整性校验 → 入口点 → AOT 白名单 | 装载期失败 = 入口点一个都没跑，等于整批回滚；入口点按 Order 升序执行 |
| 20 | 数值系统 Stats | 确定性随机 → 属性表三层求值 → 逐帧驱动效果 → 伤害管道 | 同种子同序列（分流互不扰动）；3 秒 DoT 正好跳 3 次；攻方缺攻击属性 → 失败也是值 |
| 21 | 地址目录表 AddressCatalog | 解析 → 变体解析 → 补丁合并 → 校验 → 确定性写出 | 子集匹配（不是有交集就算）；必需项解析不出 = 拦构建的错误；两次写出逐字节相同 |
| 22 | 脚本层 Script/XLua | 清单 → 装载序 → 沙箱策略 → 失败真回滚 | 依赖在前、同一块只装一次；缺最后一块时前几块被**反序卸掉**（Lua 能卸代码块，这是相对程序集热更的硬气之处）；入口跑过之后不假装回滚 |
| 23 | 技能 Skills | 技能表（JSON，代码里没有一个技能名）→ 地址解析 → 目标选择 → 冷却与吟唱 → 数值桥打掉血量 | 地址解析不出 = 拦构建的错误；拒绝优先级固定（冷却→资源→目标）；吟唱打断**不退款**；脚本钩子失败不截停施法 |

## Demo 16 想看真对话？

打开 `%USERPROFILE%/AppData/LocalLow/<公司>/<产品>/demo-settings.json`（Demo 04
运行后自动生成），加一行：

```json
"ai.apiKey": "sk-你的 DeepSeek Key"
```

再单独重放 Demo 16，即可看到流式打字。真实项目的完整聊天 UI 见
`Samples~/MinimalChat` 与 `Samples~/ChatUiSample`，深度用法见
`Documentation~/YourAI-Guide.md`。

## 从演示到项目

每个演示文件头部都有注释说明"真实项目怎么接"。两个最有用的习惯：

- **模块注册**抄 `DemoSetup.RegisterAll()`——InitOrder 数字即依赖关系；
- **轮询推进**抄任意 Demo 的 `while (!xxx.IsDone) { xxx.Pump(); yield return null; }`——框架没有
  回调风暴，跨帧的事都是这个形状。

**这两条里最容易漏的是循环里的那行 `Pump()`。** 框架里没有任何东西会"自己往前走"：

- 局部 `new` 出来的实例（演示大多如此，为了自包含）**不在** ModuleCenter 里，没人替你泵，
  写成 `while (!xxx.IsDone) { yield return null; }` 就会一直等下去，Console 停在那儿不动；
- 注册进 `GameEntry` 的模块才由 `GameEntry` 的 Update 每帧统一泵（见 `DemoSetup.Assets`）。

所以等待循环的规矩是：**里面必须真的调 `Pump()`，并且加上帧数上限**——万一对方不落定，
打印一条带原因的超时警告，比无限等下去好得多（`DemoContent.AssetsDemo` 是最小示例）。

（框架开发仓里还有一条对应的自动检查 `tools/check_demo_loops.py`，专门扫演示里的等待型循环，
缺驱动或没上限会直接点出行号。）
