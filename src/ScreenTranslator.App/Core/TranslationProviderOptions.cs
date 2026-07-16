namespace ScreenTranslator.App.Core;

public sealed record TranslationProviderOptions(
    Uri Endpoint,
    string Model,
    string ApiKey);
