﻿using CharacterEngineDiscord.Services;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using static CharacterEngineDiscord.Services.CommonService;
using static CharacterEngineDiscord.Services.IntegrationsService;
using static CharacterEngineDiscord.Services.CommandsService;
using static CharacterEngineDiscord.Services.StorageContext;
using CharacterEngineDiscord.Models.Common;
using CharacterEngineDiscord.Models.CharacterHub;
using Discord.Webhook;

namespace CharacterEngineDiscord.Handlers.SlashCommands
{
    [RequireManagerAccess]
    [Group("spawn", "Spawn new character")]
    public class SpawnCharacterCommands : InteractionModuleBase<InteractionContext>
    {
        private readonly IntegrationsService _integration;
        private readonly StorageContext _db;
        
        public SpawnCharacterCommands(IServiceProvider services)
        {
            _integration = services.GetRequiredService<IntegrationsService>();
            _db = services.GetRequiredService<StorageContext>();
        }

        public enum ApiType
        {
            OpenAI
        }

        [SlashCommand("cai-character", "Add new character from CharacterAI to this channel")]
        public async Task SpawnCaiCharacter([Summary(description: "When specify a character ID, set 'set-with-id' parameter to 'True'")] string searchQueryOrCharacterId, bool setWithId = false)
        {
            try { await SpawnCaiCharacterAsync(searchQueryOrCharacterId, setWithId); }
            catch (Exception e) { LogException(new[] { e }); }
        }


        const string sqDesc = "When specify it with a character ID, set 'set-with-id' parameter to 'True'";
        const string tagsDesc = "Tags separated by ','";
        [SlashCommand("chub-character", "Add new character from CharacterHub to this channel")]
        public async Task SpawnChubCharacter([Summary(description: sqDesc)] string searchQueryOrCharacterId, ApiType apiType, [Summary(description: tagsDesc)] string? tags = null, bool allowNSFW = true, bool setWithId = false)
        {
            
            try { await SpawnChubCharacterAsync(searchQueryOrCharacterId, apiType, tags, allowNSFW, setWithId); }
            catch (Exception e) { LogException(new[] { e }); }
        }

        [SlashCommand("custom-character", "Add new character to this channel with full customization")]
        public async Task SpawnCustomTavernCharacter()
        {
            await RespondWithCustomCharModalasync();
        }


        ////////////////////////////
        //// Main logic section ////
        ////////////////////////////

        private async Task SpawnChubCharacterAsync(string searchQueryOrCharacterId, ApiType apiType, string? tags = null, bool allowNSFW = true, bool setWithId = false)
        {
            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id, _db);
            await DeferAsync();
            switch (apiType)
            {
                case ApiType.OpenAI:
                    string? token = guild.GuildOpenAiApiToken ?? ConfigFile.DefaultOpenAiApiToken.Value;
                    if (!string.IsNullOrWhiteSpace(token)) break;

                    await FollowupAsync(embed: InlineEmbed($"{WARN_SIGN_DISCORD} You have to specify an OpenAI API token for your server first!", Color.Red));
                    return;
                default: return;
            }

            // It will be extended, don't laugh T_T
            IntegrationType integrationType = apiType is ApiType.OpenAI ? IntegrationType.OpenAI : IntegrationType.OpenAI;

            if (setWithId)
            {
                await FollowupAsync(embed: InlineEmbed(WAIT_MESSAGE, Color.Teal));

                var chubCharacter = await GetChubCharacterInfo(searchQueryOrCharacterId, _integration.HttpClient);
                var character = CharacterFromChubCharacterInfo(chubCharacter);
                await FinishSpawningAsync(integrationType, character);
            }
            else // set with search
            {
                await FollowupAsync(embed: InlineEmbed(WAIT_MESSAGE, Color.Teal));

                var response = await SearchChubCharactersAsync(new()
                {
                    Text = searchQueryOrCharacterId,
                    Tags = string.Join(',', tags),
                    AllowNSFW = allowNSFW,
                    SortBy = SortField.MostPopular
                }, _integration.HttpClient);

                var searchQueryData = SearchQueryDataFromChubResponse(response);
                await FinishSearchAsync(searchQueryData);
            }
        }

        private async Task SpawnCaiCharacterAsync([Summary(description: "When specify a character ID, set 'set-with-id' parameter to 'True'")] string searchQueryOrCharacterId, bool setWithId = false)
        {
            await DeferAsync();

            if (_integration.CaiClient is null)
            {
                await FollowupAsync(embed: InlineEmbed($"{WARN_SIGN_DISCORD} CharacterAI integration is disabled", Color.Red));
                return;
            }

            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id, _db);
            if (guild is null) return;

            var caiToken = guild.GuildCaiUserToken ?? ConfigFile.DefaultCaiUserAuthToken.Value;
            if (string.IsNullOrWhiteSpace(caiToken))
            {
                await FollowupAsync(embed: InlineEmbed($"{WARN_SIGN_DISCORD} You have to specify a CharacterAI auth token for your server first!", Color.Red));
                return;
            }

