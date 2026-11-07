namespace GpmPayWebhookLocal.Services;

/// <summary>Đổi số thành chữ tiếng Việt để TTS đọc chính xác, VD 112233 → "một trăm mười hai nghìn hai trăm ba mươi ba".</summary>
public static class VietnameseNumber
{
    private static readonly string[] Digits = ["không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín"];

    public static string ToWords(long n)
    {
        if (n == 0) return "không";
        if (n < 0) return "âm " + ToWords(-n);
        return Read(n, false).Trim();
    }

    private static string Read(long n, bool full)
    {
        const long Billion = 1_000_000_000;
        if (n >= Billion)
        {
            var head = Read(n / Billion, full) + " tỷ";
            var rest = n % Billion;
            return rest > 0 ? head + " " + BelowBillion(rest, true) : head;
        }
        return BelowBillion(n, full);
    }

    private static string BelowBillion(long n, bool full)
    {
        int[] groups = [(int)(n / 1_000_000), (int)(n / 1000 % 1000), (int)(n % 1000)];
        string[] units = [" triệu", " nghìn", ""];
        var parts = new List<string>();
        bool started = full;
        for (int i = 0; i < 3; i++)
        {
            if (groups[i] == 0) continue;
            parts.Add(ReadTriple(groups[i], started) + units[i]);
            started = true;
        }
        return string.Join(" ", parts);
    }

    private static string ReadTriple(int t, bool full)
    {
        int h = t / 100, d = t / 10 % 10, u = t % 10;
        var parts = new List<string>();
        bool hasHundred = full || h > 0;
        if (hasHundred) parts.Add(Digits[h] + " trăm");

        if (d == 0)
        {
            if (u > 0) parts.Add(hasHundred ? "linh " + Digits[u] : Digits[u]);
        }
        else if (d == 1)
        {
            parts.Add("mười" + (u == 0 ? "" : u == 5 ? " lăm" : " " + Digits[u]));
        }
        else
        {
            parts.Add(Digits[d] + " mươi" + (u == 0 ? "" : u == 1 ? " mốt" : u == 5 ? " lăm" : " " + Digits[u]));
        }
        return string.Join(" ", parts);
    }
}
