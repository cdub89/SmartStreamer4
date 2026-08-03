using System;
using System.IO;
using SDRIQStreamer.CWSkimmer;

namespace SmartSDRIQStreamer.CWSkimmer.Tests;

public sealed class LogFilesTests : IDisposable
{
    private readonly string _dir;

    public LogFilesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SmartStreamer4Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteLog(string name, int bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void RotateIfOversized_MovesOversizedFileToOld()
    {
        var path = WriteLog("a.log", bytes: 2_000);

        LogFiles.RotateIfOversized(path, maxBytes: 1_000);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".old"));
    }

    [Fact]
    public void RotateIfOversized_LeavesFileAtOrUnderCapAlone()
    {
        var path = WriteLog("b.log", bytes: 1_000);

        LogFiles.RotateIfOversized(path, maxBytes: 1_000);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".old"));
    }

    [Fact]
    public void RotateIfOversized_ReplacesPriorOldFile()
    {
        var path = WriteLog("c.log", bytes: 2_000);
        File.WriteAllText(path + ".old", "previous generation");

        LogFiles.RotateIfOversized(path, maxBytes: 1_000);

        Assert.False(File.Exists(path));
        Assert.Equal(2_000, new FileInfo(path + ".old").Length);
    }

    [Fact]
    public void RotateIfOversized_MissingFileIsANoOp()
    {
        var path = Path.Combine(_dir, "missing.log");

        LogFiles.RotateIfOversized(path, maxBytes: 1_000);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".old"));
    }
}
