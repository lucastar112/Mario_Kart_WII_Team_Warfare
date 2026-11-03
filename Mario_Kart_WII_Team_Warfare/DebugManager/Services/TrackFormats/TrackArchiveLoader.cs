using System.IO;
namespace DebugManager.Services.TrackFormats;

public sealed record LoadedTrackArchive(
    string TrackId,
    string SourcePath,
    KmpDocument Kmp,
    LexDocument? Lex,
    IReadOnlyCollection<string> ArchiveFiles);

public static class TrackArchiveLoader
{
    /// <summary>Loads the KMP and optional LE-CODE LEX file from a track .szs archive.</summary>
    public static LoadedTrackArchive Load(string szsPath)
    {
        if (string.IsNullOrWhiteSpace(szsPath)) throw new ArgumentException("A track archive path is required.", nameof(szsPath));
        byte[] source = File.ReadAllBytes(szsPath);
        SzsArchiveReader archive = SzsArchiveReader.Open(source);
        byte[] kmpBytes = archive.ReadFile("course.kmp");
        KmpDocument kmp = KmpFileReader.Read(kmpBytes);
        LexDocument? lex = archive.ContainsFile("course.lex")
            ? LexFileReader.Read(archive.ReadFile("course.lex"))
            : null;
        string trackId = Path.GetFileNameWithoutExtension(szsPath);
        return new LoadedTrackArchive(trackId, Path.GetFullPath(szsPath), kmp, lex, archive.FileNames);
    }
}
