namespace HuntReplay.App.Interop;

// Width and Height are the physical pixel size of a monitor (Kind 0); 0 when unknown or for other kinds.
internal sealed record DeviceOption(int Kind, string Id, string Name, ulong Handle = 0, bool IsDefault = false, int Vendor = 0, int Width = 0, int Height = 0)
{
    public override string ToString() => Name;

    /// <summary>
    /// Finds the adapter a saved GPU value refers to. The native layer reports the adapter description as the ID
    /// (a DXGI index only when two adapters share a description). Builds before v0.1.8 saved the DXGI index for
    /// every adapter, and such a value never equals a description, so it is read as a position in the
    /// enumeration, which is what the native layer would pick for it. Returns null when nothing matches.
    /// </summary>
    public static DeviceOption? ResolveGpu(string saved, IEnumerable<DeviceOption> devices)
    {
        string value = saved.Trim();
        if (value.Length == 0) return null;
        var gpus = devices.Where(d => d.Kind == 3 && d.Id.Length > 0).ToList();
        return gpus.FirstOrDefault(d => d.Id == value)
            ?? gpus.FirstOrDefault(d => string.Equals(d.Id, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Name, value, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
                && index < gpus.Count && !gpus.Any(d => d.Id.All(char.IsAsciiDigit)) ? gpus[index] : null);
    }
}
