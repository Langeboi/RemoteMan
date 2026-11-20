using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RemoteMan.App.Services;

/// <summary>
/// Puts login details on the clipboard without leaving them there:
/// <list type="bullet">
/// <item>Secrets are kept out of Windows clipboard history and cloud clipboard.</item>
/// <item>The clipboard is cleared after <see cref="ClearAfter"/> unless something else replaced it.</item>
/// <item>"Both": the first paste gives the user name, then the clipboard switches to the password
/// for the next paste (rendered on demand, so nothing is copied until you actually paste).</item>
/// </list>
/// </summary>
public static class CredentialClipboard
{
    public static TimeSpan ClearAfter { get; set; } = TimeSpan.FromSeconds(30);

    private static IDataObject? _current;
    private static DispatcherTimer? _clearTimer;

    public static void CopyText(string text, bool secret) => Put(new SequenceData(new[] { text }, secret));

    /// <summary>User name on the next paste, password on the one after.</summary>
    public static void CopyBoth(string userName, string password) => Put(new SequenceData(new[] { userName, password }, secret: true));

    private static void Put(SequenceData data)
    {
        var obj = new DataObject(data);
        data.Owner = obj;
        _current = obj;
        Clipboard.SetDataObject(obj, false); // false = render on demand
        _clearTimer?.Stop();
        _clearTimer = new DispatcherTimer(ClearAfter, DispatcherPriority.Background, (_, _) => ClearIfOurs(), Dispatcher.CurrentDispatcher);
        _clearTimer.Start();
    }

    private static void ClearIfOurs()
    {
        _clearTimer?.Stop();
        try { if (_current is not null && Clipboard.IsCurrent(_current)) Clipboard.Clear(); }
        catch (System.Runtime.InteropServices.COMException) { }
        _current = null;
    }

    private sealed class SequenceData(string[] values, bool secret) : IDataObject
    {
        private static readonly string[] TextFormats = [DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat, DataFormats.OemText];
        private const string NoHistory = "CanIncludeInClipboardHistory";
        private const string NoCloud = "CanUploadToCloudClipboard";
        private const string NoMonitor = "ExcludeClipboardContentFromMonitorProcessing";

        private int _index;
        private bool _advanceQueued;
        public IDataObject? Owner { get; set; }

        private string[] Formats => secret ? [.. TextFormats, NoHistory, NoCloud, NoMonitor] : TextFormats;

        public object? GetData(string format, bool autoConvert)
        {
            if (format is NoHistory or NoCloud) return new MemoryStream(BitConverter.GetBytes(0));
            if (format == NoMonitor) return new MemoryStream(new byte[] { 0 });
            if (!TextFormats.Contains(format)) return null;
            var value = values[Math.Min(_index, values.Length - 1)];
            // After the user name is pasted, switch to the password for the next paste.
            if (_index < values.Length - 1 && !_advanceQueued)
            {
                _advanceQueued = true;
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
                {
                    await Task.Delay(400); // let the target finish reading the clipboard
                    _index++;
                    _advanceQueued = false;
                    // Re-announce the clipboard so apps that cache it fetch the new value.
                    if (Owner is not null && Clipboard.IsCurrent(Owner)) Clipboard.SetDataObject(Owner, false);
                });
            }
            return value;
        }

        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => GetData(format.FullName ?? "", true);
        public bool GetDataPresent(string format, bool autoConvert) => Formats.Contains(format);
        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => format == typeof(string);
        public string[] GetFormats(bool autoConvert) => Formats;
        public string[] GetFormats() => Formats;
        public void SetData(object data) { }
        public void SetData(string format, object data) { }
        public void SetData(Type format, object data) { }
        public void SetData(string format, object data, bool autoConvert) { }
    }
}
