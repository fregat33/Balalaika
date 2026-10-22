using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Server;

/// <summary>Builds the view of an HTTP request from its repeatable <c>part</c> query parameter (see <see cref="ViewContext"/>).</summary>
public static class RequestView
{
    public const string PartParameter = "part";

    /// <summary>
    /// Sets the view of the current async flow from the request behind <paramref name="services"/> and returns the
    /// reason the request must be refused, or null. Without a request, or without <c>part</c>, the view is the base checkouts.
    /// </summary>
    public static string? Apply(IServiceProvider? services, WorkspaceManager workspaces)
    {
        HttpContext? http = services?.GetService<IHttpContextAccessor>()?.HttpContext;
        return Apply(http, workspaces);
    }

    public static string? Apply(HttpContext? http, WorkspaceManager workspaces)
    {
        string[] parts = http is null
            ? []
            : http.Request.Query[PartParameter].Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToArray();
        ViewContext.Current = workspaces.CreateView(parts, out string? error);
        return error;
    }
}
