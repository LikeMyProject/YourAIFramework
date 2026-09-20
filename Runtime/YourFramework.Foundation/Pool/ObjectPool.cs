using System;
using System.Collections.Generic;

namespace YourFramework.Pool
{
    /// <summary>
    /// 纯 C# 对象池：工厂创建、借还配平、空闲上限、借还钩子。
    ///
    /// 设计边界：
    /// - 不做优先级/多池过期淘汰那套——那是资源模块的职责，这里只做借还；
    /// - 借还配平错误（重复 Release、归还非本池对象）是程序 bug，直接抛
    ///   InvalidOperationException（fail-fast），不静默吞；
    /// - 借还钩子抛异常时，池子回滚到这次操作之前的状态：借出去的不会卡在账上，
    ///   还回来的不会两头不着。异常照旧抛给宿主，池子自己不留暗伤；
    /// - 零引擎依赖，引擎侧的预制体池见 UnityRuntime 的 GameObjectPool。
    ///
    /// **不要把这个池用在 UnityEngine.Object 上**：泛型里的 <c>T == null</c> 是引用比较，
    /// 绑不到 Unity 给 Object 重载的那个 <c>==</c>，被 Destroy 过的对象认不出来。
    /// 引擎对象请用 GameObjectPool。
    ///
    /// 线程：仅限主线程。
    /// </summary>
    public sealed class ObjectPool<T> where T : class
    {
        private readonly Func<T> _factory;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onRelease;
        private readonly int _maxIdle;
        private readonly Stack<T> _idle = new Stack<T>();
        private readonly HashSet<T> _active = new HashSet<T>();

        /// <summary>
        /// <param name="factory">Creates a fresh item. Must not return null.</param>
        /// <param name="onGet">Called after the item leaves the pool (rent begins).</param>
        /// <param name="onRelease">Called when the item returns, before it is pooled or dropped.</param>
        /// <param name="maxIdle">Idle items beyond this cap are discarded on release (onRelease still runs).</param>
        /// </summary>
        public ObjectPool(Func<T> factory, Action<T> onGet = null, Action<T> onRelease = null, int maxIdle = 128)
        {
            if (factory == null)
            {
                throw new ArgumentNullException("factory");
            }

            _factory = factory;
            _onGet = onGet;
            _onRelease = onRelease;
            _maxIdle = Math.Max(0, maxIdle);
        }

        /// <summary>Items waiting in the pool.</summary>
        public int CountIdle { get { return _idle.Count; } }

        /// <summary>Items currently rented out.</summary>
        public int CountActive { get { return _active.Count; } }

        /// <summary>The idle cap set at construction.</summary>
        public int MaxIdle { get { return _maxIdle; } }

        /// <summary>
        /// Rents an item: pops the idle stack or calls the factory. Factory nulls and
        /// foreign double-gets are contract violations and throw.
        /// </summary>
        public T Get()
        {
            T item = _idle.Count > 0 ? _idle.Pop() : _factory();
            if (item == null)
            {
                throw new InvalidOperationException(
                    "ObjectPool<" + typeof(T).Name + ">.Get: factory returned null.");
            }

            if (!_active.Add(item))
            {
                throw new InvalidOperationException(
                    "ObjectPool<" + typeof(T).Name + ">.Get: item is already active "
                    + "(it was rented without Release, or came from outside this pool).");
            }

            if (_onGet != null)
            {
                try
                {
                    _onGet(item);
                }
                catch
                {
                    // 调用方没拿到引用，谁也 Release 不了它 —— 留在 _active 里就是永久虚高。
                    // 退回空闲栈（受同一个上限约束），池子回到"这次 Get 没发生过"。
                    _active.Remove(item);
                    if (_idle.Count < _maxIdle)
                    {
                        _idle.Push(item);
                    }

                    throw;
                }
            }

            return item;
        }

        /// <summary>
        /// Returns an item. Runs onRelease, then pools it -- or drops it when the
        /// idle cap is full. Releasing an item that is not currently rented throws.
        /// </summary>
        public void Release(T item)
        {
            if (item == null)
            {
                return;
            }

            if (!_active.Remove(item))
            {
                throw new InvalidOperationException(
                    "ObjectPool<" + typeof(T).Name + ">.Release: item is not active in this pool "
                    + "(double release, or an item that never came from Get).");
            }

            if (_onRelease != null)
            {
                try
                {
                    _onRelease(item);
                }
                catch
                {
                    // 已经从 _active 摘掉了：不回滚的话它既不入池也不销毁，干净地漏掉。
                    // 放回"还拿着"的状态 —— 释放抛了异常，物归原主的语义就该是还没还。
                    _active.Add(item);
                    throw;
                }
            }

            if (_idle.Count < _maxIdle)
            {
                _idle.Push(item);
            }
        }

        /// <summary>
        /// True while the item is rented out. Cheap membership check for asserts and
        /// debug views; game logic should not branch on this.
        /// </summary>
        public bool IsActive(T item)
        {
            return item != null && _active.Contains(item);
        }

        /// <summary>
        /// Fills the idle stack to at least <paramref name="count"/> items by
        /// renting and immediately returning them, so onGet/onRelease hooks run.
        /// </summary>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Release(Get());
            }
        }

        /// <summary>
        /// Discards idle items, handing each to <paramref name="onDestroy"/> first
        /// (engine resources need an explicit destroy here). Rented items are not
        /// touched -- the renter still owns them.
        /// </summary>
        public void Clear(Action<T> onDestroy = null)
        {
            while (_idle.Count > 0)
            {
                T item = _idle.Pop();
                if (onDestroy != null)
                {
                    onDestroy(item);
                }
            }
        }
    }
}
