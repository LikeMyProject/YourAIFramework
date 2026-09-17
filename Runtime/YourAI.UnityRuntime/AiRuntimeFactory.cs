using UnityEngine;
using YourAI.Agent;
using YourAI.Providers;
using YourAI.Transport;

namespace YourAI.UnityRuntime
{
    /// <summary>
    /// The shortest path from "nothing" to "a character that can hold a conversation".
    ///
    /// This class exists because the honest answer to "how do I hook this up" should
    /// not be five paragraphs about seam placement. Everything it wires is still
    /// replaceable afterwards -- these are conveniences, not the only route:
    ///
    ///     var agent = AiRuntimeFactory.CreateDeepSeekAgent("smith", apiKey, "你是一个沉默的铁匠。");
    ///     agent.Context.Add(new DelegateContextProvider&lt;WorldState&gt;("scene", BuildSceneText));
    ///     var runtime = AiRuntimeFactory.Install(agent);
    ///
    /// Note what an empty API key does: nothing fails, and nothing throws. The provider
    /// reports itself unavailable, every turn comes back faulted with the reason, and
    /// the rest of the game runs. That is the framework's no-key stance expressed as
    /// three lines of code -- a project can integrate the AI layer, ship a build
    /// without credentials, and have the characters simply have less to say.
    /// </summary>
    public static class AiRuntimeFactory
    {
        /// <summary>
        /// A DeepSeek-backed agent, which is the reference target this framework is
        /// developed against.
        /// </summary>
        public static AiAgent CreateDeepSeekAgent(string name, string apiKey, string systemPrompt)
        {
            UnityHttpTransport transport = new UnityHttpTransport();
            OpenAiCompatibleProvider provider = OpenAiCompatibleProvider.DeepSeek(transport, apiKey);
            return Configure(new AiAgent(name, provider), systemPrompt);
        }

        /// <summary>
        /// An agent for any endpoint that speaks the OpenAI chat-completions shape --
        /// which is most of them, plus the local servers people run for testing.
        /// </summary>
        public static AiAgent CreateOpenAiCompatibleAgent(
            string name,
            string apiKey,
            string baseUrl,
            string chatPath,
            string model,
            string systemPrompt)
        {
            UnityHttpTransport transport = new UnityHttpTransport();

            OpenAiCompatibleOptions options = new OpenAiCompatibleOptions
            {
                ApiKey = apiKey,
                BaseUrl = baseUrl,
                ChatPath = chatPath,
                DefaultModel = model,
            };

            return Configure(new AiAgent(name, new OpenAiCompatibleProvider(name, transport, options)), systemPrompt);
        }

        /// <summary>
        /// Creates a runtime component for an agent, reusing the active one when there
        /// is already one in the scene.
        ///
        /// Must be called from the main thread: instantiating a GameObject is a Unity
        /// API like any other.
        /// </summary>
        public static AiRuntimeBehaviour Install(AiAgent agent, string hostName = "YourAI Runtime")
        {
            AiRuntimeBehaviour runtime = AiRuntimeBehaviour.Instance;
            if (runtime != null)
            {
                // Reconfiguring beats adding a second one, which would be disabled by
                // its own duplicate guard and leave the caller confused about why
                // nothing pumps.
                runtime.Setup(agent);
                return runtime;
            }

            GameObject host = new GameObject(string.IsNullOrEmpty(hostName) ? "YourAI Runtime" : hostName);
            runtime = host.AddComponent<AiRuntimeBehaviour>();
            runtime.Setup(agent);
            return runtime;
        }

        private static AiAgent Configure(AiAgent agent, string systemPrompt)
        {
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                agent.SystemPrompt = systemPrompt;
            }
            return agent;
        }
    }
}
