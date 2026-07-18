namespace WgbDiagnostics.Core.Wgb;

public sealed record WgbAssociationSnapshot(
    string? ParentApName,
    string? ParentBssid,
    string? Channel,
    string? Rssi,
    string? RadioId,
    string? TxRate,
    string? RxRate,
    string? WgbIp,
    string AssociationStatus,
    string? CandidateApName,
    string? CandidateBssid)
{
    public static WgbAssociationSnapshot Unknown { get; } = new(
        ParentApName: null,
        ParentBssid: null,
        Channel: null,
        Rssi: null,
        RadioId: null,
        TxRate: null,
        RxRate: null,
        WgbIp: null,
        AssociationStatus: "Unknown",
        CandidateApName: null,
        CandidateBssid: null);
}
