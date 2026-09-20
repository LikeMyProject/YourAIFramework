using System;
using System.Collections.Generic;
using YourFramework.Core;

namespace YourFramework.Audio
{
    /// <summary>
    /// One mix bus. Channels are registered up front (master, music, sfx, ui,
    /// voice-over...) and addressed by name; playing on an unregistered channel
    /// is a wiring bug and fails fast. Volume changes rebalance live voices.
    /// </summary>
    public sealed class AudioChannel
    {
        /// <summary>Channel name (ordinal key).</summary>
        public string Name;

        /// <summary>Linear volume 0..1 (clamped on set).</summary>
        public float Volume;

        /// <summary>Muted channels dispatch voices at volume 0 (loops keep playing silently).</summary>
        public bool Mute;

        /// <summary>Max simultaneously audible voices; overflow steals or rejects by priority.</summary>
        public int MaxVoices;
    }

    /// <summary>
    /// One dispatched sound. Ids are manager-minted and monotonic, so "oldest"
    /// is always the smallest id. VolumeScale is the per-play base; the final
    /// Volume (scale × channel × master) is what the backend actually plays at
    /// and is recomputed on live rebalance.
    /// </summary>
    public sealed class AudioVoice
    {
        /// <summary>Manager-minted id, monotonic; 0 means not dispatched yet.</summary>
        public int Id;

        /// <summary>Backend-resolvable clip key (e.g. an asset key for the loader).</summary>
        public string ClipKey;

        /// <summary>Mix channel name.</summary>
        public string Channel;

        /// <summary>Priority; higher steals lower on overflow, ties steal oldest.</summary>
        public int Priority;

        /// <summary>Looping voices never report finished; they are stopped explicitly.</summary>
        public bool Loop;

        /// <summary>Per-play volume scale (0..1) before channel/master mixing.</summary>
        public float VolumeScale;

        /// <summary>Final mixed volume the backend plays at.</summary>
        public float Volume;

        /// <summary>Backend-owned payload (e.g. the AudioSource index); opaque to the manager.</summary>
        public object BackendData;
    }

    /// <summary>
    /// Engine adapter: the manager decides WHAT plays and at what volume; the
    /// backend decides HOW. Contract:
    ///
    /// - Play starts (or stages) the voice; the voice instance is manager-owned,
    ///   BackendData is the backend's private slot;
    /// - SetVoiceVolume rebalances a live voice (no-op on unknown ids);
    /// - Stop halts a voice; stopping unknown ids must be a silent no-op;
    /// - TryCollectFinished reports non-loop voices that ended on their own;
    ///   the caller passes its list to avoid per-frame allocations.
    /// </summary>
    public interface IAudioBackend
    {
        /// <summary>Human-readable backend name (diagnostics).</summary>
        string Name { get; }

        /// <summary>Starts playback for the voice.</summary>
        void Play(AudioVoice voice);

        /// <summary>Halts playback. Unknown ids are silent no-ops.</summary>
        void Stop(int voiceId);

        /// <summary>Rebalances a live voice's volume. Unknown ids are silent no-ops.</summary>
        void SetVoiceVolume(int voiceId, float volume);

        /// <summary>Collects self-finished non-loop voice ids into the caller's list.</summary>
        void TryCollectFinished(List<int> finished);
    }

