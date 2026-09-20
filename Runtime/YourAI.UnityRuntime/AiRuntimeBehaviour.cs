using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using YourAI.Agent;

namespace YourAI.UnityRuntime
{
    /// <summary>
    /// The frame loop the kernel refuses to own.
    ///
    /// Everything in Core is polled, never scheduled: no coroutine, no Awaitable, no
    /// MonoBehaviour, because Core cannot reference UnityEngine. Something has to
    /// actually call Pump, and this component is that something. It is the only place
    /// in the framework that knows what a frame is.
    ///
    /// Its whole job is three lines of logic -- pump every active turn, drop the ones
    /// that finished, tell anyone listening -- wrapped in enough care that a project
    /// never has to think about them. A host writes:
    ///
    ///     runtime.Ask("blacksmith", "how much for the sword?", worldState);
    ///
    /// and subscribes to TurnFinished. That is the intended shape of use.
    ///
    /// Why a duplicate-instance guard. Two runtimes would pump the same turn twice per
    /// frame. That is not merely wasteful: a turn advanced twice between frames can
    /// consume two SSE deliveries before its caller sees either, and the visible
    /// symptom is streamed text arriving in clumps rather than smoothly. The second
    /// instance disables itself and says so.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("YourAI/AI Runtime")]
    public sealed class AiRuntimeBehaviour : MonoBehaviour
    {
        /// <summary>The active runtime, or null when none is in the scene.</summary>
        public static AiRuntimeBehaviour Instance { get; private set; }

        /// <summary>
        /// The agent this runtime drives. Assign it from code via <see cref="Setup"/>
        /// or <see cref="AiRuntimeFactory.Install"/>.
        ///
        /// Deliberately a property, not a public field. AiAgent is a plain C# object
        /// -- not UnityEngine.Object and not [Serializable] -- so a public field here
        /// is skipped by Unity's serializer, which is exactly what UAC1001 warns
        /// about. The field never had an Inspector life: its home is code, and the
        /// property keeps that contract visible to both the serializer and the reader.
        /// </summary>
        public AiAgent PrimaryAgent { get; set; }

        [Tooltip("Keep this runtime alive across scene loads, so conversation state survives a level change.")]
        public bool PersistAcrossScenes = true;

        /// <summary>
        /// When a YourFramework AiModule owns the pump loop (registered in
        /// ModuleCenter), set this so Update stays idle -- otherwise every turn is
        /// advanced twice per frame, which shows up as streamed text in clumps.
        /// </summary>
        public bool ExternallyDriven = false;

        private readonly List<IAgentTurn> _active = new List<IAgentTurn>(4);
        private readonly List<IAgentTurn> _completed = new List<IAgentTurn>(4);

        /// <summary>Raised once per turn, after it has been removed from the active set.</summary>
        public event Action<IAgentTurn> TurnFinished;

        public int ActiveTurnCount
        {
            get { return _active.Count; }
        }

        /// <summary>Turns pumped since the component was enabled. Useful as a heartbeat.</summary>
        public long PumpCount { get; private set; }

