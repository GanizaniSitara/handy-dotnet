namespace Handy.Services;

public sealed record TranscriptionOptions(
    string WhisperPrompt,
    int WhisperPromptTermCount,
    bool WhisperCarryInitialPrompt)
{
    public static TranscriptionOptions None { get; } = new(string.Empty, 0, false);

    public bool HasWhisperPrompt =>
        WhisperPromptTermCount > 0 &&
        !string.IsNullOrWhiteSpace(WhisperPrompt);
}
