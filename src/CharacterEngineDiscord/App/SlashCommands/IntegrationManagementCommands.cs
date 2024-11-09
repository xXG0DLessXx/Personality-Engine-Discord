﻿using CharacterEngine.App.CustomAttributes;
using CharacterEngine.App.Exceptions;
using CharacterEngine.App.Helpers;
using CharacterEngine.App.Helpers.Discord;
using CharacterEngine.App.Static;
using CharacterEngine.App.Static.Entities;
using CharacterEngineDiscord.Models;
using CharacterEngineDiscord.Models.Abstractions;
using CharacterEngineDiscord.Models.Db.Discord;
using CharacterEngineDiscord.Models.Db.Integrations;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;

namespace CharacterEngine.App.SlashCommands;


[ValidateAccessLevel(AccessLevels.Manager)]
[ValidateChannelPermissions]
[Group("integration", "Integrations Management")]
public class IntegrationManagementCommands : InteractionModuleBase<InteractionContext>
{
    private readonly AppDbContext _db;


    public IntegrationManagementCommands(AppDbContext db)
    {
        _db = db;
    }


    [SlashCommand("create", "Create new integration for this server")]
    public async Task Create(IntegrationType type)
    {
        var customId = InteractionsHelper.NewCustomId(ModalActionType.CreateIntegration, $"{type:D}");
        var modalBuilder = new ModalBuilder().WithTitle($"Create {type:G} integration").WithCustomId(customId);

        var modal = type switch
        {
            IntegrationType.SakuraAI => modalBuilder.BuildSakuraAiAuthModal(),
            IntegrationType.CharacterAI => modalBuilder.BuildCaiAiAuthModal(),
        };

        await RespondWithModalAsync(modal); // next in EnsureSakuraAiLoginAsync()
    }


    [SlashCommand("re-login", "Re-login into the integration")]
    public async Task ReLogin(IntegrationType type)
    {
        await DeferAsync();

        var guildIntegration = await DatabaseHelper.GetGuildIntegrationAsync(Context.Guild.Id, type);
        if (guildIntegration is null)
        {
            throw new UserFriendlyException("Integration not found");
        }

        await (guildIntegration switch
        {
            SakuraAiGuildIntegration sakuraAiGuildIntegration => InteractionsHelper.SendSakuraAiMailAsync(Context.Interaction, sakuraAiGuildIntegration.SakuraEmail),
            CaiGuildIntegration caiGuildIntegration => InteractionsHelper.SendCharacterAiMailAsync(Context.Interaction, caiGuildIntegration.CaiEmail),
            _ => throw new ArgumentOutOfRangeException()
        });
    }


    [SlashCommand("copy", "Copy existing integration from another server")]
    public async Task Copy(string integrationId)
    {
        await DeferAsync();

        var guildIntegration = await DatabaseHelper.GetGuildIntegrationAsync(Guid.Parse(integrationId));
        if (guildIntegration is null)
        {
            throw new UserFriendlyException("Integration not found");
        }

        var originalIntegrationGuild = await _db.DiscordGuilds.FirstAsync(g => g.Id == guildIntegration.DiscordGuildId);

        var allowed = originalIntegrationGuild.OwnerId == Context.User.Id
                   || await _db.GuildBotManagers.Where(m => m.DiscordGuildId == originalIntegrationGuild.Id).AnyAsync(m => m.UserId == Context.User.Id);

        if (!allowed)
        {
            throw new UserFriendlyException("You're not allowed to copy integrations from this server");
        }

        var type = guildIntegration.GetIntegrationType();
        var existingIntegration = await DatabaseHelper.GetGuildIntegrationAsync(Context.Guild.Id, type);
        if (existingIntegration is not null)
        {
            throw new UserFriendlyException($"This server already has {type.GetIcon()}{type:G} integration");
        }

        var copyGuildIntegration = Activator.CreateInstance(guildIntegration.GetType());
        foreach (var prop in guildIntegration.GetType().GetProperties())
        {
            var propValue = prop.GetValue(guildIntegration, null);
            prop.SetValue(copyGuildIntegration, propValue, null);
        }

        var castedGuildIntegration = (IGuildIntegration)copyGuildIntegration!;
        castedGuildIntegration.Id = Guid.NewGuid();
        castedGuildIntegration.DiscordGuildId = Context.Guild.Id;

        switch (castedGuildIntegration)
        {
            case SakuraAiGuildIntegration sakuraAiGuildIntegration:
            {
                await _db.SakuraAiIntegrations.AddAsync(sakuraAiGuildIntegration);
                break;
            }
            case CaiGuildIntegration caiGuildIntegration:
            {
                await _db.CaiIntegrations.AddAsync(caiGuildIntegration);
                break;
            }
            default:
            {
                throw new ArgumentException($"Unknown integration type: {copyGuildIntegration?.GetType()}");
            }
        }

        await _db.SaveChangesAsync();

        var msg = $"**{type.GetIcon()} {type:G}** integration was copied successfully | New integration ID: **`{castedGuildIntegration.Id}`**";

        await FollowupAsync(embed: msg.ToInlineEmbed(type.GetColor(), bold: false));
    }


