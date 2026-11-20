namespace M2DPlus.Grasshopper.UI.Model;

/// <summary>Inline entry rendered with a shared red validation label.</summary>
public interface IMenuTextEntry
{
    string Text { get; }
    string PromptSuffix { get; }
    bool TryAccept(string text, out string? command, out string? error);
}
