namespace WinExplorer.UI;

/// <summary>A labelled value for a combo box.</summary>
public sealed record EnumOption<T>(T Value, string Display, string? Description = null)
{
    public override string ToString() => Display;
}
