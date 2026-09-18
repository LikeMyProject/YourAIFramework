using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using YourAI.Core.Contracts;
using YourAI.Core.Json;

namespace YourAI.Memory
{
    /// <summary>
    /// Memory that survives the process: <see cref="InMemoryMemoryStore"/> plus a JSON
    /// file on disk.
    ///
    /// Composition rather than reimplementation, for the same reason
    /// <see cref="HybridMemoryStore"/> is a decorator: the ranking, the capacity cap
    /// and the "semantic records are never evicted" rule are already correct and
    /// already tested. What persistence adds is a second way to be wrong (half-written
    /// files, schema drift, a save file from an older build), so the new code should
    /// be as small as the job allows.
    ///
    /// On directories. Core is forbidden from referencing UnityEngine, so it cannot
    /// call <c>Application.persistentDataPath</c>. The directory is therefore a
    /// constructor argument, and the host supplies the platform-correct one. That is
    /// not a limitation in disguise: it is the same seam that makes this class
    /// testable without a Unity runtime, which is how thirteen of its assertions run
    /// in a console program.
    ///
    /// On when to write. <see cref="Save"/> is not called on every append, because a
    /// disk flush in the middle of a frame is exactly the kind of stall the rest of
    /// this framework refuses to introduce. The intended call site is a save point the
    /// game already has: a scene change, a pause, an explicit player action. A host
    /// that wants a safety net without owning the cadence can set
    /// <see cref="SaveAfterAppends"/> instead.
    ///
    /// Nothing here throws. A missing file, a corrupt file, a read-only directory and
    /// a platform with no file system at all are all conditions a game runs into in
    /// the field, and every one of them is reported through <see cref="LastError"/>
    /// with the store carrying on in memory. Memory persistence failing should cost
    /// the player their history, not their session.
    /// </summary>
    public sealed class FileMemoryStore : IMemoryStore
    {
        public const int FormatVersion = 1;
        public const string DefaultFileName = "your-ai-memory.json";

        private readonly InMemoryMemoryStore _inner;
        private readonly string _directory;
        private readonly string _fileName;
        private readonly string _path;

        private int _nextId;
        private int _dirty;

        public FileMemoryStore(string directory)
            : this(directory, null, 0)
        {
        }

        public FileMemoryStore(string directory, string fileName)
            : this(directory, fileName, 0)
        {
        }

        /// <param name="directory">
        /// Absolute path, or null for a store that lives only in memory. Null is a
        /// supported configuration, not an error: a project that has not decided where
        /// to persist yet gets a working store and a reason in
        /// <see cref="LastError"/>.
        /// </param>
        public FileMemoryStore(string directory, string fileName, int maxRecordsPerActor)
        {
            _directory = directory;
            _fileName = string.IsNullOrEmpty(fileName) ? DefaultFileName : fileName;
            _path = string.IsNullOrEmpty(directory)
                ? null
                : Path.Combine(directory, _fileName);

            _inner = maxRecordsPerActor > 0
                ? new InMemoryMemoryStore(maxRecordsPerActor)
                : new InMemoryMemoryStore();
        }

        /// <summary>Constructs a store and loads it. The usual entry point.</summary>
        public static FileMemoryStore Open(string directory)
        {
            return Open(directory, null);
        }

        public static FileMemoryStore Open(string directory, string fileName)
        {
            FileMemoryStore store = new FileMemoryStore(directory, fileName, 0);
            store.Load();
            return store;
        }

        /// <summary>Full path of the backing file, or null when persistence is off.</summary>
        public string FilePath
        {
            get { return _path; }
        }

        /// <summary>True when a complete save has happened since the last change.</summary>
        public bool IsClean
        {
            get { return _dirty == 0; }
        }

        /// <summary>Records read by the last <see cref="Load"/>. Zero when the file was absent.</summary>
        public int LoadedRecords { get; private set; }

        /// <summary>Times <see cref="Save"/> has written successfully.</summary>
        public int SaveCount { get; private set; }

        /// <summary>
        /// Empty on success. On failure it says what went wrong and the store keeps
        /// working from memory, which is why this is a property rather than an
        /// exception: there is nothing a caller could usefully do about a failed
        /// memory save except carry on.
        /// </summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Appends before an automatic <see cref="Save"/>. Zero, the default, means
        /// never: the host owns the cadence. Set it to something small for a safety
        /// net, and accept the frame-time cost of writing that often.
        /// </summary>
        public int SaveAfterAppends { get; set; }

        /// <summary>
        /// The in-memory half. Exposed because it carries the extra operations the
        /// interface does not: clearing one tier, counting one tier, and snapshotting
        /// an actor's records.
        /// </summary>
        public InMemoryMemoryStore Inner
        {
            get { return _inner; }
        }

