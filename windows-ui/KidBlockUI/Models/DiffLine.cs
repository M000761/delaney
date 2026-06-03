namespace KidBlockUI.Models;

public enum DiffKind
{
    Add,
    Remove,
    Modify,
}

public sealed record DiffLine(DiffKind Kind, string Text);
