using System;
using YourAI.Agent;
using YourAI.Presentation;
using YourFramework.Core;
using YourFramework.Event;

namespace YourAI.UnityRuntime
{
    /// <summary>
    /// AI 层的 ModuleCenter 适配模块：把七层 AI 栈挂进框架统一泵，并把 AI 的
    /// 一切动态转发到 EventBus。这是"AI 是框架的一个模块"这句话的落地物。
    ///
    /// 职责边界（刻意薄）：
    /// - Init：工厂建 Agent → Install 宿主 → 切外部驱动 → 接事件桥；
    /// - Pump：转调 AiRuntimeBehaviour.Pump()（Update 侧已用 ExternallyDriven 静音）；
    /// - Shutdown：CancelAll——回合不得活过宿主；
    /// - Ask：入口转发 + 给回合挂事件桥。
    ///
    /// 依赖经构造注入（EventBus / agent 工厂），组合根在引导代码里——不引 DI 容器
    /// （2026-09-17 拍板）。引擎侧薄壳，编译守卫把关；纯逻辑在事件桥与内核层。
    /// </summary>
    public sealed class AiModule : IModule
    {
        private readonly Func<AiAgent> _agentFactory;
        private readonly EventBus _bus;
        private AiRuntimeBehaviour _runtime;
        private AiTurnEventRelay _relay;

        /// <param name="agentFactory">Builds the agent (API key and persona live in bootstrap code, not here).</param>
        /// <param name="bus">The bus AI events are published to.</param>
        public AiModule(Func<AiAgent> agentFactory, EventBus bus)
        {
            if (agentFactory == null)
            {
                throw new ArgumentNullException("agentFactory");
            }

            _agentFactory = agentFactory;
            _bus = bus;
        }

        public string Name { get { return "ai"; } }

        /// <summary>Late on purpose: AI needs the event bus and (if used) save modules ready.</summary>
        public int InitOrder { get { return 100; } }

        public void Init(ModuleCenter host)
        {
            _runtime = AiRuntimeFactory.Install(_agentFactory());
            _runtime.ExternallyDriven = true;
            _relay = _bus != null ? new AiTurnEventRelay(_bus) : null;
            _runtime.TurnFinished += OnTurnFinished;
        }

        /// <summary>The installed runtime (for hosts that need Ask without going through this module).</summary>
        public AiRuntimeBehaviour Runtime { get { return _runtime; } }

        /// <summary>Starts a tracked turn and routes its stream/tool events onto the bus.</summary>
        public IAgentTurn Ask(string actorId, string userMessage, object worldState = null)
        {
            IAgentTurn turn = _runtime.Ask(actorId, userMessage, worldState);
            if (_relay != null)
            {
                _relay.Subscribe(turn);
            }

            return turn;
        }

        public void Pump()
        {
            _runtime.Pump();
        }

        public void Shutdown()
        {
            _runtime.CancelAll();
        }

        private void OnTurnFinished(IAgentTurn turn)
        {
            if (_relay != null)
            {
                _relay.PublishFinished(turn);
            }
        }
    }
}
