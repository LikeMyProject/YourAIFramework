using System;
using YourAI.Agent;
using YourAI.Core.Contracts;
using YourFramework.Event;

namespace YourAI.Presentation
{
    /// <summary>
    /// AI 回合 → 框架事件总线的桥。有了它，游戏代码订阅 EventBus 即可看到 AI 的
    /// 一切动态（增量文本/工具调用/回合终结），AI 层与游戏逻辑彻底解耦——
    /// 这是 AI 层作为 ModuleCenter 模块存在的关键一环。
    ///
    /// 纯 C#：与具体引擎无关，用离线断言覆盖（aimodule/* 用例组）。
    /// 线程：回调来自泵线程；宿主在主线程泵即全部在主线程。
    /// 每回合闭包各捕各的回合实例，多回合并发时负载里永远带对了 Turn。
    /// </summary>
    public sealed class AiTurnEventRelay
    {
        /// <summary>增量文本到达。Text 为该次增量的新增部分。</summary>
        public sealed class AiDelta
        {
            public IAgentTurn Turn;
            public string Text;
        }

        /// <summary>一次工具调用完成（成功或失败都算完成，结果在 Result 里）。</summary>
        public sealed class AiToolFinished
        {
            public IAgentTurn Turn;
            public ToolInvocation Invocation;
            public ToolResult Result;
        }

        /// <summary>回合终结（Completed / Faulted / Cancelled 均可能）。</summary>
        public sealed class AiTurnFinished
        {
            public IAgentTurn Turn;
        }

        private readonly EventBus _bus;

        public AiTurnEventRelay(EventBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException("bus");
            }

            _bus = bus;
        }

        /// <summary>
        /// Subscribes a running turn's Delta and ToolFinished events onto the bus,
        /// each payload carrying its turn. Call right after the turn starts
        /// (AiModule.Ask does it for you). Unsubscribing is unnecessary: a finished
        /// turn fires nothing further, and the turn is discarded with its closures.
        /// </summary>
        public void Subscribe(IAgentTurn turn)
        {
            if (turn == null)
            {
                throw new ArgumentNullException("turn");
            }

            IAgentTurn captured = turn;
            turn.Delta += text => _bus.Publish(new AiDelta { Turn = captured, Text = text });
            turn.ToolFinished += (invocation, result) => _bus.Publish(
                new AiToolFinished { Turn = captured, Invocation = invocation, Result = result });
        }

        /// <summary>
        /// Publishes the terminal event. Call when the pump owner observes
        /// IsDone (AiRuntimeBehaviour.TurnFinished → AiModule does it for you);
        /// the relay cannot know completion on its own without polling, and
        /// polling is the pump owner's job.
        /// </summary>
        public void PublishFinished(IAgentTurn turn)
        {
            if (turn == null)
            {
                throw new ArgumentNullException("turn");
            }

            _bus.Publish(new AiTurnFinished { Turn = turn });
        }
    }
}
