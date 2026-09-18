using System;
using System.Collections.Generic;

namespace YourFramework.Event
{
    /// <summary>
    /// 类型键控的发布/订阅总线。两种派发模式，按事件频率与语义选择：
    ///
    /// - <see cref="Publish{T}"/>(evt)：同步、按订阅顺序、引用与值类型事件均零装箱。
    ///   适合帧内必须立刻看到的游戏逻辑事件。
    /// - <see cref="Post{T}"/>(evt) + <see cref="Pump"/>()：先入队，宿主每帧泵一次。
    ///   适合“下一帧再说”的跨模块通知（值类型入队时装箱一次，处理器经
    ///   DynamicInvoke 调用——高频事件请走 Publish，这是刻意的取舍）。
    ///
    /// 派发期修改规则（与 UI Toolkit 一致，避免快照拷贝的每帧分配）：
    /// - 派发中 Unsubscribe：槽位立即置空，后续订阅者不再收到，派发完惰性压实；
    /// - 派发中 Subscribe：允许入列，但本轮派发不调用它，下一次才生效；
    /// - 派发中 Publish（嵌套/递归）：合法，内层按当时订阅表派发。
    ///
    /// Post during Pump 落入新队列、本轮不排空（一次 Pump = 一帧，与 AI 层同一条
    /// 语义）。线程：仅限主线程。不派发给 null 处理器；Subscribe(null) 忽略。
    /// </summary>
    public sealed class EventBus
    {
        /// <summary>Convenience bus for small projects. Testable code should still prefer injecting its own instance.</summary>
        public static readonly EventBus Global = new EventBus();

        private readonly Dictionary<Type, List<Delegate>> _subscribers =
            new Dictionary<Type, List<Delegate>>();

        private readonly HashSet<List<Delegate>> _dirtyLists = new HashSet<List<Delegate>>();

        // Double-buffered queue: Pump swaps it with _drain, so events Posted during
        // the drain land in a fresh queue and are dispatched on the next pump.
        private List<KeyValuePair<Type, object>> _queue =
            new List<KeyValuePair<Type, object>>();
        private List<KeyValuePair<Type, object>> _drain =
            new List<KeyValuePair<Type, object>>();

        // The one subscriber list currently being dispatched (null when idle). Nested
        // publishes save/restore it, which is why a single field suffices.
        private List<Delegate> _iterating;

        /// <summary>Subscribes a handler. Order of delivery: subscription order.</summary>
        public void Subscribe<T>(Action<T> handler)
        {
            if (handler == null)
            {
                return;
            }

            GetList(typeof(T), true).Add(handler);
        }

        /// <summary>
        /// Unsubscribes a handler. Safe during dispatch: the handler being removed
        /// finishes its current invocation, handlers after it are skipped, and list
        /// compaction happens once the outermost dispatch ends. Returns whether
        /// anything was removed.
        ///
        /// Matching uses delegate equality (same method + same target), so a method
        /// group subscription can be cancelled with the same method group --
        /// <c>bus.Subscribe(h.OnTick); bus.Unsubscribe(h.OnTick);</c> works even
        /// though each conversion creates a new delegate instance. Distinct lambdas
        /// never match each other; keep the reference if you need to be exact.
        /// </summary>
        public bool Unsubscribe<T>(Action<T> handler)
        {
            if (handler == null)
            {
                return false;
            }

            List<Delegate> list = GetList(typeof(T), false);
            if (list == null)
            {
                return false;
            }

            if (ReferenceEquals(_iterating, list))
            {
                // Do not mutate indices mid-iteration: null the slots instead.
                bool found = false;
                for (int i = 0; i < list.Count; i++)
                {
                    Delegate slot = list[i];
                    if (slot != null && slot.Equals(handler))
                    {
                        list[i] = null;
                        found = true;
                        _dirtyLists.Add(list);
                    }
                }

                return found;
            }

            return list.Remove(handler);
        }

