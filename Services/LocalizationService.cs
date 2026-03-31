using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;
using USBShare.Models;

namespace USBShare.Services;

public static class LocalizationService
{
    public const string SystemLanguage = "system";
    public const string ChineseLanguage = "zh-CN";
    public const string EnglishLanguage = "en-US";

    private static string _preferredLanguage = SystemLanguage;
    private static string _effectiveLanguage = EnglishLanguage;
    private static ResourceManager? _resourceManager;
    private static ResourceMap? _resourceMap;

    public static void InitializeFromConfig()
    {
        ApplyLanguageOverride(ReadPreferredLanguageFromConfig());
    }

    public static string NormalizePreference(string? preferredLanguage)
    {
        if (string.Equals(preferredLanguage, ChineseLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return ChineseLanguage;
        }

        if (string.Equals(preferredLanguage, EnglishLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return EnglishLanguage;
        }

        return SystemLanguage;
    }

    public static string ResolveEffectiveLanguage(string? preferredLanguage)
    {
        var normalized = NormalizePreference(preferredLanguage);
        if (!string.Equals(normalized, SystemLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return CultureInfo.InstalledUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? ChineseLanguage
            : EnglishLanguage;
    }

    public static void ApplyLanguageOverride(string? preferredLanguage)
    {
        _preferredLanguage = NormalizePreference(preferredLanguage);
        _effectiveLanguage = ResolveEffectiveLanguage(_preferredLanguage);
        ApplicationLanguages.PrimaryLanguageOverride = _effectiveLanguage;

        var culture = CultureInfo.GetCultureInfo(_effectiveLanguage);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;

        _resourceManager = null;
        _resourceMap = null;
    }

    public static string GetString(string resourceKey)
    {
        try
        {
            var lookupKey = resourceKey.Replace('.', '/');
            var resourceContext = ResourceManager.CreateResourceContext();
            resourceContext.QualifierValues["Language"] = _effectiveLanguage;

            var candidate = ResourceMap.GetValue(lookupKey, resourceContext);
            var value = candidate.ValueAsString;
            return string.IsNullOrWhiteSpace(value) ? resourceKey : value;
        }
        catch
        {
            return resourceKey;
        }
    }

    public static string Format(string resourceKey, params object[] args)
    {
        var template = GetString(resourceKey);
        return args.Length == 0
            ? template
            : string.Format(CultureInfo.CurrentUICulture, template, args);
    }

    public static string GetAuthTypeLabel(AuthType authType)
    {
        return authType switch
        {
            AuthType.Password => GetString("AuthType.Password"),
            AuthType.PrivateKey => GetString("AuthType.PrivateKey"),
            _ => authType.ToString(),
        };
    }

    private static ResourceManager ResourceManager => _resourceManager ??= CreateResourceManager();

    private static ResourceMap ResourceMap => _resourceMap ??= ResourceManager.MainResourceMap.GetSubtree("Resources");

    private static ResourceManager CreateResourceManager()
    {
        var priPath = System.IO.Path.Combine(AppContext.BaseDirectory, "resources.pri");
        return File.Exists(priPath)
            ? new ResourceManager(priPath)
            : new ResourceManager();
    }

    private static string ReadPreferredLanguageFromConfig()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFilePath))
            {
                return SystemLanguage;
            }

            var content = File.ReadAllText(AppPaths.ConfigFilePath);
            if (string.IsNullOrWhiteSpace(content))
            {
                return SystemLanguage;
            }

            var config = System.Text.Json.JsonSerializer.Deserialize(content, AppJsonSerializerContext.Default.AppConfig);

            return NormalizePreference(config?.Settings?.PreferredLanguage);
        }
        catch
        {
            return SystemLanguage;
        }
    }
}
