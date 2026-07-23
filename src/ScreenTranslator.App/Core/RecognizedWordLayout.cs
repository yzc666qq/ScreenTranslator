namespace ScreenTranslator.App.Core;

public sealed record RecognizedWordLayout(
    string Text,
    double X,
    double Y,
    double Width,
    double Height);
