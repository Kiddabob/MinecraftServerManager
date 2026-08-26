namespace MinecraftServerManager.Models;

public sealed class ProfileValidationResult
{
    public ProfileValidationResult(
        IReadOnlyList<string> errors,
        IReadOnlyList<string>? warnings = null)
    {
        Errors = errors;
        Warnings = warnings ?? [];
    }

    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool IsValid => Errors.Count == 0;

    public string ToDisplayText()
    {
        if (!IsValid)
        {
            return string.Join(Environment.NewLine, Errors.Select(error => $"• {error}"));
        }

        return Warnings.Count == 0
            ? "Profile paths and Java settings validated."
            : string.Join(Environment.NewLine, Warnings.Select(warning => $"⚠ {warning}"));
    }
}
