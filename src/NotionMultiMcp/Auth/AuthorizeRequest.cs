using Microsoft.Extensions.Primitives;

namespace NotionMultiMcp.Auth;

/// <summary>The parameters of an authorization request, read from the query string or from the login form.</summary>
internal sealed record AuthorizeRequest(
    string ClientId,
    string RedirectUri,
    string? State,
    string CodeChallenge,
    string? CodeChallengeMethod,
    string? ResponseType,
    string? Resource,
    string? Scope)
{
    public static AuthorizeRequest From(Func<string, StringValues> get)
    {
        string? Value(string key)
        {
            var v = get(key);
            return StringValues.IsNullOrEmpty(v) ? null : v[0];
        }

        return new AuthorizeRequest(
            Value("client_id") ?? "",
            Value("redirect_uri") ?? "",
            Value("state"),
            Value("code_challenge") ?? "",
            Value("code_challenge_method"),
            Value("response_type"),
            Value("resource"),
            Value("scope"));
    }

    /// <summary>The fields the login form posts back unchanged.</summary>
    public IEnumerable<(string Name, string? Value)> HiddenFields()
    {
        yield return ("client_id", ClientId);
        yield return ("redirect_uri", RedirectUri);
        yield return ("state", State);
        yield return ("code_challenge", CodeChallenge);
        yield return ("code_challenge_method", CodeChallengeMethod);
        yield return ("response_type", ResponseType);
        yield return ("resource", Resource);
        yield return ("scope", Scope);
    }
}
