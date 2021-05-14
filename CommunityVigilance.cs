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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Community Vigilance", "PinguinNordpol", "0.2.2")]
    [Description("Adds the possibility to start votes to kick players from the server")]
    class CommunityVigilance : CovalencePlugin
    {
        #region Fields
        private List<string> ReceivedVotes = new List<string>();

        private bool IsVoteOpen = false;
        private IPlayer TargetPlayer = null;
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
            AdminAbort = 2
        }
        #endregion

        #region Oxide Hooks
        void Init()
        {
            // Register our permissions
            permission.RegisterPermission("communityvigilance.use", this);
            permission.RegisterPermission("communityvigilance.startvote", this);
            permission.RegisterPermission("communityvigilance.admin", this);
        }

        void Loaded() => lang.RegisterMessages(Messages, this);

        void OnServerInitialized()
        {
            LoadVariables();
            this.TimeRemMSG = GetMSG("timeRem").Replace("{cmd}", this.configData.Commands.CommandVoteKick);
            if (this.configData.Messaging.DisplayCountEvery == -1) this.DisplayCountEveryVote = true;

            // Register our commands
            AddCovalenceCommand(this.configData.Commands.CommandVoteKick, "cmdVoteKick");
            AddCovalenceCommand(this.configData.Commands.CommandVoteKickCancel, "cmdVoteKickCancel");
        }

        void Unload()
        {
            if (this.VotingTimer != null) this.VotingTimer.Destroy();
            if (this.CountTimer != null) this.CountTimer.Destroy();
            if (this.CooldownTimer != null) this.CooldownTimer.Destroy();
        }

        void OnUserDisconnected(IPlayer player)
        {
            if (this.IsVoteOpen && this.TargetPlayer.Id == player.Id)
            {
                VoteEnd(false, VoteEndReason.PlayerDisconnected);
            }
        }
        #endregion

        #region Functions
        /*
         * OpenVote
         *
         * Starts a new vote
         */
        private void OpenVote(IPlayer player, string playerNameOrId)
        {
            // Make sure cooldown is over
            if (!player.HasPermission("communityvigilance.admin") && this.CooldownTimer != null)
            {
                player.Reply(GetMSG("CooldownActive", player.Id).Replace("{secs}", this.CooldownTime.ToString()));
                return;
            }

            // Find target player
            this.TargetPlayer = FindPlayer(player, playerNameOrId);
            if (this.TargetPlayer == null) return;

            // Make sure target player is of lower or same authlevel
            if (GetPlayerAuthlevel(this.TargetPlayer) > GetPlayerAuthlevel(player))
            {
                player.Reply(GetMSG("CantKickHigherAuthlevel", player.Id));
                return;
            }

            // Make sure server population is above configured minimum
            if (!player.HasPermission("communityvigilance.admin") && server.Players < this.configData.Options.RequiredMinPlayers)
            {
                player.Reply(GetMSG("NotEnoughPlayers", player.Id).Replace("{minPlayers}", this.configData.Options.RequiredMinPlayers.ToString()));
                return;
            }

            // Calculate required votes to pass
            var rVotes = (server.Players - 1) * this.configData.Options.RequiredVotePercentage;
            if (rVotes < 1) rVotes = 1;
            this.RequiredVotes = Convert.ToInt32(rVotes);

            // Log votekick attempt
            Puts($"Player '{player.Name.Sanitize()}' ({player.Id}) initiated a votekick against '{this.TargetPlayer.Name.Sanitize()}' ({this.TargetPlayer.Id})");

            // Opening a vote is considered as casting a vote too
            this.ReceivedVotes.Add(player.Id);
            if (this.RequiredVotes == 1)
            {
                // If only one vote is required, we're already done
                VoteEnd(true);
                return;
            }

            // Start vote
            this.IsVoteOpen = true;
            var msg = GetMSG("voteMSG").Replace("{reqVote}", this.RequiredVotes.ToString()).Replace("{cmd}", this.configData.Commands.CommandVoteKick).Replace("{player}", this.TargetPlayer.Name.Sanitize());
            server.Broadcast(msg);
            VoteTimer();
            if (!this.DisplayCountEveryVote) this.CountTimer = timer.In(this.configData.Messaging.DisplayCountEvery, ShowCountTimer);
        }

        /*
         * VoteTimer
         *
         * Starts a voting timer
         */
        private void VoteTimer()
        {
            this.TimeRemaining = this.configData.Timers.VoteOpenSecs;
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
                    VoteEnd((this.ReceivedVotes.Count >= this.RequiredVotes));
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
            server.Broadcast(GetMSG("HaveVotedToKick").Replace("{recVotes}", this.ReceivedVotes.Count.ToString()).Replace("{reqVotes}", this.RequiredVotes.ToString()).Replace("{player}", this.TargetPlayer.Name.Sanitize()));
            this.CountTimer = timer.In(this.configData.Messaging.DisplayCountEvery, ShowCountTimer);
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

            switch(reason)
            {
                default:
                case VoteEndReason.VoteEnded:
                {
                    if (success)
                    {
                        server.Broadcast(GetMSG("VoteSuccess").Replace("{player}", this.TargetPlayer.Name.Sanitize()));
                        Puts($"Votekick against '{this.TargetPlayer.Name.Sanitize()}' ({this.TargetPlayer.Id}) was successful ({this.ReceivedVotes.Count} player(s) voted in favor)");
                        this.TargetPlayer.Kick(GetMSG("KickReason", TargetPlayer.Id));
                    }
                    else
                    {
                        server.Broadcast(GetMSG("VoteFailed").Replace("{player}", this.TargetPlayer.Name.Sanitize()));
                        Puts($"Votekick against '{this.TargetPlayer.Name.Sanitize()}' ({this.TargetPlayer.Id}) failed ({this.ReceivedVotes.Count} player(s) voted in favor, {this.RequiredVotes} votes were needed)");
                    }
                    break;
                }
                case VoteEndReason.PlayerDisconnected:
                {
                    server.Broadcast(GetMSG("VoteEndedDisconnected"));
                    Puts($"Votekick against '{this.TargetPlayer.Name.Sanitize()}' ({this.TargetPlayer.Id}) was cancelled. Player disconnected");
                    break;
                }
                case VoteEndReason.AdminAbort:
                {
                    server.Broadcast(GetMSG("VoteWasAborted"));
                    break;
                }
            }

            // Reset values
            this.IsVoteOpen = false;
            this.RequiredVotes = 0;
            this.ReceivedVotes.Clear();
            this.TimeRemaining = 0;
            this.TargetPlayer = null;

            // Start cooldown timer
            if (this.configData.Timers.VoteCooldownSecs > 0)
            {
                if (this.CooldownTimer != null) this.CooldownTimer.Destroy();
                this.CooldownTime = this.configData.Timers.VoteCooldownSecs;
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
            return msg.Replace("{MsgCol}", this.configData.Messaging.MsgColor).Replace("{HilCol}", this.configData.Messaging.MainColor).Replace("{ErrCol}", this.configData.Messaging.ErrColor).Replace("{ColEnd}","</color>");
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
            else if ((player.HasPermission("communityvigilance.startvote") || player.HasPermission("communityvigilance.admin")) && args != null && args.Length > 0)
            {
                // Start new vote
                if (args.Length != 1)
                {
                    player.Reply(GetMSG("OpenVote", player.Id).Replace("{cmd}", this.configData.Commands.CommandVoteKick));
                    return;
                }
                if (this.IsVoteOpen)
                {
                    player.Reply(GetMSG("AlreadyVoteOpen", player.Id));
                    return;
                }
                OpenVote(player, args[0]);
            }
            else if (this.IsVoteOpen)
            {
                // Cast vote
                if (this.TargetPlayer.Id == player.Id)
                {
                    player.Reply(GetMSG("TargetedPlayerCantVote", player.Id));
                    return;
                }
                else if (!this.AlreadyVoted(player.Id))
                {
                    this.ReceivedVotes.Add(player.Id);
                    player.Reply(GetMSG("YouHaveVoted", player.Id).Replace("{player}", this.TargetPlayer.Name.Sanitize()));
                    if (this.DisplayCountEveryVote)
                        server.Broadcast(GetMSG("HaveVotedToKick", player.Id).Replace("{recVotes}", this.ReceivedVotes.Count.ToString()).Replace("{reqVotes}", this.RequiredVotes.ToString()).Replace("{player}", this.TargetPlayer.Name.Sanitize()));
                    if (this.ReceivedVotes.Count >= this.RequiredVotes)
                        VoteEnd(true);
                    return;
                }
                else player.Reply(GetMSG("AlreadyVoted", player.Id));
            }
            else if (player.HasPermission("communityvigilance.startvote") || player.HasPermission("communityvigilance.admin"))
            {
                player.Reply(GetMSG("NoOpenVoteButPermission", player.Id).Replace("{cmd}", this.configData.Commands.CommandVoteKick));
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
            Puts($"Votekick against {this.TargetPlayer.Name.Sanitize()} ({this.TargetPlayer.Id}) was cancelled by '{player.Name.Sanitize()}' ({player.Id})");
            VoteEnd(false, VoteEndReason.AdminAbort);
        }
        #endregion

        #region Config
        private ConfigData configData;
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
        }
        class ConfigData
        {
            public Messaging Messaging { get; set; }
            public Timers Timers { get; set; }
            public Options Options { get; set; }
            public Commands Commands { get; set; }

        }
        private void LoadVariables()
        {
            LoadConfigVariables();
            SaveConfig();
        }
        protected override void LoadDefaultConfig()
        {
            var config = new ConfigData
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
                    CommandVoteKickCancel = "votekickcancel"
                }
            };
            SaveConfig(config);
        }
        private void LoadConfigVariables() => this.configData = Config.ReadObject<ConfigData>();
        void SaveConfig(ConfigData config) => Config.WriteObject(config, true);
        #endregion

        #region Messaging
        private string GetMSG(string key, string userid = null) => ColorizeText(lang.GetMessage(key, this, userid));
        Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            {"OpenVote", "Use {HilCol}/{cmd} PlayerName|SteamID{ColEnd} {MsgCol}to open a new vote{ColEnd}" },
            {"NoOpenVote", "{ErrCol}There is currently no ongoing vote!{ColEnd}" },
            {"NoOpenVoteAndNoPermission", "{ErrCol}There is currently no ongoing vote and you don't have permission to initiate a new one!{ColEnd}" },
            {"NoOpenVoteButPermission", "{ErrCol}There is currently no ongoing vote!{ColEnd}\n{MsgCol}To start a new vote use {ColEnd}{HilCol}/{cmd} PlayerName|SteamID{ColEnd}" },
            {"YouHaveVoted", "{MsgCol}You have voted to kick '{player}'{ColEnd}" },
            {"HaveVotedToKick", "{HilCol}{recVotes} / {reqVotes}{ColEnd} {MsgCol}players have voted to kick '{player}'{ColEnd}" },
            {"VoteSuccess", "{HilCol}Voting was successful, bye bye '{player}'.{ColEnd}" },
            {"VoteFailed", "{HilCol}Voting was unsuccessful, '{player}' remains in the game.{ColEnd}" },
            {"Minutes", "Minute(s)" },
            {"Seconds", "Seconds" },
            {"voteMSG", "{MsgCol}Type</color> {HilCol}/{cmd}</color> {MsgCol}now if you want to kick '{player}'. A total of {ColEnd}{HilCol}{reqVote}{ColEnd} {MsgCol}votes are needed.{ColEnd}" },
            {"timeRem", "{MsgCol}Voting ends in{ColEnd} {HilCol}{time} {type}{ColEnd}{MsgCol}, use {ColEnd}{HilCol}/{cmd}{ColEnd}{MsgCol} now to cast your vote{ColEnd}" },
            {"NoPlayerFound", "{ErrCol}No players found by that name / id!{ColEnd}" },
            {"MultiplePlayersFound", "{ErrCol}Given player identification string matches multiple players!{ColEnd}" },
            {"KickReason", "We're sorry but a majority of players wanted you to leave" },
            {"AlreadyVoteOpen", "{ErrCol}A vote is already ongoing!{ColEnd}" },
            {"AlreadyVoted", "{ErrCol}You have already voted!{ColEnd}" },
            {"TargetedPlayerCantVote", "{ErrCol}You are excluded from the current vote!{ColEnd}" },
            {"CantKickHigherAuthlevel", "{ErrCol}You are not allowed to kick higher-level players!{ColEnd}" },
            {"VoteEndedDisconnected", "{MsgCol}Previous vote was cancelled, player disconnected.{ColEnd}" },
            {"VoteWasAborted", "{MsgCol}Previous vote was cancelled by and admin.{ColEnd}" },
            {"NotEnoughPlayers", "{ErrCol}A minimum of {minPlayers} players are needed to be able to initiate a vote!{ColEnd}" },
            {"CooldownActive", "{ErrCol}You have to wait another {secs} second(s) before a new vote can be started!{ColEnd}" }
        };
        #endregion
    }
}
