namespace Capacitor.App.Services.Driver;

/// <summary>The server reads behind the Driver view, for the app profile's own viewer.</summary>
public interface IDriverApi {
    Task<NextWorkRead<DriverWorkItemsDto>> WorkItemsAsync(CancellationToken ct);
}
