using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using VSCDeck.Core;

namespace VSCDeck.App;

internal enum StatusSound { Run, Question, Done }

internal sealed class StatusSounds : IDisposable
{
    private readonly string directory;
    private readonly HashSet<MediaPlayer> voices = [];
    private bool disposed;
    internal IReadOnlyCollection<MediaPlayer> Voices => voices;
    internal string? Warning { get; private set; }
    internal event Action? WarningChanged;

    internal StatusSounds(string? directory = null) =>
        this.directory = directory ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");

    internal static StatusSound? ForTransition(Activity previous, Activity current) =>
        previous == current || previous == Activity.Unknown ? null : current switch
        {
            Activity.Running => StatusSound.Run,
            Activity.Question => StatusSound.Question,
            Activity.Idle when previous == Activity.Running => StatusSound.Done,
            _ => null
        };

    internal static string FolderName(StatusSound sound) => sound switch
    {
        StatusSound.Run => "run",
        StatusSound.Question => "question",
        StatusSound.Done => "done",
        _ => throw new ArgumentOutOfRangeException(nameof(sound), sound, "Unknown status sound.")
    };

    private static string? FindRecording(string folder)
    {
        string[] recordings;
        try
        {
            recordings = Directory.EnumerateFiles(folder, "*.wav", SearchOption.TopDirectoryOnly)
                .Take(2).ToArray();
        }
        catch (DirectoryNotFoundException) { return null; }
        return recordings.Length switch
        {
            0 => null,
            1 => recordings[0],
            _ => throw new InvalidDataException("Place exactly one WAV file in this sound folder.")
        };
    }

    internal void Play(StatusSound sound)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var path = Path.Combine(directory, FolderName(sound));
        MediaPlayer? voice = null;
        try
        {
            var recording = FindRecording(path);
            if (recording is null)
            {
                ClearWarning();
                return;
            }
            path = recording;
            var player = new MediaPlayer { Volume = 0.3 };
            voice = player;
            voices.Add(player);
            player.MediaEnded += (_, _) => Release(player);
            player.MediaFailed += (_, args) =>
            {
                Release(player);
                ReportFailure(recording, args.ErrorException);
            };
            player.MediaOpened += (_, _) => ClearWarning();
            player.Open(new Uri(recording, UriKind.Absolute));
            player.Play();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or COMException)
        {
            if (voice is not null) Release(voice);
            ReportFailure(path, ex);
        }
    }

    private void ClearWarning()
    {
        if (Warning is null) return;
        Warning = null;
        WarningChanged?.Invoke();
    }

    private void ReportFailure(string path, Exception error)
    {
        Warning = $"Cannot play status sound {path}: {error.Message}";
        AppStorage.Log(Warning);
        WarningChanged?.Invoke();
    }

    private void Release(MediaPlayer voice)
    {
        if (voices.Remove(voice)) voice.Close();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var voice in voices.ToArray()) Release(voice);
    }
}
