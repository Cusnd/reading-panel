using Google.Apis.Auth.OAuth2;

namespace ReadingPanel.Core;

internal static class GoogleCloudCredentials
{
    internal static async Task<GoogleCredential> GetAsync(string project, CancellationToken token)
    {
        var credential = (await GoogleCredential.GetApplicationDefaultAsync(token)).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        return project.Length > 0 ? credential.CreateWithQuotaProject(project) : credential;
    }
}
