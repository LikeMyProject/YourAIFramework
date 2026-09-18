using System;
using System.Collections.Generic;
using YourFramework.Core;

namespace YourFramework.Flow
{
    /// <summary>
    /// One game-phase state: 启动 → 检查更新 → 登录 → 主菜单 → 战斗……
    ///
    /// Callback order guaranteed by <see cref="ProcedureFlow"/>:
    ///     OnEnter (once) → OnUpdate (per pump) → OnExit (once, before the next
    ///     procedure's OnEnter or on flow shutdown)
    ///
    /// Switching: call <c>Host.ChangeProcedure{T}()</c> from any callback. The
    /// switch is DEFERRED to the start of the next pump, so OnUpdate always
    /// finishes and there is no reentrancy. Requesting an unknown procedure
    /// throws immediately (programmer error, fail-fast).
    /// </summary>
    public abstract class Procedure
    {
        /// <summary>Unique within its flow. Convention: human-readable phase names.</summary>
        public abstract string Name { get; }

        /// <summary>The flow this procedure belongs to. Set at registration.</summary>
        protected IProcedureHost Host { get; private set; }

        internal void Bind(IProcedureHost host)
        {
            Host = host;
        }

        public virtual void OnEnter() { }

        public virtual void OnUpdate() { }

        public virtual void OnExit() { }
    }

    /// <summary>What a procedure can ask of its flow.</summary>
    public interface IProcedureHost
    {
        /// <summary>Schedules the switch; effective at the start of the next pump.</summary>
        void ChangeProcedure<TProcedure>() where TProcedure : Procedure;

        /// <summary>Schedules the switch by name; effective at the start of the next pump.</summary>
        void ChangeProcedure(string procedureName);

        /// <summary>Schedules a graceful stop: current procedure exits, flow ends.</summary>
        void Stop();
    }

    /// <summary>Lifecycle of the flow itself.</summary>
    public enum ProcedureFlowState
    {
        /// <summary>Registered but not started (before module Init).</summary>
        Idle = 0,

        /// <summary>Normal operation: pumps advance the current procedure.</summary>
        Running = 1,

        /// <summary>A procedure callback threw; the flow halted (error was reported).</summary>
        Faulted = 2,

        /// <summary>Graceful end (Stop or module Shutdown).</summary>
        Stopped = 3,
    }

    /// <summary>
    /// 流程状态机：游戏整体生命周期的骨架。
    ///
    /// 设计取舍：
    /// 1. 本身就是 <see cref="IModule"/> —— 插进 ModuleCenter 即被统一泵驱动，
    ///    不需要第二个驱动循环；
    /// 2. 切换延迟到下一泵首 —— 状态回调永远完整跑完，无重入；
    /// 3. 异常姿态遵循框架红线：过程回调抛错 = 运行期故障，流程标记
    ///    <see cref="ProcedureFlowState.Faulted"/> 停转并上报（其他模块继续跑），
    ///    而注册期错误（重复名/未知目标/缺起始流程）= 程序 bug，直接抛。
    /// </summary>
    public sealed class ProcedureFlow : IModule, IProcedureHost
    {
        private readonly string _name;
        private readonly int _initOrder;
        private readonly Dictionary<string, Procedure> _procedures =
            new Dictionary<string, Procedure>();

        private Procedure _current;
        private string _pendingName;
        private bool _pendingStop;
        private string _initialName;

        public ProcedureFlow(string name, int initOrder)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("ProcedureFlow: name must not be empty.", "name");
            }

