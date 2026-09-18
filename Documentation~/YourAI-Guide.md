# YourAI Framework 使用指南

> 面向任何 Unity 开发者的完整说明：这是什么、每个模块干什么、怎么跑起来、怎么接进你的游戏。
> 本文中所有代码签名都与源码逐一核对过，可直接抄。

---

## 目录

1. [这是什么](#1-这是什么)
2. [十分钟上手](#2-十分钟上手)
3. [架构总览](#3-架构总览)
4. [核心概念：回合与 Pump](#4-核心概念回合与-pump)
5. [模块详解](#5-模块详解)
6. [常见配方](#6-常见配方)
7. [调试与诊断](#7-调试与诊断)
8. [验证与测试](#8-验证与测试)
9. [设计红线（先读，能省一半时间）](#9-设计红线)
10. [目录结构](#10-目录结构)

---

## 1. 这是什么

一套为 Unity 游戏设计的 LLM（大语言模型）驱动 AI 框架：让 NPC 对话、剧情演出、GM 助手这类功能接上 DeepSeek / OpenAI / Anthropic / Gemini（或任何 OpenAI 兼容网关），同时保证**游戏帧率不抖、出错不炸、关键操作可控**。

它跟"直接调 SDK"的差别在于五条设计决策，全部贯穿到底：

| 决策 | 含义 | 对你的意义 |
|---|---|---|
| **轮询，不是 async** | 内核零 Unity 依赖、零 async/await，宿主每帧调 `Pump()` 推进一步 | 不会在 WebGL 上踩 ThreadPool 的坑；随时可暂停、可取消 |
| **失败是值，不是异常** | 网络断了、Key 错了、模型罢工——都返回带原因的结果对象 | 你不用 try/catch 包游戏逻辑；断网时 NPC 给出"理由"，游戏照跑 |
| **交付文本即定稿** | 只有校验通过、回合终结后的文本才进入记忆与 UI | 没有半截话污染存档 |
| **不可逆工具须审批** | `SideEffect.Irreversible` 的工具默认**永不执行**，除非宿主显式批准 | 删档、扣钱这类操作不靠提示词求模型别乱来，靠代码拦截 |
| **内核零 Unity 依赖** | Core/Memory/Grounding/Agent/Presentation 是纯 C#（netstandard2.1） | 可在控制台程序里跑 135 条离线断言，改动不会到了真机才炸 |

**要求**：Unity 6000.5（Unity 6.5）及以上；无第三方依赖；无 API Key 也能完整跑通（降级路径）。

---

## 2. 十分钟上手

### 2.1 安装

1. 把本仓库（含 `YourAIFramework/` 目录）放到工程外任意位置，或直接用本地路径。
2. Unity 菜单：**Window → Package Manager → 左上角 + → Add package from disk**，
   选择 `YourAIFramework/package.json`。
3. 编译通过、Console 无报错即完成。无第三方依赖，不需要装任何别的包。

### 2.2 最小可运行（三条语句）

```csharp
using YourAI.UnityRuntime;

// 建一个 DeepSeek 角色
AiAgent agent = AiRuntimeFactory.CreateDeepSeekAgent(
    "smith",                    // 名字（也是记忆的 actorId）
    apiKey,                     // 你的 Key；空串 = 降级模式
    "你是一个沉默寡言的铁匠。");   // 人设（SystemPrompt）

// 装进场景：自动建一个 GameObject，挂 AiRuntimeBehaviour
var runtime = AiRuntimeFactory.Install(agent);

// 开一回合：宿主会自动在 Update 里泵它
runtime.Ask("player", "有什么趁手的兵器？");
```

收结果的方式任选其一：

```csharp
// 方式 A：事件（推荐，适合多个并发回合）
runtime.TurnFinished += turn =>
{
    if (turn.Result.Success) Debug.Log(turn.Result.Text);
    else                     Debug.Log("失败：" + turn.Result.Error);
};

// 方式 B：协程（顺序演出最顺手）
IEnumerator Play()
{
    var turn = runtime.Ask("player", "谁在那里？");
    yield return runtime.WaitFor(turn);        // 内部替你 Pump，混用两种方式无害
    Debug.Log(turn.Result.Text);
}

// 方式 C：Unity 6 Awaitable
var result = await runtime.AskAsync("player", "检查一下库存");
// 注意：AskAsync 自己泵回合，不要再 Track 它，否则一帧推两步
```

### 2.3 无 Key 会发生什么（降级路径）

`apiKey` 传空串，一切照常工作：回合立即以 `Faulted` 终结，`Result.Error` 带上"未配置 API Key"一类原因；安装时 Console **警告一次**（不是每帧刷屏）。UI、记忆、工具、事件全部照常，所以你可以**先写全部游戏逻辑，最后才填 Key**。

### 2.4 跑通样例

Package Manager 里选中本包 → **Samples** → Import：

| 样例 | 内容 |
|---|---|
| **Minimal Chat** | 纯文字教程：一条请求、一次流式回复，打印到 Console |
| **Chat UI** | 完整聊天窗口（UI Toolkit）：流式文本、阶段指示、错误横幅、对话日志 |

---

## 3. 架构总览

七层单向依赖，上层只能看见下一层的接口：

```
你的游戏代码
    │
┌───▼──────────────────────────────────────────────────────┐
│ UnityRuntime  AiRuntimeBehaviour / AgentViewBehaviour /   │  引用引擎
│               AiRuntimeFactory                            │
├──────────────────────────────────────────────────────────┤
│ Presentation  AgentViewModel / AgentDiagnostics /         │  纯 C#
│               PromptPreview                               │  (可离线测试)
├──────────────────────────────────────────────────────────┤
│ Agent         AiAgent / AgentTurn / ToolRegistry /        │  纯 C#
│               AgentTeam                                   │
├──────────────────────────────────────────────────────────┤
│ Grounding     ContextAssembler（上下文装配与预算裁剪）      │  纯 C#
├──────────────────────────────────────────────────────────┤
│ Memory        HybridMemoryStore / InMemory / File / 嵌入   │  纯 C#
├──────────────────────────────────────────────────────────┤
│ Transport     UnityHttpTransport / UnitySseClient         │  引用引擎
├──────────────────────────────────────────────────────────┤
│ Providers     DeepSeek·OpenAI·Anthropic·Gemini / 路由 /    │  纯 C#
│               重试 / 限流                                  │
├──────────────────────────────────────────────────────────┤
│ Core          契约接口 / JSON / SSE 帧队列 / HTTP 交换      │  纯 C#
└──────────────────────────────────────────────────────────┘
```

| 程序集 | 引擎依赖 | 一句话职责 |
|---|---|---|
| `YourAI.Core` | 无 | 全部契约（`ILlmProvider`、`ITool`、`IMemoryStore`…）+ 自研 JSON + SSE 解析 |
| `YourAI.Providers` | 无 | 四家厂商协议实现 + 故障切换（Router）、重试（Retry）、令牌桶限流（RateLimit） |
| `YourAI.Transport` | **有** | 用 `UnityWebRequest` 实现 `IHttpTransport` 与 SSE 客户端 |
| `YourAI.Memory` | 无 | 三种记忆库 + 三种嵌入器 + 混合检索 |
| `YourAI.Grounding` | 无 | 上下文装配：把世界状态、记忆、历史装进 token 预算 |
| `YourAI.Agent` | 无 | `AiAgent` 回合状态机、工具注册、审批、多角色团队 |
| `YourAI.Presentation` | 无 | MVVM 桥（ViewModel）、体检诊断、提示词预览 |
| `YourAI.UnityRuntime` | **有** | 场景宿主 `AiRuntimeBehaviour`、UI Toolkit 聊天视图、工厂 |
| `Editor` | **有** | Agent 体检窗口、一键离线冒烟测试 |

---

## 4. 核心概念：回合与 Pump

框架里**一切 LLM 交互都是回合（Turn）**——一个小状态机：

```
Idle → Building → Streaming ⇄ RunningTools → Validating → Completed
                                                      ↘ Faulted
                                  任意时刻可 Cancel → Cancelled
```

- `turn.Pump()`：推进一步。返回 `true` 表示还有后续。
- `turn.State`：当前阶段（`AgentTurnState` 枚举）。
- `turn.Text`：**目前为止**的文本，流式时逐字增长，校验后定稿。
- `turn.Delta` 事件：每来一段增量文本触发（在调用 `Pump` 的线程上）。
- `turn.ToolFinished` 事件：每次工具调用完成后触发，参数是 `(ToolInvocation, ToolResult)`。
- `turn.Result`：终结后才有值，见 `AgentTurnResult`（`Success / Text / Error / HttpStatus / ToolRounds / ToolCallsMade / ToolLog / ContextTokens / ElapsedMs …`）。

`AiRuntimeBehaviour.Update()` 替你泵所有被 `Ask`/`Track` 的回合——**大多数情况你不需要手动 Pump**。手动 Pump 的场景：自己的驱动循环、编辑器工具、单元测试里的假推进。

> **一条语义要知道**：一次 `Pump()` = 前进一帧。一个同步的假 Provider 在单次 `Pump()` 里就完成了整个响应，所以"发起请求"和"收割结果"天然隔两帧。写离线测试时用循环：`while (!turn.IsDone && n++ < 200) turn.Pump();`

---

## 5. 模块详解

### 5.1 Core —— 契约与基础设施

- **`LlmTypes.cs`**：`LlmMessage`（role/content/tool calls）、`LlmRequest`、`LlmStreamResult` 等。`LlmMessage.EstimateTokens` 供预算估算。
- **`ILlmProvider`**：`StartStream(LlmRequest)` → `ILlmStreamHandle`；`IsAvailable` / `UnavailableReason`（为什么不可用，给降级 UI 的话术）。
- **`ITool` / `IToolRegistry` / `ToolResult`**：工具契约，见 §5.6。
- **`IMemoryStore`**：`Append / Query / Remove / Clear / Count`。注意 `Query` 是**填调用方的列表**而不是返回新集合——检索每回合都跑，这里不许制造 GC。
- **`JsonParser` / `JsonValue`**：自研 JSON（解析 + 序列化），内核不依赖 Newtonsoft。
- **`SseFrameQueue` / `SseEventBuffer`**：把 `UnityWebRequest` 的下载流切成 SSE 事件，纯 C#，可离线测试。

### 5.2 Providers —— 四家厂商 + 三个包装器

| 类型 | 说明 |
|---|---|
| `OpenAiCompatibleProvider` | OpenAI 协议族：DeepSeek、本地 vLLM/Ollama、任何兼容网关 |
| `AnthropicProvider` | Claude 协议 |
| `GeminiProvider` | Gemini 协议 |
| `ProviderRouter` | 把多个 provider 串成候选链，前一个不可用自动切下一个（`Add(...)` 链式） |
| `RetryProvider` | 带退避策略的重试包装 |
| `RateLimitedProvider` + `PollingRateLimiter` | 令牌桶限流（`tokensPerSecond` + `burst`），避免刷爆免费额度 |

一般不用直接 new：`AiRuntimeFactory.CreateDeepSeekAgent(...)` / `CreateOpenAiCompatibleAgent(name, apiKey, baseUrl, chatPath, model, systemPrompt)` 已包好。要接 Anthropic/Gemini 就自己组合：

```csharp
var transport = new UnityHttpTransport();
var provider  = new AnthropicProvider("claude", transport, new AnthropicOptions { /* ... */ });
var agent     = new AiAgent("sage", provider);
```

### 5.3 Transport —— 引擎与内核的唯一桥

`UnityHttpTransport` 实现 `IHttpTransport`；`UnitySseClient` 流式收 SSE。内核定义接口、引擎层给实现，这就是"内核零 Unity 依赖"能被 asmdef 强制的原因。**你几乎永远不用碰这一层**，除非要接非 HTTP 通道。

### 5.4 Memory —— 让角色记得发生过什么

三种库 + 三种嵌入器自由组合：

| 组件 | 用途 |
|---|---|
| `InMemoryMemoryStore` | 进程内，够快，重启即忘。`maxRecordsPerActor` 可设上限 |
| `FileMemoryStore(directory)` | JSON 落盘（建议放 `Application.persistentDataPath` 下），重启不忘 |
| `HybridMemoryStore(inner, embedder)` | 关键词 + 向量混合检索，包装任何 inner 库 |
| `HashedEmbeddingProvider` | 零依赖的本地哈希嵌入：能用、不联网，语义质量一般 |
| `RemoteEmbeddingProvider` | 真嵌入（`RemoteEmbeddingProvider.OpenAI(transport, apiKey)` 或自定义 options），带向量缓存 |

```csharp
agent.Memory = new HybridMemoryStore(
    new FileMemoryStore(Path.Combine(Application.persistentDataPath, "ai-memory")),
    new RemoteEmbeddingProvider.OpenAI(new UnityHttpTransport(), embeddingApiKey));
```

`agent.AutoMemory` 默认 `true`：回合结束后自动按 `AutoMemorySalience`（默认 0.4）把重要对话写入记忆。要手工管理就关掉它，自己 `Append(MemoryRecord)`——字段：`ActorId / Kind(Working·Episodic·Semantic) / Text / UnixTime / Tags / Salience / EstimatedTokens`。

### 5.5 Grounding —— 上下文装配与预算

`agent.Context` 是一个 `ContextAssembler`：

- `TokenBudget`（默认 1500）：整个上下文的目标 token 上限。
- `Add(IContextProvider)`：注册上下文来源。世界状态、任务列表、玩家档案……每来源声明**优先级与预估 token**，装不下时按优先级从低往高丢（丢了几段记在 `agent.LastDroppedSections`）。
- 最简单的接入：`DelegateContextProvider`，一行委托搞定：

```csharp
agent.Context.Add(new DelegateContextProvider(
    name: "quest",
    priority: 90,
    estimate: () => 60,
    provide: ws => new ContextSection { Title = "当前任务", Content = questText }));
```

### 5.6 Agent —— 回合、工具、审批、团队

**`AiAgent`** 是一切的核心，可配置项一览（构造：`new AiAgent(name, provider)`）：

| 成员 | 默认 | 说明 |
|---|---|---|
| `SystemPrompt` | — | 人设与常驻指令 |
| `Model` / `Temperature` / `MaxTokens` / `ResponseFormat` | — | 采样参数（`Temperature` 默认 0.7） |
| `Tools` (`IToolRegistry`) | null | 工具注册表 |
| `ToolApproval` (`Func<ToolInvocation, ITool, bool>`) | null | 不可逆工具的审批回调，**null = 不可逆工具一律拒绝** |
| `Validators` (`ValidatorChain`) | 空 | 输出校验链：拒绝 / 修复后继续 |
| `ExpectedSchemaJson` | — | 期望的 JSON Schema（配合校验器做结构化输出） |
| `Memory` (`IMemoryStore`) | null | 记忆库，见 §5.4 |
| `Context` (`ContextAssembler`) | — | 上下文装配，见 §5.5 |
| `MaxHistoryMessages` | 12 | 进上下文的历史条数上限 |
| `MaxToolRounds` | 4 | 单回合最多几轮工具调用（防模型绕圈） |
| `MaxConcurrentToolJobs` | 0 | 并行长任务工具上限（0 = 不限） |
| `AutoMemory` / `AutoMemorySalience` | true / 0.4 | 自动记忆 |
| `Conversation` / `LastRenderedContext` / `LastContextTokens` | — | 只读：对话史 / 实际发出去的完整上下文 / token 数 |
| `Seed(userText, assistantText)` | — | 预埋对话史（开场剧情用） |
| `PreviewMessages(worldState, userMessage)` | — | **不发送**，返回本次会真正发出的消息列表（调试利器） |
| `BeginTurn(actorId, userMessage, worldState)` | — | 开一回合 |

**写一个工具**（模型可调用的游戏能力）：

```csharp
public class CheckHealthTool : ITool
{
    public string Name => "check_health";
    public string Description => "查询某角色当前血量";
    public string ParametersSchemaJson =>
        "{\"type\":\"object\",\"properties\":{\"who\":{\"type\":\"string\"}},\"required\":[\"who\"]}";
    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;   // 只读，可自动执行

    public ToolResult Invoke(ToolInvocation inv)
    {
        // 必须自己兜住异常：返回 Fail 而不是抛出
        try {
            string who = JsonParser.Parse(inv.ArgumentsJson)["who"].AsString();
            return ToolResult.Ok($"{who} 血量 72/100");
        }
        catch (System.Exception e) { return ToolResult.Fail(e.Message); }
    }
}

agent.Tools = new ToolRegistry();
agent.Tools.Register(new CheckHealthTool());
```

副作用分三档，行为不同：

| `ToolSideEffect` | 行为 |
|---|---|
| `ReadOnly` | 自动执行 |
| `Reversible` | 自动执行 + 记录日志 |
| `Irreversible` | **先问宿主**：调用 `agent.ToolApproval(inv, tool)`，返回 true 才执行；没设回调就拒绝 |

**长任务工具**（跨多帧的锻造、寻路…）：实现 `IToolJob`（或继承 `ToolJobBase`，自带 `State / Result / Pump / Cancel`），agent 会在 `RunningTools` 阶段每帧泵它们，上限就是 `MaxConcurrentToolJobs`。

**多角色团队 `AgentTeam`**：

```csharp
var team = new AgentTeam("forge-crew");
team.Register("smith",  smithAgent);
team.Register("appraiser", appraiserAgent);

// 接力：铁匠说完整理官接着说
ITeamTurn relay = team.BeginRelay(new[] { "smith", "appraiser" }, "player", "这把剑怎么卖？", null);
// 或圆桌：每人各说一轮
ITeamTurn parley = team.BeginParleyAll("player", "商量一下汛期布防", null);
runtime.Track(relay);           // 交给宿主泵
relay.StepFinished += step => Debug.Log($"[{step.Role}] {step.Text}");
```

### 5.7 Presentation —— UI 之前的那一层

- **`AgentViewModel`**：把 `AiAgent` 包成可绑定的属性 + 变更事件（`Text / State / Error / LastTool / IsBusy / CanSend` ↔ `TextChanged / StateChanged / ErrorChanged / ToolChanged / BusyChanged`），`Send / Pump / Cancel / Track`。**纯 C#，可脱机测试**——这就是为什么 UI 层薄得只剩贴纸。
- **`AgentDiagnostics.Inspect(agent)`**：体检。缺 provider=Error，provider 不可用=Warning，工具没配轮数上限、温度越界=Warning……返回 `List<Diagnostic>`（`Severity + Message`）。
- **`PromptPreview.Render(agent.PreviewMessages(null, "你好"))`**：把"即将发出什么"渲染成人话，含每条消息的估算 token。

### 5.8 UnityRuntime —— 场景里的胶水

| 类型 | 说明 |
|---|---|
| `AiRuntimeBehaviour` | 宿主：Update 泵全部回合、发 `TurnFinished`；`Ask / AskAsync / WaitFor / Track / CancelAll`；`PersistAcrossScenes` 跨场景不销毁 |
| `AgentViewBehaviour` | UI Toolkit 聊天视图。挂在与 **PanelRenderer**（配 PanelSettings 资产）同名的 GameObject 上，绑 `Bind(agent)` 即得完整聊天窗；UXML 里 `log / status / text / error / input / send` 六个名字是契约，不给 UXML 就代码自建 |
| `AiRuntimeFactory` | 三个静态方法：`CreateDeepSeekAgent` / `CreateOpenAiCompatibleAgent` / `Install` |

### 5.9 Editor —— 两个菜单

| 菜单 | 用途 |
|---|---|
| **Window → YourAI → Agent Settings** | 场景里选中任意含 `AiAgent` 的宿主：体检报告（按严重度着色）+ 提示词预览（输入一句话，看实际会发出什么、占多少 token） |
| **Window → YourAI → Smoke Test (Offline)** | 一键跑 6 项确定性冒烟（降级、回合生命周期、并行工具、ViewModel 事件、团队接力、记忆往返），全绿说明装好了 |

---

## 6. 常见配方

### 6.1 接自己的 OpenAI 兼容网关

```csharp
AiAgent agent = AiRuntimeFactory.CreateOpenAiCompatibleAgent(
    "npc", apiKey,
    "https://your-gateway.example.com",   // baseUrl
    "/v1/chat/completions",               // chatPath
    "deepseek-chat",                      // model
    "你是客栈掌柜。");
```

### 6.2 主备切换 + 限流

```csharp
var router = new ProviderRouter("main")
    .Add(new OpenAiCompatibleProvider("deepseek", transport, dsOptions))
    .Add(new OpenAiCompatibleProvider("local", transport, localOptions));   // 主家挂了用本地
var agent = new AiAgent("smith", new RateLimitedProvider(
    new RetryProvider(router), new PollingRateLimiter(tokensPerSecond: 30, burst: 60)));
```

### 6.3 结构化输出（让模型吐严格 JSON）

```csharp
agent.ResponseFormat = "json_object";
agent.ExpectedSchemaJson  = "{...你期望的 schema...}";
agent.Validators.Add(new MySchemaValidator());   // Validate 收到 RawText + ExpectedSchemaJson
// 校验拒绝 → 回合以 Faulted 终结，Error 是校验理由；支持"修复"：替换 NormalizedText 后继续
```

### 6.4 记忆持久化到存档目录

见 §5.4 代码。存档时只需带上 `FileMemoryStore` 的目录；不同角色用不同 `actorId` 自然隔离，`store.Clear(actorId)` 单独遗忘某人。

### 6.5 NPC 之间自己聊（圆桌）

见 §5.6 `AgentTeam` 的 `BeginParleyAll`；`Handoff` 委托可改写每个角色收到的输入（比如注入上一人的发言摘要）。

### 6.6 换掉聊天窗、接自己的 UI

只要你的 UI 能读属性、能收事件，就绑定 `AgentViewModel`，剩下的（泵回合、流式拼字、错误文案）它全包了。`AgentViewBehaviour` 本身就是"用 ViewModel 贴一个 UI Toolkit 皮肤"的参考实现。

---

## 7. 调试与诊断

| 想知道什么 | 看哪里 |
|---|---|
| 实际发给模型的完整上下文 | `agent.LastRenderedContext`（字符串）/ `agent.LastContextTokens`、`LastDroppedSections` |
| 本次会发出什么（不发送） | `PromptPreview.Render(agent.PreviewMessages(ws, msg))` 或 Editor 窗口直接看 |
| 配置有没有问题 | `AgentDiagnostics.Inspect(agent)` / Editor 菜单体检页 |
| 工具到底调了什么 | `turn.Result.ToolLog`（每条含 ToolName + ArgumentsJson + 结果） |
| 请求失败为什么 | `turn.Result.Error` + `turn.Result.HttpStatus`（失败是值，永远有原因） |
| 装好了没有 | Editor 菜单跑一次 Smoke Test (Offline) |

---

## 8. 验证与测试

框架自带**双离线门禁**（改完代码各跑一次，全绿才提交）：

| 门禁 | 位置 | 内容 |
|---|---|---|
| `core-test` | `tools/core-test` | 135 条断言：契约、记忆、装配、回合、团队、ViewModel、诊断 |
| `transport-compile` | `tools/transport-compile` | 把全部源码连着**真 Unity 6.5 引擎程序集**编译一遍：引擎侧 API 改名立即在这里报错 |

真机验收按根目录 `SMOKE-TEST.md`：1 个自动步骤（Editor 冒烟）+ 7 个手动步骤（降级、真实流式、取消、断网、存档记忆等，每步都写了预期现象）。

---

## 9. 设计红线

先读这五条，能省一半排查时间：

1. **一切靠 `Pump()` 推进，没有 async/await**。别在工具里 `await`——需要跨帧的工具请实现 `IToolJob`。
2. **一次 `Pump()` = 一帧**。同步假 provider 单次 Pump 就完成响应，收割发生在下一次 Pump；写测试用 `while (!turn.IsDone)` 循环。
3. **工具不许抛异常**。`Invoke` 里自己 try/catch，失败返回 `ToolResult.Fail(reason)`；抛出去等于把游戏循环炸了。
4. **`AskAsync` 的回合不要再 `Track`**——它自己每帧泵一次，双重泵会一帧推两步。
5. **回调都在调用 Pump 的线程上触发**。主线程泵就都在主线程；自己另起线程泵的话，Unity API 别在回调里直接摸。

---

## 10. 目录结构

```
YourAIFramework/
├── package.json              # UPM 包描述（Unity 6000.5+）
├── Documentation~/
│   └── YourAI-Guide.md       # 本文档
├── Editor/                   # 体检窗口 + 离线冒烟
├── Runtime/
│   ├── YourAI.Core/          # 契约 / JSON / SSE（零依赖）
│   ├── YourAI.Providers/     # 四家厂商 + 路由/重试/限流
│   ├── YourAI.Transport/     # UnityWebRequest 实现
│   ├── YourAI.Memory/        # 记忆库 + 嵌入器
│   ├── YourAI.Grounding/     # 上下文装配
│   ├── YourAI.Agent/         # 回合 / 工具 / 团队
│   ├── YourAI.Presentation/  # ViewModel / 诊断 / 预览
│   └── YourAI.UnityRuntime/  # 宿主 / 聊天视图 / 工厂
└── Samples~/                 # MinimalChat / ChatUiSample
```

仓库根目录另有 `DESIGN-ANALYSIS.md`（设计分析与决策记录）、`SMOKE-TEST.md`（真机验收清单）、`tools/`（离线门禁与 mock 服务器）。

---

## 附录 A：通用内核（YourFramework.Foundation）

除 AI 七层外，框架自带业务中立的通用内核（游戏 / XR / 仿真 / 孪生通用），同一套纪律：纯 C# 内核 + 引擎薄壳、双离线门禁、失败是值。

| 组件 | 命名空间 | 一句话职责 |
|---|---|---|
| `ModuleCenter` / `IModule` | `YourFramework.Core` | 模块管理中心：按 `InitOrder` 稳定排序 Init → 每帧 Pump → 逆序 Shutdown；依赖=更小的 InitOrder 数字，`Get<T>()` 保证唯一实例（重复类型/重名 fail-fast） |
| `GameEntry` | `YourFramework.UnityRuntime` | 引擎宿主：Awake 站位、Start 初始化、Update 泵模块、OnDestroy 逆序关停；引导脚本在自己的 Awake 里 `GameEntry.Ensure().Register(...)` |
| `EventBus` | `YourFramework.Event` | 类型安全事件总线：`Publish<T>`（同步、结构体零装箱）与 `Post/Pump`（跨帧延迟，一次泵=一帧）双模式；派发期订阅/退订安全 |
| `ObjectPool<T>` | `YourFramework.Pool` | 纯 C# 对象池：借还配平校验、空闲上限、借还钩子；重复借还 = 程序 bug，直接抛 |
| `GameObjectPool` | `YourFramework.Pool`（引擎侧） | 预制体池：回收 `SetActive(false)` 而非 Destroy，杜绝高频实例化的 GC 尖峰 |
| `SaveSystem` | `YourFramework.Save` | 槽位存档：JSON 信封 + 原子写（tmp+替换）+ 备份轮转 + 损坏隔离（.corrupt）；槽名 `[A-Za-z0-9_-]` 防目录穿越 |
| `SettingStore` / `IValueCodec` | `YourFramework.Save` | 键值配置：类型化读写（int/float/bool/string/JsonValue），可选 `XorValueCodec` 混淆（防手改，非加密）；首次运行=空表不报错 |

最小接线：

```csharp
// 引导脚本 Awake
GameEntry.Ensure()
    .Register(new EventBusModule())          // IModule 实现由各模块自带
    .Register(new SaveModule(saveDirectory));

// 任意位置发布/订阅
EventBus bus = EventBus.Global;
bus.Subscribe<DamageEvent>(e => hpBar.Show(e.Amount));
bus.Publish(new DamageEvent { Target = "goblin", Amount = 12 });

// 存档
saves.Save("slot_1", payload, version: 2);
saves.TryLoad("slot_1", out JsonValue data, out int version, out string error);
if (error != null) ui.ShowLoadError(error);   // 失败是值
```

对应离线用例组：`module/*`、`event/*`、`pool/*`、`save/*`、`setting/*`（tools/core-test，154 项断言的一部分）。

### A.1 流程状态机（YourFramework.Flow）

游戏生命周期骨架（启动 → 检查更新 → 登录 → 主菜单 → 战斗……），M2 交付：

- `ProcedureFlow : IModule`——注册进 `ModuleCenter` 即被统一泵驱动，无需第二个循环；
- 自定义流程继承 `Procedure`（OnEnter / OnUpdate / OnExit），`Host.ChangeProcedure<T>()` 切换——**延迟到下一泵首**生效，回调永远完整跑完；
- 过程回调抛错 → 流转 `Faulted` 停转并上报，其他模块照常；注册期错误（重名/未知目标/缺起始流程）直接抛；
- `Host.Stop()` 优雅收尾：当前流程正常 OnExit 后停机。

```csharp
var flow = new ProcedureFlow("boot", initOrder: 10)
    .Add(new CheckUpdateProcedure())
    .Add(new LoginProcedure())
    .Add(new MainMenuProcedure())
    .Start<CheckUpdateProcedure>();
GameEntry.Ensure().Register(flow);
```

对应离线用例组：`flow/*`（tools/core-test，159 项断言的一部分）。

### A.2 UI 面板栈（YourFramework.UI 内核）

面板系统（PanelStack/UIPanelManager）自研实现，M2 交付（纯内核已闭环，PanelRenderer 适配壳随后）：

- `PanelStack`——面板注册/开合状态机（`Closed → Opening → Open → Closing → Closed`，两段式：宿主完成视觉挂接后调 `CompleteOpen/CompleteClose`，为动画与异步加载预留同一接口）；
- `PanelController`——`Name + Layer + Exclusive` + 四个回调；`VisibleStack()` 按层级自底向上排序；
- `IsInputBlocked(name)`——更高层有 OPEN 状态的独占面板（弹窗/遮罩）即屏蔽，Closing 立即解除；
- 引擎壳（下一刀）：`UIPanelManager : IModule` + PanelRenderer 每层根节点 + 聊天窗迁入为普通面板。

对应离线用例组：`ui/*` + `module/pump-error-isolation`（tools/core-test，166 项断言的一部分）。

### A.3 AI 层模块化（AiModule）

AI 七层挂进 ModuleCenter 的适配物，M2 交付：

- `AiModule : IModule`（YourAI.UnityRuntime）——Init 建宿主并切 `ExternallyDriven`（引擎 Update 静音，由 ModuleCenter 统一泵）、Shutdown `CancelAll()`；依赖构造注入（agent 工厂 + EventBus），不引 DI 容器；
- `AiTurnEventRelay`（YourAI.Presentation，纯 C# 离线可测）——回合的 Delta / ToolFinished / 终结事件全部转成 EventBus 事件（`AiDelta` / `AiToolFinished` / `AiTurnFinished`，负载带回合实例），游戏代码订阅总线即可，AI 与游戏逻辑零耦合；
- `AiRuntimeBehaviour` 新增 `ExternallyDriven` 开关：单独用（老工程）照常自泵，进模块体系后由 AiModule 驱动，一帧只推一步。

```csharp
// 引导脚本 Awake
GameEntry.Ensure().Register(new AiModule(
    () => AiRuntimeFactory.CreateDeepSeekAgent("smith", apiKey, "你是铁匠。"),
    EventBus.Global));

// 游戏任意处
bus.Subscribe<AiTurnEventRelay.AiTurnFinished>(e => Debug.Log(e.Turn.Result.Text));
```

对应离线用例组：`aimodule/*`（tools/core-test，169 项断言的一部分）。

**A.2.1 引擎壳（M2-4 交付）**：`UIPanelManager : IModule` + `UIPanel`——一个 PanelRenderer 根下按 PanelLayer 分五个容器；`Open/Close` 同帧完成挂接/摘除（CompleteOpen/Close 两段式接口为动画/异步资产预留）；PanelRenderer 重载自动重建分层容器并重挂 WantsVisual 面板；实现 `IPanelTickable` 的面板在 Open 期间获得每帧泵。聊天窗已迁入：`ChatPanel : UIPanel`（与独立形态 `AgentViewBehaviour` 共用 `ChatUiBuilder`，对话历史在 AiAgent 上，关窗重开不丢）。

```csharp
// 引导（agent 只建一次，喂给 AI 模块与聊天面板同一实例）
AiAgent agent = AiRuntimeFactory.CreateDeepSeekAgent("smith", apiKey, "你是铁匠。");
var ui = new UIPanelManager(panelSettings)
    .Register(new ChatPanel(agent));
GameEntry.Ensure()
    .Register(ui)
    .Register(new AiModule(() => agent, EventBus.Global));
ui.Open("chat");
```

### A.4 数据表（YourFramework.Data）

`DataTable` / `DataTableSet`——蓝图"数据表管理"的运行时半边（策划配 → 工具导出 JSON → 运行时读）。约定格式：JSON 行数组，主键默认 `id`：

- `DataTable.FromJson(json, keyField)`——解析并按主键建索引；**表错误一律 fail-fast（统一抛 `ArgumentException`）**：坏 JSON、非对象行、缺主键、主键为空、主键重复都在加载时炸出来（构建期产物，启动时查坏好过运行中静默查空）；
- 类型化读取：`GetString/GetInt/GetFloat/GetBool(key, fieldPath, fallback)`，`fieldPath` 支持点路径（`"stats.hp"`）；缺行/缺字段走 fallback（业务上"没有"不是错误，数据错误才是）；
- `Format(key, fieldPath)`——`{field}` 占位符用同行字段替换（描述文本/本地化惯例），未知占位符原样保留；
- `DataTableSet.Load(name, table)`——同表名再 Load = **整表替换，即数值热更落点**（热更管线落地后直接对接）；`Get(name)` 缺表返回 null，是否致命由调用方决定。

```csharp
var set = new DataTableSet();
set.LoadJson("items", itemsJson);                 // items.json: [{"id":"sword_001","name":"铁剑","price":120}, ...]
var items = set.Get("items");
string name  = items.GetString("sword_001", "name");
int    price = items.GetInt("sword_001", "price", 999);
string tip   = items.Format("sword_001", "desc"); // "{name} 售价 {price} 文" → "铁剑 售价 120 文"
```

对应离线用例组：`data/*`（tools/core-test，174 项断言的一部分）。

### A.5 资源管理（YourFramework.Assets）

`AssetManager` / `IAssetProvider` —— 自家接口在前、引擎后端在后：游戏代码永远只面对
AssetManager，自研资源包 / 本地直读都只是 Provider 实现，随时可换。

- `AssetManager.Load(key)` → `AssetRequest`：同 key 全程唯一（去重 + Ready 缓存一体）；
  Pump 每帧推进：并发上限（默认 4，`MaxConcurrent` 可调）内派发到第一个 `Handles(key)`
  的 Provider，轮询直到落定；**轮询泵语义，无 Task/回调风暴**；
- `AssetRequest` 粘性终态 `Ready / Failed`：失败带原因（`Error`），不抛异常 —— 运行期
  I/O 故障一律"失败是值"；`Load("")` 这类参数错误才 fail-fast；
- Provider 契约：`Handles` 是纯谓词，`Pump(request)` 每帧调用直至落定（同步 Provider
  第一帧就落定）；Provider 在 Pump 内抛异常 → 该请求 Failed 带原因，管理器照常运转；
- 内置 Provider：`MemoryAssetProvider`（测试替身/代码内内容）、`FileAssetProvider`
  （根目录相对键，文本扩展名解 UTF-8、其余读字节；路径穿越键拒绝并报错）；
- `Drop(key)` / `Clear()`：显式逐出，加载中的请求以 Failed "dropped while loading"
  落定，轮询方看得到失败，不会有孤儿请求。

**自研资源包**：`PackAssetProvider`（YourFramework.Foundation/Assets/Pack）把自家资源包
（清单 + 版本 + 下载队列 + 缓存账本）包进同一个 Provider 契约：地址 → 依赖闭包（拓扑序）
→ 逐帧装载 → 取出资源。清单/版本由 Editor 菜单 **YourAI → 资源包 → 构建** 产出；
`IBundleBackend` / `IDownloadBackend` 是引擎边界，默认实现是 `AssetBundleBackend`
（`LoadFromFileAsync` 泵）与 `UnityWebDownloadBackend`（断点续传 + Range 校验）。

```csharp
// 引导：Provider 按优先级排列，第一个 Handles(key) 的生效
var assets = new AssetManager(new IAssetProvider[] {
    new MemoryAssetProvider(),                          // 代码内内容/测试
    new PackAssetProvider(manifest, bundleBackend, ledger), // 自研资源包
    new FileAssetProvider(Application.streamingAssetsPath), // 兜底
});
GameEntry.Ensure().Register(assets);

// 使用：轮询，不等待
AssetRequest req = assets.Load("configs/items.json");
void Update() {
    if (req.IsDone) { /* req.IsReady ? req.Payload.Text : req.Error */ }
}
```

对应离线用例组：`asset/*` 与 `pack/*`（tools/core-test，370 项断言的一部分）。

### A.6 本地化（YourFramework.Localization）

`LocalizationStore` / `ILocalizationSource` —— 自家取词链在前、外部源为可选插件：
游戏代码只面对一个 Get，挂了几个源、有没有源，调用方无感。

- `LoadJson(language, json)`：JSON 平铺 key→text（`{"ui.title":"山河问剑录"}`）；
  **同语言再 Load = 整表替换**（与 DataTableSet 同一热更语义）；坏 JSON / 非字符串值 /
  空语言名 fail-fast（构建期产物错误）；
- 取词链：`CurrentLanguage` → `FallbackLanguages`（显式逐跳，如 ["zh","en"]，序数精确
  匹配，"zh-CN" 绝不隐式回落 "zh"）→ `DefaultLanguage`（通常是开发语言）→ 外部源 →
  **主键兜底**——缺 key 是业务不是 bug，调用方总有话可显，配套 `Has(key)` 显式检查；
- `Get(key, args)`：`{0}/{1}` 位置参数，`CultureInfo.InvariantCulture` 渲染，数字不随
  设备区域漂移；
- `OnLanguageChanged`：真实切换触发一次（同值重设不触发）；
- `AddSource(ILocalizationSource)`：外部取词插槽，全链未命中后、主键兜底前尝试；
  实现契约：可重入纯查询，取不到返回 false，永不抛异常。

**外部取词源**：`ILocalizationSource` 是插槽 —— 全链未命中后、主键兜底前尝试。
框架不绑定任何外部本地化后端：多语言表直接用 `LoadJson` 灌进来就是完整功能；
需要多表集合 / 复数变体 / 资产表（按语言切图标与语音）时，挂一个实现该接口的源即可。

```csharp
var loc = new LocalizationStore();
loc.LoadJson("zh", zhJson);
loc.FallbackLanguages.Add("en");
loc.DefaultLanguage = "en";
loc.OnLanguageChanged += lang => RefreshAllTexts();
loc.CurrentLanguage = "zh";                     // 或设备语言自动判定
titleText.text = loc.Get("ui.title");
swordTip.Text  = loc.Get("item.sword", item.Name, item.Price);

// 需要并表（多表集合 / 远端表 / 资产表）时挂一个源：
loc.AddSource(new MyTableSource(...));
```

对应离线用例组：`loc/*` 与 `locale/*`、`plural/*`（tools/core-test，370 项断言的一部分）。

### A.7 声音（YourFramework.Audio）

`AudioManager` / `IAudioBackend` —— 决策与发声分离：本类只做调度与混音决策
（纯 C#，离线全测），发声本体归引擎后端（AudioSource 池 / Wwise / FMOD 可换）。

- **混音数学**：最终音量 = 每次播放 scale × 通道音量 × master 音量（钳制 0..1）；
  `RegisterChannel("music", maxVoices, volume, mute)` 预注册通道（master 自带，
  重注册 master 即全局音量）；
- **声部上限与抢断**：通道满时，新声音优先级高过"最低优先级者"（平级取最老）就偷，
  否则拒绝（`Play` 返回 null）——拒绝是业务不是错误；通道未注册是布线 bug，fail-fast；
- **实时重平衡**：`SetVolume` / `SetMute` 立即推送进所有存活声部——不是只对下一个
  声音生效；静音的循环声部保持"播放中"（音量 0），解静音无缝恢复；
- **回收**：非循环声部自然结束后由后端在 Pump 时上报（isPlaying 轮询引擎真值），
  槽位释放；`Stop(id)` 未知 id 静默无操作（业务）；Shutdown 全停。

**引擎后端**：`UnityAudioBackend`（YourFramework.UnityRuntime）——AudioSource 池 +
线性槽位映射，零分配；ClipKey 由宿主注入的解析器翻译成 AudioClip（通常接
AssetManager，声音模块自身不碰资源系统）。

```csharp
// 引导
var audio = new AudioManager(
    new UnityAudioBackend(rootGameObject, poolSize: 16,
        key => /* AssetManager 轮询包装 */ null));
audio.RegisterChannel("music", 2);
audio.RegisterChannel("sfx", 8);
GameEntry.Ensure().Register(audio);

// 使用
audio.Play("music/town", "music", loop: true);
audio.Play("sfx/slash", "sfx", priority: 1, volumeScale: 0.8f);
audio.SetVolume("master", 0.8f);   // 全局音量滑条
```

对应离线用例组：`audio/*`（tools/core-test，195 项断言的一部分）。

### A.8 网络（YourFramework.Net）

`NetClient` / `INetLink` —— 连接状态机与字节管道分离：状态机（心跳/超时/退避重连/
断线补发）纯 C# 离线全测；管道是可替换的 `INetLink`（内置 BCL `ClientWebSocketLink`，
WebGL 等平台换自己的实现）。

- **状态机**：`Idle → Connecting → Open ⇄ Reconnecting → Closed`；全部时间由
  `Pump(deltaSeconds)` 注入 —— 无墙钟、无 async/await 出现在公开 API，回放与
  快进测试天然支持；
- **红线四条全落实**：超时/心跳/重试/退避全部在 `NetClientConfig` 显式可配
  （连接超时、ping 间隔、静默判死、最大重试次数、退避基数与上限、队列容量）；
  失败带原因（`OnDropped(reason)` / `OnClosed(reason)`，空串=干净关闭）；
- **断线补发**：未 Open 时的 Send 进有界队列，Open 后按序补发；溢出丢最旧
  （游戏语义：新状态比旧状态重要），丢弃计数可见；终态 Closed 后 Send 直接
  fail-fast（会话已死，开新会话才是正路）；
- **数据零拷贝**：入站字节直接透传（link 所有权）；出站 Send 即拷贝；
- 心跳：ping 按 `HeartbeatIntervalSeconds` 定时发；任意入站字节都刷新活性；
  静默超过 `HeartbeatTimeoutSeconds` 判死入重连。

```csharp
var config = NetClientConfig.CreateDefault("wss://game.example.com/ws");
config.HeartbeatTimeoutSeconds = 30f;
var net = new NetClient(new ClientWebSocketLink(), config, new MyNetListener());
GameEntry.Ensure().Register(net);
net.Open();
```

**下载能力（决策记录）**：资源下载不另起网络模块 —— 自研下载队列（`DownloadQueue`）
已覆盖并发/重试/退避/超时/断点续传，属资源包的一部分；本模块只管长连接游戏业务流量。
两条线职责不重叠。

对应离线用例组：`net/*`（tools/core-test，370 项断言的一部分；ClientWebSocketLink
为编译门禁级验证，活链路联调在真机/本地回环进行）。

### A.9 实体 / SDK 管道 / 诊断台（M5 三件套）

**实体注册表（YourFramework.Entity / `EntityRegistry`）**：NPC、机关、子弹、系统
组件的统一住址 —— `Spawn` 进门（`ISpawnable.OnSpawn`）、每帧 `EntityPump(delta)`
（`IEntityTickable`，时间注入可回放）、`Despawn` 出门（`IDespawnable.OnDespawn`）。
组件式拼装而非继承树：实体类实现哪些小接口就获得哪些能力，不被迫继承框架基类。
Spawn 抛异常 = 实体未进门（无半孵化状态）；Tick 抛异常 = 单实体被隔离上报，帧继续；
重名 fail-fast；Shutdown 逆序全 Despawn。

**SDK 管道（YourFramework.Platform / `SdkHub`）**：登录/支付/广告/推送等平台渠道
的唯一入口。`Register` 后 `Init` 按 `InitOrder` 稳定排序初始化；**渠道故障降级**——
某渠道 Init 抛异常只标记该渠道不可用（`FailureReason(name)` 带原因），绝不阻断其他
渠道；游戏永远经 `Get<T>(name)`（不可用即 null）访问，UI 与业务自然降级。重复注册、
初始化后注册 = 布线 bug，fail-fast。

**诊断台（YourFramework.Diagnostics / `FrameworkDiagnostics`）**：named counters
（`Inc`）/gauges（`Set`）+ 最近错误环形缓冲 + `Snapshot()` 一键文本快照（调试 UI 与
远程上报只消费快照）。`Init` 自动挂钩 `AiDiagnosticsHost.LogError` —— ModuleCenter
的模块错误统一落进诊断台，Shutdown 自动摘钩。

```csharp
GameEntry.Ensure()
    .Register(new FrameworkDiagnostics())   // InitOrder -100，最早
    .Register(new EntityRegistry())
    .Register(new SdkHub());

entities.Spawn(new NpcGuards { Name = "guard_001" });
sdk.Register(new WeChatLoginChannel());
// 游戏侧：
var login = sdk.Get<ILoginChannel>("weChat");   // 不可用即 null，UI 自然降级
Debug.Log(diagnostics.Snapshot());              // 一键导出全框架状态
```

对应离线用例组：`entity/*`、`sdk/*`、`diag/*`（tools/core-test，209 项断言的一部分）。

### A.10 热更编排（YourFramework.HotUpdate）

`HotUpdateFlow` / `IHotUpdateStep` —— 阶段是插槽：编排只管顺序、进度事件、
失败截停与终态语义；版本检查、补丁下载、程序集重载都由 `IHotUpdateStep` 实现。

- **阶段形态**：每个阶段实现 `IHotUpdateStep`——`Begin(context)` 一次，
  `Pump()` 每帧推进至 `Done` 或 `Failed`（与 AssetProvider/NetClient 同一泵哲学）；
- **失败是值**：任何阶段 Failed（或 Begin/Pump 抛异常）→ `OnFailed(阶段名, 原因)`
  → 终态，后面的阶段不启动；异常原文进 reason；
- **共享上下文**：`flow.Context.Set/Get` 传递版本号、名单、产物（如热更
  程序集装载完的清单）；
- **终态纪律**：终态后不 Reset 重跑 fail-fast；活跑中 Reset 也 fail-fast；
  Shutdown 中断活跑 = 一次带原因的 OnFailed；
- **进度事件**：`OnStageStarted/Done/OnFailed/OnSucceeded`，进度 UI 只消费事件。

**程序集重载阶段**：元数据与热更 DLL 的顺序、字节来源、失败回滚都由阶段实现负责。
框架提供的是：`IHotUpdateStep` 契约（`Begin` 一次 + `Pump` 每帧）、失败带原因的终态
语义、以及 `Assembly.Load(byte[])` 这条 BCL 路径；AOT 元数据与热更程序集名单的产物
格式由后端决定。框架**已自带实现**：`AssemblyReloadStep` + `AssemblyManifest`
（清单 → 拓扑序 → 逐帧装载 → 入口点），字节来源（`IAssemblyBytesSource`）
与装载器（`IAssemblyLoader`）是插槽，换加密包或 HybridCLR 不动上半场；
清单字段与回滚点语义见 [Framework-Guide 17.1](Framework-Guide.md)。

```csharp
var flow = new HotUpdateFlow(progressUi);
var manifest = AssemblyManifest.LoadJson(manifestJson);   // 上一阶段下载到本地的清单
flow.Run(new IHotUpdateStep[] {
    new CheckVersionStep(),                 // 你的阶段：比版本差量，写下载名单
    new DownloadPatchStep(),                // 你的阶段：驱动 DownloadQueue 到 Idle
    new AssemblyReloadStep("ReloadAssemblies",   // 框架自带：拓扑序 → 逐帧装载 → 入口点
        manifest, new[] { "HotUpdate" },
        new FileAssemblyBytesSource(cacheRoot),
        new ByteArrayAssemblyLoader(), new HotfixEntryRunner()),
});
GameEntry.Ensure().Register(flow);
```

对应离线用例组：`hot/*`、`asm/*`（tools/core-test，370 项断言的一部分）。

### A.11 数值系统（YourFramework.Stats）

`StatSheet` / `EffectHost` / `DamagePipeline` / `DeterministicRandom` —— 属性、持续效果、
伤害结算三件事，外加一个可复现的随机源。它不认识实体、也不认识资源：实体要挂数值，就在
自己的组件里持有一张 `StatSheet`（模块间不互相引用这条红线照旧）。

- **随机**：PCG32 + 命名分流（`Fork("crit")` 与 `Fork("loot")` 互不扰动），状态就是一个
  `ulong`，直接存档或发网络包即可还原；`Chance(0)` / `Chance(1)` / 空区间**不消耗**随机数；
- **属性表**：`Value = clamp((Base + ΣFlat) × max(0, 1+ΣPercentAdd) × Π max(0, 1+PercentMul))`。
  分三层是因为"多个 +10% 该相加还是连乘"必须能分别调；写严读松（写未声明的属性当场抛，
  读返回 0，要区分"0"与"不存在"用 `TryValue`）；`RemoveBySource` 就是"脱装备"那一句；
- **持续效果**：三种堆叠策略（`Refresh` 只刷新 / `Stack` 每层各加一份 / `Independent` 各自
  计时），`Pump(delta, List<EffectEvent>)` 把 tick 与到期作为**值**交出来。先 tick 后到期、
  且只结算"还活着的那段时间"，于是 **步长无关**：`Pump(1)×3` 与 `Pump(3)×1` 的事件序列
  完全相同，快进或卡顿不会改变战斗结果。到期按引用逐条摘修饰符，不误伤同标签的外来项；
- **伤害**：`DamageProfile` 把公式参数化（属性名、减伤常数 K、浮动幅度、上下限），减伤走
  `K / (K + 防御)` 比例曲线而非减法（减法在高等级会出现"一下打不痛"的数值悬崖）。随机消费
  次数由配置决定（`DamageResult.Rolls` ∈ {0,1,2}）；守方缺防御按 0 处理，攻方缺攻击属性以
  `AggressorStatMissing` 返回 —— 失败是值；
- **可解释**：`StatSheet.Explain(stat)` 与 `DamageResult.Explain()` 一行答出"这个 37 是
  怎么算出来的"，把"伤害看着不对"变成可定位的问题。

```csharp
var sheet = new StatSheet();
sheet.Declare("atk", 10f);
sheet.AddModifier(new StatModifier("atk", StatKind.PercentAdd, 0.2f, "天赋A"));

var rng = new DeterministicRandom(seed: 20260918, stream: "battle");
var pipeline = new DamagePipeline(DamageProfile.WithCrit(), rng.Fork("damage"));

DamageResult hit = pipeline.Resolve(attackerSheet, defenderSheet, DamageRequest.Of(1f));
if (hit.Ok) { Debug.Log(hit.Explain()); }   // 一行推导：基础值 → 暴击 → 减伤 → 浮动 → 取整
else { Debug.Log(hit.Outcome); }            // 缺攻方属性 / 无伤害 —— 失败是值
```

对应离线用例组：`stat/*`（tools/core-test，370 项断言的一部分）。

---

### A.12 地址目录表（YourFramework.Catalog）

`AddressCatalog` / `CatalogEntry` / `CatalogValidator` / `CatalogWriter` —— 把散落在代码里的地址收成一张可离线校验的表：地址 → 定位符，支持标签变体（画质档/平台/语言）、补丁合并（整条替换 + 来源归属）、注入谓词的校验、以及确定性写出。

```csharp
AddressCatalog catalog = AddressCatalog.FromJson(json);

// 同一个地址，不同环境取不同的东西（变体按子集匹配，取最具体者）
CatalogTagSet env = new CatalogTagSet(new[] { "hd", "zh" });
CatalogResolution hit = catalog.Resolve("ui/login", env);
if (hit.Resolved) { Debug.Log(hit.Locator + " (tags=" + hit.MatchedTags + ")"); }
else { /* 可选地址缺失：值，不是异常 */ }

// 补丁：只写差异，合并后来源可追溯
catalog.Merge(AddressCatalog.FromJson(patchJson));
string source = catalog.Require("ui/hud").Source;   // 哪张表改的

// 校验：外部世界（定位符是否存在）由调用方注入，模块不认识打包实现
CatalogReport report = CatalogValidator.Validate(catalog, env, locator => files.Contains(locator));
if (!report.IsClean) { Debug.LogError(report.Dump()); }

// 确定性写出：输出只取决于内容，不取决于书写顺序（可 diff、可热更比对）
string normalized = CatalogWriter.Write(catalog);
```

对应离线用例组：`catalog/*`（tools/core-test，370 项断言的一部分）。

---

### A.13 脚本层与 XLua 适配（YourFramework.Scripting）

游戏侧要热更代码时，程序集重载在 IL2CPP 上会被平台拒绝，而**解释执行不会被拒绝** —— 于是框架把「热更代码」这件事在 IL2CPP 上的落点做成了脚本层：内核定义接缝（代码块 / 清单 / 装载序 / 沙箱策略 / 失败姿态，零第三方依赖），XLua 是一个可插拔实现。

```csharp
// 内核：清单 + 来源 + 沙箱策略（这三样与引擎无关，离线可测）
ScriptManifest manifest = ScriptManifest.LoadJson(patchJson);
FileScriptChunkSource source = new FileScriptChunkSource(cacheDir);
ScriptSandboxPolicy sandbox = ScriptSandboxPolicy.CreateStrict();
sandbox.Allow(ScriptCapability.Modules);           // 补丁要 require 自己的模块才开

// 适配层（需要工程里有 XLua + YOURFRAMEWORK_XLUA 宏）
IScriptRuntime runtime = new XLuaScriptRuntime("xlua", source, sandbox);

// 插进热更流水线：与程序集重载同一个插槽，失败原因照样透传到 UI
var step = new ScriptPatchStep(manifest, new[] { "battle.damage" }, source, runtime);
flow.Run(new IHotUpdateStep[] { versionCheck, download, assetApply, step });

// 让运行时随模块系统逐帧泵（LuaEnv.Tick）
GameEntry.Ensure().Register(new ScriptRuntimeModule(runtime));   // InitOrder 50
```

三件值得单独记住的事：

1. **回滚是真的**：Lua 能卸代码块，所以装载期失败会把已装载的块**反序卸掉**，真的回到补丁前；入口点跑过之后则如实汇报而不假装回滚；
2. **沙箱在构造时就应用并回读确认**：关不上就直接转 `Faulted` 并拒绝装载 —— 没关门的解释器里跑下载来的补丁比不热更更糟；
3. **`require` 走我们自己的来源**：补丁代码里的 `require("core.util")` 由 `LuaEnv.AddLoader` 接到 `IScriptChunkSource`，并套同一道路径闸（拒绝绝对路径与「..」）—— 请求名来自下载来的代码，属于不可信输入。

对应离线用例组：`script/*`（内核）；适配层另有门禁 `tools/xlua-adapter`。详见 `Framework-Guide.md` 第 20 节。