        /// <summary>
        /// Synchronous dispatch, subscription order. Boxing-free even for struct
        /// events. Handlers added during this dispatch are not called until the next
        /// one; handlers removed during it are skipped.
        /// </summary>
        public void Publish<T>(T evt)
        {
            List<Delegate> list = GetList(typeof(T), false);
            if (list == null || list.Count == 0)
            {
                return;
            }

            List<Delegate> outer = _iterating;
            _iterating = list;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                Delegate handler = list[i];
                if (handler == null)
                {
                    continue;
                }

                ((Action<T>)handler)(evt);
            }

            _iterating = outer;
            if (outer == null)
            {
                CompactDirty();
            }
        }

        /// <summary>Queues an event for the next <see cref="Pump"/>. Returns the queue length after enqueue.</summary>
        public int Post<T>(T evt)
        {
            _queue.Add(new KeyValuePair<Type, object>(typeof(T), evt));
            return _queue.Count;
        }

        /// <summary>
        /// Drains the queue in FIFO order. Events Posted during the drain (including
        /// from handlers) stay queued for the NEXT pump: one Pump is one frame, and
        /// a handler that unconditionally Posts cannot recurse the pump. Returns how
        /// many events were dispatched.
        /// </summary>
        public int Pump()
        {
            if (_queue.Count == 0)
            {
                return 0;
            }

            List<KeyValuePair<Type, object>> drain = _queue;
            _queue = _drain;
            _drain = drain;

            int count = drain.Count;
            for (int i = 0; i < count; i++)
            {
                DispatchBoxed(drain[i].Key, drain[i].Value);
            }

            drain.Clear();
            return count;
        }

        /// <summary>Events waiting for the next pump.</summary>
        public int QueuedCount { get { return _queue.Count; } }

        /// <summary>Current subscriber count for T.</summary>
        public int SubscriberCount<T>()
        {
            List<Delegate> list = GetList(typeof(T), false);
            return list == null ? 0 : list.Count;
        }

        /// <summary>
        /// Drops every subscription and queued event. Not callable during dispatch
        /// (throwing there beats leaving the iterator pointing into a cleared table).
        /// </summary>
        public void Clear()
        {
            if (_iterating != null)
            {
                throw new InvalidOperationException("EventBus.Clear: not valid while dispatching.");
            }

            _subscribers.Clear();
            _dirtyLists.Clear();
            _queue.Clear();
            _drain.Clear();
        }

        // ------------------------------------------------------------------ core

        private void DispatchBoxed(Type type, object evt)
        {
            List<Delegate> list = GetList(type, false);
            if (list == null || list.Count == 0)
            {
                return;
            }

            List<Delegate> outer = _iterating;
            _iterating = list;
            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                Delegate handler = list[i];
                if (handler == null)
                {
                    continue;
                }

                // Deliberate tradeoff, documented on the class: the deferred path
                // pays a DynamicInvoke per handler so that Publish<T> can stay
                // allocation-free. High-frequency events belong on Publish.
                handler.DynamicInvoke(evt);
            }

            _iterating = outer;
            if (outer == null)
            {
                CompactDirty();
            }
        }

        private List<Delegate> GetList(Type type, bool createIfMissing)
        {
            List<Delegate> list;
            if (_subscribers.TryGetValue(type, out list))
            {
                return list;
            }

            if (!createIfMissing)
            {
                return null;
            }

            list = new List<Delegate>();
            _subscribers.Add(type, list);
            return list;
        }

        private void CompactDirty()
        {
            if (_dirtyLists.Count == 0)
            {
                return;
            }

            foreach (List<Delegate> list in _dirtyLists)
            {
                list.RemoveAll(IsNullSlot);
            }

            _dirtyLists.Clear();
        }

        private static readonly Predicate<Delegate> IsNullSlot = delegate(Delegate d) { return d == null; };
    }
}
