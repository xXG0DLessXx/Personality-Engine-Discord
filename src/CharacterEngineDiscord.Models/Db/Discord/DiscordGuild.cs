﻿namespace CharacterEngineDiscord.Db.Models.Db.Discord;


public class DiscordGuild
{
    public required ulong Id { get; set; }

    public required string GuildName { get; set; }

    public required ulong OwnerId { get; set; }

    public required string OwnerUsername { get; set; }

    public required uint MessagesSent { get; set; }

    public required DateTime FirstJoinDate { get; set; }
}