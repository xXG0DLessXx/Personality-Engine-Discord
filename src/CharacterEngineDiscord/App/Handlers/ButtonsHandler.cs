﻿using CharacterEngine.App.Helpers.Discord;
using CharacterEngineDiscord.Models;
using Discord.WebSocket;

namespace CharacterEngine.App.Handlers;


public class ButtonsHandler
{
    public required LocalStorage LocalStorage { get; set; }
    public required DiscordSocketClient DiscordClient { get; set; }
    public required AppDbContext db { get; set; }


    public async Task HandleButtonAsync(SocketMessageComponent component)
    {
        await component.DeferAsync();

        var actionType = GetActionType(component.Data.CustomId);
        await (actionType switch
        {
            ButtonActionType.SearchQuery => UpdateSearchQueryAsync(component)
        });
    }


    private static ButtonActionType GetActionType(string customId)
    {
        var i = customId.IndexOf(InteractionsHelper.SEP, StringComparison.Ordinal);
        if (i == -1)
        {
            return ButtonActionType.Unknown;
        }

        return customId[..i] switch
        {
            "sq" => ButtonActionType.SearchQuery,

            _ => ButtonActionType.Unknown
        };
    }

    private async Task UpdateSearchQueryAsync(SocketMessageComponent component)
    {
        var sq = LocalStorage.SearchQueries.FirstOrDefault(sq => sq.ChannelId == component.ChannelId && sq.UserId == component.User.Id);
        if (sq is null)
        {
            return;
        }

        var action = component.Data.CustomId.Replace($"sq{InteractionsHelper.SEP}", string.Empty);
        switch (action)
        {
            case "up":
            {
                sq.CurrentRow = sq.CurrentRow == 1 ? Math.Min(sq.Characters.Count, 10) : sq.CurrentRow - 1;
                break;
            }
            case "down":
            {
                sq.CurrentRow = sq.CurrentRow == Math.Min(sq.Characters.Count, 10) ? 1 : sq.CurrentRow + 1;
                break;
            }
            case "left":
            {
                sq.CurrentPage = sq.CurrentPage == 1 ? sq.Pages : sq.CurrentPage - 1;
                break;
            }
            case "right":
            {
                sq.CurrentPage = sq.CurrentPage == sq.Pages ? 1 : sq.CurrentPage + 1;
                break;
            }
            case "select":
            {
                break;
            }
        }

        await component.Message.ModifyAsync(m => { m.Embed = InteractionsHelper.BuildSearchResultList(sq); }).ConfigureAwait(false);
    }
}
