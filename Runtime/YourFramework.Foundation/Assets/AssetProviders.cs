using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YourFramework.Assets
{
    /// <summary>
    /// Dictionary-backed provider: the test double and the "content from code"
    /// provider. Missing keys do not claim Handles, so the manager keeps trying
    /// lower-priority providers and finally reports "no provider handles key"
    /// -- honest routing instead of a synthetic failure.
    /// </summary>
    public sealed class MemoryAssetProvider : IAssetProvider
    {
        private readonly Dictionary<string, AssetPayload> _items =
            new Dictionary<string, AssetPayload>(StringComparer.Ordinal);

        /// <summary>Provider name.</summary>
        public string Name { get { return "Memory"; } }

        /// <summary>Adds (or replaces) a payload under the key. Setup phase.</summary>
        public void Add(string key, AssetPayload payload)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("MemoryAssetProvider.Add: key must not be null or empty.", "key");
            }

            if (payload == null)
            {
                throw new ArgumentNullException("payload");
            }

            _items[key] = payload;
        }

        /// <summary>Whether the key exists.</summary>
        public bool Handles(string key)
        {
            return key != null && _items.ContainsKey(key);
        }

        /// <summary>Settles immediately: the payload is already in hand.</summary>
        public void Pump(AssetRequest request)
        {
            AssetPayload payload;
            if (_items.TryGetValue(request.Key, out payload))
            {
                request.Complete(payload);
            }
            else
            {
                request.Fail("Memory: key '" + request.Key + "' vanished between Handles and Pump");
            }
        }
    }

    /// <summary>
    /// File-backed provider: resolves keys against a root directory and settles
    /// on the first Pump (synchronous local I/O; a networked backend would pump
    /// across frames exactly like the YooAsset adapter does).
    ///
    /// Payload kind by extension: .json/.txt/.csv/.md/.xml decode as UTF-8 text,
    /// everything else loads as bytes. Keys are container-relative; path
    /// traversal ("..", rooted or drive-qualified keys) is rejected as a failed
    /// request, not a crash -- but a key that escapes the root is suspicious
    /// enough to be loud in the error string.
    /// </summary>
    public sealed class FileAssetProvider : IAssetProvider
    {
        private readonly string _root;

        /// <summary>
        /// Builds the provider over a root directory. A missing root is a setup
        /// bug: fail fast here rather than failing every request later.
        /// </summary>
        public FileAssetProvider(string root)
        {
            if (string.IsNullOrEmpty(root))
            {
                throw new ArgumentException("FileAssetProvider: root must not be null or empty.", "root");
            }

            if (!Directory.Exists(root))
            {
                throw new ArgumentException(
                    "FileAssetProvider: root does not exist: " + root, "root");
            }

            _root = root;
        }

        /// <summary>Provider name.</summary>
        public string Name { get { return "File"; } }

        /// <summary>Claims everything (the usual last-resort provider).</summary>
        public bool Handles(string key)
        {
            return !string.IsNullOrEmpty(key);
        }

        /// <summary>Settles on the first pump: read the file, or fail with the reason.</summary>
        public void Pump(AssetRequest request)
        {
            string relative = request.Key;
            if (relative.IndexOf("..", StringComparison.Ordinal) >= 0
                || relative.StartsWith("/", StringComparison.Ordinal)
                || relative.StartsWith("\\", StringComparison.Ordinal)
                || relative.IndexOf(':') >= 0)
            {
                request.Fail("File: unsafe key '" + relative + "' (path traversal is not a lookup, it is a bug)");
                return;
            }

            string path;
            try
            {
                path = Path.Combine(_root, relative);
            }
            catch (Exception ex)
            {
                request.Fail("File: bad key '" + relative + "': " + ex.Message);
                return;
            }

            try
            {
                if (!File.Exists(path))
                {
                    request.Fail("File: no such file: " + relative);
                    return;
                }

                if (IsTextExtension(path))
                {
                    request.Complete(AssetPayload.FromText(File.ReadAllText(path, Encoding.UTF8)));
                }
                else
                {
                    request.Complete(AssetPayload.FromBytes(File.ReadAllBytes(path)));
                }
            }
            catch (IOException ex)
            {
                // Runtime I/O trouble (locked file, disk hiccup): failure as value.
                request.Fail("File: I/O failed for '" + relative + "': " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                request.Fail("File: access denied for '" + relative + "': " + ex.Message);
            }
        }

        private static bool IsTextExtension(string path)
        {
            string ext = Path.GetExtension(path);
            return ext == ".json" || ext == ".txt" || ext == ".csv"
                || ext == ".md" || ext == ".xml";
        }
    }
}
