using System;
using System.Collections.Generic;
using System.Text;
using YourFramework.Core;

namespace YourFramework.Diagnostics
{
    /// <summary>
    /// 诊断台：运行期 named counters/gauges + 最近错误环形缓冲 + 一键文本快照。
    /// 调试器 UI（IMGUI/UITK）与远程上报都只消费 <c>Snapshot()</c> 的产物。
    ///
    /// 与 AiDiagnosticsHost 的关系：宿主把 AiDiagnosticsHost.LogError 指到这里，
    /// ModuleCenter 的模块错误就有了统一落点（错误行进环形缓冲，计数器自增）。
    /// 纯 C#，离线可测；无锁设计 —— 诊断数据允许轻微失真，读取方无需阻塞生产方。
    /// </summary>
    public sealed class FrameworkDiagnostics : IModule
    {
        private readonly Dictionary<string, double> _values =
            new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly string[] _errorRing;
        private int _errorRingHead;
        private int _errorRingCount;
        private int _totalErrors;
        private bool _hooked;

        /// <summary>Builds the diagnostics with an error ring of the given size (default 64).</summary>
        public FrameworkDiagnostics(int errorRingSize = 64)
        {
            if (errorRingSize < 1)
            {
                throw new ArgumentException(
                    "FrameworkDiagnostics: error ring size must be >= 1.", "errorRingSize");
            }

            _errorRing = new string[errorRingSize];
        }

        /// <summary>Total errors seen since startup.</summary>
        public int TotalErrors { get { return _totalErrors; } }

        /// <summary>
        /// Hooks AiDiagnosticsHost.LogError so every module fault lands here.
        /// Called by Init; a previous hook is chained, not discarded.
        /// </summary>
        public void HookAiDiagnostics()
        {
            if (_hooked)
            {
                return;
            }

            _hooked = true;
            Action<string> previous = AiDiagnosticsHost.LogError;
            AiDiagnosticsHost.LogError = delegate(string line)
            {
                RecordError(line);
                Action<string> prior = previous;
                if (prior != null)
                {
                    prior(line);
                }
            };
        }

        /// <summary>Increments a named counter (missing counters start at 0).</summary>
        public void Inc(string name)
        {
            Inc(name, 1.0);
        }

        /// <summary>Increments a named counter by an amount.</summary>
        public void Inc(string name, double amount)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("FrameworkDiagnostics.Inc: name must not be empty.", "name");
            }

            double current;
            _values.TryGetValue(name, out current);
            _values[name] = current + amount;
        }

        /// <summary>Sets a named gauge to a value (overwrites).</summary>
        public void Set(string name, double value)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("FrameworkDiagnostics.Set: name must not be empty.", "name");
            }

            _values[name] = value;
        }

        /// <summary>Reads a value, or 0 when never set.</summary>
        public double GetValue(string name)
        {
            double value;
            return name != null && _values.TryGetValue(name, out value) ? value : 0.0;
        }

        /// <summary>Records an error line into the ring buffer and bumps counters.</summary>
        public void RecordError(string line)
        {
            _errorRing[_errorRingHead] = line ?? "(null error)";
            _errorRingHead = (_errorRingHead + 1) % _errorRing.Length;
            if (_errorRingCount < _errorRing.Length)
            {
                _errorRingCount++;
            }

            _totalErrors++;
            Inc("errors.total");
        }

        /// <summary>
        /// The newest errors, oldest first, up to the ring size. Returns via the
        /// caller's list (no per-call allocation on hot paths).
        /// </summary>
        public void CopyRecentErrors(List<string> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            into.Clear();
            int start = (_errorRingHead - _errorRingCount + _errorRing.Length) % _errorRing.Length;
            for (int i = 0; i < _errorRingCount; i++)
            {
                into.Add(_errorRing[(start + i) % _errorRing.Length]);
            }
        }

        /// <summary>Human-readable full snapshot (counters sorted by name, then errors).</summary>
        public string Snapshot()
        {
            StringBuilder text = new StringBuilder();
            text.Append("=== framework diagnostics ===\n");
            List<string> names = new List<string>(_values.Keys);
            names.Sort(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                text.Append(names[i]).Append(" = ").Append(_values[names[i]])
                    .Append('\n');
            }

            List<string> errors = new List<string>();
            CopyRecentErrors(errors);
            if (errors.Count > 0)
            {
                text.Append("-- recent errors (").Append(errors.Count).Append(") --\n");
                for (int i = 0; i < errors.Count; i++)
                {
                    text.Append(errors[i]).Append('\n');
                }
            }

            return text.ToString();
        }

        /// <summary>Module plumbing.</summary>
        public string Name { get { return "Diagnostics"; } }

        /// <summary>Module plumbing: earliest — everything reports into it.</summary>
        public int InitOrder { get { return -100; } }

        /// <summary>Module plumbing: hooks the diagnostics host.</summary>
        public void Init(ModuleCenter host)
        {
            HookAiDiagnostics();
        }

        /// <summary>Module plumbing.</summary>
        public void Pump()
        {
        }

        /// <summary>Module plumbing: unhooks the host so a rebuilt centre stays clean.</summary>
        public void Shutdown()
        {
            if (_hooked)
            {
                AiDiagnosticsHost.LogError = null;
                _hooked = false;
            }
        }
    }
}
