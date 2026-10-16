using System.Text;

namespace quikbar.Helpers;

/// <summary>
/// Tiny always-on diagnostic log for the hotkey/show/hide pipeline.
/// Location: %AppData%\quikbar\debug.log (rotates to debug.log.old at 200 KB).
/// Best-effort and lock-guarded; never throws.
/// </summary>
public static class DebugLog
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "quikbar", "debug.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = System.IO.Path.GetDirectoryName(Path_);
                if (dir is not null)
                    System.IO.Directory.CreateDirectory(dir);
                var sb = new StringBuilder()
                    .Append(DateTime.Now.ToString("HH:mm:ss.fff"))
                    .Append(" [t").Append(Environment.CurrentManagedThreadId).Append("] ")
                    .Append(message).AppendLine();
                System.IO.File.AppendAllText(Path_, sb.ToString());
                var fi = new System.IO.FileInfo(Path_);
                if (fi.Length > 200 * 1024)
                {
                    var bak = Path_ + ".old";
                    if (System.IO.File.Exists(bak))
                        System.IO.File.Delete(bak);
                    System.IO.File.Move(Path_, bak);
                }
            }
        }
        catch { }
    }
}
