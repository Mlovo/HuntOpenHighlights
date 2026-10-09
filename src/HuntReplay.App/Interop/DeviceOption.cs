namespace HuntReplay.App.Interop;

internal sealed record DeviceOption(int Kind, string Id, string Name, ulong Handle = 0, bool IsDefault = false, int Vendor = 0)
{
    public override string ToString() => Name;
}
