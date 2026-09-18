using System;
using System.Collections.Generic;

namespace YourFramework.Scripting
{
    /// <summary>
    /// 脚本被允许碰的能力。**一个开关对应一组确实能拿掉的全局名** ——
    /// 没有任何「声明了但没人执行」的旋钮：旋钮不落地比没有旋钮更坏，
    /// 因为它会让宿主以为自己已经关上了门。
    /// </summary>
    [Flags]
    public enum ScriptCapability
    {
        /// <summary>什么都不给（白名单从零开始）。</summary>
        None = 0,

        /// <summary>文件系统：<c>io</c> / <c>loadfile</c> / <c>dofile</c>。</summary>
        FileSystem = 1,

        /// <summary>操作系统：<c>os</c>（时钟、环境变量、进程）。</summary>
        OperatingSystem = 2,

        /// <summary>调试库：<c>debug</c>（能读写别的栈帧，等于绕过一切封装）。</summary>
        Debug = 4,

        /// <summary>动态代码：<c>load</c> / <c>loadstring</c>（补丁不该自己再编译字符串）。</summary>
        DynamicCode = 8,

        /// <summary>模块系统：<c>require</c> / <c>package</c> / <c>module</c>。</summary>
        Modules = 16
    }

    /// <summary>
    /// 脚本沙箱策略：补丁代码**能碰什么**。
    ///
    /// 为什么策略在内核、执行在适配层：策略是「哪些全局名该消失」这件可判定的事（纯数据，
    /// 离线可断言到每一个名字），而「怎么让一个名字消失」是各脚本引擎自己的事
    /// （Lua 是置 nil，别的引擎可能是从环境表里摘键）。分开之后，同一份策略能在
    /// 任何实现上生效，且策略本身不需要跑起一个解释器就能测。
    ///
    /// 默认姿态是**从零开始**（<see cref="CreateStrict"/>）：热更补丁是从网上来的代码，
    /// 它默认不该能读写文件、不该能起进程、不该能再编译字符串。要开哪一项，显式开。
    /// </summary>
    public sealed class ScriptSandboxPolicy
    {
        private readonly List<string> _extraDenied = new List<string>();
        private List<string> _denied = new List<string>();
        private bool _dirty = true;

        private ScriptSandboxPolicy(ScriptCapability allowed)
        {
            Allowed = allowed;
        }

        /// <summary>当前放行的能力集合。</summary>
        public ScriptCapability Allowed { get; private set; }

        /// <summary>额外点名禁掉的全局（策略覆盖不到的自定义注入，例如宿主塞进去的网络服务）。</summary>
        public IList<string> ExtraDeniedGlobals { get { return _extraDenied; } }

        /// <summary>
        /// 最严策略：一项能力都不放行。热更补丁的**默认起点**。
        /// 返回新实例，改它不会污染任何人。
        /// </summary>
        public static ScriptSandboxPolicy CreateStrict()
        {
            return new ScriptSandboxPolicy(ScriptCapability.None);
        }

        /// <summary>
        /// 最宽策略：全部放行（空表沙箱没有任何保护，只适合「本地调试」这类明确场景）。
        /// 返回新实例。
        /// </summary>
        public static ScriptSandboxPolicy CreatePermissive()
        {
            return new ScriptSandboxPolicy(
                ScriptCapability.FileSystem | ScriptCapability.OperatingSystem | ScriptCapability.Debug
                | ScriptCapability.DynamicCode | ScriptCapability.Modules);
        }

        /// <summary>放行一项能力。重复放行是幂等的。</summary>
        public void Allow(ScriptCapability capability)
        {
            ScriptCapability next = Allowed | capability;
            if (next != Allowed)
            {
                Allowed = next;
                _dirty = true;
            }
        }

        /// <summary>收回一项能力。重复收回是幂等的。</summary>
        public void Deny(ScriptCapability capability)
        {
            ScriptCapability next = Allowed & ~capability;
            if (next != Allowed)
            {
                Allowed = next;
                _dirty = true;
            }
        }

        /// <summary>是否放行了某一项。</summary>
        public bool IsAllowed(ScriptCapability capability)
        {
            return (Allowed & capability) == capability;
        }

        /// <summary>
        /// 显式点名禁掉一个全局名（比能力开关更细的那一档）。
        /// 空名字是程序 bug —— 它会在执行侧变成「什么都没禁」，抛。
        /// </summary>
        public void DenyGlobal(string globalName)
        {
            if (string.IsNullOrEmpty(globalName))
            {
                throw new ArgumentException(
                    "ScriptSandboxPolicy.DenyGlobal: globalName must not be empty "
                    + "(an empty name would deny nothing while looking like it denied something).",
                    "globalName");
            }

            if (_extraDenied.Contains(globalName))
            {
                return;
            }

            _extraDenied.Add(globalName);
            _dirty = true;
        }

        /// <summary>
        /// 本策略要摘掉的全局名：能力开关打出来的那一批 ∪ 点名禁掉的那一批，
        /// 去重后按 Ordinal 排序 —— **顺序确定**，所以它可以被逐字断言，也可以被写进诊断日志比对。
        /// </summary>
        public List<string> DeniedGlobals
        {
            get
            {
                if (_dirty)
                {
                    Rebuild();
                }

                return _denied;
            }
        }

        /// <summary>某个全局名是否在本策略下要消失。</summary>
        public bool IsGlobalDenied(string globalName)
        {
            if (string.IsNullOrEmpty(globalName))
            {
                return false;
            }

            if (_extraDenied.Contains(globalName))
            {
                return true;
            }

            List<string> denied = DeniedGlobals;
            for (int i = 0; i < denied.Count; i++)
            {
                if (string.Equals(denied[i], globalName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>诊断用一行（能力开关 + 禁用全局名单）。</summary>
        public override string ToString()
        {
            List<string> denied = DeniedGlobals;
            string capabilities = Allowed == ScriptCapability.None ? "none" : Allowed.ToString();
            return "sandbox[" + capabilities + "] deny(" + denied.Count + ")=["
                + string.Join(" ", denied.ToArray()) + "]";
        }

        private void Rebuild()
        {
            _denied.Clear();

            if (!IsAllowed(ScriptCapability.FileSystem))
            {
                Add("io");
                Add("loadfile");
                Add("dofile");
            }

            if (!IsAllowed(ScriptCapability.OperatingSystem))
            {
                Add("os");
            }

            if (!IsAllowed(ScriptCapability.Debug))
            {
                Add("debug");
            }

            if (!IsAllowed(ScriptCapability.DynamicCode))
            {
                Add("load");
                Add("loadstring");
            }

            if (!IsAllowed(ScriptCapability.Modules))
            {
                Add("require");
                Add("package");
                Add("module");
            }

            for (int i = 0; i < _extraDenied.Count; i++)
            {
                Add(_extraDenied[i]);
            }

            _denied.Sort(StringComparer.Ordinal);
            _dirty = false;
        }

        private void Add(string name)
        {
            if (!_denied.Contains(name))
            {
                _denied.Add(name);
            }
        }
    }
}
