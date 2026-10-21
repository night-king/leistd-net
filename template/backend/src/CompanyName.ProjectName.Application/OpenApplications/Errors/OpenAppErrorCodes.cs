namespace CompanyName.ProjectName.Application.OpenApplications.Errors;

/// <summary>OpenApp 业务错误码。</summary>
public static class OpenAppErrorCodes
{
    public const string ExchangeClientInvalid = "OpenApp:ExchangeClientInvalid";
    public const string AudienceUnsupported = "OpenApp:AudienceUnsupported";
    public const string ClientIdTaken = "OpenApp:ClientIdTaken";
    public const string CreateFailed = "OpenApp:CreateFailed";
    public const string MachineScopeRejectsUserGrants = "OpenApp:MachineScopeRejectsUserGrants";
    public const string MachineScopeRequiresClientCredentials = "OpenApp:MachineScopeRequiresClientCredentials";
    public const string MachineScopeRequiresConfidential = "OpenApp:MachineScopeRequiresConfidential";
    public const string MachineScopeRequiresTokenEndpoint = "OpenApp:MachineScopeRequiresTokenEndpoint";
    public const string NotFound = "OpenApp:NotFound";
    public const string PkceRequired = "OpenApp:PkceRequired";
    public const string ScopeUnsupported = "OpenApp:ScopeUnsupported";
    public const string SecretResetConfidentialOnly = "OpenApp:SecretResetConfidentialOnly";
}
