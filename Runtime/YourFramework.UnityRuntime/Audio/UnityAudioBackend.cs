using System;
using System.Collections.Generic;
using UnityEngine;
using YourFramework.Audio;

namespace YourFramework.UnityRuntime.Audio
{
    /// <summary>
    /// AudioSource 池后端：把 AudioManager 的调度决策落到 Unity 的发声本体上。
    ///
    /// - 池固定大小，槽位与声部 id 双向映射（槽位少、线性扫描即可，热路径零分配）；
    /// - ClipKey 由调用方注入的解析器翻译成 AudioClip——通常接 AssetManager：
    ///   游戏引导里写 <c>key => assets.Load(key).Payload.Raw as AudioClip</c> 的轮询包装，
    ///   声音模块自身不碰资源系统（单向依赖，零耦合）；
    /// - 自然结束靠每帧 isPlaying 轮询（引擎实际内容），循环声部只能被显式 Stop；
    /// - 池满时不抢不炸：放弃本次发声并告警一次（声部上限的兜底是管理器的
    ///   通道 MaxVoices，池只是物理余量）。
    /// </summary>
    public sealed class UnityAudioBackend : IAudioBackend
    {
        private readonly AudioSource[] _pool;
        private readonly int[] _slotVoice;
        private readonly Func<string, AudioClip> _clipResolver;

        /// <summary>
        /// Builds the backend over a pool of AudioSources parented under
        /// <paramref name="root"/>. Pool size is the physical voice cap; the
        /// resolver translates clip keys (wired to the asset system by the host).
        /// </summary>
        public UnityAudioBackend(GameObject root, int poolSize, Func<string, AudioClip> clipResolver)
        {
            if (root == null)
            {
                throw new ArgumentNullException("root");
            }

            if (poolSize < 1)
            {
                throw new ArgumentException("UnityAudioBackend: poolSize must be >= 1.", "poolSize");
            }

            if (clipResolver == null)
            {
                throw new ArgumentNullException("clipResolver");
            }

            _pool = new AudioSource[poolSize];
            _slotVoice = new int[poolSize];
            _clipResolver = clipResolver;

            for (int i = 0; i < poolSize; i++)
            {
                AudioSource source = root.AddComponent<AudioSource>();
                source.playOnAwake = false;
                _pool[i] = source;
                _slotVoice[i] = 0;
            }
        }

        /// <summary>Backend name (diagnostics).</summary>
        public string Name { get { return "UnityAudio(" + _pool.Length + ")"; } }

        /// <summary>Starts the voice on a free slot, or drops with a warning when the pool is full.</summary>
        public void Play(AudioVoice voice)
        {
            int slot = FindFreeSlot();
            if (slot < 0)
            {
                Debug.LogWarning("[YourFramework] UnityAudioBackend: voice pool full, dropping '"
                    + voice.ClipKey + "'. Enlarge the pool or tighten channel MaxVoices.");
                return;
            }

            AudioClip clip = _clipResolver(voice.ClipKey);
            if (clip == null)
            {
                // 未知/未加载的 clip 是业务缺口：静默放弃本声部，错误由资源系统
                // 的 Failed 请求负责（失败也是值，这里不做二次诊断）。
                return;
            }

            _slotVoice[slot] = voice.Id;
            voice.BackendData = slot;

            AudioSource source = _pool[slot];
            source.clip = clip;
            source.loop = voice.Loop;
            source.volume = voice.Volume;
            source.Play();
        }

        /// <summary>Halts and frees the slot. Unknown ids are silent no-ops.</summary>
        public void Stop(int voiceId)
        {
            int slot = FindSlotOf(voiceId);
            if (slot < 0)
            {
                return;
            }

            AudioSource source = _pool[slot];
            source.Stop();
            source.clip = null;
            source.loop = false;
            _slotVoice[slot] = 0;
        }

        /// <summary>Rebalances a live voice. Unknown ids are silent no-ops.</summary>
        public void SetVoiceVolume(int voiceId, float volume)
        {
            int slot = FindSlotOf(voiceId);
            if (slot >= 0)
            {
                _pool[slot].volume = volume;
            }
        }

        /// <summary>Reports non-loop voices that stopped on their own; frees their slots.</summary>
        public void TryCollectFinished(List<int> finished)
        {
            for (int i = 0; i < _pool.Length; i++)
            {
                int voiceId = _slotVoice[i];
                if (voiceId == 0)
                {
                    continue;
                }

                AudioSource source = _pool[i];
                if (!source.loop && !source.isPlaying)
                {
                    finished.Add(voiceId);
                    source.clip = null;
                    _slotVoice[i] = 0;
                }
            }
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < _slotVoice.Length; i++)
            {
                if (_slotVoice[i] == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindSlotOf(int voiceId)
        {
            for (int i = 0; i < _slotVoice.Length; i++)
            {
                if (_slotVoice[i] == voiceId)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