            var plusMode = guild.GuildCaiPlusMode ?? ConfigFile.DefaultCaiPlusMode.Value.ToBool();

            if (setWithId)
            {
                await FollowupAsync(embed: InlineEmbed(WAIT_MESSAGE, Color.Teal));

                var caiCharacter = await _integration.CaiClient.GetInfoAsync(searchQueryOrCharacterId, customAuthToken: caiToken, customPlusMode: plusMode);
                var character = CharacterFromCaiCharacterInfo(caiCharacter);

                await FinishSpawningAsync(IntegrationType.CharacterAI, character);
            }
            else // set with search
            {
                await FollowupAsync(embed: InlineEmbed(WAIT_MESSAGE, Color.Teal));

                var response = await _integration.CaiClient.SearchAsync(searchQueryOrCharacterId, customAuthToken: caiToken, customPlusMode: plusMode);
                var searchQueryData = SearchQueryDataFromCaiResponse(response);

                await FinishSearchAsync(searchQueryData);
            }
        }

        private async Task FinishSpawningAsync(IntegrationType type, Models.Database.Character? character)
        {
            if (character is null)
            {
                await ModifyOriginalResponseAsync(msg => msg.Embed = FailedToSetCharacterEmbed());
                return;
            }

            var channel = await FindOrStartTrackingChannelAsync(Context.Channel.Id, Context.Guild.Id, _db);
            var characterWebhook = await CreateCharacterWebhookAsync(type, Context, character, _db, _integration);

            if (characterWebhook is null)
            {
                await ModifyOriginalResponseAsync(msg => msg.Embed = InlineEmbed($"{WARN_SIGN_DISCORD} Something went wrong!", Color.Red));
                return;
            }

            var webhookClient = new DiscordWebhookClient(characterWebhook.Id, characterWebhook.WebhookToken);
            _integration.WebhookClients.Add(characterWebhook.Id, webhookClient);

            await ModifyOriginalResponseAsync(msg => msg.Embed = SpawnCharacterEmbed(characterWebhook));
            await webhookClient.SendMessageAsync($"{Context.User.Mention} {character.Greeting}");
        }

        private async Task FinishSearchAsync(SearchQueryData searchQueryData)
        {
            var query = await BuildAndSendSelectionMenuAsync(Context, searchQueryData, _db);
            if (query is null) return;

            // Stop tracking last query in this channel
            var lastSQ = _integration.SearchQueries.Find(sq => sq.ChannelId == query.ChannelId);
            if (lastSQ is not null) _integration.SearchQueries.Remove(lastSQ);

            // Start tracking this one
            _integration.SearchQueries.Add(query);
        }

        private async Task RespondWithCustomCharModalasync()
        {
            var modal = new ModalBuilder().WithTitle($"Create a character")
                                            .WithCustomId("spawn")
                                            .AddTextInput($"Name", "name", TextInputStyle.Short, required: true)
                                            .AddTextInput($"First message", "first-message", TextInputStyle.Paragraph, "*{{char}} joins server*\nHello everyone!", required: true)
                                            .AddTextInput($"Definition-1", "definition-1", TextInputStyle.Paragraph, required: true, value:
                                                        "((DELETE THIS SECTION))\n" +
                                                        "  Discord doesn't allow to set\n" +
                                                        "  more than 5 rows in one modal, so\n" +
                                                        "  you'll have to write the whole\n" +
                                                        "  character definition in these two.\n" +
                                                        "  It's highly recommended to follow\n" +
                                                        "  this exact pattern below and fill\n" +
                                                        "  each line one by one.\n" +
                                                        "  Remove or rename lines that are not\n" +
                                                        "  needed. Custom Jailbreak prompt can\n" +
                                                        "  be set with `update-character` command\n" +
                                                        "  later. Default one can be seen be seen\n" +
                                                        "  with `show-jailbreak-prompt` command." +
                                                        "((DELETE THIS SECTION))\n\n" +
                                                        "{{char}}'s personality: ALL BASIC INFO ABOUT CHARACTER, CONTINUE IN THE NEXT FIELD IF OUT OF SPACE.")
                                            .AddTextInput($"Definition-2", "definition-2", TextInputStyle.Paragraph, required: false, value:
                                                        "((DELETE THIS SECTION))\n" +
                                                        "  This section will simply continue\n" +
                                                        "  the previous one, as if these two were\n" +
                                                        "  just one big text field.\n" +
                                                        "((DELETE THIS SECTION))\n\n" +
                                                        "Scenario of roleplay: {{char}} has joined Discord!\n\n" +
                                                        "Example conversations between {{char}} and {{user}}:\n<START>\n{{user}}: Nullpo;\n{{char}}: Gah!\n<END>")
                                            .AddTextInput($"Avatar url", "avatar-url", TextInputStyle.Short, "e.g. \"https://avatars.charhub.io/avatars/x-x-x/avatar.webp\"", required: false)
                                            .Build();

            await RespondWithModalAsync(modal);
        }
    }
}
