namespace AutoProdScripts.Models;

public sealed class AppSettings
{
    public string ProjectPath { get; set; } = string.Empty;
    public string AzureDevOpsOrgUrl { get; set; } = "https://cit-damu.visualstudio.com/";
    public string AzureDevOpsProject { get; set; } = string.Empty;
    public string AzureDevOpsRepo { get; set; } = string.Empty;
    public string LastBaseBranch { get; set; } = "master";
    public string RemoteName { get; set; } = "origin";
    public bool IsConfigured { get; set; }
    public AzureAuthMode AuthMode { get; set; } = AzureAuthMode.Pat;
    public List<SavedReviewer> SavedReviewers { get; set; } = new();

    public bool AutoPrTemplateConfigured { get; set; }
    public string AutoBaseBranch { get; set; } = string.Empty;
    public string AutoBranchNameTemplate { get; set; } = string.Empty;
    public string TelegramMessageTemplate { get; set; } = string.Empty;
    public string TelegramLinkCaption { get; set; } = string.Empty;
    public string TelegramTags { get; set; } = string.Empty;
    public string TelegramTimeZoneId { get; set; } = string.Empty;
    public List<TelegramTimeTextRule> TelegramTimeTexts { get; set; } = new();
    public List<TelegramCustomTemplate> TelegramCustomTemplates { get; set; } = new();
    public List<SavedReviewer> AutoReviewers { get; set; } = new();
}

public sealed class TelegramTimeTextRule
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed class TelegramCustomTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public TelegramCustomTemplate Copy() => new() { Name = Name, Value = Value };
}

public enum AzureAuthMode
{
    Pat = 0,
    MicrosoftAccount = 1
}

public sealed class SavedReviewer
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string UniqueName { get; set; } = string.Empty;
    public bool IsRequired { get; set; }

    public SavedReviewer Copy() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        UniqueName = UniqueName,
        IsRequired = IsRequired
    };
}
