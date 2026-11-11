using NAudio.CoreAudioApi;

namespace InterviewTrace.Core;

public static class AudioEndpoints
{
    public static IReadOnlyList<AudioDevice> ListMicrophones() => List(DataFlow.Capture);
    public static IReadOnlyList<AudioDevice> ListOutputs() => List(DataFlow.Render);

    private static IReadOnlyList<AudioDevice> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = TryDefault(enumerator, flow, Role.Multimedia);
        string? communicationsId = TryDefault(enumerator, flow, Role.Communications);
        var devices = new List<AudioDevice>();
        using var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        foreach (MMDevice endpoint in collection)
        {
            using (endpoint)
                devices.Add(new AudioDevice(endpoint.ID, endpoint.FriendlyName,
                    endpoint.ID == defaultId, endpoint.ID == communicationsId));
        }

        var duplicateNames = devices.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return devices.Select(d => duplicateNames.Contains(d.Name) ? d with { Name = $"{d.Name} [{d.Id}]" } : d)
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string? TryDefault(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.ID;
        }
        catch { return null; }
    }
}
