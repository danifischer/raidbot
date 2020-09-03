﻿using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using static Raidbot.Raid;

namespace Raidbot.Services
{
    public class RaidService
    {
        private static readonly string _jsonFile = Path.Combine(Constants.SAVEFOLDER, "plannedRaids.json");
        private static Dictionary<string, Raid> Raids { get; set; }

        private readonly UserService _userService;

        private readonly ConversationService _conversationService;

        private static readonly Random _random = new Random();

        public RaidService(UserService userService, ConversationService conversationService)
        {
            Raids = new Dictionary<string, Raid>();
            LoadRaids();
            _userService = userService;
            _conversationService = conversationService;
        }

        public IEnumerable<Raid> ListRaids()
        {
            return Raids.Values;
        }

        public void AddRaid(Raid raid, ulong guildId, ulong channelId, ulong messageId)
        {
            raid.GuildId = guildId;
            raid.ChannelId = channelId;
            raid.MessageId = messageId;
            Raids.Add(raid.RaidId, raid);
            SaveRaids();
        }

        public async Task<bool> RemoveRaid(string raidId, SocketGuild guild)
        {
            if (TryFindRaid(raidId, out Raid raid) && raid.GuildId.Equals(guild.Id))
            {
                IUserMessage userMessage = (IUserMessage)await guild.GetTextChannel(raid.ChannelId).GetMessageAsync(raid.MessageId);
                await userMessage.DeleteAsync();
                Raids.Remove(raidId);
                SaveRaids();
                return true;
            }
            return false;
        }

        public void SaveRaids()
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(Raids);
            File.WriteAllText(_jsonFile, json);
        }

        private void LoadRaids()
        {
            if (File.Exists(_jsonFile))
            {
                string json = File.ReadAllText(_jsonFile);
                Raids = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, Raid>>(json);
            }
        }

        public void RemoveUserFromAllRaids(IUser user)
        {
            foreach (var raid in Raids.Values)
            {
                RemoveUser(raid.RaidId, user.Id);
            }
        }

        public bool TryFindRaid(string raidId, out Raid raid)
        {
            if (Raids.ContainsKey(raidId))
            {
                raid = Raids[raidId];
                return true;
            }
            raid = null;
            return false;
        }

        public bool TryFindRaid(ulong GuildId, ulong ChannelId, ulong MessageId, out Raid raid)
        {
            foreach (var r in Raids)
            {
                if (r.Value.GuildId.Equals(GuildId) && r.Value.ChannelId.Equals(ChannelId) && r.Value.MessageId.Equals(MessageId))
                {
                    raid = r.Value;
                    return true;
                }
            }
            raid = null;
            return false;
        }

        public bool AddUser(string raidId, string userName, string role, Availability availability, out string resultMessage)
        {
            if (!Raids.ContainsKey(raidId))
            {
                resultMessage = "raid not found";
                return false;
            }
            Raid raid = Raids[raidId];
            User raidUser = new User(role, availability, userName, userName, raid.GetFreeUserId());
            return AddUser(raidId, raidUser, role, availability, out resultMessage);
        }

        public bool AddUser(string raidId, IGuildUser user, string role, Availability availability, string usedAccount, out string resultMessage)
        {
            string nickname = _userService.GetUser(user.GuildId, user.Id).Name;
            if (string.IsNullOrEmpty(nickname))
            {
                nickname = user.Nickname ?? user.Username;
            }
            User raidUser = new User(role, availability, nickname, usedAccount, user.Id);
            return AddUser(raidId, raidUser, role, availability, out resultMessage);
        }

        private bool AddUser(string raidId, User user, string role, Availability availability, out string resultMessage)
        {
            if (!Raids.ContainsKey(raidId))
            {
                resultMessage = "raid not found";
                return false;
            }
            Raid raid = Raids[raidId];
            if (!raid.CheckRoleAvailability(user.DiscordId, role, availability, out resultMessage))
            {
                return false;
            }

            if (availability.Equals(Availability.Flex))
            {
                raid.FlexRoles.Add(user);
            }
            else
            {
                raid.Users.Add(user.DiscordId, user);
            }

            resultMessage = "Added to raid roster";
            return true;
        }

