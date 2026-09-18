using System;
using System.Collections.Generic;

namespace YourFramework.Assets.Pack
{
    /// <summary>
    /// 本地缓存账本：谁在磁盘上、多大、有没有坏、被谁用着（引用计数）、多久没用过。
    ///
    /// 设计取舍：
    /// 1. 只做簿记，**不碰文件系统** —— 文件的删除/存在性检查由引擎侧后端负责，
    ///    所以本类纯 C#、可离线断言，逐出只产出"该删哪些"的名单；
    /// 2. 引用计数违反合约（对未登记项 Acquire、重复 Release）一律 fail-fast：
    ///    这是接线 bug，放过去就会变成越用越漏的隐性故障；
    /// 3. 校验失配不删账本项，而是标记"不可用 + 原因" —— 复用同一份账本表达
    ///    "要重下"（失败是值），UI 也就有原因可显示；
    /// 4. 逐出是冷路径（启动/清理时跑），用两遍扫描选最小 LastUsed，避免排序带来的
    ///    比较器分配；热路径（Acquire/Release/Touch）全是字典查 + 计数器，零分配。
    /// </summary>
    public sealed class CacheLedger
    {
        private sealed class Entry
        {
            public string Path;
            public long Size;
            public int Refs;
            public long LastUsed;
            public bool Usable = true;
            public string Reason;
        }

        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        private long _tick;
        private long _totalBytes;

        /// <summary>Tracked bundle count.</summary>
        public int Count { get { return _entries.Count; } }

        /// <summary>Total bytes the ledger believes are on disk.</summary>
        public long TotalBytes { get { return _totalBytes; } }

        /// <summary>
        /// Registers (or re-registers after a re-download) a cached bundle.
        /// Re-tracking keeps the current reference count: a re-download while the
        /// bundle is in use must not pretend the users went away.
        /// </summary>
        public void Track(string bundle, string path, long size)
        {
            if (string.IsNullOrEmpty(bundle))
            {
                throw new ArgumentException("CacheLedger.Track: bundle must not be empty.", "bundle");
            }

            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("CacheLedger.Track: path must not be empty.", "path");
            }

            if (size < 0)
            {
                throw new ArgumentException("CacheLedger.Track: size must not be negative.", "size");
            }

            Entry entry;
            if (_entries.TryGetValue(bundle, out entry))
            {
                _totalBytes -= entry.Size;
                entry.Path = path;
                entry.Size = size;
                entry.Usable = true;
                entry.Reason = null;
                entry.LastUsed = ++_tick;
                _totalBytes += size;
                return;
            }

            _entries.Add(bundle, new Entry
            {
                Path = path,
                Size = size,
                Refs = 0,
                LastUsed = ++_tick,
                Usable = true,
                Reason = null,
            });

            _totalBytes += size;
        }

        /// <summary>Whether the bundle is tracked (cached) at all.</summary>
        public bool IsTracked(string bundle)
        {
            return bundle != null && _entries.ContainsKey(bundle);
        }

        /// <summary>Local path of a tracked bundle.</summary>
        public bool TryGetPath(string bundle, out string path)
        {
            path = null;
            Entry entry;
            if (bundle == null || !_entries.TryGetValue(bundle, out entry))
            {
                return false;
            }

            path = entry.Path;
            return true;
        }

        /// <summary>Forgets a bundle (after eviction or a successful re-layout).</summary>
        public bool Untrack(string bundle)
        {
            Entry entry;
            if (bundle == null || !_entries.TryGetValue(bundle, out entry))
            {
                return false;
            }

            if (entry.Refs != 0)
            {
                throw new InvalidOperationException(
                    "CacheLedger.Untrack: bundle '" + bundle + "' is still referenced ("
                    + entry.Refs + "). Releasing before untracking is the caller's job.");
            }

            _entries.Remove(bundle);
            _totalBytes -= entry.Size;
            return true;
        }