        /// <summary>
        /// Clears the static instance at every play-mode start.
        ///
        /// 开着 "Enter Play Mode Options"（关闭域重载）时，静态字段会从上一次 Play 活到
        /// 下一次：这个字段仍指着上次那个已经销毁的实例。于是新场景里的 Awake 把自己当成
        /// "第二个运行时"，自己把自己 enabled = false —— 全场景再没有东西泵回合，
        /// AI 整个哑掉，症状是"我什么都没改，怎么第二次进去就不动了"。
        /// SubsystemRegistration 在每次进播放时都跑，关不关域重载都一样。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning(
                    "[YourAI] A second AiRuntimeBehaviour was found and disabled. "
                    + "Two runtimes would pump every turn twice per frame, which shows up "
                    + "as streamed text arriving in clumps.");
                enabled = false;
                return;
            }

            Instance = this;

            if (PersistAcrossScenes && transform.parent == null)
            {
                DontDestroyOnLoad(gameObject);
            }
            else if (PersistAcrossScenes)
            {
                // Unity 只留得住根物体。挂在 Systems 之类父节点下面是很常见的摆法，
                // 那种情况下这个勾是沉默失效的 —— 说一声，免得宿主以为"这功能没用"。
                Debug.LogWarning(
                    "[YourAI] PersistAcrossScenes is ticked on '" + name + "', but it is not a "
                    + "root object, so it will NOT survive a scene load. Unity only persists root "
                    + "GameObjects: unparent it, or untick the box to make the intent honest.");
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        private void Update()
        {
            if (!ExternallyDriven)
            {
                Pump();
            }
        }

        /// <summary>Attaches an agent after construction.</summary>
        public AiRuntimeBehaviour Setup(AiAgent agent)
        {
            PrimaryAgent = agent;
            return this;
        }

        /// <summary>
        /// Starts a turn and tracks it, so the runtime pumps it from Update and reports
        /// it through <see cref="TurnFinished"/>.
        ///
        /// Never returns null. With no agent assigned the returned turn is already
        /// faulted and carries the reason, so callers that ignore the return value and
        /// callers that inspect it both behave sensibly.
        /// </summary>
        public IAgentTurn Ask(string actorId, string userMessage, object worldState = null)
        {
            AiAgent agent = PrimaryAgent;
            if (agent == null)
            {
                return new FailedAgentTurn("no agent is assigned to the AI runtime");
            }

            IAgentTurn turn = agent.BeginTurn(actorId, userMessage, worldState);
            Track(turn);
            return turn;
        }

        /// <summary>Adds an externally created turn to the pumped set.</summary>
        public void Track(IAgentTurn turn)
        {
            if (turn == null)
            {
                return;
            }

            if (turn.IsDone)
            {
                // Already finished; report it rather than storing something that will
                // be removed on the next Pump anyway.
                RaiseFinished(turn);
                return;
            }

            if (_active.Contains(turn))
            {
                return;
            }

            _active.Add(turn);
        }

        public bool Untrack(IAgentTurn turn)
        {
            return _active.Remove(turn);
        }

        /// <summary>
        /// Cancels every in-flight turn. Call this when the scene that asked the
        /// questions is being torn down -- a turn that outlives its owner will write
        /// memory and raise events for an object that no longer exists.
        /// </summary>
        public void CancelAll()
        {
            for (int i = 0; i < _active.Count; i++)
            {
                _active[i].Cancel();
            }
            _active.Clear();
        }

        /// <summary>
        /// Advances every active turn once and returns how many are still running.
        /// Called from Update; callable by hand for hosts that drive their own loop.
        /// </summary>
        public int Pump()
        {
            if (_active.Count == 0)
            {
                return 0;
            }

            _completed.Clear();
            int stillRunning = 0;

            // Reverse iteration so removals during the sweep cannot skip an entry.
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                IAgentTurn turn = _active[i];

                bool alive = turn.Pump();

                if (!alive || turn.IsDone)
                {
                    _active.RemoveAt(i);
                    _completed.Add(turn);
                }
                else
                {
                    stillRunning++;
                }
            }

            PumpCount++;

            // Notify after the sweep, not during it. A handler that reacts by starting
            // another turn would otherwise mutate the list being walked.
            for (int i = 0; i < _completed.Count; i++)
            {
                RaiseFinished(_completed[i]);
            }
            _completed.Clear();

            return stillRunning;
        }

        /// <summary>
        /// Yields until a turn finishes, pumping it each frame.
        ///
        /// For a turn created through <see cref="Ask"/> this is redundant -- Update
        /// already pumps it -- but it is the natural way to write a sequence:
        ///
        ///     yield return runtime.WaitFor(runtime.Ask("guard", "who goes there?"));
        ///
        /// Pumping an already-finished turn is a no-op, so mixing the two styles is
        /// harmless.
        /// </summary>
        public IEnumerator WaitFor(IAgentTurn turn)
        {
            if (turn == null)
            {
                yield break;
            }

            while (!turn.IsDone)
            {
                turn.Pump();
                yield return null;
            }
        }

        /// <summary>
        /// The async counterpart of <see cref="WaitFor"/>, on Unity 6's Awaitable.
        ///
        /// Unlike <see cref="Ask"/> this does not track the turn, because this method
        /// pumps it itself; tracking as well would advance it twice per frame.
        /// </summary>
        public async Awaitable<AgentTurnResult> AskAsync(
            string actorId, string userMessage, object worldState = null)
        {
            AiAgent agent = PrimaryAgent;
            if (agent == null)
            {
                return new FailedAgentTurn("no agent is assigned to the AI runtime").Result;
            }

            IAgentTurn turn = agent.BeginTurn(actorId, userMessage, worldState);

            while (!turn.IsDone)
            {
                turn.Pump();
                await Awaitable.NextFrameAsync();
            }

            return turn.Result;
        }

        private void RaiseFinished(IAgentTurn turn)
        {
            Action<IAgentTurn> handler = TurnFinished;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(turn);
            }
            catch (Exception ex)
            {
                // One subscriber throwing must not stop the remaining turns in this
                // frame from being advanced, and must not leave the list half-cleared.
                Debug.LogException(ex, this);
            }
        }
    }
}