    /// <summary>
    /// 声音管理器：分组音量（master × channel × scale）、静音、每通道声部上限、
    /// 优先级抢断、完成回收、实时重平衡。
    ///
    /// 设计要点：
    /// 1. 决策与发声分离 —— 本类只做调度与混音决策（纯 C#，离线可测），发声本体
    ///    归 IAudioBackend（引擎 AudioSource / Wwise / FMOD 皆可换）；
    /// 2. 抢断规则一句话 —— 上限满了：新声音优先级高过最低者就偷，偷谁=同优先级
    ///    取最老；高不过就拒（返回 null），"拒绝"是业务不是错误；
    /// 3. 通道未注册是布线 bug，fail-fast；停止未知 id 是业务，静默无操作；
    /// 4. SetVolume/SetMute 立即重平衡存活声部 —— 改音量不是只对"下一个声音"生效。
    ///
    /// 泵：每帧收尸（backend 上报的自然结束声部），无快照分配。
    /// </summary>
    public sealed class AudioManager : IModule
    {
        private readonly Dictionary<string, AudioChannel> _channels =
            new Dictionary<string, AudioChannel>(StringComparer.Ordinal);
        private readonly Dictionary<int, AudioVoice> _voices =
            new Dictionary<int, AudioVoice>();
        private readonly List<int> _finishedScratch = new List<int>();
        private readonly IAudioBackend _backend;
        private int _nextId = 1;

        /// <summary>Builds the manager over a backend; the master channel starts at volume 1.</summary>
        public AudioManager(IAudioBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException("backend");
            }

            _backend = backend;
            AudioChannel master = new AudioChannel
            {
                Name = "master",
                Volume = 1f,
                Mute = false,
                MaxVoices = int.MaxValue
            };
            _channels.Add(master.Name, master);
        }

        /// <summary>Backend in use.</summary>
        public string BackendName { get { return _backend.Name; } }

        /// <summary>Active (dispatched, not yet finished) voice count.</summary>
        public int ActiveVoices { get { return _voices.Count; } }

        /// <summary>Active voices currently on a channel.</summary>
        public int ActiveVoicesOn(string channel)
        {
            int count = 0;
            foreach (AudioVoice voice in _voices.Values)
            {
                if (voice.Channel == channel)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Registers (or reconfigures) a channel. Re-registering "master"
        /// adjusts the master bus; that is the intended way to set global volume.
        /// Volume clamps to 0..1; max voices must be >= 1.
        /// </summary>
        public void RegisterChannel(string name, int maxVoices, float volume = 1f, bool mute = false)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("AudioManager.RegisterChannel: name must not be empty.", "name");
            }

            if (maxVoices < 1)
            {
                throw new ArgumentException(
                    "AudioManager.RegisterChannel: maxVoices must be >= 1 (int.MaxValue for unbounded).", "maxVoices");
            }

            AudioChannel channel = new AudioChannel
            {
                Name = name,
                Volume = Clamp01(volume),
                Mute = mute,
                MaxVoices = maxVoices
            };
            _channels[name] = channel;
        }

        /// <summary>The channel config, or null when unregistered.</summary>
        public AudioChannel GetChannel(string name)
        {
            AudioChannel channel;
            return name != null && _channels.TryGetValue(name, out channel) ? channel : null;
        }

        /// <summary>
        /// Dispatches a sound. Returns the live voice, or null when rejected
        /// (channel full and priority not high enough to steal). Unknown channel
        /// is a wiring bug and throws.
        /// </summary>
        public AudioVoice Play(string clipKey, string channel, int priority = 0, bool loop = false, float volumeScale = 1f)
        {
            if (string.IsNullOrEmpty(clipKey))
            {
                throw new ArgumentException("AudioManager.Play: clip key must not be empty.", "clipKey");
            }

            AudioChannel bus = GetChannel(channel);
            if (bus == null)
            {
                throw new ArgumentException(
                    "AudioManager.Play: channel '" + channel + "' is not registered. Register channels up front.", "channel");
            }

            int active = 0;
            AudioVoice steal = null;
            foreach (AudioVoice candidate in _voices.Values)
            {
                if (candidate.Channel != channel)
                {
                    continue;
                }

                active++;
                if (steal == null || candidate.Priority < steal.Priority
                    || (candidate.Priority == steal.Priority && candidate.Id < steal.Id))
                {
                    steal = candidate;
                }
            }

            if (active >= bus.MaxVoices)
            {
                if (steal == null || priority <= steal.Priority)
                {
                    return null; // full, and not important enough to steal
                }

                _voices.Remove(steal.Id);
                _backend.Stop(steal.Id);
            }

            AudioVoice voice = new AudioVoice
            {
                Id = _nextId++,
                ClipKey = clipKey,
                Channel = channel,
                Priority = priority,
                Loop = loop,
                VolumeScale = Clamp01(volumeScale)
            };
            voice.Volume = Mix(voice);
            _voices.Add(voice.Id, voice);
            _backend.Play(voice);
            return voice;
        }

