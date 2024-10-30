using CharacterEngineDiscord.Models.Abstractions;
using CharacterEngineDiscord.Models.Common;

namespace CharacterEngineDiscord.IntegrationModules.Abstractions;


public interface IIntegrationModule
{
    public Task<List<CommonCharacter>> SearchAsync(string query);

    public Task<(string chatId, string? characterMessage)> CreateNewChatAsync(ISpawnedCharacter spawnedCharacter, IGuildIntegration guildIntegration, string firstUserMessage);

    public Task<CommonCharacterMessage> CallCharacterAsync(ISpawnedCharacter spawnedCharacter, IGuildIntegration guildIntegration, string message);


}