        public string RemoveUser(string raidId, string username)
        {
            if (Raids.ContainsKey(raidId))
            {
                Raid raid = Raids[raidId];
                foreach (User user in raid.Users.Values)
                {
                    if (user.Nickname == username && user.DiscordId < 256)
                    {
                        return RemoveUser(raid.RaidId, user.DiscordId);
                    }
                }
                return "user not found";
            }
            else
            {
                return "raid not found";
            }
        }

        public string RemoveUser(string raidId, ulong userId)
        {
            if (Raids.ContainsKey(raidId))
            {
                Raid raid = Raids[raidId];
                string message = "user not found";
                if (raid.Users.ContainsKey(userId))
                {
                    string name = raid.Users[userId].Nickname;
                    raid.Users.Remove(userId);
                    message = $"Successfully removed {name} from raid {raid.MessageId}";
                }
                raid.FlexRoles.RemoveAll(flexRole => flexRole.DiscordId.Equals(userId));
                return message;
            }
            else
            {
                return "raid not found";
            }
        }

        public async Task HandleReaction(SocketReaction reaction, ulong guildId, string raidId)
        {
            if (!Raids.ContainsKey(raidId))
            {
                return;
            }

            Raid raid = Raids[raidId];
            IUserMessage userMessage = (IUserMessage)await reaction.Channel.GetMessageAsync(raid.MessageId);
            IEmote emote = reaction.Emote;

            if (emote.Equals(Constants.SignOffEmoji))
            {
                RemoveUser(raid.RaidId, reaction.User.Value.Id);
                SaveRaids();
                await userMessage.ModifyAsync(msg => msg.Embed = raid.CreateRaidMessage());
                await userMessage.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
                return;
            }

            ulong userId = reaction.User.Value.Id;
            if (_userService.GetAccounts(guildId, userId, raid.AccountType).Count == 0)
            {
                await UserExtensions.SendMessageAsync(reaction.User.Value, $"No Account found, please add an Account with \"!user add {raid.AccountType} <AccountName>\".\n" +
                    "\n**This command only works on a server.**");
                return;
            }

            if (emote.Equals(Constants.FlexEmoji))
            {
                if (!_conversationService.UserHasConversation(reaction.User.Value.Id))
                {
                    _conversationService.OpenSignUpConversation(this, reaction, raid, Availability.Flex);
                }
            }
            else if (raid.Users.ContainsKey(userId))
            {
                if (emote.Equals(Constants.SignOnEmoji))
                {
                    if (raid.IsAvailabilityChangeAllowed(userId, Raid.Availability.Yes))
                    {
                        raid.Users[userId].Availability = Raid.Availability.Yes;
                    }
                }
                else if (emote.Equals(Constants.UnsureEmoji))
                {
                    raid.Users[userId].Availability = Raid.Availability.Maybe;
                }
                else if (emote.Equals(Constants.BackupEmoji))
                {
                    raid.Users[userId].Availability = Raid.Availability.Backup;
                }
            }
            else if (!_conversationService.UserHasConversation(reaction.User.Value.Id))
            {
                if (emote.Equals(Constants.SignOnEmoji))
                {
                    _conversationService.OpenSignUpConversation(this, reaction, raid, Availability.Yes);
                }
                else if (emote.Equals(Constants.UnsureEmoji))
                {
                    _conversationService.OpenSignUpConversation(this, reaction, raid, Availability.Maybe);
                }
                else if (emote.Equals(Constants.BackupEmoji))
                {
                    _conversationService.OpenSignUpConversation(this, reaction, raid, Availability.Backup);
                }
            }
            SaveRaids();
            await userMessage.ModifyAsync(msg => msg.Embed = raid.CreateRaidMessage());
            await userMessage.RemoveReactionAsync(reaction.Emote, reaction.User.Value);
        }

        public string CreateRaidId()
        {
            string raidId = _random.Next().ToString();
            if (Raids.ContainsKey(raidId))
            {
                return CreateRaidId();
            }
            return raidId;
        }

        public bool UpdateRaid(string raidId, Raid raid)
        {
            if (Raids.ContainsKey(raidId))
            {
                Raids[raidId] = raid;
                SaveRaids();
                return true;
            }
            return false;
        }
    }
}