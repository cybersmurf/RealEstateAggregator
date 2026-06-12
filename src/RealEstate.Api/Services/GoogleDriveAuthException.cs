namespace RealEstate.Api.Services;

/// <summary>Google Drive OAuth token is missing, expired, or revoked.</summary>
public sealed class GoogleDriveAuthException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public const string ReauthorizePath = GoogleDriveAuthHelper.ReauthorizePath;
}
