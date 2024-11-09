using CharacterEngineDiscord.Models.Abstractions;

namespace CharacterEngineDiscord.Models.Common;


public class CommonCharacter : ICharacter
{
    public required string CharacterId { get; set; }
    public required string CharacterName { get; set; }
    public required string CharacterFirstMessage { get; set; }
    public required string CharacterAuthor { get; set; }
    public required string? CharacterImageLink { get; set; }

    public required object? OriginalCharacterObject { get; init; }

    public required bool IsNfsw { get; set; }
    public required string? CharacterStat { get; set; }

    public required IntegrationType IntegrationType { get; init; }
}
