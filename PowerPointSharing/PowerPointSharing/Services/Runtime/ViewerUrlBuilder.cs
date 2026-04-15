namespace PowerPointSharing
{
    internal static class ViewerUrlBuilder
    {
        public static string Build(
            string frontendBaseUrl,
            string activePresentationId,
            string activeSignalRGroupId,
            string backendViewerUrl)
        {
            if (!string.IsNullOrEmpty(activePresentationId))
            {
                return frontendBaseUrl.TrimEnd('/')
                    + "/viewer/"
                    + activePresentationId
                    + "?sessionId="
                    + activeSignalRGroupId;
            }

            return backendViewerUrl;
        }
    }
}