        /// <summary>
        /// Takes a reference. Contract violation (untracked bundle) fails fast --
        /// silently no-op'ing here would let a caller use data that is not on disk.
        /// </summary>
        public void Acquire(string bundle)
        {
            Entry entry = Require(bundle, "Acquire");
            entry.Refs = entry.Refs + 1;
            entry.LastUsed = ++_tick;
        }

        /// <summary>Drops a reference. Releasing below zero fails fast (double release).</summary>
        public void Release(string bundle)
        {
            Entry entry = Require(bundle, "Release");
            if (entry.Refs <= 0)
            {
                throw new InvalidOperationException(
                    "CacheLedger.Release: bundle '" + bundle + "' has no outstanding reference.");
            }

            entry.Refs = entry.Refs - 1;
            entry.LastUsed = ++_tick;
        }

        /// <summary>Outstanding references (0 when cached but unused).</summary>
        public int RefCount(string bundle)
        {
            Entry entry;
            return bundle != null && _entries.TryGetValue(bundle, out entry) ? entry.Refs : 0;
        }

        /// <summary>
        /// Marks a tracked bundle unusable with a reason (CRC/size mismatch, read
        /// error). The entry stays so the caller can see why a re-download is due.
        /// </summary>
        public void MarkUnusable(string bundle, string reason)
        {
            Entry entry = Require(bundle, "MarkUnusable");
            entry.Usable = false;
            entry.Reason = string.IsNullOrEmpty(reason) ? "unspecified" : reason;
        }

        /// <summary>Whether the bundle is tracked and not marked unusable.</summary>
        public bool IsUsable(string bundle)
        {
            Entry entry;
            return bundle != null && _entries.TryGetValue(bundle, out entry) && entry.Usable;
        }

        /// <summary>Reason a bundle was marked unusable, or null.</summary>
        public string UnusableReason(string bundle)
        {
            Entry entry;
            return bundle != null && _entries.TryGetValue(bundle, out entry) ? entry.Reason : null;
        }

        /// <summary>Marks a bundle as recently used (LRU bookkeeping).</summary>
        public void Touch(string bundle)
        {
            Entry entry;
            if (bundle != null && _entries.TryGetValue(bundle, out entry))
            {
                entry.LastUsed = ++_tick;
            }
        }

        /// <summary>
        /// Evicts least-recently-used unused bundles until the ledger fits the
        /// budget. Referenced bundles are never evicted: if the budget cannot be
        /// met without touching them, the method stops and says how far it got.
        /// Fills <paramref name="into"/> with the evicted bundle names (the caller
        /// deletes the files); returns the number evicted.
        /// </summary>
        public int Evict(long capacityBytes, List<string> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            if (capacityBytes < 0)
            {
                throw new ArgumentException("CacheLedger.Evict: capacity must not be negative.", "capacityBytes");
            }

            into.Clear();
            while (_totalBytes > capacityBytes)
            {
                string victim = null;
                long oldest = long.MaxValue;

                foreach (KeyValuePair<string, Entry> pair in _entries)
                {
                    if (pair.Value.Refs != 0)
                    {
                        continue; // in use: not a candidate
                    }

                    if (pair.Value.LastUsed < oldest)
                    {
                        oldest = pair.Value.LastUsed;
                        victim = pair.Key;
                    }
                }

                if (victim == null)
                {
                    break; // everything left is referenced; the budget cannot be met
                }

                Untrack(victim);
                into.Add(victim);
            }

            return into.Count;
        }

        private Entry Require(string bundle, string operation)
        {
            if (string.IsNullOrEmpty(bundle))
            {
                throw new ArgumentException(
                    "CacheLedger." + operation + ": bundle must not be empty.", "bundle");
            }

            Entry entry;
            if (!_entries.TryGetValue(bundle, out entry))
            {
                throw new InvalidOperationException(
                    "CacheLedger." + operation + ": bundle '" + bundle
                    + "' is not tracked; Track() it before taking references.");
            }

            return entry;
        }
    }
}
