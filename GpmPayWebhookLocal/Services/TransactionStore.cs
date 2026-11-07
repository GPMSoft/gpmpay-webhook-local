using System.Text.Json;

namespace GpmPayWebhookLocal.Services;

public sealed record TxRecord(
    string? Id,
    DateTimeOffset Time,
    decimal Amount,
    bool Incoming,
    bool Simulated,
    string Gateway,
    string Content);

/// <summary>Lưu 500 giao dịch gần nhất vào transactions.json để mở lại app vẫn có lịch sử và tổng "Hôm nay".</summary>
public sealed class TransactionStore
{
    private const int MaxItems = 500;
    private static readonly string FilePath = Path.Combine(AppSettings.DataDir, "transactions.json");

    /// <summary>Mới nhất đứng đầu.</summary>
    public List<TxRecord> Items { get; } = [];

    public static TransactionStore Load()
    {
        var store = new TransactionStore();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJson.Default.ListTxRecord) is { } list)
                store.Items.AddRange(list.OrderByDescending(t => t.Time).Take(MaxItems));
        }
        catch { /* file hỏng → bắt đầu trống */ }
        return store;
    }

    public TxRecord Add(WebhookPayload p)
    {
        var tx = new TxRecord(
            p.Id,
            (p.TransactionDate ?? DateTimeOffset.Now).ToLocalTime(),
            p.TransferAmount,
            p.IsIncoming,
            p.IsSimulated,
            p.Gateway ?? "",
            p.Content ?? "");
        Items.Insert(0, tx);
        if (Items.Count > MaxItems) Items.RemoveRange(MaxItems, Items.Count - MaxItems);
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, AppJson.Default.ListTxRecord));
        }
        catch { }
        return tx;
    }

    /// <summary>Số giao dịch tiền vào và tổng tiền vào trong ngày hôm nay (giờ máy).</summary>
    public (int Count, decimal Sum) TodayIncoming()
    {
        var today = DateTime.Today;
        var list = Items.Where(t => t.Incoming && t.Time.LocalDateTime.Date == today).ToList();
        return (list.Count, list.Sum(t => t.Amount));
    }
}
