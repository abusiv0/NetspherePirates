using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Options;
using Netsphere.Common.Configuration;
using Netsphere.Network.Message.GameRule;
using ProudNet.Hosting.Services;

namespace Netsphere.Server.Game.GameRules
{
    public class Touchdown : GameRuleBase
    {
        private static readonly TimeSpan s_touchdownWaitTime = TimeSpan.FromSeconds(10);

        private readonly TouchdownOptions _options;
        private readonly TouchdownAssistHelper _assistHelper;
        private readonly ISchedulerService _schedulerService;

        public override GameRule GameRule => GameRule.Touchdown;
        public bool IsInTouchdown { get; private set; }
        protected override bool HasHalfTime => true;

        public Touchdown(GameRuleStateMachine stateMachine, IOptions<GameOptions> gameOptions,
            IOptions<TouchdownOptions> options, ISchedulerService schedulerService)
            : base(stateMachine, gameOptions)
        {
            _options = options.Value;
            _assistHelper = new TouchdownAssistHelper();
            _schedulerService = schedulerService;

            StateMachine.GameStateChanged += OnGameStateChanged;
            stateMachine.TimeStateChanged += OnTimeStateChanged;
        }

        public override void Initialize(Room room)
        {
            base.Initialize(room);

            var playersPerTeam = Room.Options.MatchKey.PlayerLimit / 2;
            var spectatorsPerTeam = Room.Options.MatchKey.SpectatorLimit / 2;
            Room.TeamManager.Add(TeamId.Alpha, playersPerTeam, spectatorsPerTeam);
            Room.TeamManager.Add(TeamId.Beta, playersPerTeam, spectatorsPerTeam);
        }

        public override void Cleanup()
        {
            base.Cleanup();

            Room.TeamManager.Remove(TeamId.Alpha);
            Room.TeamManager.Remove(TeamId.Beta);
        }

        protected override bool CanStartGame()
        {
            if (StateMachine.GameState != GameState.Waiting)
                return false;

            // Is atleast one player per team ready?
            var teams = TeamManager.Values;
            return teams.All(team => team.Players.Any(plr => plr.IsReady || Room.Master == plr));
        }

        protected override bool HasEnoughPlayers()
        {
            return TeamManager.Values.All(team => team.PlayersPlaying.Any());
        }

        protected override PlayerScore CreateScore()
        {
            return new TouchdownPlayerScore(_options);
        }

        protected internal override BriefingTeam[] CreateBriefingTeams()
        {
            return GetTeams().ToArray();

            IEnumerable<BriefingTeam> GetTeams()
            {
                foreach (var team in TeamManager.Values)
                    yield return new BriefingTeam(team.Id, team.Score);
            }
        }

        protected override BriefingPlayer CreateBriefingPlayer(Player plr)
        {
            return new BriefingPlayerTouchdown(plr);
        }

        protected override (uint baseGain, uint bonusGain) CalculateExperienceGained(Player plr)
        {
            var experienceRates = _options.ExperienceRates;
            var place = 1;

            var plrs = TeamManager.Players
                .Where(x => x.State == PlayerState.Waiting && x.Mode == PlayerGameMode.Normal)
                .ToArray();

            foreach (var x in plrs.OrderByDescending(x => x.Score.GetTotalScore()))
            {
                if (x == plr)
                    break;

                place++;
                if (place > 3)
                    break;
            }

            var rankingBonus = 0f;
            switch (place)
            {
                case 1:
                    rankingBonus = experienceRates.FirstPlaceBonus;
                    break;

                case 2:
                    rankingBonus = experienceRates.SecondPlaceBonus;
                    break;

                case 3:
                    rankingBonus = experienceRates.ThirdPlaceBonus;
                    break;
            }

            var experienceGained = (uint)(plr.Score.GetTotalScore() * experienceRates.ScoreFactor +
                                          rankingBonus +
                                          plrs.Length * experienceRates.PlayerCountFactor +
                                          plr.GetCurrentPlayTime().TotalMinutes * experienceRates.ExperiencePerMinute);

            return (experienceGained, 0);
        }

        protected override (uint baseGain, uint bonusGain) CalculatePENGained(Player plr)
        {
            return (0, 0);
        }

        protected internal override void OnScoreKill(Player killer, Player assist, Player target, AttackAttribute attackAttribute)
        {
            if (IsInTouchdown)
                return;

            killer.Score.Kills++;
            target.Score.Deaths++;

            if (assist != null)
                assist.Score.KillAssists++;

            SendScoreKill(killer, assist, target, attackAttribute);
        }

        protected internal override void OnScoreOffense(Player killer, Player assist, Player target,
            AttackAttribute attackAttribute)
        {
            if (IsInTouchdown)
                return;

            GetScore(killer).OffenseScore++;
            target.Score.Deaths++;

            if (assist != null)
                GetScore(killer).OffenseAssistScore++;

            SendScoreOffense(killer, assist, target, attackAttribute);
        }

        protected internal override void OnScoreDefense(Player killer, Player assist, Player target,
            AttackAttribute attackAttribute)
        {
            if (IsInTouchdown)
                return;

            GetScore(killer).DefenseScore++;
            target.Score.Deaths++;

            if (assist != null)
                GetScore(killer).DefenseAssistScore++;

            SendScoreDefense(killer, assist, target, attackAttribute);
        }

        protected internal override void OnScoreFumbi(Player newPlr, Player oldPlr)
        {
            if (IsInTouchdown)
                return;

            if (oldPlr != null)
                _assistHelper.Update(oldPlr);

            if (newPlr != null)
                GetScore(newPlr).FumbiScore++;

            SendScoreFumbi(newPlr, oldPlr);
        }

