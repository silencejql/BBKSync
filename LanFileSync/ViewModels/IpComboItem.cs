namespace LanFileSync.ViewModels;

/// <summary>远端 IP 下拉项：Ip 为实际值，Label 为界面显示文本。</summary>
public sealed class IpComboItem
{
    public string Ip { get; init; } = "";
    public string Label { get; init; } = "";
    public override string ToString() => Ip;
}