        // ------------------------------------------------------------ IMemoryStore

        public void Append(MemoryRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException("record");
            }

            // Ids are assigned here, not by the inner store, so that the counter can be
            // restored from the file. Letting the inner store number records while this
            // one loaded a different numbering is how a reloaded session starts
            // overwriting its own history with ids like "m1".
            if (string.IsNullOrEmpty(record.Id))
            {
                record.Id = "f" + (++_nextId).ToString(CultureInfo.InvariantCulture);
            }

            _inner.Append(record);
            _dirty++;

            if (SaveAfterAppends > 0 && _dirty >= SaveAfterAppends)
            {
                Save();
            }
        }

        public int Query(MemoryQuery query, List<MemoryRecord> results)
        {
            return _inner.Query(query, results);
        }

        public bool Remove(string id)
        {
            bool removed = _inner.Remove(id);
            if (removed)
            {
                _dirty++;
            }
            return removed;
        }

        public void Clear(string actorId)
        {
            _inner.Clear(actorId);
            _dirty++;
        }

        public int Count(string actorId)
        {
            return _inner.Count(actorId);
        }

        // ---------------------------------------------------------------- persistence

        /// <summary>
        /// Replaces the in-memory contents with what is on disk, and returns how many
        /// records were read.
        ///
        /// Replace rather than merge, and named accordingly: the file is the state of
        /// the world, so "load" that added to whatever was already in memory would
        /// duplicate every record on a second call. Call it once, at startup.
        ///
        /// A missing file is not a failure: a new player has no history, and that is
        /// the normal first-run experience. A file that exists but cannot be parsed is
        /// a failure, and the unreadable file is copied aside before anything else
        /// touches it, so that the next save cannot destroy evidence of what happened.
        /// </summary>
        public int Load()
        {
            LoadedRecords = 0;

            if (_path == null)
            {
                LastError = "No directory configured; memory is in-process only.";
                return 0;
            }

            if (!File.Exists(_path))
            {
                LastError = null;
                _nextId = 0;
                _dirty = 0;
                return 0;
            }

            string text;
            try
            {
                text = File.ReadAllText(_path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                LastError = "Could not read " + _path + ": " + ex.Message;
                return 0;
            }

            JsonValue root;
            string parseError;
            if (!JsonParser.TryParse(text, out root, out parseError) || !root.IsObject)
            {
                Quarantine("unparseable: " + parseError);
                return 0;
            }

            JsonValue records = root["records"];
            if (!records.IsArray)
            {
                Quarantine("no records array");
                return 0;
            }

            List<MemoryRecord> loaded = new List<MemoryRecord>(records.Count);
            int highestId = 0;

            for (int i = 0; i < records.Count; i++)
            {
                MemoryRecord record = ReadRecord(records[i]);
                if (record == null)
                {
                    continue;
                }
                loaded.Add(record);

                int suffix = ParseIdSuffix(record.Id);
                if (suffix > highestId)
                {
                    highestId = suffix;
                }
            }

            _inner.Clear(null);
            for (int i = 0; i < loaded.Count; i++)
            {
                _inner.Append(loaded[i]);
            }

            // Trust the file's counter, but never below the ids actually present: a
            // hand-edited or truncated file must not be able to hand out a live id.
            int savedNext = root["nextId"].AsInt(0);
            _nextId = savedNext > highestId ? savedNext : highestId;

            LoadedRecords = _inner.Count(null);
            _dirty = 0;
            LastError = null;
            return LoadedRecords;
        }

        /// <summary>
        /// Writes the whole store to disk. Returns false with <see cref="LastError"/>
        /// set on any failure; the in-memory store is unaffected either way.
        ///
        /// The write goes to a sibling <c>.tmp</c> file first and is then copied over
        /// the target, so a crash mid-write leaves a readable file plus a stray
        /// temporary one rather than a truncated save. The copy itself is not atomic
        /// on every platform, which is the honest limitation here; what the two-step
        /// form buys is that the window is a copy rather than however long the
        /// serialiser took.
        /// </summary>
        public bool Save()
        {
            if (_path == null)
            {
                LastError = "No directory configured; nothing was written.";
                return false;
            }

            string json = WriteJson();
            string tempPath = _path + ".tmp";

            try
            {
                if (!string.IsNullOrEmpty(_directory))
                {
                    Directory.CreateDirectory(_directory);
                }

                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                File.Copy(tempPath, _path, true);
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                LastError = "Could not write " + _path + ": " + ex.Message;
                TryDelete(tempPath);
                return false;
            }

            SaveCount++;
            _dirty = 0;
            LastError = null;
            return true;
        }

        // ---------------------------------------------------------------- serialising

        private string WriteJson()
        {
            List<MemoryRecord> records = new List<MemoryRecord>();
            _inner.Snapshot(null, records);

            StringBuilder sb = new StringBuilder(records.Count * 96 + 64);
            sb.Append("{\n");
            sb.Append("  \"version\": ").Append(FormatVersion).Append(",\n");
            sb.Append("  \"nextId\": ").Append(_nextId).Append(",\n");
            sb.Append("  \"savedAtUnix\": ")
              .Append(MemoryRanker.DefaultClock().ToString("R", CultureInfo.InvariantCulture))
              .Append(",\n");
            sb.Append("  \"records\": [");

            for (int i = 0; i < records.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append('\n').Append("    ");
                WriteRecord(sb, records[i]);
            }

            if (records.Count > 0)
            {
                sb.Append('\n').Append("  ");
            }
            sb.Append("]\n}\n");
            return sb.ToString();
        }

        private static void WriteRecord(StringBuilder sb, MemoryRecord record)
        {
            sb.Append("{\"id\": ").Append(Quote(record.Id));
            sb.Append(", \"actor\": ").Append(Quote(record.ActorId));
            sb.Append(", \"kind\": ").Append(Quote(KindName(record.Kind)));
            sb.Append(", \"text\": ").Append(Quote(record.Text));
            sb.Append(", \"unixTime\": ")
              .Append(record.UnixTime.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(", \"salience\": ")
              .Append(((double)record.Salience).ToString("R", CultureInfo.InvariantCulture));
            sb.Append(", \"tokens\": ").Append(record.EstimatedTokens);

            sb.Append(", \"tags\": [");
            if (record.Tags != null)
            {
                for (int i = 0; i < record.Tags.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append(Quote(record.Tags[i]));
                }
            }
            sb.Append("]}");
        }

        private static MemoryRecord ReadRecord(JsonValue value)
        {
            if (!value.IsObject)
            {
                return null;
            }

            string text = value["text"].AsString(null);
            if (text == null)
            {
                return null;
            }

            MemoryRecord record = new MemoryRecord
            {
                Id = value["id"].AsString(null),
                ActorId = value["actor"].AsString(string.Empty),
                Kind = ParseKind(value["kind"].AsString(null)),
                Text = text,
                UnixTime = value["unixTime"].AsDouble(0),
                Salience = (float)value["salience"].AsDouble(0.5),
                EstimatedTokens = value["tokens"].AsInt(0),
            };

            JsonValue tags = value["tags"];
            if (tags.IsArray && tags.Count > 0)
            {
                string[] parsed = new string[tags.Count];
                for (int i = 0; i < tags.Count; i++)
                {
                    parsed[i] = tags[i].AsString(string.Empty);
                }
                record.Tags = parsed;
            }

            return record;
        }

        private static string Quote(string text)
        {
            return "\"" + JsonParser.Escape(text) + "\"";
        }

        private static string KindName(MemoryKind kind)
        {
            switch (kind)
            {
                case MemoryKind.Working: return "working";
                case MemoryKind.Semantic: return "semantic";
                default: return "episodic";
            }
        }

        /// <summary>
        /// Unknown kinds read as episodic rather than semantic. Semantic is the tier
        /// that is never evicted, so guessing it for a value this build does not
        /// recognise would quietly make every record permanent and defeat the cap.
        /// </summary>
        private static MemoryKind ParseKind(string name)
        {
            if (string.Equals(name, "working", StringComparison.OrdinalIgnoreCase))
            {
                return MemoryKind.Working;
            }
            if (string.Equals(name, "semantic", StringComparison.OrdinalIgnoreCase))
            {
                return MemoryKind.Semantic;
            }
            return MemoryKind.Episodic;
        }

        private static int ParseIdSuffix(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 2 || id[0] != 'f')
            {
                return 0;
            }

            int value;
            if (int.TryParse(id.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            return 0;
        }

        /// <summary>
        /// Moves an unreadable file aside so the next save cannot overwrite it. The
        /// cost is one orphan file; the alternative is losing the only evidence of
        /// what the store could not read.
        /// </summary>
        private void Quarantine(string reason)
        {
            string backup = _path + ".bad";
            try
            {
                File.Copy(_path, backup, true);
                LastError = "Ignored " + _path + " (" + reason + "); a copy is at " + backup;
            }
            catch (Exception ex)
            {
                LastError = "Ignored " + _path + " (" + reason + "), and could not back it up: " + ex.Message;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best effort. A leftover .tmp file is harmless and more informative
                // than whatever a second failure here would tell us.
            }
        }
    }
}
