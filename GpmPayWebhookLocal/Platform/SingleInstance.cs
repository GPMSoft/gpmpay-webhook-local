using System.IO.Pipes;
using GpmPayWebhookLocal.Services;

namespace GpmPayWebhookLocal.Platform;

/// <summary>
/// Chỉ cho chạy 1 bản. Khoá bằng file lock (hoạt động trên cả Windows / macOS / Linux, tự nhả khi tiến trình chết),
/// bản mở sau gửi lệnh "show" qua named pipe (Unix: domain socket) để bản đang chạy hiện cửa sổ rồi tự thoát.
/// </summary>
public static class SingleInstance
{
    private static readonly string PipeName = "GpmPayWebhookLocal." + Environment.UserName;
    private static FileStream? _lock;

    public static bool TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            _lock = new FileStream(Path.Combine(AppSettings.DataDir, "app.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Báo bản đang chạy (có thể đang ẩn dưới khay) hiện cửa sổ.</summary>
    public static void SignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            client.WriteByte(1);
        }
        catch { }
    }

    public static void Listen(Action onShow)
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    if (server.ReadByte() == 1) onShow();
                }
                catch (IOException) { Thread.Sleep(500); }
                catch { return; }
            }
        }) { IsBackground = true, Name = "single-instance" }.Start();
    }
}