        protected internal override void OnScoreTouchdown(Player plr)
        {
            if (IsInTouchdown)
                return;

            IsInTouchdown = true;

            Player assist = null;
            if (_assistHelper.IsAssist(plr))
            {
                assist = _assistHelper.LastPlayer;
                GetScore(assist).GoalAssistScore++;
            }

            plr.Team.Score++;
            GetScore(plr).GoalScore++;
            SendScoreTouchdown(plr, assist);

            if (plr.Team.Score == Room.Options.ScoreLimit)
            {
                StateMachine.StartResult();
                return;
            }

            if (plr.Team.Score == Room.Options.ScoreLimit / 2)
            {
                StateMachine.StartHalfTime();
                return;
            }

            var halfTime = TimeSpan.FromSeconds(Room.Options.TimeLimit.TotalSeconds / 2);
            var diff = halfTime - StateMachine.RoundTime;
            if (diff <= s_touchdownWaitTime + TimeSpan.FromSeconds(2))
                return;

            Room.Broadcast(new SEventMessageAckMessage(GameEventMessage.NextRoundIn,
                (ulong)s_touchdownWaitTime.TotalMilliseconds, 0, 0, ""));
            _schedulerService.ScheduleAsync(OnNextRound, this, null, s_touchdownWaitTime);
        }

        protected internal override void OnScoreSuicide(Player plr)
        {
            if (IsInTouchdown)
                return;

            plr.Score.Deaths++;
            plr.Score.Suicides++;
            SendScoreSuicide(plr);
        }

        protected internal override void OnScoreTeamKill(Player killer, Player target, AttackAttribute attackAttribute)
        {
            if (IsInTouchdown)
                return;

            target.Score.Deaths++;
            SendScoreTeamKill(killer, target, attackAttribute);
        }

        protected internal override void OnScoreHeal(Player plr)
        {
            if (IsInTouchdown)
                return;

            plr.Score.HealAssists++;
            SendScoreHeal(plr);
        }

        private void OnGameStateChanged(object sender, EventArgs e)
        {
            IsInTouchdown = false;
        }

        private void OnTimeStateChanged(object sender, EventArgs e)
        {
            IsInTouchdown = false;
        }

        private static void OnNextRound(object state, object _)
        {
            var This = (Touchdown)state;
            if (This.StateMachine.GameState != GameState.Playing)
                return;

            This.IsInTouchdown = false;
            This.Room.Broadcast(new SEventMessageAckMessage(GameEventMessage.ResetRound, 0, 0, 0, ""));
        }

        private static TouchdownPlayerScore GetScore(Player plr)
        {
            return (TouchdownPlayerScore)plr.Score;
        }

        private class BriefingPlayerTouchdown : BriefingPlayer
        {
            private readonly Player _player;

            public BriefingPlayerTouchdown(Player plr)
            {
                _player = plr;

                AccountId = plr.Account.Id;
                Experience = plr.TotalExperience;
                TeamId = plr.Team.Id;
                State = plr.State;
                Mode = plr.Mode;
                IsReady = plr.IsReady;
                TotalScore = plr.Score.GetTotalScore();
            }

            public override void Serialize(BinaryWriter w)
            {
                base.Serialize(w);

                var score = (TouchdownPlayerScore)_player.Score;
                w.Write(score.GoalScore);
                w.Write(score.GoalAssistScore);
                w.Write(score.Kills);
                w.Write(score.KillAssists);
                w.Write(score.OffenseScore);
                w.Write(score.OffenseAssistScore);
                w.Write(score.DefenseScore);
                w.Write(score.DefenseAssistScore);
                w.Write(score.HealAssists);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(score.FumbiScore);
                w.Write(0);
                w.Write(0);
            }
        }

        private class TouchdownAssistHelper
        {
            private static readonly TimeSpan s_touchdownAssistTimer = TimeSpan.FromSeconds(10);

            public DateTime LastTime { get; set; }
            public Player LastPlayer { get; set; }

            public void Update(Player plr)
            {
                LastTime = DateTime.Now;
                LastPlayer = plr;
            }

            public bool IsAssist(Player plr)
            {
                if (LastPlayer == null)
                    return false;

                if (plr.Team != LastPlayer.Team)
                    return false;

                return DateTime.Now - LastTime < s_touchdownAssistTimer;
            }
        }
    }

    public class TouchdownPlayerScore : PlayerScore
    {
        private readonly TouchdownOptions _options;

        public uint GoalScore { get; set; }
        public uint GoalAssistScore { get; set; }
        public uint OffenseScore { get; set; }
        public uint OffenseAssistScore { get; set; }
        public uint DefenseScore { get; set; }
        public uint DefenseAssistScore { get; set; }
        public uint FumbiScore { get; set; }

        public TouchdownPlayerScore(TouchdownOptions options)
        {
            _options = options;
        }

        public override uint GetTotalScore()
        {
            return (uint)(GoalScore * _options.PointsPerGoal +
                          GoalAssistScore * _options.PointsPerGoalAssist +
                          Kills * _options.PointsPerKill +
                          KillAssists * _options.PointsPerKillAssist +
                          OffenseScore * _options.PointsPerOffense +
                          OffenseAssistScore * _options.PointsPerOffenseAssist +
                          DefenseScore * _options.PointsPerDefense +
                          DefenseAssistScore * _options.PointsPerDefenseAssist +
                          HealAssists * _options.PointsPerHealAssist +
                          FumbiScore * _options.PointsPerFumbi);
        }
    }
}