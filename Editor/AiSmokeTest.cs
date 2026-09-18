using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using YourAI.Agent;
using YourAI.Core.Contracts;
using YourAI.Memory;
using YourAI.Presentation;

namespace YourAI.EditorTools
{
    /// <summary>
    /// The offline smoke test: six checks that run inside the real editor, on the
    /// real engine threading model, with no network and no scene setup.
    ///
    /// What this is for. DESIGN-ANALYSIS §7.1 says the engine-facing code has been
    /// compile-verified but never *run* in Unity, and that a real-machine session
    /// is the one remaining unknown. Most of that session can be automated: the
    /// checks below exercise the turn lifecycle, the parallel tool batch, the
    /// reactive view model, the team relay and the persistence layer against a
    /// scripted in-process provider, so what is left for the human is exactly the
    /// part that needs a human -- a live network call, UI Toolkit on screen, and
    /// domain reload behaviour. See SMOKE-TEST.md at the repository root for that
    /// remaining checklist.
    ///
    /// Run it from Window / YourAI / Smoke Test (Offline). Every check logs
    /// PASS or FAIL with the detail it promised; a summary dialog closes the run.
    /// A FAIL here is a real bug report, not a flake -- the checks are
    /// deterministic by construction (a scripted provider, no real I/O except one
    /// temp directory that cleans up after itself).
    /// </summary>
    public static class AiSmokeTest
    {
        // =========================================================== the runner

        [MenuItem("Window/YourAI/Smoke Test (Offline)")]
        public static void Run()
        {
            List<string> lines = new List<string>(8);
            int failures = 0;

            Run("turn/no-provider-degrades", CheckNoProviderDegrades, lines, ref failures);
            Run("turn/lifecycle-with-scripted-provider", CheckTurnLifecycle, lines, ref failures);
            Run("tools/parallel-batch-replays-in-call-order", CheckParallelTools, lines, ref failures);
            Run("viewmodel/change-events", CheckViewModel, lines, ref failures);
            Run("team/relay-two-roles", CheckTeamRelay, lines, ref failures);
            Run("memory/file-roundtrip", CheckMemoryRoundTrip, lines, ref failures);

            string summary = failures == 0
                ? "全部通过：" + lines.Count + " 项检查。剩余人工项见 SMOKE-TEST.md。"
                : "有 " + failures + " 项失败，明细见控制台。";
            foreach (string line in lines)
            {
                Debug.Log("[YourAI][Smoke] " + line);
            }
            EditorUtility.DisplayDialog("YourAI Smoke Test", summary, "好");
        }

        private static void Run(string name, Func<string> check, List<string> lines, ref int failures)
        {
            try
            {
                string detail = check();
                lines.Add("PASS " + name + " — " + detail);
            }
            catch (Exception ex)
            {
                failures++;
                lines.Add("FAIL " + name + " — " + ex.GetType().Name + ": " + ex.Message);
                Debug.LogException(ex);
            }
        }

        // ============================================================== stubs

        /// <summary>
        /// A provider whose streams are fully scripted: each StartStream pops the
        /// next (chunks, result) pair and replays it one delta per Pump. This is
        /// the same shape the offline harness scripts, moved into the editor so
        /// the engine path gets exercised without an endpoint.
        /// </summary>
        private sealed class SmokeProvider : ILlmProvider
        {
            private readonly Queue<LlmStreamResult> _results = new Queue<LlmStreamResult>();
            private readonly Queue<string[]> _chunks = new Queue<string[]>();

            public string Name { get { return "smoke"; } }

            public bool IsAvailable { get { return true; } }

            public string UnavailableReason { get { return string.Empty; } }

            public void Push(string[] chunks, LlmStreamResult result)
            {
                _chunks.Enqueue(chunks ?? new string[0]);
                _results.Enqueue(result ?? new LlmStreamResult { Success = true });
            }

            public void PushText(params string[] chunks)
            {
                string joined = string.Concat(chunks);
                Push(chunks, new LlmStreamResult { Success = true, Content = joined });
            }

            public ILlmStreamHandle StartStream(LlmRequest request)
            {
                return new SmokeHandle(_chunks.Count > 0 ? _chunks.Dequeue() : new string[0],
                    _results.Count > 0 ? _results.Dequeue() : new LlmStreamResult { Success = false, Error = "smoke script exhausted" });
            }
        }

        private sealed class SmokeHandle : ILlmStreamHandle
        {
            private readonly string[] _chunks;
            private readonly LlmStreamResult _result;
            private string _content = string.Empty;
            private int _step;
            private LlmStreamState _state = LlmStreamState.Running;

            public SmokeHandle(string[] chunks, LlmStreamResult result)
            {
                _chunks = chunks;
                _result = result;
            }