            _name = name;
            _initOrder = initOrder;
        }

        /// <summary>Flow name (also the module name).</summary>
        public string Name { get { return _name; } }

        /// <summary>Module init order. Give the flow a LARGER InitOrder than modules it needs in procedures.</summary>
        public int InitOrder { get { return _initOrder; } }

        public ProcedureFlowState State { get; private set; }

        /// <summary>Name of the currently active procedure, or null.</summary>
        public string CurrentProcedureName { get { return _current == null ? null : _current.Name; } }

        /// <summary>
        /// Registers a procedure. Setup phase only (before module Init). Binding the
        /// host here is deliberate: a procedure cannot change flow before the flow
        /// exists. Duplicate names throw -- same rule as ModuleCenter.
        /// </summary>
        public ProcedureFlow Add<TProcedure>(TProcedure procedure)
            where TProcedure : Procedure
        {
            if (procedure == null)
            {
                throw new ArgumentNullException("procedure");
            }

            if (State != ProcedureFlowState.Idle)
            {
                throw new InvalidOperationException(
                    "ProcedureFlow.Add: procedures must be registered before the flow starts.");
            }

            if (_procedures.ContainsKey(procedure.Name))
            {
                throw new InvalidOperationException(
                    "ProcedureFlow.Add: procedure name '" + procedure.Name + "' is already used.");
            }

            procedure.Bind(this);
            _procedures.Add(procedure.Name, procedure);
            return this;
        }

        /// <summary>Sets the entry procedure. Required before module Init.</summary>
        public ProcedureFlow Start<TProcedure>() where TProcedure : Procedure
        {
            return Start(typeof(TProcedure).Name);
        }

        /// <summary>Sets the entry procedure by name.</summary>
        public ProcedureFlow Start(string procedureName)
        {
            if (!_procedures.ContainsKey(procedureName))
            {
                throw new InvalidOperationException(
                    "ProcedureFlow.Start: no procedure named '" + procedureName + "' is registered.");
            }

            _initialName = procedureName;
            return this;
        }

        // -------------------------------------------------- IModule (the engine side)

        public void Init(ModuleCenter host)
        {
            if (_initialName == null)
            {
                throw new InvalidOperationException(
                    "ProcedureFlow.Init: no start procedure set -- call Start<T>() during setup.");
            }

            State = ProcedureFlowState.Running;
            Enter(_procedures[_initialName]);
        }

        public void Pump()
        {
            if (State != ProcedureFlowState.Running)
            {
                return;
            }

            if (_pendingStop)
            {
                _pendingStop = false;
                ExitCurrent();
                if (State == ProcedureFlowState.Running)
                {
                    State = ProcedureFlowState.Stopped;
                }

                return;
            }

            if (_pendingName != null)
            {
                string next = _pendingName;
                _pendingName = null;
                ExitCurrent();
                if (State == ProcedureFlowState.Running)
                {
                    Enter(_procedures[next]);
                }

                if (State != ProcedureFlowState.Running)
                {
                    return;
                }
            }

            try
            {
                _current.OnUpdate();
            }
            catch (Exception ex)
            {
                Fault("OnUpdate", ex);
            }
        }

        public void Shutdown()
        {
            if (State == ProcedureFlowState.Running)
            {
                ExitCurrent();
                if (State == ProcedureFlowState.Running)
                {
                    State = ProcedureFlowState.Stopped;
                }
            }
        }

        // --------------------------------------------- IProcedureHost (the API side)

        public void ChangeProcedure<TProcedure>() where TProcedure : Procedure
        {
            ChangeProcedure(typeof(TProcedure).Name);
        }

        public void ChangeProcedure(string procedureName)
        {
            if (!_procedures.ContainsKey(procedureName))
            {
                throw new InvalidOperationException(
                    "ProcedureFlow.ChangeProcedure: no procedure named '"
                    + procedureName + "' is registered.");
            }

            _pendingName = procedureName;
        }

        public void Stop()
        {
            _pendingStop = true;
        }

        // -------------------------------------------------------------------- core

        private void Enter(Procedure procedure)
        {
            _current = procedure;
            try
            {
                procedure.OnEnter();
            }
            catch (Exception ex)
            {
                Fault("OnEnter", ex);
            }
        }

        private void ExitCurrent()
        {
            Procedure leaving = _current;
            _current = null;
            try
            {
                leaving.OnExit();
            }
            catch (Exception ex)
            {
                Fault("OnExit", ex, leaving.Name);
            }
        }

        private void Fault(string callback, Exception ex, string procedureName = null)
        {
            State = ProcedureFlowState.Faulted;
            _pendingName = null;
            _pendingStop = false;
            string line = "[YourFramework] procedure flow '" + _name + "' faulted in "
                + callback + " of '" + (procedureName ?? CurrentProcedureName ?? "?")
                + "': " + ex.GetType().Name + ": " + ex.Message;
            YourFramework.Core.AiDiagnosticsHost.LogError(line);
        }
    }
}
