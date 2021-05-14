/*
CommunityVigilance Copyright (c) 2021 by PinguinNordpol

This plugin is loosely based on "Skip Night Vote" plugin which is

Copyright (c) 2019 k1lly0u

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

//#define DEBUG

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rust;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Community Vigilance", "PinguinNordpol", "0.3.0")]
    [Description("Adds the possibility to start votes to kick players from the server")]
    class CommunityVigilance : CovalencePlugin
    {
        #region Fields
        private ConfigData config_data;
        private List<string> ReceivedVotes = new List<string>();
        private bool IsVoteOpen = false;
        private VoteType vote_type;
        private List<IPlayer> target_players = new List<IPlayer>();
        private bool DisplayCountEveryVote = false;
        private int TimeRemaining = 0;
        private int CooldownTime = 0;
        private int RequiredVotes = 0;
        private string TimeRemMSG = "";
        private Timer VotingTimer = null;
        private Timer CountTimer = null;
        private Timer CooldownTimer = null;

        private enum VoteEndReason : int
        {
            VoteEnded = 0,
            PlayerDisconnected = 1,
            AdminAbort = 2,
            AdminForce = 3
        }

        private enum VoteType : int
        {
            Player = 0,
            Team = 1
        }
        #endregion

        #region Oxide Hooks
        void Init()
        {
            // Register our permissions
            permission.RegisterPermission("communityvigilance.use", this);
            permission.RegisterPermission("communityvigilance.startvote", this);
            permission.RegisterPermission("communityvigilance.startvoteteam", this);
            permission.RegisterPermission("communityvigilance.admin", this);

            // Unsubscript from some hooks until we actually need them
            Unsubscribe(nameof(OnUserDisconnected));
            Unsubscribe(nameof(OnTeamLeave));
        }

        void Loaded() => lang.RegisterMessages(Messages, this);

        void OnServerInitialized()
        {
            this.LoadConfig();
            this.TimeRemMSG = GetMSG("timeRem").Replace("{cmd}", this.config_data.Commands.CommandVoteKick);
            if (this.config_data.Messaging.DisplayCountEvery == -1) this.DisplayCountEveryVote = true;

            // Register our commands
            AddCovalenceCommand(this.config_data.Commands.CommandVoteKick, "cmdVoteKick");
            AddCovalenceCommand(this.config_data.Commands.CommandVoteKickCancel, "cmdVoteKickCancel");
            AddCovalenceCommand(this.config_data.Commands.CommandVoteKickForce, "cmdVoteKickForce");
        }

        void Unload()
        {
            if (this.VotingTimer != null) this.VotingTimer.Destroy();
            if (this.CountTimer != null) this.CountTimer.Destroy();
            if (this.CooldownTimer != null) this.CooldownTimer.Destroy();
        }

        void OnUserDisconnected(IPlayer player)
        {
            if (this.IsVoteOpen)
            {
                // If a vote is ongoing, check if one of the targets has left
                switch (this.vote_type)
                {
                    default:
                    case VoteType.Player:
                    {
                        if (this.target_players[0].Id == player.Id) this.VoteEnd(false, VoteEndReason.PlayerDisconnected);
                        break;
                    }
                    case VoteType.Team:
                    {
                        // Check if all targeted players disconnected
                        bool all_left = true;
                        foreach (IPlayer p in this.target_players)
                        {
                            if (player.Id == p.Id) continue;
                            if (!p.IsConnected) continue;
                            all_left = false;
                            break;
                        }
                        if (all_left) this.VoteEnd(false, VoteEndReason.PlayerDisconnected);
                        break;
                    }
                }
            }
        }

        object OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            if (!this.IsVoteOpen) return null;

            // If a vote against a whole team is undergoing, prevent players leaving that team
            if (this.vote_type == VoteType.Team)
            {
                foreach (IPlayer p in this.target_players)
                {
                    if (p.Id == player.IPlayer.Id)
                    {
                        player.IPlayer.Reply(GetMSG("CantLeaveTeam", player.IPlayer.Id));
                        return false;
                    }
                }
            }

            return null;
        }
        #endregion

        #region Functions
        /*
         * OpenVote
         *
         * Starts a new vote
         */
        private void OpenVote(IPlayer player, string playerNameOrId, VoteType _vote_type = VoteType.Player)
        {
            // Make sure cooldown is over
            if (!player.HasPermission("communityvigilance.admin") && this.CooldownTimer != null)
            {
                player.Reply(GetMSG("CooldownActive", player.Id).Replace("{secs}", this.CooldownTime.ToString()));
                return;
            }

            // Make sure server population is above configured minimum
            if (!player.HasPermission("communityvigilance.admin") && server.Players < this.config_data.Options.RequiredMinPlayers)
            {
                player.Reply(GetMSG("NotEnoughPlayers", player.Id).Replace("{minPlayers}", this.config_data.Options.RequiredMinPlayers.ToString()));
                return;
            }

            this.vote_type = _vote_type;

            // Find target player
            IPlayer target_player = this.FindPlayer(player, playerNameOrId);
            if (target_player == null) return;

            switch (this.vote_type)
            {
                default:
                case VoteType.Player:
                {
                    // Vote against a single player, add him
                    this.target_players.Add(target_player);
                    break;
                }
                case VoteType.Team:
                {
                    // Vote against a player and his team, add him and his team mates
                    this.target_players.Add(target_player);

                    // Get teammates of target player
                    RelationshipManager.PlayerTeam target_team = (target_player.Object as BasePlayer)?.Team;
                    if (target_team == null) break;

                    // Add teammates as targets
                    foreach (UInt64 member_id in target_team.members)
                    {
                        IPlayer team_member = players.FindPlayerById(member_id.ToString());
                        if (team_member == null)
                        {
                            Puts($"Unable to find teammate with id {member_id}");
                            continue;
                        }
                        if (team_member.Id == target_player.Id) continue;
                        this.target_players.Add(team_member);
                    }
                    break;
                }
            }

            // Make sure target players are of lower or same authlevel
            foreach (IPlayer p in this.target_players)
            {
                if (this.GetPlayerAuthlevel(p) > this.GetPlayerAuthlevel(player))
                {
                    this.target_players.Clear();
                    player.Reply(GetMSG("CantKickHigherAuthlevel", player.Id));
                    return;
                }
            }

            // Calculate required votes to pass
            var rVotes = (server.Players - this.target_players.Count) * this.config_data.Options.RequiredVotePercentage;
            if (rVotes < 1) rVotes = 1;
            this.RequiredVotes = Convert.ToInt32(rVotes);

            // Log votekick attempt
            switch (this.vote_type)
            {
                default:
                case VoteType.Player:
                {
                    Puts($"Player '{player.Name.Sanitize()}' ({player.Id}) initiated a votekick against '{this.target_players[0].Name.Sanitize()}' ({this.target_players[0].Id})");
                    break;
                }
                case VoteType.Team:
                {
                    Puts($"Player '{player.Name.Sanitize()}' ({player.Id}) initiated a votekick against '{this.target_players[0].Name.Sanitize()}' ({this.target_players[0].Id}) and his team");
                    break;
                }
            }

            // Opening a vote is considered as casting a vote too
            this.ReceivedVotes.Add(player.Id);
            if (this.RequiredVotes == 1)
            {
                // If only one vote is required, we're already done
                this.VoteEnd(true);
                return;
            }

            // Subscribe to required hooks
            Subscribe(nameof(OnUserDisconnected));
            if (this.vote_type == VoteType.Team) Subscribe(nameof(OnTeamLeave));

            // Broadcast a message to inform players about vote
            string msg;
            switch (this.vote_type)
            {
                default:
                case VoteType.Player:
                {
                    msg = GetMSG("VoteKickMsg");
                    break;
                }
                case VoteType.Team:
                {
                    msg = GetMSG("VoteKickTeamMsg");
                    break;
                }
            }
            msg = msg.Replace("{reqVote}", this.RequiredVotes.ToString()).Replace("{cmd}", this.config_data.Commands.CommandVoteKick).Replace("{player}", this.target_players[0].Name.Sanitize());
            server.Broadcast(msg);

            // Start vote
            this.IsVoteOpen = true;
            this.VoteTimer();
            if (!this.DisplayCountEveryVote) this.CountTimer = timer.In(this.config_data.Messaging.DisplayCountEvery, ShowCountTimer);
        }

        /*
         * VoteTimer
         *
         * Starts a voting timer
         */
        private void VoteTimer()
        {
            this.TimeRemaining = this.config_data.Timers.VoteOpenSecs;
            this.VotingTimer = timer.Repeat(1, this.TimeRemaining, () =>
            {
                this.TimeRemaining--;

                // Show message every full minute, then every 10 seconds
                if (this.TimeRemaining/60 > 0 && this.TimeRemaining%60 == 0)
                {
                    server.Broadcast(TimeRemMSG.Replace("{time}", (this.TimeRemaining/60).ToString()).Replace("{type}", GetMSG("Minutes")));
                }
                else if (this.TimeRemaining/60 == 0 && this.TimeRemaining/10 > 0 && this.TimeRemaining%10 == 0)
                {
                    server.Broadcast(TimeRemMSG.Replace("{time}", this.TimeRemaining.ToString()).Replace("{type}", GetMSG("Seconds")));
                }
                else if (this.TimeRemaining == 0)
                {
                    this.VoteEnd((this.ReceivedVotes.Count >= this.RequiredVotes));
                }
            });
        }

        /*
         * ShowCountTimer
         *
         * Broadcasts the current voting stats
         */
        private void ShowCountTimer()
        {
            string msg;
            switch (this.vote_type)
            {
                default:
                case VoteType.Player:
                {
                    msg = GetMSG("HaveVotedToKick");
                    break;
                }
                case VoteType.Team:
                {
                    msg = GetMSG("HaveVotedToKickTeam");
                    break;
                }
            }
            msg = msg.Replace("{recVotes}", this.ReceivedVotes.Count.ToString()).Replace("{reqVotes}", this.RequiredVotes.ToString()).Replace("{player}", this.target_players[0].Name.Sanitize());
            server.Broadcast(msg);

            // Restart timer
            this.CountTimer = timer.In(this.config_data.Messaging.DisplayCountEvery, ShowCountTimer);
        }

        /*
         * VoteEnd
         *
         * Ends a vote
         */
        private void VoteEnd(bool success, VoteEndReason reason = VoteEndReason.VoteEnded)
        {
            // Stop timers
            if (this.VotingTimer != null)
            {
                this.VotingTimer.Destroy();
                this.VotingTimer = null;
            }
            if (this.CountTimer != null)
            {
                this.CountTimer.Destroy();
                this.CountTimer = null;
            }

            // Unsubscribe from hooks we don't need anymore
            Unsubscribe(nameof(OnUserDisconnected));
            if (this.vote_type == VoteType.Team) Unsubscribe(nameof(OnTeamLeave));

            switch (reason)
            {
                default:
                case VoteEndReason.VoteEnded:
                case VoteEndReason.AdminForce:
                {
                    if (success)
                    {
                        string players_user = $"'{this.target_players[0].Name.Sanitize()}'";
                        string players_server = $"'{this.target_players[0].Name.Sanitize()}' ({this.target_players[0].Id})";
                        if (this.target_players.Count > 1)
                        {
                            for(int i=1; i<this.target_players.Count-1; i++)
                            {
                                players_user += $", '{this.target_players[i].Name.Sanitize()}'";
                                players_server += $", '{this.target_players[i].Name.Sanitize()}' ({this.target_players[i].Id})";
                            }
                            players_user += $" and '{this.target_players[this.target_players.Count-1].Name.Sanitize()}'";
                            players_server += $" and '{this.target_players[this.target_players.Count-1].Name.Sanitize()}' ({this.target_players[this.target_players.Count-1].Id})";
                        }
                        server.Broadcast(GetMSG("VoteKickSuccess").Replace("{players}", players_user));
                        Puts($"Votekick against {players_server} was successful ({this.ReceivedVotes.Count} player(s) voted in favor)");

                        foreach(IPlayer p in this.target_players)
                        {
#if !DEBUG
                            if (p.IsConnected) p.Kick(GetMSG("KickReason", p.Id));
#endif
                        }
                    }
                    else
                    {
                        switch (this.vote_type)
                        {
                            default:
                            case VoteType.Player:
                            {
                                server.Broadcast(GetMSG("VoteKickFailed").Replace("{player}", this.target_players[0].Name.Sanitize()));
                                Puts($"Votekick against '{this.target_players[0].Name.Sanitize()}' ({this.target_players[0].Id}) failed ({this.ReceivedVotes.Count} player(s) voted in favor, {this.RequiredVotes} votes were needed)");
                                break;
                            }
                            case VoteType.Team:
                            {
                                server.Broadcast(GetMSG("VoteKickTeamFailed").Replace("{player}", this.target_players[0].Name.Sanitize()));
                                Puts($"Votekick against '{this.target_players[0].Name.Sanitize()}' ({this.target_players[0].Id}) and his team failed ({this.ReceivedVotes.Count} player(s) voted in favor, {this.RequiredVotes} votes were needed)");
                                break;
                            }
                        }
                    }
                    break;
                }
                case VoteEndReason.PlayerDisconnected:
                {
                    switch (this.vote_type)
                    {
                        default:
                        case VoteType.Player:
                        {
                            server.Broadcast(GetMSG("VoteKickEndedDisconnected"));
                            Puts($"Votekick against '{this.target_players[0].Name.Sanitize()}' ({this.target_players[0].Id}) was cancelled. Player disconnected");
                            break;
                        }
                        case VoteType.Team:
                        {
                            server.Broadcast(GetMSG("VoteKickTeamEndedDisconnected"));
                            Puts($"Votekick against '{this.target_players[0].Name.Sanitize()}' ({this.target_players[0].Id}) and his team was cancelled. Players disconnected");
                            break;
                        }
                    }
                    break;
                }
                case VoteEndReason.AdminAbort:
                {
                    server.Broadcast(GetMSG("VoteWasAborted"));
                    break;
                }
            }

            // Start cooldown timer
            if (this.config_data.Timers.VoteCooldownSecs > 0)
            {
                if (this.CooldownTimer != null) this.CooldownTimer.Destroy();
                this.CooldownTime = this.config_data.Timers.VoteCooldownSecs;
                this.CooldownTimer = timer.Repeat(1, this.CooldownTime, () =>
                {
                    this.CooldownTime--;
                    if (this.CooldownTime == 0)
                    {
                        this.CooldownTimer.Destroy();
                        this.CooldownTimer = null;
                    }
                });
            }

            // Reset values
            this.IsVoteOpen = false;
            this.RequiredVotes = 0;
            this.ReceivedVotes.Clear();
            this.TimeRemaining = 0;
            this.target_players.Clear();
        }
        #endregion

        #region Helpers
        /*
         * AlreadyVoted
         *
         * Check if a player has already voted
         */
        private bool AlreadyVoted(string player) => this.ReceivedVotes.Contains(player);

        /*
         * FindPlayer
         *
         * Find a player base on steam id or name
         */
        private IPlayer FindPlayer(IPlayer player, string playerNameOrId)
        {
            IPlayer[] foundPlayers = players.FindPlayers(playerNameOrId).ToArray();
            if (foundPlayers.Length > 1)
            {
                player.Reply(GetMSG("MultiplePlayersFound", player.Id));
                return null;
            }

            if (foundPlayers.Length != 1)
            {
                player.Reply(GetMSG("NoPlayerFound", player.Id));
                return null;
            }

            return foundPlayers[0];
        }

        /*
         * GetPlayerAuthlevel
         *
         * Get a player's authlevel
         */
        private uint GetPlayerAuthlevel(IPlayer player) {
          BasePlayer base_player = player.Object as BasePlayer;
          return base_player.net.connection.authLevel;
        }

        /*
         * ColorizeText
         *
         * Replace color placeholders in messages
         */
        private string ColorizeText(string msg)
        {
            return msg.Replace("{MsgCol}", this.config_data.Messaging.MsgColor).Replace("{HilCol}", this.config_data.Messaging.MainColor).Replace("{ErrCol}", this.config_data.Messaging.ErrColor).Replace("{ColEnd}","</color>");
        }
        #endregion

        #region ChatCommands
        /*
         * cmdVoteKick
         *
         * Chat command to start / cast a vote
         */
        private void cmdVoteKick(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("communityvigilance.use")) return;
            else if ((player.HasPermission("communityvigilance.startvote") || player.HasPermission("communityvigilance.admin")) && args?.Length == 1)
            {
                // Start new vote on a single player
                if (this.IsVoteOpen)
                {
                    player.Reply(GetMSG("AlreadyVoteOpen", player.Id));
                    return;
                }

                // Start a vote
                this.OpenVote(player, args[0]);
            }
            else if ((player.HasPermission("communityvigilance.startvoteteam") || player.HasPermission("communityvigilance.admin")) && args?.Length == 2)
            {
                // Start a vote on a team
                if (this.IsVoteOpen)
                {
                    player.Reply(GetMSG("AlreadyVoteOpen", player.Id));
                    return;
                }

                switch (args[0])
                {
                    case "team":
                    {
                        this.OpenVote(player, args[1], VoteType.Team);
                        break;
                    }
                    default:
                    {
                        player.Reply(GetMSG("OpenVote", player.Id).Replace("{cmd}", this.config_data.Commands.CommandVoteKick));
                        return;
                    }
                }
            }
            else if (this.IsVoteOpen)
            {
                // Cast vote
                // Targeted players can't vote
                foreach(IPlayer p in this.target_players)
                {
                    if (player.Id == p.Id)
                    {
                        player.Reply(GetMSG("TargetedPlayerCantVote", player.Id));
                        return;
                    }
                }

                if (!this.AlreadyVoted(player.Id))
                {
                    // Add player to received votes
                    this.ReceivedVotes.Add(player.Id);

                    // Inform player about vote
                    switch (this.vote_type)
                    {
                        default:
                        case VoteType.Player:
                        {
                            player.Reply(GetMSG("YouHaveVoted", player.Id).Replace("{player}", this.target_players[0].Name.Sanitize()));
                            if (this.DisplayCountEveryVote)
                            {
                                server.Broadcast(GetMSG("HaveVotedToKick", player.Id).Replace("{recVotes}", this.ReceivedVotes.Count.ToString()).Replace("{reqVotes}", this.RequiredVotes.ToString()).Replace("{player}", this.target_players[0].Name.Sanitize()));
                            }
                            break;
                        }
                        case VoteType.Team:
                        {
                            player.Reply(GetMSG("YouHaveVotedTeam", player.Id).Replace("{player}", this.target_players[0].Name.Sanitize()));
                            if (this.DisplayCountEveryVote)
                            {
                                server.Broadcast(GetMSG("HaveVotedToKickTeam", player.Id).Replace("{recVotes}", this.ReceivedVotes.Count.ToString()).Replace("{reqVotes}", this.RequiredVotes.ToString()).Replace("{player}", this.target_players[0].Name.Sanitize()));
                            }
                            break;
                        }
                    }

                    // If we reached our vote goal, end vote immediately
                    if (this.ReceivedVotes.Count >= this.RequiredVotes) VoteEnd(true);

                    return;
                }
                else player.Reply(GetMSG("AlreadyVoted", player.Id));
            }
            else if (player.HasPermission("communityvigilance.startvote") || player.HasPermission("communityvigilance.startvoteteam") || player.HasPermission("communityvigilance.admin"))
            {
                player.Reply(GetMSG("NoOpenVoteButPermission", player.Id));
                player.Reply(GetMSG("OpenVote", player.Id).Replace("{cmd}", this.config_data.Commands.CommandVoteKick));
            }
            else player.Reply(GetMSG("NoOpenVoteAndNoPermission", player.Id));
        }

        /*
         * cmdVoteKickCancel
         *
         * Chat command to cancel a vote
         */
        private void cmdVoteKickCancel(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("communityvigilance.admin")) return;
            else if (!this.IsVoteOpen)
            {
                player.Reply(GetMSG("NoOpenVote", player.Id));
                return;
            }

            // Log cancel
            switch (this.vote_type)
            {
                default:
                case VoteType.Player:
                {
                    Puts($"Votekick against {this.target_players[0].Name.Sanitize()} ({this.target_players[0].Id}) was cancelled by '{player.Name.Sanitize()}' ({player.Id})");
                    break;
                }
                case VoteType.Team:
                {
                    Puts($"Votekick against {this.target_players[0].Name.Sanitize()} ({this.target_players[0].Id}) and his team was cancelled by '{player.Name.Sanitize()}' ({player.Id})");
                    break;
                }
            }

            this.VoteEnd(false, VoteEndReason.AdminAbort);
        }

        /*
         * cmdVoteKickForce
         *
         * Chat command to force a vote
         */
        private void cmdVoteKickForce(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("communityvigilance.admin")) return;
            else if (!this.IsVoteOpen)
            {
                player.Reply(GetMSG("NoOpenVote", player.Id));
                return;
            }

            // Log force
            switch (this.vote_type)
            {
                default:
                case VoteType.Player:
                {
                    Puts($"Votekick against {this.target_players[0].Name.Sanitize()} ({this.target_players[0].Id}) was forced by '{player.Name.Sanitize()}' ({player.Id})");
                    break;
                }
                case VoteType.Team:
                {
                    Puts($"Votekick against {this.target_players[0].Name.Sanitize()} ({this.target_players[0].Id}) and his team was forced by '{player.Name.Sanitize()}' ({player.Id})");
                    break;
                }
            }

            this.VoteEnd(true, VoteEndReason.AdminForce);
        }
        #endregion

        #region Config
        class Messaging
        {
            public int DisplayCountEvery { get; set; }
            public string MainColor { get; set; }
            public string MsgColor { get; set; }
            public string ErrColor { get; set; }
        }        
        class Timers
        {
            public int VoteOpenSecs { get; set; }
            public int VoteCooldownSecs { get; set; }
        }
        class Options
        {
            public float RequiredVotePercentage { get; set; }
            public int RequiredMinPlayers { get; set; }
        }
        class Commands
        {
            public string CommandVoteKick { get; set; }
            public string CommandVoteKickCancel { get; set; }
            public string CommandVoteKickForce { get; set; }
        }
        class ConfigData
        {
            public Messaging Messaging { get; set; }
            public Timers Timers { get; set; }
            public Options Options { get; set; }
            public Commands Commands { get; set; }
            public Oxide.Core.VersionNumber Version { get; set; }

        }
        private void LoadConfig()
        {
            ConfigData configdata = Config.ReadObject<ConfigData>();

            if (configdata.Version < Version)
            {
                this.config_data = this.UpdateConfig(configdata);
            }
            else
            {
                this.config_data = configdata;
            }
        }
        private ConfigData CreateNewConfig()
        {
            return new ConfigData
            {
                Messaging = new Messaging
                {
                    DisplayCountEvery = 30,
                    MsgColor = "<color=#939393>",
                    MainColor = "<color=orange>",
                    ErrColor = "<color=red>"
                },
                Options = new Options
                {
                    RequiredVotePercentage = 0.8f,
                    RequiredMinPlayers = 4
                },
                Timers = new Timers
                {
                    VoteOpenSecs = 240,
                    VoteCooldownSecs = 300
                },
                Commands = new Commands
                {
                    CommandVoteKick = "votekick",
                    CommandVoteKickCancel = "votekickcancel",
                    CommandVoteKickForce = "votekickforce"
                },
                Version = Version
            };
        }
        protected override void LoadDefaultConfig() => this.SaveConfig(this.CreateNewConfig());
        private ConfigData UpdateConfig(ConfigData old_config)
        {
            ConfigData new_config;
            bool config_changed = false;

            if (old_config.Version < new VersionNumber(0, 3, 0))
            {
                new_config = this.CreateNewConfig();
                new_config.Messaging.DisplayCountEvery = old_config.Messaging.DisplayCountEvery;
                new_config.Messaging.MsgColor = old_config.Messaging.MsgColor;
                new_config.Messaging.MainColor = old_config.Messaging.MainColor;
                new_config.Messaging.ErrColor = old_config.Messaging.ErrColor;
                new_config.Options.RequiredVotePercentage = old_config.Options.RequiredVotePercentage;
                new_config.Options.RequiredMinPlayers = old_config.Options.RequiredMinPlayers;
                new_config.Timers.VoteOpenSecs = old_config.Timers.VoteOpenSecs;
                new_config.Timers.VoteCooldownSecs = old_config.Timers.VoteCooldownSecs;
                new_config.Commands.CommandVoteKick = old_config.Commands.CommandVoteKick;
                new_config.Commands.CommandVoteKickCancel = old_config.Commands.CommandVoteKickCancel;
                config_changed = true;
            }
            else
            {
                new_config = old_config;
                new_config.Version = Version;
            }

            this.SaveConfig(new_config);
            if (config_changed) Puts("Configuration of CommunityVigilance was updated. Please check configuration file for changes!");

            return new_config;
        }
        void SaveConfig(ConfigData config) => Config.WriteObject(config, true);
        #endregion

        #region Messaging
        private string GetMSG(string key, string userid = null) => ColorizeText(lang.GetMessage(key, this, userid));
        Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            {"OpenVote", "Use {HilCol}/{cmd} [team] PlayerName|SteamID{ColEnd} {MsgCol}to open a new vote{ColEnd}" },
            {"NoOpenVote", "{ErrCol}There is currently no ongoing vote!{ColEnd}" },
            {"NoOpenVoteAndNoPermission", "{ErrCol}There is currently no ongoing vote and you don't have permission to initiate a new one!{ColEnd}" },
            {"NoOpenVoteButPermission", "{ErrCol}There is currently no ongoing vote!{ColEnd}" },
            {"YouHaveVoted", "{MsgCol}You have voted to kick '{player}'{ColEnd}" },
            {"YouHaveVotedTeam", "{MsgCol}You have voted to kick '{player}'{ColEnd}" },
            {"HaveVotedToKick", "{HilCol}{recVotes} / {reqVotes}{ColEnd} {MsgCol}players have voted to kick '{player}'{ColEnd}" },
            {"HaveVotedToKickTeam", "{HilCol}{recVotes} / {reqVotes}{ColEnd} {MsgCol}players have voted to kick '{player}' and his team{ColEnd}" },
            {"VoteKickSuccess", "{HilCol}Voting was successful, bye bye {players}.{ColEnd}" },
            {"VoteKickFailed", "{HilCol}Voting was unsuccessful, '{player}' remains in the game.{ColEnd}" },
            {"VoteKickTeamFailed", "{HilCol}Voting was unsuccessful, '{player}' and his team remains in the game.{ColEnd}" },
            {"Minutes", "Minute(s)" },
            {"Seconds", "Seconds" },
            {"VoteKickMsg", "{MsgCol}Type</color> {HilCol}/{cmd}</color> {MsgCol}now if you want to kick '{player}'. A total of {ColEnd}{HilCol}{reqVote}{ColEnd} {MsgCol}votes are needed.{ColEnd}" },
            {"VoteKickTeamMsg", "{MsgCol}Type</color> {HilCol}/{cmd}</color> {MsgCol}now if you want to kick '{player}' and his team. A total of {ColEnd}{HilCol}{reqVote}{ColEnd} {MsgCol}votes are needed.{ColEnd}" },
            {"timeRem", "{MsgCol}Voting ends in{ColEnd} {HilCol}{time} {type}{ColEnd}{MsgCol}, use {ColEnd}{HilCol}/{cmd}{ColEnd}{MsgCol} now to cast your vote{ColEnd}" },
            {"NoPlayerFound", "{ErrCol}No players found by that name / id!{ColEnd}" },
            {"MultiplePlayersFound", "{ErrCol}Given player identification string matches multiple players!{ColEnd}" },
            {"KickReason", "We're sorry but a majority of players wanted you to leave" },
            {"AlreadyVoteOpen", "{ErrCol}A vote is already ongoing!{ColEnd}" },
            {"AlreadyVoted", "{ErrCol}You have already voted!{ColEnd}" },
            {"TargetedPlayerCantVote", "{ErrCol}You are excluded from the current vote!{ColEnd}" },
            {"CantKickHigherAuthlevel", "{ErrCol}You are not allowed to kick higher-level players!{ColEnd}" },
            {"VoteKickEndedDisconnected", "{MsgCol}Previous vote was cancelled, player disconnected.{ColEnd}" },
            {"VoteKickTeamEndedDisconnected", "{MsgCol}Previous vote was cancelled, player and his team disconnected.{ColEnd}" },
            {"VoteWasAborted", "{MsgCol}Previous vote was cancelled by and admin.{ColEnd}" },
            {"NotEnoughPlayers", "{ErrCol}A minimum of {minPlayers} players are needed to be able to initiate a vote!{ColEnd}" },
            {"CooldownActive", "{ErrCol}You have to wait another {secs} second(s) before a new vote can be started!{ColEnd}" },
            {"CantLeaveTeam", "{MsgCol}A votekick against your team is ongoing! You can't leave your team until voting has ended.{ColdEnd}" }
        };
        #endregion
    }
}