            public LlmStreamState State { get { return _state; } }

            public string Content { get { return _content; } }

            public string Reasoning { get { return string.Empty; } }

            public string Error { get { return _state == LlmStreamState.Faulted ? _result.Error : null; } }

            public event Action<string> ContentDelta;

            public event Action<string> ReasoningDelta;

            public event Action<LlmStreamResult> Completed;

            public bool Pump()
            {
                if (_state != LlmStreamState.Running)
                {
                    return false;
                }

                if (_step < _chunks.Length)
                {
                    string chunk = _chunks[_step];
                    _step++;
                    _content += chunk;
                    Action<string> delta = ContentDelta;
                    if (delta != null)
                    {
                        delta(chunk);
                    }
                }

                if (_step < _chunks.Length)
                {
                    return true;
                }

                LlmStreamResult result = _result;
                _state = result != null && result.Success ? LlmStreamState.Completed : LlmStreamState.Faulted;

                // Reasoning is never scripted by the smoke checks, but the path is
                // real: a result that carries reasoning text is raised here, which
                // is also what keeps this stub's ReasoningDelta event alive.
                if (result != null && !string.IsNullOrEmpty(result.Reasoning))
                {
                    Action<string> reasoning = ReasoningDelta;
                    if (reasoning != null)
                    {
                        reasoning(result.Reasoning);
                    }
                }

                Action<LlmStreamResult> completed = Completed;
                if (completed != null)
                {
                    completed(result);
                }
                return false;
            }

            public void Cancel()
            {
                if (_state == LlmStreamState.Running)
                {
                    _state = LlmStreamState.Cancelled;
                }
            }
        }

        private sealed class SmokeTool : ITool
        {
            public string Name { get { return _name; } }
            public string Description { get { return "smoke tool"; } }
            public string ParametersSchemaJson { get { return "{\"type\":\"object\"}"; } }
            public ToolSideEffect SideEffect { get { return ToolSideEffect.ReadOnly; } }

            private readonly string _name;

            public SmokeTool(string name) { _name = name; }

            public ToolResult Invoke(ToolInvocation invocation)
            {
                return ToolResult.Ok(_name + " ok");
            }
        }

        // ============================================================== checks

        /// <summary>No provider wired: the turn must fault with a reason, never throw.</summary>
        private static string CheckNoProviderDegrades()
        {
            AiAgent agent = new AiAgent("smoke", null);
            IAgentTurn turn = agent.BeginTurn("smoke", "在吗", null);

            while (turn.Pump()) { }

            if (turn.State != AgentTurnState.Faulted)
            {
                throw new Exception("expected Faulted, got " + turn.State);
            }
            if (turn.Result == null || string.IsNullOrEmpty(turn.Result.Error))
            {
                throw new Exception("the faulted turn carried no reason");
            }
            return "reason='" + turn.Result.Error + "'";
        }

        /// <summary>Scripted deltas stream through a real turn and land in the result.</summary>
        private static string CheckTurnLifecycle()
        {
            SmokeProvider provider = new SmokeProvider();
            provider.PushText("剑要", "三十两。");

            AiAgent agent = new AiAgent("smoke", provider);
            agent.SystemPrompt = "你是沉默的铁匠。";

            int deltas = 0;
            IAgentTurn turn = agent.BeginTurn("smoke", "多少钱", null);
            turn.Delta += delegate { deltas++; };

            while (turn.Pump()) { }

            if (turn.State != AgentTurnState.Completed || turn.Result == null || !turn.Result.Success)
            {
                throw new Exception("turn did not complete cleanly: " + turn.State);
            }
            if (turn.Text != "剑要三十两。")
            {
                throw new Exception("text was '" + turn.Text + "'");
            }
            if (deltas != 2)
            {
                throw new Exception("expected 2 deltas, got " + deltas);
            }
            return "text ok, deltas=" + deltas;
        }

