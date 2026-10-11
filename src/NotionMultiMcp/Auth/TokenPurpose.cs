namespace NotionMultiMcp.Auth;

/// <summary>
/// Why a signed token was issued. The purpose is part of the signed data, so a token issued for one
/// purpose is rejected for any other.
/// </summary>
internal enum TokenPurpose
{
    Client,
    Code,
    Access,
    Refresh,
}
