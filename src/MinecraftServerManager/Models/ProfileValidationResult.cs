namespace MinecraftServerManager.Models;

public sealed class ProfileValidationResult
{
    public ProfileValidationResult(IReadOnlyList<string> errors)
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public string ToDisplayText() => IsValid
        ? "Profile paths validated."
        : string.Join(Environment.NewLine, Errors.Select(error => $"• {error}"));
}
