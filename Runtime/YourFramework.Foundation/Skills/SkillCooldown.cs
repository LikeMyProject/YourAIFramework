using System;
using System.Collections.Generic;
using System.Globalization;

namespace YourFramework.Skills
{
    /// <summary>
    /// 冷却与充能。一个技能一个槽，状态只有一个计数加一个剩余时间。
    ///
    /// 语义（三条，都是被断言钉住的）：
    ///   - **余量保留**：一帧跨过两个充能的恢复点时，两个都要补上，且多出来的时间继续
    ///     计入下一个充能 —— 不许"一帧只处理一个充能"（那样帧率会影响技能可用性，
    ///     60 帧和 30 帧打出的连招数会不一样）；
    ///   - **满了就不再累计**：充能满时剩余时间归零，不攒"欠账"；
    ///   - **速率是乘上去的**：冷却恢复速率（冷却缩减属性）直接乘在推进量上，
    ///     不是"把冷却时间改短" —— 后者在中途改属性时会算错已经过去的时间。
    ///
    /// 线程：可变状态，单线程（帧循环）内使用。
    /// </summary>
    public sealed class SkillCooldown
    {
        private readonly int _maxCharges;
        private readonly float _rechargeTime;

        private int _available;
        private float _remaining;

        /// <summary>
        /// Builds a cooldown tracker. A new skill starts fully charged (that is what "ready"
        /// means for a skill nobody has cast yet).
        /// </summary>
        public SkillCooldown(int maxCharges, float rechargeTime)
        {
            if (maxCharges < 1)
            {
                throw new ArgumentException(
                    "SkillCooldown: maxCharges must be at least 1 (got " + maxCharges + ").",
                    "maxCharges");
            }

            _maxCharges = maxCharges;
            _rechargeTime = rechargeTime;
            _available = maxCharges;
            _remaining = 0f;
        }

        /// <summary>Total charges.</summary>
        public int MaxCharges { get { return _maxCharges; } }

        /// <summary>Charges available right now.</summary>
        public int Available { get { return _available; } }

        /// <summary>Seconds until the next charge comes back (0 when full).</summary>
        public float Remaining { get { return _remaining; } }

        /// <summary>Whether at least one charge is available.</summary>
        public bool IsReady { get { return _available > 0; } }

        /// <summary>Whether every charge is available.</summary>
        public bool IsFull { get { return _available >= _maxCharges; } }

        /// <summary>
        /// Progress towards the next charge, 0..1 (1 when full). Diagnostic only -- read it in
        /// UI code, not in the frame loop.
        /// </summary>
        public float Progress
        {
            get
            {
                if (_available >= _maxCharges || _rechargeTime <= 0f)
                {
                    return 1f;
                }

                float done = (_rechargeTime - _remaining) / _rechargeTime;
                return done < 0f ? 0f : (done > 1f ? 1f : done);
            }
        }

        /// <summary>Human-readable one-liner for logs and diagnostics.</summary>
        public string Describe()
        {
            return _available + "/" + _maxCharges + " charges"
                + (_remaining > 0f ? ", next in " + _remaining.ToString("0.##", CultureInfo.InvariantCulture) + "s" : string.Empty);
        }

        /// <summary>
        /// Spends one charge. Returns false when nothing is available -- the caller turns that
        /// into a "still recharging" outcome rather than throwing, because mashing a skill
        /// button is normal player behaviour, not a bug.
        /// </summary>
        public bool TryConsume()
        {
            if (_available <= 0)
            {
                return false;
            }

            bool wasFull = _available >= _maxCharges;
            _available--;

            // 从满仓消耗的那一刻才开始计时。不满仓时已经有计时在跑，不能重置
            // （否则连按会把回充越推越远）。
            if (wasFull && _rechargeTime > 0f)
            {
                _remaining = _rechargeTime;
            }

            return true;
        }

        /// <summary>
        /// Advances the recharge clock. <paramref name="rate"/> is the recharge speed
        /// multiplier (1 = normal); it multiplies the elapsed time rather than shortening the
        /// recharge, so changing it mid-way keeps the already-elapsed time honest.
        /// Returns how many charges came back this frame (0 most frames).
        /// </summary>
        public int Pump(float delta, float rate)
        {
            if (delta < 0f)
            {
                throw new ArgumentException(
                    "SkillCooldown: delta must not be negative (got " + delta + ").", "delta");
            }

            if (rate < 0f)
            {
                throw new ArgumentException(
                    "SkillCooldown: rate must not be negative (got " + rate + ").", "rate");
            }

            if (_available >= _maxCharges)
            {
                _remaining = 0f;
                return 0;
            }

            if (_rechargeTime <= 0f)
            {
                int instant = _maxCharges - _available;
                _available = _maxCharges;
                _remaining = 0f;
                return instant;
            }

            _remaining -= delta * rate;

            int restored = 0;
            while (_remaining <= 0f && _available < _maxCharges)
            {
                _available++;
                restored++;

                if (_available >= _maxCharges)
                {
                    _remaining = 0f;
                    break;
                }

                // 关键的一行：把超出恢复点的部分**留给下一个充能**，
                // 而不是丢弃或重置成整个恢复时长。丢时间 = 帧率影响手感。
                _remaining += _rechargeTime;
            }

            return restored;
        }

        /// <summary>Refills every charge (respawn, encounter reset, debug).</summary>
        public void ResetToFull()
        {
            _available = _maxCharges;
            _remaining = 0f;
        }

        /// <summary>Drains every charge without touching the clock (used by "silence" style bans).</summary>
        public void DrainAll()
        {
            _available = 0;
            if (_rechargeTime > 0f && _remaining <= 0f)
            {
                _remaining = _rechargeTime;
            }
        }

        /// <summary>Snapshot for tooling: charges + remaining time, no allocation.</summary>
        public void Snapshot(List<int> chargesInto, List<float> remainingInto)
        {
            if (chargesInto == null || remainingInto == null)
            {
                throw new ArgumentNullException(chargesInto == null ? "chargesInto" : "remainingInto");
            }

            chargesInto.Add(_available);
            remainingInto.Add(_remaining);
        }
    }
}