    [SlashCommand("confirm", "Confirm intergration")]
    public async Task Confirm(IntegrationType type, string data)
    {
        await DeferAsync(ephemeral: true);

        string message = null!;
        string? thumbnailUrl = null;

        switch (type)
        {
            case IntegrationType.CharacterAI:
            {
                var caiUser = await MemoryStorage.IntegrationModules.CaiModule.LoginByLinkAsync(data);

                var newCaiIntergration = new CaiGuildIntegration
                {
                    CaiAuthToken = caiUser.Token,
                    CaiUserId = caiUser.UserId,
                    CaiUsername = caiUser.Username,
                    DiscordGuildId = Context.Guild.Id,
                    CreatedAt = DateTime.Now,
                    CaiEmail = caiUser.UserEmail
                };

                await _db.CaiIntegrations.AddAsync(newCaiIntergration);
                await _db.SaveChangesAsync();

                message = $"Username: **{caiUser.Username}**\n" +
                          "From now on, this account will be used for all CharacterAI interactions on this server.\n" +
                          "For the next step, use *`/character spawn`* command to spawn new CharacterAI character in this channel.";

                thumbnailUrl = caiUser.UserImageUrl;

                break;
            }
            default:
            {
                throw new UserFriendlyException($"This command is not intended to be used for {type:G} integrations");
            }
        }

        var embed = new EmbedBuilder().WithTitle($"{type.GetIcon()} {type:G} user authorized")
                   .WithDescription(message)
                   .WithColor(IntegrationType.CharacterAI.GetColor())
                   .WithThumbnailUrl(thumbnailUrl);

        await FollowupAsync(ephemeral: true, embed: $"{MessagesTemplates.OK_SIGN_DISCORD} OK".ToInlineEmbed(Color.Green));
        await Context.Channel.SendMessageAsync(Context.User.Mention, embed: embed.Build());
    }


    [SlashCommand("remove", "Remove integration from this server")]
    public async Task Remove(IntegrationType type, bool removeAssociatedCharacters)
    {
        await DeferAsync();

        IGuildIntegration? integration = (type switch
        {
            IntegrationType.SakuraAI => await _db.SakuraAiIntegrations.FirstOrDefaultAsync(i => i.DiscordGuildId == Context.Guild.Id),
            IntegrationType.CharacterAI => await _db.CaiIntegrations.FirstOrDefaultAsync(i => i.DiscordGuildId == Context.Guild.Id),
        });

        if (integration is null)
        {
             await FollowupAsync(embed: $"There's no {type:G} integration on this server".ToInlineEmbed(Color.Orange));
             return;
        }

        if (removeAssociatedCharacters)
        {
            var channels = await _db.DiscordChannels.Where(c => c.DiscordGuildId == Context.Guild.Id).ToListAsync();

            var charactersInChannels = channels.Select(TargetedCharacters).SelectMany(character => character);

            foreach (var character in charactersInChannels)
            {
                try
                {
                    var webhookId = ulong.Parse(character.WebhookId);
                    var webhookClient = MemoryStorage.CachedWebhookClients.Find(webhookId);
                    if (webhookClient is not null)
                    {
                        await webhookClient.DeleteWebhookAsync();
                        MemoryStorage.CachedWebhookClients.Remove(webhookId);
                    }

                    MemoryStorage.CachedCharacters.Remove(character.Id);
                }
                catch (Exception e) // TODO: handle
                {
                    //
                }
            }

            IEnumerable<CachedCharacterInfo> TargetedCharacters(DiscordChannel channel)
                => MemoryStorage.CachedCharacters.ToList(channel.Id).Where(c => c.IntegrationType == type);
        }

        await DatabaseHelper.DeleteGuildIntegrationAsync(integration, removeAssociatedCharacters);


        await FollowupAsync(embed: $"{type.GetIcon()} {type:G} integration {(removeAssociatedCharacters ? "and all associated characters were" : "was")} successfully removed".ToInlineEmbed(Color.Green));
    }
}
