using System;
using System.Collections.Generic;
using System.Reflection;

namespace YourFramework.HotUpdate
{
    /// <summary>
    /// 热更程序集里的入口点：程序集装载完成后，框架按 <see cref="Order"/> 升序逐个调用。
    ///
    /// 为什么用接口而不是特性（attribute）：特性与接口在同一次热更装载里都存在**类型标识**
    /// 问题，但接口是编译期强约束 —— 漏写方法编译不过，写错签名编译不过，而特性写错只是
    /// 运行期静默不生效。热更这种"错了就上线"的场景，编译期能抓住的绝不留到运行期。
    ///
    /// 约定：入口类型必须有无参构造（构造里别做重活 —— 框架每次扫描会构造一次，真正
    /// 的初始化写在 <see cref="OnHotfixLoaded"/> 里）。
    /// </summary>
    public interface IHotfixEntry
    {
        /// <summary>执行顺序，小的先。同序按类型全名 Ordinal 排序，保证确定性。</summary>
        int Order { get; }

        /// <summary>
        /// 程序集全部装载完成后调用一次。抛异常 = 该入口失败（记进失败清单，不影响其它
        /// 入口 —— 一个入口坏了不该让整批热更静默不发生，把全部失败一次性报给调用方才有用）。
        /// </summary>
        void OnHotfixLoaded();
    }

    /// <summary>
    /// 热更入口点的扫描与执行：程序集 → 入口实例列表 → 确定性排序 → 逐个调用。
    ///
    /// 失败姿态：
    /// - 扫描期的问题（assembly 为 null、GetTypes 被类型加载异常打断、入口类型没有无参构造）
    ///   记进 problems 而不是抛异常 —— 调用方要的是"哪些没生效"的完整清单；
    /// - 执行期的异常同样记进 failures，**不截停**：全部入口都会被执行，失败的逐条列出。
    ///
    /// 单线程使用（泵、主线程）。
    /// </summary>
    public sealed class HotfixEntryRunner
    {
        private readonly List<IHotfixEntry> _entries = new List<IHotfixEntry>();
        private readonly List<Type> _types = new List<Type>();

        /// <summary>收集到的入口类型，与执行顺序一致。</summary>
        public List<Type> Types { get { return _types; } }

        /// <summary>收集到的入口数量。</summary>
        public int Count { get { return _entries.Count; } }

        /// <summary>清空（一次热更装载开始时调用）。</summary>
        public void Clear()
        {
            _entries.Clear();
            _types.Clear();
        }

        /// <summary>
        /// 扫描一个程序集里所有类型（含非 public），把实现 <see cref="IHotfixEntry"/> 的收进来。
        /// 问题写进 <paramref name="problems"/>（可为 null = 不关心）。
        /// </summary>
        public void Collect(Assembly assembly, List<string> problems)
        {
            if (assembly == null)
            {
                Add(problems, "assembly is null");
                return;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 类型加载异常不该让整次扫描失败：能拿到的类型照常扫，拿不到的如实报告。
                Add(problems, "assembly '" + assembly.GetName().Name + "' partially loaded: "
                    + ex.GetType().Name + ": " + ex.Message);
                types = ex.Types;
            }
            catch (Exception ex)
            {
                Add(problems, "assembly '" + assembly.GetName().Name + "' types could not be read: "
                    + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (types == null)
            {
                return;
            }

            for (int i = 0; i < types.Length; i++)
            {
                CollectType(types[i], problems);
            }
        }

        /// <summary>按 Order 升序、同序按全名 Ordinal 排序（结果确定，不受 GetTypes 顺序影响）。</summary>
        public void Sort()
        {
            _entries.Sort(CompareEntries);
            _types.Clear();
            for (int i = 0; i < _entries.Count; i++)
            {
                _types.Add(_entries[i].GetType());
            }
        }

        /// <summary>
        /// 按当前顺序调用全部入口。返回"尝试执行的入口数"；失败逐条写进
        /// <paramref name="failures"/>（不为 null 时）。一个入口抛异常不影响后面的。
        /// </summary>
        public int RunAll(List<string> failures)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                IHotfixEntry entry = _entries[i];
                try
                {
                    entry.OnHotfixLoaded();
                }
                catch (Exception ex)
                {
                    Add(failures, entry.GetType().FullName + " failed: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return _entries.Count;
        }

        private void CollectType(Type type, List<string> problems)
        {
            if (type == null || type.IsAbstract || type.IsInterface)
            {
                return;
            }

            // 开放泛型没法实例化（而且热更入口本来就该是具体类型）——静默跳过，
            // 不是问题，因为没有任何合法的热更入口会写成开放泛型。
            if (type.ContainsGenericParameters)
            {
                return;
            }

            if (!typeof(IHotfixEntry).IsAssignableFrom(type) || _types.Contains(type))
            {
                return;
            }

            IHotfixEntry instance;
            try
            {
                instance = Activator.CreateInstance(type) as IHotfixEntry;
            }
            catch (Exception ex)
            {
                Add(problems, "hotfix entry '" + type.FullName + "' could not be constructed: "
                    + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (instance == null)
            {
                Add(problems, "hotfix entry '" + type.FullName + "' has no usable parameterless constructor.");
                return;
            }

            _entries.Add(instance);
            _types.Add(type);
        }

        private static int CompareEntries(IHotfixEntry a, IHotfixEntry b)
        {
            int byOrder = a.Order.CompareTo(b.Order);
            if (byOrder != 0)
            {
                return byOrder;
            }

            return string.CompareOrdinal(a.GetType().FullName, b.GetType().FullName);
        }

        private static void Add(List<string> list, string message)
        {
            if (list != null)
            {
                list.Add(message);
            }
        }
    }
}
