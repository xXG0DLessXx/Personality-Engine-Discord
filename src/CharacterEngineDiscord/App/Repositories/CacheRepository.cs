using System.Collections.Concurrent;
using CharacterEngine.App.Repositories.Abstractions;
using CharacterEngine.App.Repositories.Storages;
using CharacterEngineDiscord.Domain.Models.Db.Discord;
using CharacterEngineDiscord.Models;
using Discord;
using Discord.WebSocket;

namespace CharacterEngine.App.Repositories;


public class CacheRepository : RepositoryBase
{
    /// <summary>
    /// ChannelId : NoWarn
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, bool> _cachedChannels = [];
    public static bool GetCachedChannelNoWarnState(ulong channelId)
        => _cachedChannels.TryGetValue(channelId, out var noWarn) && noWarn;


    private static readonly ConcurrentDictionary<ulong, object?> _cachedGuilds = [];

    private static readonly ConcurrentDictionary<ulong, object?> _cachedUsers = [];


    public CachedCharacerInfoStorage CachedCharacters { get; } = new();

    public CachedWebhookClientsStorage CachedWebhookClients { get; } = new();

    public ActiveSearchQueriesStorage ActiveSearchQueries { get; } = new();

    private readonly SemaphoreSlim _dbCallsSemaphore = new(1, 1);

    public CacheRepository(AppDbContext db) : base(db) { }


    public void CacheUser(ulong userId)
    {
        _cachedUsers.TryAdd(userId, null);
    }

    public Task EnsureUserCached(IGuildUser guildUser)
    {
        if (!_cachedUsers.TryAdd(guildUser.Id, null))
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            await EnsureGuildCached(guildUser.Guild);

            await _dbCallsSemaphore.WaitAsync();
            try
            {


                DB.DiscordUsers.Add(new DiscordUser
                {
                    Id = guildUser.Id
                });

                await DB.SaveChangesAsync();
            }
            finally
            {
                _dbCallsSemaphore.Release();
            }
        });
    }

    public Task EnsureChannelCached(IGuildChannel channel)
    {
        if (!_cachedChannels.TryAdd(channel.Id, false))
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            await EnsureGuildCached(channel.Guild);

            await _dbCallsSemaphore.WaitAsync();
            try
            {
                var discordChannel = await DB.DiscordChannels.FindAsync(channel.Id);
                if (discordChannel is null)
                {
                    var newChannel = new DiscordChannel
                    {
                        Id = channel.Id,
                        ChannelName = channel.Name,
                        DiscordGuildId = channel.GuildId,
                        NoWarn = false
                    };

                    DB.DiscordChannels.Add(newChannel);
                    await DB.SaveChangesAsync();
                }
                else
                {
                    _cachedChannels[channel.Id] = discordChannel.NoWarn;

                    if (discordChannel.ChannelName != channel.Name)
                    {
                        discordChannel.ChannelName = channel.Name;
                        await DB.SaveChangesAsync();
                    }
                }
            }
            finally
            {
                _dbCallsSemaphore.Release();
            }
        });
    }


    public Task EnsureGuildCached(IGuild guild)
    {
        if (!_cachedGuilds.TryAdd(guild.Id, null))
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            await _dbCallsSemaphore.WaitAsync();
            try
            {
                var discordGuild = await DB.DiscordGuilds.FindAsync(guild.Id);

                string? ownerUsername = null;
                int? memberCount = null;

                if (guild is SocketGuild socketGuild)
                {
                    ownerUsername = socketGuild.Owner?.Username;
                    memberCount = socketGuild.MemberCount;
                }

                ownerUsername ??= (await guild.GetOwnerAsync())?.Username;
                memberCount ??= guild.ApproximateMemberCount;

                if (discordGuild is null)
                {
                    var newGuild = new DiscordGuild
                    {
                        Id = guild.Id,
                        GuildName = guild.Name,
                        MessagesSent = 0,
                        NoWarn = false,
                        OwnerId = guild.OwnerId,
                        OwnerUsername = ownerUsername,
                        MemberCount = memberCount ?? 0,
                        Joined = true,
                        FirstJoinDate = DateTime.Now
                    };

                    DB.DiscordGuilds.Add(newGuild);
                }
                else
                {
                    discordGuild.Joined = true;
                    discordGuild.GuildName = guild.Name ?? "";
                    discordGuild.OwnerId = guild.OwnerId;
                    discordGuild.OwnerUsername = ownerUsername;
                    discordGuild.MemberCount = memberCount ?? 0;
                }

                await DB.SaveChangesAsync();
            }
            finally
            {
                _dbCallsSemaphore.Release();
            }
        });
    }
}
