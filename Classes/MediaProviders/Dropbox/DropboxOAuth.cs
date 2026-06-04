using System.Collections.Concurrent;
using Dropbox.Api;

namespace Diyokee.MediaProviders;

// Implements the Dropbox OAuth2 PKCE flow using the manual (no-redirect) code
// entry mode. BeginAuth returns an authorize URL that the user opens in their
// browser; Dropbox then displays an authorization code that the user pastes back
// into the app, which CompleteAuth exchanges for a refresh token. Because no
// redirect URI is registered, the same flow works on every user's machine
// regardless of host or port.
public static class DropboxOAuth {
    // The application's own Dropbox App Key. This identifies the Diyokee app to
    // Dropbox and is shared by every user, so it is baked in here rather than
    // asked of each user. Set this to the App Key from the Dropbox App Console
    // (https://www.dropbox.com/developers/apps) before building for distribution.
    public const string AppKey = "yp8jejnokjqghnn";

    private static readonly ConcurrentDictionary<string, PKCEOAuthFlow> pending = new();

    private static readonly string[] scopes = [
        "account_info.read",
        "files.metadata.read",
        "files.content.read"
    ];

    // Starts an authorization flow for the named provider and returns the URL the
    // user should open in their browser. The PKCE flow (which holds the code
    // verifier) is stashed keyed by provider name so CompleteAuth can finish the
    // exchange against the code the user pastes back.
    public static string BeginAuth(string providerName) {
        PKCEOAuthFlow flow = new();
        Uri authorizeUri = flow.GetAuthorizeUri(
            OAuthResponseType.Code,
            AppKey,
            tokenAccessType: TokenAccessType.Offline,
            scopeList: scopes,
            includeGrantedScopes: IncludeGrantedScopes.None);

        pending[providerName] = flow;
        return authorizeUri.AbsoluteUri;
    }

    // Completes the flow with the code the user pasted from Dropbox and returns the
    // resulting refresh token. Throws if BeginAuth was not called for the provider
    // or Dropbox rejects the code.
    public static async Task<string> CompleteAuth(string providerName, string code) {
        if(!pending.TryRemove(providerName, out PKCEOAuthFlow? flow)) {
            throw new InvalidOperationException("No pending Dropbox authorization. Click Connect first.");
        }

        OAuth2Response token = await flow.ProcessCodeFlowAsync(code.Trim(), AppKey);
        return token.RefreshToken;
    }
}