        /// <summary>
        /// Two tool calls in one round run as a parallel batch and replay in call
        /// order even though the script finishes them out of order.
        /// </summary>
        private static string CheckParallelTools()
        {
            SmokeProvider provider = new SmokeProvider();
            LlmStreamResult calls = new LlmStreamResult
            {
                Success = true,
                Content = string.Empty,
                ToolCalls = new List<ToolInvocation>
                {
                    new ToolInvocation { ToolName = "weigh", CallId = "c1", ArgumentsJson = "{}" },
                    new ToolInvocation { ToolName = "look", CallId = "c2", ArgumentsJson = "{}" },
                },
            };
            provider.Push(null, calls);
            provider.PushText("都办好了。");

            AiAgent agent = new AiAgent("smoke", provider);
            agent.Tools = new ToolRegistry();
            agent.Tools.Register(new SmokeTool("look"));
            agent.Tools.Register(new SmokeTool("weigh"));
            agent.MaxToolRounds = 2;

            List<string> finishedOrder = new List<string>(2);
            IAgentTurn turn = agent.BeginTurn("smoke", "称一下看看", null);
            turn.ToolFinished += delegate(ToolInvocation call, ToolResult result)
            {
                finishedOrder.Add(call.ToolName);
            };

            while (turn.Pump()) { }

            if (turn.State != AgentTurnState.Completed || !turn.Result.Success)
            {
                throw new Exception("tool round did not complete: " + turn.State + " " + (turn.Result != null ? turn.Result.Error : ""));
            }
            if (turn.Result.ToolCallsMade != 2)
            {
                throw new Exception("expected 2 tool calls made, got " + turn.Result.ToolCallsMade);
            }
            if (turn.Result.ToolRounds != 1)
            {
                throw new Exception("expected 1 round, got " + turn.Result.ToolRounds);
            }
            if (turn.Text != "都办好了。")
            {
                throw new Exception("final text was '" + turn.Text + "'");
            }
            return "calls=2 rounds=1 final='" + turn.Text + "'";
        }

        /// <summary>
        /// The view model over a real turn: events fire on change, a quiet frame
        /// fires nothing, and the finished answer lands.
        /// </summary>
        private static string CheckViewModel()
        {
            SmokeProvider provider = new SmokeProvider();
            provider.PushText("三十两，", "不还价。");

            AiAgent agent = new AiAgent("smoke", provider);
            AgentViewModel vm = new AgentViewModel(agent);

            int textEvents = 0;
            int busyEvents = 0;
            vm.TextChanged += delegate { textEvents++; };
            vm.BusyChanged += delegate { busyEvents++; };

            if (!vm.Send("多少钱"))
            {
                throw new Exception("Send was refused");
            }
            if (!vm.IsBusy)
            {
                throw new Exception("a running turn did not open busy");
            }

            while (vm.Pump()) { }

            if (vm.Text != "三十两，不还价。")
            {
                throw new Exception("final text was '" + vm.Text + "'");
            }
            if (vm.IsBusy || vm.State != AgentTurnState.Completed)
            {
                throw new Exception("the turn did not settle: " + vm.State);
            }

            int quietBefore = textEvents;
            vm.Pump();
            if (textEvents != quietBefore)
            {
                throw new Exception("a quiet frame raised a text event");
            }
            return "textEvents=" + textEvents + " busyToggles=" + busyEvents;
        }

        /// <summary>Two roles in a relay: both speak, in registration order.</summary>
        private static string CheckTeamRelay()
        {
            SmokeProvider first = new SmokeProvider();
            first.PushText("生铁", "一块。");
            SmokeProvider second = new SmokeProvider();
            second.PushText("成交。");

            AgentTeam team = new AgentTeam("smoke-team");
            team.Register("smith", new AiAgent("smith", first));
            team.Register("merchant", new AiAgent("merchant", second));

            ITeamTurn relay = team.BeginRelayAll("smoke", "有什么", null);
            while (relay.Pump()) { }

            if (!relay.IsDone || relay.Steps.Count != 2)
            {
                throw new Exception("relay did not run both roles: steps=" + relay.Steps.Count);
            }
            if (relay.Text != "成交。")
            {
                throw new Exception("final text was '" + relay.Text + "'");
            }
            return "steps=2 final='" + relay.Text + "'";
        }

        /// <summary>One record survives a save and a reload through the real file layer.</summary>
        private static string CheckMemoryRoundTrip()
        {
            string dir = Path.Combine(Application.temporaryCachePath, "yourai-smoke-memory");
            string file = Path.Combine(dir, "smoke-memory.json");

            try
            {
                FileMemoryStore store = new FileMemoryStore(dir, "smoke-memory.json");
                store.Append(new MemoryRecord
                {
                    ActorId = "smoke",
                    Kind = MemoryKind.Episodic,
                    Text = "玩家买了一把长剑。",
                    Salience = 0.9f,
                });
                if (!store.Save())
                {
                    throw new Exception("Save returned false: " + store.LastError);
                }

                FileMemoryStore reloaded = new FileMemoryStore(dir, "smoke-memory.json");
                int loaded = reloaded.Load();
                if (loaded != 1 || reloaded.Count("smoke") != 1)
                {
                    throw new Exception("reloaded " + loaded + " records, expected 1");
                }
                return "records=1 file=" + file.Length + " chars on disk";
            }
            finally
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception)
                {
                    // The temp file is disposable; a failure to delete it must not
                    // turn a passing check into a failing one.
                }
            }
        }
    }
}
