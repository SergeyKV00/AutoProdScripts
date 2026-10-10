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
}
