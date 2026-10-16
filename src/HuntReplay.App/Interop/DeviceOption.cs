namespace HuntReplay.App.Interop;

// Width and Height are the physical pixel size of a monitor (Kind 0); 0 when unknown or for other kinds.
internal sealed record DeviceOption(int Kind, string Id, string Name, ulong Handle = 0, bool IsDefault = false, int Vendor = 0, int Width = 0, int Height = 0)
{
    public override string ToString() => Name;
}
