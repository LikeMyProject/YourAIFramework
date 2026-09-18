# Chat UI sample

A UI Toolkit chat window over `AgentViewBehaviour` + `AgentViewModel`: one input
line, one streaming answer area, a scrolling log, a phase indicator
(思考中… / 说话中… / 出错了) and an error banner that appears only when there is
something to say.

## Wiring

1. Create a `PanelSettings` asset if the project does not have one yet
   (Project window: Create > UI Toolkit > Panel Settings Asset).
2. Add a `PanelRenderer` to any GameObject, assign the `PanelSettings` asset
   to it (UIDocument is in maintenance mode in Unity 6.5+; the view sits on
   PanelRenderer).
3. Add `AgentViewBehaviour` to the same GameObject.
4. Assign `ChatUi.uxml` to the PanelRenderer's Visual Tree Asset slot and
   `ChatUi.uss` to the UXML root. Assigning nothing also works: the behaviour
   then builds the same tree in code, so the sample runs in a build with zero
   asset wiring.
5. Create and bind an agent, for example from a bootstrap script:

```csharp
AiAgent agent = AiRuntimeFactory.CreateDeepSeekAgent("smith", apiKey, "你是一个沉默的铁匠。");
GetComponent<AgentViewBehaviour>().Bind(agent);
```

Or bind a view model you built yourself, when the host manages its own agents:

```csharp
var viewModel = new AgentViewModel(agent, actorId: "smith");
GetComponent<AgentViewBehaviour>().Bind(viewModel);
```

## What is worth reading

- `AgentViewModel` (YourAI.Presentation) -- the whole reactive surface: Text,
  State, Error, LastTool, IsBusy, plus one change event each. It is engine-free
  and covered by the offline test harness (`presentation/*` cases).
- `AgentViewBehaviour` -- deliberately thin: pump in Update, apply on events.
  The element names `log`, `status`, `text`, `error`, `input`, `send` are the
  contract between this UXML and the behaviour.

Nothing here checks for null or catches exceptions. An agent with no key binds
and renders; every turn comes back faulted with the reason in the banner, which
is the framework's no-key stance carried all the way to the screen.
