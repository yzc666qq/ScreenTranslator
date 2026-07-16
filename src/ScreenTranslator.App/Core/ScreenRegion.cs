namespace ScreenTranslator.App.Core;

public readonly record struct ScreenRegion(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
