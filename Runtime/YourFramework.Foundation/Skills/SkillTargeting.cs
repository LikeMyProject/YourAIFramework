using System;
using System.Collections.Generic;

namespace YourFramework.Skills
{
    /// <summary>
    /// 一个可被选中的单位。**故意是一份纯数据快照**，不是场景对象引用：
    /// 目标选择因此可以在无头环境里逐字断言（谁被选中、按什么顺序），
    /// 也不会把技能模块绑死在某个实体系统上。调用方每帧从自己的实体系统里填快照。
    /// </summary>
    public struct SkillTargetCandidate
    {
        /// <summary>单位 id。</summary>
        public string Id;

        /// <summary>阵营（同队 = 友军）。</summary>
        public int Team;

        /// <summary>是否存活。</summary>
        public bool Alive;

        /// <summary>与施法者的距离（数据，供调用方筛射程；本模块不自己筛）。</summary>
        public float Distance;

        /// <summary>血量比例（0..1），用于"血最少的友军"。</summary>
        public float HealthRatio;

        public SkillTargetCandidate(string id, int team, bool alive, float distance, float healthRatio)
        {
            Id = id;
            Team = team;
            Alive = alive;
            Distance = distance;
            HealthRatio = healthRatio;
        }

        public override string ToString()
        {
            return Id + "/t" + Team + (Alive ? "" : "/dead");
        }
    }

    /// <summary>
    /// 一次目标选择请求。施法者阵营单独传，而不是"去候选列表里找施法者"——
    /// 后者会让"敌对"这件事取决于候选列表里恰好包含了谁，是个隐性的数据依赖。
    /// </summary>
    public struct SkillTargetQuery
    {
        public string CasterId;
        public int CasterTeam;
        public SkillTargeting Mode;
        public string RequestedTargetId;

        public static SkillTargetQuery For(string casterId, int casterTeam, SkillTargeting mode)
        {
            return new SkillTargetQuery
            {
                CasterId = casterId,
                CasterTeam = casterTeam,
                Mode = mode,
                RequestedTargetId = null
            };
        }

        public SkillTargetQuery WithTarget(string targetId)
        {
            SkillTargetQuery copy = this;
            copy.RequestedTargetId = targetId;
            return copy;
        }
    }

    /// <summary>
    /// 目标选择：纯函数，只看候选快照，不查场景、不查物理、不查寻路。
    ///
    /// 与 <c>AddressCatalog.FindByGroup</c> 那类检索接口的一个**故意的不一致**：
    /// 这里的 <c>into</c> 会被**清空**再填。检索的语义是"满足条件的都给我"，
    /// 多次调用求并集说得通；目标选择的语义是"这一次打谁"，结果集必须是精确的 ——
    /// 沿用追加语义的话，每个调用点都得自己记得先 Clear，漏一次就是"打了一堆不该打的人"。
    /// </summary>
    public static class SkillTargetSelector
    {
        /// <summary>
        /// Fills <paramref name="into"/> (cleared first) with the units this cast hits.
        /// Returns the hit count; zero is a value (the cast has nothing to hit), not a throw.
        /// </summary>
        public static int Select(
            SkillTargetQuery query,
            IList<SkillTargetCandidate> candidates,
            List<SkillTargetCandidate> into)
        {
            if (candidates == null)
            {
                throw new ArgumentNullException("candidates");
            }

            if (into == null)
            {
                throw new ArgumentNullException("into");
            }

            if (string.IsNullOrEmpty(query.CasterId))
            {
                throw new ArgumentException("SkillTargetSelector: caster id must not be empty.");
            }

            into.Clear();

            switch (query.Mode)
            {
                case SkillTargeting.Self:
                    return SelectSelf(query, candidates, into);

                case SkillTargeting.SingleTarget:
                    return SelectSingle(query, candidates, into);

                case SkillTargeting.AllEnemies:
                    return SelectEnemies(query, candidates, into);

                case SkillTargeting.LowestHealthAlly:
                    return SelectWeakestAlly(query, candidates, into);

                default:
                    throw new ArgumentException(
                        "SkillTargetSelector: unknown targeting mode " + query.Mode + ".");
            }
        }

        private static int SelectSelf(
            SkillTargetQuery query, IList<SkillTargetCandidate> candidates, List<SkillTargetCandidate> into)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                SkillTargetCandidate candidate = candidates[i];
                if (string.Equals(candidate.Id, query.CasterId, StringComparison.Ordinal))
                {
                    if (!candidate.Alive)
                    {
                        return 0;
                    }

                    into.Add(candidate);
                    return 1;
                }
            }

            // 施法者自己不在候选列表里：这是调用方的数据缺口，不是技能的问题。
            return 0;
        }

        private static int SelectSingle(
            SkillTargetQuery query, IList<SkillTargetCandidate> candidates, List<SkillTargetCandidate> into)
        {
            if (string.IsNullOrEmpty(query.RequestedTargetId))
            {
                return 0;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                SkillTargetCandidate candidate = candidates[i];
                if (!string.Equals(candidate.Id, query.RequestedTargetId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!candidate.Alive)
                {
                    return 0;
                }

                into.Add(candidate);
                return 1;
            }

            return 0;
        }

        private static int SelectEnemies(
            SkillTargetQuery query, IList<SkillTargetCandidate> candidates, List<SkillTargetCandidate> into)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                SkillTargetCandidate candidate = candidates[i];
                if (candidate.Alive && candidate.Team != query.CasterTeam)
                {
                    into.Add(candidate);
                }
            }

            return into.Count;
        }

        private static int SelectWeakestAlly(
            SkillTargetQuery query, IList<SkillTargetCandidate> candidates, List<SkillTargetCandidate> into)
        {
            int best = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                SkillTargetCandidate candidate = candidates[i];
                if (!candidate.Alive || candidate.Team != query.CasterTeam)
                {
                    continue;
                }

                // 严格小于：并列时保留先出现的那个，顺序由调用方给的候选顺序决定 ——
                // 这就是"同一份快照永远选出同一个人"的依据。
                if (best < 0 || candidate.HealthRatio < candidates[best].HealthRatio)
                {
                    best = i;
                }
            }

            if (best < 0)
            {
                return 0;
            }

            into.Add(candidates[best]);
            return 1;
        }
    }
}