        /// <summary>Stops one voice. Unknown ids are business, not errors.</summary>
        public bool Stop(int voiceId)
        {
            AudioVoice voice;
            if (!_voices.TryGetValue(voiceId, out voice))
            {
                return false;
            }

            _voices.Remove(voiceId);
            _backend.Stop(voiceId);
            return true;
        }

        /// <summary>Stops every active voice on a channel (also used on Shutdown).</summary>
        public int StopChannel(string channel)
        {
            if (channel == null)
            {
                return 0;
            }

            int stopped = 0;
            List<int> doomed = new List<int>();
            foreach (AudioVoice voice in _voices.Values)
            {
                if (voice.Channel == channel)
                {
                    doomed.Add(voice.Id);
                }
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                _voices.Remove(doomed[i]);
                _backend.Stop(doomed[i]);
                stopped++;
            }

            return stopped;
        }

        /// <summary>
        /// Sets a channel's volume and rebalances every live voice on it
        /// (setting "master" rebalances everything).
        /// </summary>
        public void SetVolume(string channel, float volume)
        {
            AudioChannel bus = GetChannel(channel);
            if (bus == null)
            {
                throw new ArgumentException(
                    "AudioManager.SetVolume: channel '" + channel + "' is not registered.", "channel");
            }

            bus.Volume = Clamp01(volume);
            Rebalance(bus);
        }

        /// <summary>Mutes (or unmutes) a channel; live voices rebalance to silence.</summary>
        public void SetMute(string channel, bool mute)
        {
            AudioChannel bus = GetChannel(channel);
            if (bus == null)
            {
                throw new ArgumentException(
                    "AudioManager.SetMute: channel '" + channel + "' is not registered.", "channel");
            }

            bus.Mute = mute;
            Rebalance(bus);
        }

        /// <summary>Advances one frame: reap self-finished voices (no allocation).</summary>
        public void Pump()
        {
            _finishedScratch.Clear();
            _backend.TryCollectFinished(_finishedScratch);
            for (int i = 0; i < _finishedScratch.Count; i++)
            {
                _voices.Remove(_finishedScratch[i]);
            }
        }

        /// <summary>Module plumbing.</summary>
        public string Name { get { return "Audio"; } }

        /// <summary>Module plumbing: depends on nothing but its backend.</summary>
        public int InitOrder { get { return 20; } }

        /// <summary>Module plumbing. Nothing to fetch.</summary>
        public void Init(ModuleCenter host)
        {
        }

        /// <summary>Module plumbing: stops everything, backend owns the real resources.</summary>
        public void Shutdown()
        {
            List<int> all = new List<int>(_voices.Keys);
            for (int i = 0; i < all.Count; i++)
            {
                _backend.Stop(all[i]);
            }

            _voices.Clear();
        }

        private void Rebalance(AudioChannel bus)
        {
            foreach (AudioVoice voice in _voices.Values)
            {
                if (voice.Channel == bus.Name || bus.Name == "master")
                {
                    voice.Volume = Mix(voice);
                    _backend.SetVoiceVolume(voice.Id, voice.Volume);
                }
            }
        }

        private float Mix(AudioVoice voice)
        {
            AudioChannel bus = _channels[voice.Channel];
            AudioChannel master = _channels["master"];
            float volume = voice.VolumeScale * bus.Volume * master.Volume;
            return (bus.Mute || master.Mute) ? 0f : Clamp01(volume);
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }
    }
}
