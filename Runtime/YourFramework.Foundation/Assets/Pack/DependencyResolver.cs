using System.Collections.Generic;

namespace YourFramework.Assets.Pack
{
    /// <summary>
    /// 依赖闭包解析：地址 → 该资源所需的全部 bundle，按**拓扑序**（被依赖者在前、
    /// 目标 bundle 在最后）填进调用方给的列表。
    ///
    /// 设计取舍：
    /// 1. 填调用方列表、内部暂存复用（红线的检索类接口姿势），解析本身零分配；
    /// 2. 用显式栈做后序 DFS，不用递归 —— 依赖链深度由内容决定，递归深度不该由
    ///    美术的目录结构决定；
    /// 3. 成环不在本类处理：清单解析时已 fail-fast 拒绝（构建期 bug），本类只需
    ///    假定清单合法，因此不需要环检测的运行时开销，也不可能死循环（visited
    ///    在入栈时标记）；
    /// 4. 单线程使用（泵、主线程）；跨线程共享需要外部加锁。
    /// </summary>
    public sealed class DependencyResolver
    {
        private readonly HashSet<string> _visited = new HashSet<string>(System.StringComparer.Ordinal);
        private readonly List<ManifestBundle> _stackBundles = new List<ManifestBundle>();

        // Parallel to _stackBundles: how many of the frame's dependencies have
        // been walked. Two parallel lists instead of a frame struct keeps the
        // per-frame bookkeeping allocation-free after the first call.
        private readonly List<int> _stackDepIndex = new List<int>();

        /// <summary>
        /// Resolves the load closure of an address. Returns false (and leaves
        /// <paramref name="into"/> empty) when the address is unknown -- the
        /// caller owns the failure wording, this class never throws for content.
        /// </summary>
        public bool TryResolve(PackageManifest manifest, string location, List<string> into)
        {
            if (into == null)
            {
                throw new System.ArgumentNullException("into");
            }

            into.Clear();
            if (manifest == null || location == null)
            {
                return false;
            }

            ManifestAsset asset;
            if (!manifest.TryGetAsset(location, out asset))
            {
                return false;
            }

            ManifestBundle root;
            if (!manifest.TryGetBundle(asset.Bundle, out root))
            {
                // Unreachable for a parsed manifest (load-time validation), but a
                // hand-built manifest could still get here: honest false, not a crash.
                return false;
            }

            _visited.Clear();
            _stackBundles.Clear();
            _stackDepIndex.Clear();

            _visited.Add(root.Name);
            _stackBundles.Add(root);
            _stackDepIndex.Add(0);

            while (_stackBundles.Count > 0)
            {
                int top = _stackBundles.Count - 1;
                ManifestBundle bundle = _stackBundles[top];
                List<string> dependencies = PackageManifest.DependenciesOf(bundle);

                if (_stackDepIndex[top] < dependencies.Count)
                {
                    string dependency = dependencies[_stackDepIndex[top]];
                    _stackDepIndex[top] = _stackDepIndex[top] + 1;

                    if (_visited.Add(dependency))
                    {
                        ManifestBundle child;
                        if (manifest.TryGetBundle(dependency, out child))
                        {
                            _stackBundles.Add(child);
                            _stackDepIndex.Add(0);
                        }
                    }

                    continue;
                }

                // Every dependency of this frame is emitted already: this is the
                // post-order position, i.e. dependencies before dependents.
                into.Add(bundle.Name);
                _stackBundles.RemoveAt(top);
                _stackDepIndex.RemoveAt(top);
            }

            return true;
        }

        /// <summary>Sum of the declared sizes of the given bundles (progress estimation).</summary>
        public static long SumBytes(PackageManifest manifest, List<string> bundles)
        {
            if (manifest == null || bundles == null)
            {
                return 0;
            }

            long total = 0;
            for (int i = 0; i < bundles.Count; i++)
            {
                ManifestBundle bundle;
                if (manifest.TryGetBundle(bundles[i], out bundle))
                {
                    total += bundle.Size;
                }
            }

            return total;
        }
    }
}
