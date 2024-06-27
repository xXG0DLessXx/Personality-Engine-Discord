﻿using Discord;
using Discord.WebSocket;
using NLog;

namespace CharacterEngine.Helpers.Discord;

public static class DiscordMessagesHelper
{
    private static readonly Logger _log = LogManager.GetCurrentClassLogger();


    public static Embed ToInlineEmbed(this string text, Color color, bool bold = true, string? imageUrl = null, bool imageAsThumb = false)
    {
        string desc = bold ? $"**{text}**" : text;

        var embed = new EmbedBuilder().WithDescription(desc).WithColor(color);
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            if (imageAsThumb)
            {
                embed.WithThumbnailUrl(imageUrl);
            }
            else
            {
                embed.WithImageUrl(imageUrl);
            }
        }

        return embed.Build();
    }


    public static Task ReportErrorAsync(this IDiscordClient discordClient, Exception e)
        => discordClient.ReportErrorAsync("Unknown exception", $"{e}");

    public static Task ReportErrorAsync(this IDiscordClient discordClient, string title, Exception e)
        => discordClient.ReportErrorAsync(title, $"{e}");


    private const int LIMIT = 1990;
    public static async Task ReportErrorAsync(this IDiscordClient discordClient, string title, string content)
    {
        _log.Error($"Error report: [ {title} ] {content}");

        var channel = (ITextChannel)await discordClient.GetChannelAsync(BotConfig.ERRORS_CHANNEL_ID);
        var thread = await channel.CreateThreadAsync($"💀 {title}", autoArchiveDuration: ThreadArchiveDuration.ThreeDays);

        while (content.Length > 0)
        {
            if (content.Length <= LIMIT)
            {
                await thread.SendMessageAsync($"```js\n{content}```");
                break;
            }

            await thread.SendMessageAsync(text: $"```js\n{content[..(LIMIT-1)]}```");
            content = content[LIMIT..];
        }
    }


    public static async Task ReportLogAsync(this IDiscordClient discordClient, string title, string? content, uint colorHex = 2067276U, string? imageUrl = null)
    {
        _log.Info($"[ {title} ] {content}");

        var channel = (ITextChannel)await discordClient.GetChannelAsync(BotConfig.LOGS_CHANNEL_ID);
        var message = await channel.SendMessageAsync(embed: title.ToInlineEmbed(Color.Green, false, imageUrl));
        if (content is null)
        {
            return;
        }

        var thread = await channel.CreateThreadAsync("Info", autoArchiveDuration: ThreadArchiveDuration.ThreeDays, message: message);
        while (content.Length > 0)
        {
            if (content.Length <= LIMIT)
            {
                await thread.SendMessageAsync(content);
                break;
            }

            await thread.SendMessageAsync(content[..(LIMIT-1)]);
            content = content[LIMIT..];
        }
    }

}