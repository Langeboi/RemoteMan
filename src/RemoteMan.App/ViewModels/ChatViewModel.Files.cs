using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteMan.Core.Chat;

namespace RemoteMan.App.ViewModels;

/// <summary>Files, images and GIFs in chat: sending (paperclip, drop, paste) and opening them.</summary>
public sealed partial class ChatViewModel
{
    /// <summary>Whether this database takes files (the files table exists).</summary>
    public bool CanSendFiles => _hub?.SupportsFiles == true;

    [ObservableProperty] private string _sendingText = "";
    public bool IsSending => SendingText.Length > 0;
    partial void OnSendingTextChanged(string value) => OnPropertyChanged(nameof(IsSending));

    [RelayCommand]
    private async Task AttachAsync()
    {
        if (Current is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Send files to {Current.Title}", Multiselect = true, CheckFileExists = true };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return;
        await SendFilesAsync(dlg.FileNames);
    }

    /// <summary>Files dropped from Explorer or pasted: each one goes as its own message.</summary>
    public async Task SendFilesAsync(IEnumerable<string> paths, bool ask = false)
    {
        if (_hub is null || Current is null) return;
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) { if (paths.Any(Directory.Exists)) _main.Message = "Folders can't be sent; zip it first, or pick the files"; return; }
        if (!CanSendFiles) { _main.Message = "This database can't store chat files yet: someone who may create tables has to connect once"; return; }
        var tooBig = files.Where(f => new FileInfo(f).Length > ChatFileRef.MaxBytes).ToList();
        foreach (var f in tooBig) _main.Message = $"{Path.GetFileName(f)} is {ChatFileRef.FormatSize(new FileInfo(f).Length)}; files up to {ChatFileRef.FormatSize(ChatFileRef.MaxBytes)} can be sent in chat";
        files = files.Except(tooBig).ToList();
        if (files.Count == 0) return;
        if (ask)
        {
            var what = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files";
            if (_main.Confirm?.Invoke($"Send {what} to {Current.Title}?", string.Join("\n", files.Select(Path.GetFileName).Take(8)), "Send") != true) return;
        }
        foreach (var f in files)
        {
            byte[] data;
            try { data = await File.ReadAllBytesAsync(f); }
            catch (Exception ex) { _main.Message = $"Could not read {Path.GetFileName(f)}: {ex.Message}"; continue; }
            await SendBytesAsync(Path.GetFileName(f), data);
        }
    }

    /// <summary>A pasted screenshot or copied picture: sent as a PNG after asking.</summary>
    public async Task SendClipboardImageAsync(BitmapSource image)
    {
        if (_hub is null || Current is null) return;
        if (!CanSendFiles) { _main.Message = "This database can't store chat files yet: someone who may create tables has to connect once"; return; }
        if (_main.Confirm?.Invoke($"Send the picture to {Current.Title}?", $"The image on the clipboard, {image.PixelWidth} × {image.PixelHeight}.", "Send") != true) return;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        await SendBytesAsync($"Picture {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png", ms.ToArray());
    }

    private async Task SendBytesAsync(string name, byte[] data)
    {
        if (_hub is null || Current is null) return;
        SendingText = $"Sending {name} ({ChatFileRef.FormatSize(data.LongLength)})…";
        try { await _hub.SendFileAsync(Current.Room, name, data); }
        catch (Exception ex) { _main.Message = $"Could not send {name}: {ex.Message}"; }
        finally { SendingText = ""; }
    }

    internal async Task<byte[]?> FileBytesAsync(ChatFileRef f) => _hub is null ? null : await _hub.GetFileAsync(f);

    internal MainViewModel Main => _main;
}

/// <summary>A file, image or GIF on a line of its own. Images load when shown; other files on Save or Open.</summary>
public sealed partial class ChatFileBlock : ObservableObject
{
    private readonly ChatViewModel _chat;

    public ChatFileBlock(ChatFileRef file, ChatViewModel chat)
    {
        File = file;
        _chat = chat;
        if (file.IsImage) _ = LoadImageAsync();
    }

    public ChatFileRef File { get; }
    public string Name => File.Name;
    public string Detail => $"{File.Kind} · {File.SizeText}";
    public bool IsImage => File.IsImage;
    public bool IsFile => !File.IsImage || Failed;
    /// <summary>Two-to-four letter badge for the card ("PDF", "ZIP").</summary>
    public string Badge => File.Extension.TrimStart('.') is { Length: > 0 and <= 4 } x ? x.ToUpperInvariant() : "FILE";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsFile), nameof(ShowImage))] private bool _failed;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowImage))] private ImageSource? _image;
    [ObservableProperty] private string _status = "";
    public bool ShowImage => Image is not null && !Failed;

    private async Task LoadImageAsync()
    {
        Status = "Loading…";
        try
        {
            var data = await _chat.FileBytesAsync(File);
            if (data is null) { Status = "Deleted with old messages"; Failed = true; return; }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(data);   // kept open: GIF frames are read from it
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (!File.IsGif) bmp.DecodePixelWidth = 640;
            bmp.EndInit();
            Image = bmp;
            Status = "";
        }
        catch (Exception) { Status = "Can't show this picture"; Failed = true; }
    }

    private async Task<byte[]?> BytesAsync()
    {
        try
        {
            Status = "Downloading…";
            var data = await _chat.FileBytesAsync(File);
            Status = data is null ? "Deleted with old messages" : "";
            return data;
        }
        catch (Exception ex) { Status = ""; _chat.Main.Message = $"Could not get {Name}: {ex.Message}"; return null; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Name, Title = "Save file from chat" };
        var ext = File.Extension;
        if (ext.Length > 0) dlg.Filter = $"{ext.TrimStart('.').ToUpperInvariant()} file|*{ext}|All files|*.*";
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return;
        if (await BytesAsync() is not { } data) return;
        try
        {
            await System.IO.File.WriteAllBytesAsync(dlg.FileName, data);
            MarkFromInternet(dlg.FileName);
            _chat.Main.Message = $"Saved {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { _chat.Main.Message = $"Could not save: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (File.IsProgram && _chat.Main.Confirm?.Invoke($"Run {Name}?",
                "This file runs a program on your PC. Open it only if you trust the person who sent it and expected it.", "Run") != true) return;
        if (await BytesAsync() is not { } data) return;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "RemoteMan", "chat", File.Id.ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Name);
            if (!System.IO.File.Exists(path)) { await System.IO.File.WriteAllBytesAsync(path, data); MarkFromInternet(path); }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { _chat.Main.Message = $"Could not open {Name}: {ex.Message}"; }
    }

    [RelayCommand]
    private void CopyImage()
    {
        if (Image is not BitmapSource b) return;
        try { Clipboard.SetImage(b); _chat.Main.Message = "Picture copied"; }
        catch (Exception) { _chat.Main.Message = "Could not use the clipboard"; }
    }

    /// <summary>Marks a file as downloaded, so Windows (SmartScreen, Office Protected View) treats it like one from the internet.</summary>
    private static void MarkFromInternet(string path)
    {
        try { System.IO.File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (Exception) { /* not NTFS: nothing to mark */ }
    }
}
