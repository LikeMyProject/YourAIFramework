# Minimal Chat sample

Planned shape of the first sample: one request, one streaming response, printed as
it arrives.

The pieces it waits on -- `YourAI.Transport` (UnityWebRequest wiring) and a
concrete provider in `YourAI.Providers` -- now exist, so wiring it up is
`AiRuntimeFactory.CreateDeepSeekAgent` plus a runtime component. What is not
shipped here is a scene file; the Chat UI sample under `Samples~/ChatUiSample`
is the fuller, window-based version.

The API it will exercise, for reference:

```csharp
// ai is never null. No null check, no try/catch, even with no key configured.
ILlmStreamHandle handle = ai.Chat(new LlmRequest
{
    Model = "deepseek-chat",
    Messages = { LlmMessage.User("Say hello in one short sentence.") },
});

handle.ContentDelta += text => Console.Write(text);

handle.Completed += result =>
{
    if (!result.Success)
    {
        // A value, not an exception.
        Console.WriteLine("failed: " + result.Error);
        return;
    }
    Console.WriteLine();
    Console.WriteLine("tokens " + result.PromptTokens + "/" + result.CompletionTokens
        + "  ttft " + result.FirstTokenMs.ToString("F0") + "ms");
};

// Driven by the host's frame loop. Inside a MonoBehaviour this is Update().
while (handle.Pump())
{
    // The kernel has no notion of frames; the shell supplies them.
}
```

Two details in that snippet are the whole point of the framework:

- Nothing checks for null and nothing catches. Unavailability is expressed through
  the handle, not through the absence of one.
- `Pump()` is called by the sample, not by the framework. The core never asks the
  engine when the next frame is, which is what keeps it compilable outside Unity.
