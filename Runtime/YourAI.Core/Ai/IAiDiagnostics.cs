using System;

namespace YourAI.Core.Ai
{
    /// <summary>
    /// Where the core layer reports what it is doing.
    ///
    /// Core cannot call Debug.Log because it is forbidden from referencing
    /// UnityEngine. Rather than let that force silence, the core takes a sink and
    /// the Unity layer supplies a logging one. The default sink writes to the
    /// console, so a misconfigured AI module is visible even if nobody wired
    /// anything up -- degradation that happens quietly is worse than no AI at all,
    /// because the symptom shows up far away from the cause.
    /// </summary>
    public interface IAiDiagnostics
    {
        void Info(string message);
        void Warn(string message);
    }

    /// <summary>Writes to the console. Visible in the Unity editor console.</summary>
    public sealed class ConsoleDiagnostics : IAiDiagnostics
    {
        public static readonly ConsoleDiagnostics Instance = new ConsoleDiagnostics();

        public void Info(string message)
        {
            Console.WriteLine("[YourAI] " + message);
        }

        public void Warn(string message)
        {
            Console.Error.WriteLine("[YourAI] " + message);
        }
    }

    /// <summary>Drops everything. Useful in tests where warnings are expected noise.</summary>
    public sealed class SilentDiagnostics : IAiDiagnostics
    {
        public static readonly SilentDiagnostics Instance = new SilentDiagnostics();

        public void Info(string message) { }
        public void Warn(string message) { }
    }

    /// <summary>Routes to caller-supplied delegates, e.g. Debug.LogWarning.</summary>
    public sealed class DelegateDiagnostics : IAiDiagnostics
    {
        private readonly Action<string> _info;
        private readonly Action<string> _warn;

        public DelegateDiagnostics(Action<string> info, Action<string> warn)
        {
            _info = info;
            _warn = warn;
        }

        public void Info(string message)
        {
            if (_info != null) { _info(message); }
        }

        public void Warn(string message)
        {
            if (_warn != null) { _warn(message); }
        }
    }
}
